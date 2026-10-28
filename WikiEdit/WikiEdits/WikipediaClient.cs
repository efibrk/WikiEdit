using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WikiEdits;

/// <summary>Access to the MediaWiki API of a Wikipedia site.</summary>
public sealed class WikipediaClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>
    /// Ensures API requests are sent one at a time, never in parallel.
    /// Wikipedia requires this, and it prevents most 429 (Too Many Requests) responses.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cache of tag display names per site. Fetched once per site.</summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tagNamesBySite = new();

    /// <summary>Cache of diffs already fetched, so returning to a row does not send another request.</summary>
    private readonly Dictionary<(string Site, long RevisionId, DiffTarget Target), WikiDiff> _diffCache = new();
    private const int MaxCachedDiffs = 300;

    /// <summary>
    /// Raised when the server asked the client to slow down (429 or 503) and the client is about to wait
    /// for the given time before retrying. Lets the UI show the wait instead of appearing frozen.
    /// </summary>
    public event Action<TimeSpan>? Throttled;

    public WikipediaClient()
    {
        // Compression matters for large diffs and articles; HttpClient does not request it by default.
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // On .NET Framework the connection pool is configured through ServicePointManager rather than
        // through the handler. Keeping connections alive and reusable matters here: without it, a request
        // sent after an idle pause pays for a fresh TCP and TLS handshake, which was measured at
        // 300-600 ms and made the diff panel feel slow.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        ServicePointManager.DefaultConnectionLimit = 8;
        ServicePointManager.Expect100Continue = false;

        // Wikipedia requires an identifying User-Agent. Replace the e-mail address with real contact details.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WikiEdits/1.0 (contact: your-email@example.com)");
    }

    /// <summary>
    /// Returns one page of the user's contributions, newest first.
    /// The API returns at most 500 results per request, so a larger page is built from several requests.
    /// </summary>
    /// <param name="site">Site domain, e.g. he.wikipedia.org</param>
    /// <param name="user">User name without the "User:" prefix</param>
    /// <param name="ns">Namespace filter (0 = articles), or null for all namespaces</param>
    /// <param name="pageSize">Maximum number of results in the page</param>
    /// <param name="continueToken">Continuation token from the previous page, or null for the first page</param>
    /// <param name="progress">Reports the number of results received so far for this page</param>
    public async Task<ContributionsPage> GetUserContributionsPageAsync(
        string site,
        string user,
        int? ns,
        int pageSize,
        string? continueToken,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var tagNames = await GetTagDisplayNamesAsync(site, ct);
        var results = new List<WikiEdit>();

        do
        {
            int batch = Compat.Clamp(pageSize - results.Count, 1, 500);

            var query = new Dictionary<string, string>
            {
                ["list"] = "usercontribs",
                ["ucuser"] = user,
                ["uclimit"] = batch.ToString(CultureInfo.InvariantCulture),
                ["ucprop"] = "ids|title|timestamp|comment|size|sizediff|flags|tags",
            };
            if (ns is not null)
                query["ucnamespace"] = ns.Value.ToString(CultureInfo.InvariantCulture);
            if (continueToken is not null)
                query["uccontinue"] = continueToken;

            using var doc = await QueryAsync(site, query, ct);
            var root = doc.RootElement;

            foreach (var e in root.GetProperty("query").GetProperty("usercontribs").EnumerateArray())
            {
                var tags = ReadTags(e);

                results.Add(new WikiEdit
                {
                    RevisionId = e.GetProperty("revid").GetInt64(),
                    ParentId = e.GetProperty("parentid").GetInt64(),
                    Title = e.GetProperty("title").GetString() ?? "",
                    User = e.TryGetProperty("user", out var usr) ? usr.GetString() ?? user : user,
                    Timestamp = ParseTimestamp(e.GetProperty("timestamp").GetString()!),
                    Comment = e.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "(תקציר מוסתר)",
                    Size = e.TryGetProperty("size", out var sz) ? sz.GetInt32() : 0,
                    SizeDiff = e.TryGetProperty("sizediff", out var sd) ? sd.GetInt32() : 0,
                    Minor = e.TryGetProperty("minor", out var m) && m.GetBoolean(),
                    IsNew = e.TryGetProperty("new", out var n) && n.GetBoolean(),
                    IsTop = e.TryGetProperty("top", out var t) && t.GetBoolean(),
                    Tags = tags,
                    TagsDisplay = FormatTags(tags, tagNames),
                });
            }

            progress?.Report(results.Count);

            continueToken = root.TryGetProperty("continue", out var cont)
                ? cont.GetProperty("uccontinue").GetString()
                : null;
        }
        while (continueToken is not null && results.Count < pageSize);

        return new ContributionsPage(results, continueToken);
    }

    /// <summary>
    /// Returns one page of a page's revision history, newest first, in the same shape as a user contributions page.
    /// Redirects are resolved automatically and the title in the results is the normalized page title.
    /// </summary>
    public async Task<ContributionsPage> GetPageRevisionsPageAsync(
        string site,
        string title,
        int pageSize,
        string? continueToken,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var tagNames = await GetTagDisplayNamesAsync(site, ct);
        var results = new List<WikiEdit>();
        long lastRevisionId = 0;
        string resolvedTitle = title;

        do
        {
            int batch = Compat.Clamp(pageSize - results.Count, 1, 500);

            var query = new Dictionary<string, string>
            {
                ["prop"] = "revisions|info",
                ["titles"] = title,
                ["redirects"] = "1",
                ["rvprop"] = "ids|timestamp|user|comment|size|flags|tags",
                ["rvlimit"] = batch.ToString(CultureInfo.InvariantCulture),
                ["rvdir"] = "older",
            };
            if (continueToken is not null)
                query["rvcontinue"] = continueToken;

            using var doc = await QueryAsync(site, query, ct);
            var root = doc.RootElement;
            var page = root.GetProperty("query").GetProperty("pages")[0];

            if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _))
                throw new InvalidOperationException($"הדף \"{title}\" לא קיים באתר {site}.");

            resolvedTitle = page.GetProperty("title").GetString() ?? title;
            if (page.TryGetProperty("lastrevid", out var lastRev))
                lastRevisionId = lastRev.GetInt64();

            if (page.TryGetProperty("revisions", out var revisions))
            {
                foreach (var r in revisions.EnumerateArray())
                {
                    var tags = ReadTags(r);
                    long revId = r.GetProperty("revid").GetInt64();
                    long parentId = r.TryGetProperty("parentid", out var pid) ? pid.GetInt64() : 0;

                    results.Add(new WikiEdit
                    {
                        RevisionId = revId,
                        ParentId = parentId,
                        Title = resolvedTitle,
                        User = r.TryGetProperty("user", out var usr) ? usr.GetString() ?? "" : "(משתמש מוסתר)",
                        Timestamp = ParseTimestamp(r.GetProperty("timestamp").GetString()!),
                        Comment = r.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "(תקציר מוסתר)",
                        Size = r.TryGetProperty("size", out var sz) ? sz.GetInt32() : 0,
                        Minor = r.TryGetProperty("minor", out var m) && m.GetBoolean(),
                        IsNew = parentId == 0,
                        IsTop = revId == lastRevisionId,
                        Tags = tags,
                        TagsDisplay = FormatTags(tags, tagNames),
                    });
                }
            }

            progress?.Report(results.Count);

            continueToken = root.TryGetProperty("continue", out var cont) && cont.TryGetProperty("rvcontinue", out var rc)
                ? rc.GetString()
                : null;
        }
        while (continueToken is not null && results.Count < pageSize);

        // The API does not return a size diff for revisions, so compute it against each revision's predecessor.
        // A page's history is linear, so the next item in the list is always the parent.
        for (int i = 0; i + 1 < results.Count; i++)
            results[i].SizeDiff = results[i].Size - results[i + 1].Size;

        if (results.Count > 0)
        {
            var oldest = results[^1];
            if (oldest.ParentId == 0)
                oldest.SizeDiff = oldest.Size;
            else if (await GetRevisionSizeAsync(site, oldest.ParentId, ct) is int parentSize)
                oldest.SizeDiff = oldest.Size - parentSize;
        }

        return new ContributionsPage(results, continueToken);
    }

    /// <summary>The revision adjacent to a given one in the page history, newer or older. Null at either end of the history.</summary>
    public async Task<RevisionRef?> GetAdjacentRevisionAsync(string site, string title, long revisionId, bool newer, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["prop"] = "revisions",
            ["titles"] = title,
            ["redirects"] = "1",
            ["rvstartid"] = revisionId.ToString(CultureInfo.InvariantCulture),
            ["rvdir"] = newer ? "newer" : "older",
            ["rvlimit"] = "2",
            ["rvprop"] = "ids",
        };

        using var doc = await QueryAsync(site, query, ct);
        var page = doc.RootElement.GetProperty("query").GetProperty("pages")[0];
        if (!page.TryGetProperty("revisions", out var revisions))
            return null;

        // The first result is the revision itself; the second (if any) is the adjacent one.
        foreach (var r in revisions.EnumerateArray())
        {
            long id = r.GetProperty("revid").GetInt64();
            if (id == revisionId)
                continue;
            return new RevisionRef(id, r.TryGetProperty("parentid", out var p) ? p.GetInt64() : 0);
        }
        return null;
    }

    private async Task<int?> GetRevisionSizeAsync(string site, long revisionId, CancellationToken ct)
    {
        try
        {
            var query = new Dictionary<string, string>
            {
                ["prop"] = "revisions",
                ["revids"] = revisionId.ToString(CultureInfo.InvariantCulture),
                ["rvprop"] = "size",
            };
            using var doc = await QueryAsync(site, query, ct);
            var page = doc.RootElement.GetProperty("query").GetProperty("pages")[0];
            return page.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0 && revs[0].TryGetProperty("size", out var s)
                ? s.GetInt32()
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null; // Deleted or hidden revision. The last row's size diff stays 0.
        }
    }

    private static string[] ReadTags(JsonElement e) =>
        e.TryGetProperty("tags", out var tagsEl)
            ? tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>Account details: edit count, registration date, user groups and block status.</summary>
    public async Task<WikiUserInfo> GetUserInfoAsync(string site, string user, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["list"] = "users",
            ["ususers"] = user,
            ["usprop"] = "editcount|registration|groups|blockinfo",
        };

        using var doc = await QueryAsync(site, query, ct);
        var users = doc.RootElement.GetProperty("query").GetProperty("users");
        if (users.GetArrayLength() == 0)
            return new WikiUserInfo { Name = user, Exists = false };

        var u = users[0];
        bool missing = u.TryGetProperty("missing", out _) || u.TryGetProperty("invalid", out _);
        if (missing)
            return new WikiUserInfo { Name = u.GetProperty("name").GetString() ?? user, Exists = false };

        string? registration = u.TryGetProperty("registration", out var reg) ? reg.GetString() : null;

        return new WikiUserInfo
        {
            Name = u.GetProperty("name").GetString() ?? user,
            Exists = true,
            EditCount = u.TryGetProperty("editcount", out var ec) ? ec.GetInt64() : 0,
            Registration = registration is null ? null : ParseTimestamp(registration),
            Groups = u.TryGetProperty("groups", out var g)
                ? g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>(),
            IsBlocked = u.TryGetProperty("blockid", out _),
            BlockedBy = u.TryGetProperty("blockedby", out var bb) ? bb.GetString() : null,
            BlockReason = u.TryGetProperty("blockreason", out var br) ? br.GetString() : null,
            BlockExpiry = u.TryGetProperty("blockexpiry", out var be) ? be.GetString() : null,
        };
    }

    /// <summary>
    /// The diff between a given revision and either its predecessor or the page's current revision,
    /// as an HTML table in Wikipedia's own format.
    /// </summary>
    public async Task<WikiDiff> GetDiffAsync(string site, long revisionId, DiffTarget target, CancellationToken ct)
    {
        var key = (site, revisionId, target);
        if (_diffCache.TryGetValue(key, out var cached))
            return cached;

        var parameters = new Dictionary<string, string>
        {
            ["fromrev"] = revisionId.ToString(CultureInfo.InvariantCulture),
            ["torelative"] = target == DiffTarget.Previous ? "prev" : "cur",
            ["difftype"] = "table",
            ["prop"] = "ids|title|user|comment|timestamp|diffsize|diff",
        };

        using var doc = await ApiAsync(site, "compare", parameters, ct);
        var compare = doc.RootElement.GetProperty("compare");

        // The body is the tr rows of the diff table, exactly what Wikipedia shows on its compare page.
        string body = compare.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        // With "prev" the server swaps sides so the requested revision is "to"; with "cur" it stays "from".
        string side = target == DiffTarget.Previous ? "to" : "from";

        var diff = new WikiDiff
        {
            RevisionId = revisionId,
            FromRevisionId = compare.TryGetProperty("fromrevid", out var f) ? f.GetInt64() : 0,
            ToRevisionId = compare.TryGetProperty("torevid", out var t) ? t.GetInt64() : 0,
            Title = compare.TryGetProperty(side + "title", out var tt) ? tt.GetString() ?? "" : "",
            RevisionUser = compare.TryGetProperty(side + "user", out var u)
                ? u.GetString() ?? ""
                : compare.TryGetProperty(side + "userhidden", out _) ? "(משתמש מוסתר)" : "",
            RevisionTimestamp = compare.TryGetProperty(side + "timestamp", out var ts) ? ParseTimestamp(ts.GetString()!) : null,
            RevisionComment = compare.TryGetProperty(side + "comment", out var cm)
                ? cm.GetString() ?? ""
                : compare.TryGetProperty(side + "commenthidden", out _) ? "(תקציר מוסתר)" : "",
            DiffSize = compare.TryGetProperty("diffsize", out var s) ? s.GetInt32() : 0,
            Html = body.Trim(),
        };

        // A diff against the previous revision never changes. A diff against the current revision may change,
        // but within a single run of the program that is a fair price for the saved requests.
        if (_diffCache.Count >= MaxCachedDiffs)
            _diffCache.Clear();
        _diffCache[key] = diff;

        return diff;
    }

    /// <summary>
    /// The full article as the wiki renders it, at a given revision.
    /// Section edit links and the table of contents are left out, since there is nothing to edit here.
    /// </summary>
    public async Task<WikiArticle> GetArticleAsync(string site, long revisionId, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["oldid"] = revisionId.ToString(CultureInfo.InvariantCulture),
            ["prop"] = "text",
            ["disableeditsection"] = "1",
            ["disabletoc"] = "1",
            ["disablelimitreport"] = "1",
        };

        using var doc = await ApiAsync(site, "parse", parameters, ct);
        var parse = doc.RootElement.GetProperty("parse");

        return new WikiArticle(
            parse.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            parse.TryGetProperty("revid", out var r) ? r.GetInt64() : revisionId,
            parse.TryGetProperty("text", out var html) ? html.GetString() ?? "" : "");
    }

    // ----------------------------------------------------------------------
    // Queries for the voting eligibility check
    // ----------------------------------------------------------------------

    /// <summary>Result of counting edits: manual, automated (by tag), and whether counting stopped before the end.</summary>
    public sealed record EditCount(int Manual, int Auto, bool Truncated);

    /// <summary>Timestamp of the user's first edit (UTC), or null if the user has no edits.</summary>
    public async Task<DateTime?> GetFirstEditAsync(string site, string user, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["list"] = "usercontribs",
            ["ucuser"] = user,
            ["ucdir"] = "newer",
            ["uclimit"] = "1",
            ["ucprop"] = "timestamp",
        };

        using var doc = await QueryAsync(site, query, ct);
        var contribs = doc.RootElement.GetProperty("query").GetProperty("usercontribs");
        return contribs.GetArrayLength() == 0
            ? null
            : ParseTimestampUtc(contribs[0].GetProperty("timestamp").GetString()!);
    }

    /// <summary>
    /// Counts the user's edits in the given namespaces within a time range (UTC),
    /// separating edits that carry an automated-tool tag.
    /// </summary>
    /// <param name="fromUtc">Start of the range, or null for the beginning of the history</param>
    /// <param name="toUtc">End of the range</param>
    /// <param name="maxBatches">Maximum number of requests (500 edits each)</param>
    /// <param name="stopWhen">Early stop: receives (manual, auto) and returns true when counting can stop</param>
    public async Task<EditCount> CountEditsAsync(
        string site,
        string user,
        IEnumerable<int> namespaces,
        DateTime? fromUtc,
        DateTime toUtc,
        IReadOnlyCollection<string> autoTags,
        int maxBatches,
        Func<int, int, bool>? stopWhen,
        CancellationToken ct)
    {
        int manual = 0, auto = 0, batches = 0;
        string? continueToken = null;
        string nsList = string.Join("|", namespaces);

        do
        {
            var query = new Dictionary<string, string>
            {
                ["list"] = "usercontribs",
                ["ucuser"] = user,
                ["ucdir"] = "newer",
                ["uclimit"] = "500",
                ["ucprop"] = "timestamp|tags",
                ["ucnamespace"] = nsList,
                ["ucend"] = ToMwTimestamp(toUtc),
            };
            if (fromUtc is DateTime from)
                query["ucstart"] = ToMwTimestamp(from);
            if (continueToken is not null)
                query["uccontinue"] = continueToken;

            using var doc = await QueryAsync(site, query, ct);
            var root = doc.RootElement;

            foreach (var e in root.GetProperty("query").GetProperty("usercontribs").EnumerateArray())
            {
                bool isAuto = e.TryGetProperty("tags", out var tags) &&
                              tags.EnumerateArray().Any(t => autoTags.Contains(t.GetString() ?? ""));
                if (isAuto) auto++; else manual++;
            }

            continueToken = root.TryGetProperty("continue", out var cont)
                ? cont.GetProperty("uccontinue").GetString()
                : null;
            batches++;

            if (stopWhen is not null && stopWhen(manual, auto))
                break;
        }
        while (continueToken is not null && batches < maxBatches);

        return new EditCount(manual, auto, Truncated: continueToken is not null);
    }

    /// <summary>
    /// Number of admin actions (delete, protect, block) the user performed within a time range.
    /// The API accepts a single log type per request, so one request is sent per type.
    /// </summary>
    public async Task<int> CountAdminActionsAsync(string site, string user, DateTime? fromUtc, DateTime toUtc, CancellationToken ct)
    {
        int total = 0;
        foreach (var logType in new[] { "delete", "protect", "block" })
        {
            string? continueToken = null;
            do
            {
                var query = new Dictionary<string, string>
                {
                    ["list"] = "logevents",
                    ["leuser"] = user,
                    ["letype"] = logType,
                    ["leprop"] = "timestamp",
                    ["lelimit"] = "500",
                    ["ledir"] = "newer",
                    ["leend"] = ToMwTimestamp(toUtc),
                };
                if (fromUtc is DateTime from)
                    query["lestart"] = ToMwTimestamp(from);
                if (continueToken is not null)
                    query["lecontinue"] = continueToken;

                using var doc = await QueryAsync(site, query, ct);
                var root = doc.RootElement;
                total += root.GetProperty("query").GetProperty("logevents").GetArrayLength();
                continueToken = root.TryGetProperty("continue", out var cont)
                    ? cont.GetProperty("lecontinue").GetString()
                    : null;
            }
            while (continueToken is not null && total < 5000);
        }
        return total;
    }

    /// <summary>Whether the page contains at least one of the given templates (full names, including the "Template:" prefix).</summary>
    public async Task<bool> PageHasAnyTemplateAsync(string site, string title, IEnumerable<string> templates, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["prop"] = "templates",
            ["titles"] = title,
            ["tltemplates"] = string.Join("|", templates),
            ["tllimit"] = "50",
        };

        using var doc = await QueryAsync(site, query, ct);
        foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray())
        {
            if (page.TryGetProperty("templates", out var found) && found.GetArrayLength() > 0)
                return true;
        }
        return false;
    }

    /// <summary>URL for viewing the diff of a given edit in a browser.</summary>
    public static string DiffUrl(string site, WikiEdit edit) =>
        edit.ParentId == 0
            ? $"https://{site}/w/index.php?oldid={edit.RevisionId}"
            : $"https://{site}/w/index.php?diff={edit.RevisionId}&oldid={edit.ParentId}";

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    // ----------------------------------------------------------------------
    // Internal helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Display names of the site's tags. Hidden tags (no display name) are excluded.
    /// If the fetch fails an empty map is returned and tags are shown by their internal names.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetTagDisplayNamesAsync(string site, CancellationToken ct)
    {
        if (_tagNamesBySite.TryGetValue(site, out var cached))
            return cached;

        var names = new Dictionary<string, string>();
        try
        {
            var query = new Dictionary<string, string>
            {
                ["list"] = "tags",
                ["tgprop"] = "displayname",
                ["tglimit"] = "500",
            };
            using var doc = await QueryAsync(site, query, ct);
            foreach (var tag in doc.RootElement.GetProperty("query").GetProperty("tags").EnumerateArray())
            {
                string name = tag.GetProperty("name").GetString() ?? "";
                string display = tag.TryGetProperty("displayname", out var d) ? StripHtml(d.GetString() ?? "") : "";
                if (name.Length > 0)
                    names[name] = display;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Display names are a convenience only. Without them the internal names are shown.
        }

        _tagNamesBySite[site] = names;
        return names;
    }

    private static string FormatTags(IReadOnlyList<string> tags, IReadOnlyDictionary<string, string> names)
    {
        var parts = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            if (names.TryGetValue(tag, out var display))
            {
                if (display.Length > 0) parts.Add(display); // Empty display name = hidden tag
            }
            else
            {
                parts.Add(tag);
            }
        }
        return string.Join(", ", parts);
    }

    /// <summary>Sends an action=query request and returns the JSON, or throws if the API returned an error.</summary>
    private Task<JsonDocument> QueryAsync(string site, Dictionary<string, string> parameters, CancellationToken ct) =>
        ApiAsync(site, "query", parameters, ct);

    /// <summary>Sends an API request with the given action and returns the JSON, or throws if the API returned an error.</summary>
    private async Task<JsonDocument> ApiAsync(string site, string action, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var query = new Dictionary<string, string>(parameters)
        {
            ["action"] = action,
            ["format"] = "json",
            ["formatversion"] = "2",
        };

        string url = $"https://{site}/w/api.php?" +
                     string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        string json;
        await _gate.WaitAsync(ct);
        try
        {
            json = await GetWithRetryAsync(url, ct);
        }
        finally
        {
            _gate.Release();
        }

        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            string info = error.TryGetProperty("info", out var i) ? i.GetString() ?? "" : "שגיאה לא ידועה";
            doc.Dispose();
            throw new InvalidOperationException($"ה-API החזיר שגיאה: {info}");
        }
        return doc;
    }

    /// <summary>
    /// Sends a GET request. On 429 (Too Many Requests) or 503 (overload) it waits and retries,
    /// honouring the Retry-After header when present, otherwise with exponential backoff.
    /// </summary>
    private async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 4;

        for (int attempt = 1; ; attempt++)
        {
            using var response = await _http.GetAsync(url, ct);

            bool throttled = response.StatusCode is Compat.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!throttled)
            {
                response.EnsureSuccessStatusCode();
                // .NET Framework has no cancellable overload; the request itself is already cancellable.
                return await response.Content.ReadAsStringAsync();
            }

            if (attempt >= maxAttempts)
                throw new HttpRequestException(
                    "השרת מגביל את קצב הבקשות (429). יש להמתין כמה שניות ולנסות שוב.");

            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset until)
                retryAfter = until - DateTimeOffset.UtcNow;

            var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2, 4, 8 seconds

            // Honour Retry-After as given (observed up to 40 s). Retrying earlier just earns another 429.
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);

            Throttled?.Invoke(delay);
            await Task.Delay(delay, ct);
        }
    }

    private static DateTime ParseTimestamp(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime();

    private static DateTime ParseTimestampUtc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>Timestamp in the format the API accepts: ISO 8601 in UTC.</summary>
    private static string ToMwTimestamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // The [GeneratedRegex] source generator needs .NET 7 or later, so the pattern is compiled once here instead.
    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled);

    private static string StripHtml(string html) =>
        WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, "")).Trim();
}
