namespace WikiEdits;

/// <summary>Result of the voting eligibility check for a user in a given vote.</summary>
public sealed class VoteEligibilityResult
{
    public bool Eligible => Failures.Count == 0;

    /// <summary>Reasons for disqualification. Empty when the user is eligible.</summary>
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public DateTime VoteStartUtc { get; init; }

    /// <summary>The determining day: 14 days before the vote opens.</summary>
    public DateTime DeterminingDateUtc { get; init; }

    public DateTime? FirstEditUtc { get; init; }
    public int SeniorityDays { get; init; }

    // Edits in the 90 days before the determining day
    public int RecentManual { get; init; }
    public int RecentAuto { get; init; }
    public int RecentAdminActions { get; init; }
    public int RecentCounted { get; init; }
    public bool RecentTruncated { get; init; }

    // Edits from registration until the determining day
    public int TotalManual { get; init; }
    public int TotalAuto { get; init; }
    public int TotalAdminActions { get; init; }
    public int TotalCounted { get; init; }
    public bool TotalTruncated { get; init; }

    public bool PaidEditor { get; init; }
}

/// <summary>
/// Hebrew Wikipedia voting eligibility rules, as implemented by the checkVoteRights gadget:
/// 365 days of seniority since the first edit, 900 edits in the main namespace and the namespaces adjacent to it,
/// 100 edits in the 90 days before the determining day (14 days before the vote),
/// automated edits count up to 20% of each threshold, and a "paid editor" template disqualifies.
/// </summary>
public static class VoteEligibility
{
    public const int SeniorityDays = 365;
    public const int MinTotalEdits = 900;
    public const int MinRecentEdits = 100;
    public const int DeterminingOffsetDays = 14;
    public const int RecentWindowDays = 90;
    public const double MaxAutoEditShare = 0.20;

    /// <summary>Tags of automated tools. Edits carrying these tags are counted only up to a cap.</summary>
    public static readonly string[] AutoEditTags = { "AWB", "Cat-a-lot", "quick linker", "newcomer task add link" };

    /// <summary>The main namespace and the ones adjacent to it: articles, files, MediaWiki, templates, help, categories, portals, modules.</summary>
    public static readonly int[] CountedNamespaces = { 0, 6, 8, 10, 12, 14, 100, 828 };

    public static readonly string[] PaidEditorTemplates =
    {
        "תבנית:משתמש כותב בתשלום",
        "תבנית:משתמש גילוי נאות",
        "תבנית:משתמש עורך בתשלום",
    };

    /// <param name="determiningOffsetDays">
    /// Days between the determining day and the vote start. The rule says 14; a smaller value lets the caller
    /// preview eligibility for a vote that has not been scheduled yet, as if it opened sooner.
    /// </param>
    public static async Task<VoteEligibilityResult> CheckAsync(
        WikipediaClient client,
        string site,
        string user,
        DateTime voteStartUtc,
        IProgress<string>? progress,
        CancellationToken ct,
        int determiningOffsetDays = DeterminingOffsetDays)
    {
        var determining = voteStartUtc.AddDays(-determiningOffsetDays);
        var recentStart = determining.AddDays(-RecentWindowDays);
        var failures = new List<string>();

        // 1. Seniority: the first edit must be at least 365 days before the vote opens
        progress?.Report("בודק ותק...");
        var firstEdit = await client.GetFirstEditAsync(site, user, ct);
        int seniorityDays = firstEdit is DateTime fe ? (int)(voteStartUtc - fe).TotalDays : 0;
        if (firstEdit is null)
        {
            // No edits, nothing to count. Saves six pointless requests to the server.
            return new VoteEligibilityResult
            {
                Failures = new[] { "למשתמש אין עריכות כלל" },
                VoteStartUtc = voteStartUtc,
                DeterminingDateUtc = determining,
            };
        }
        if (voteStartUtc.AddDays(-SeniorityDays) < firstEdit.Value)
            failures.Add($"אין {SeniorityDays} ימי ותק (העריכה הראשונה לפני {seniorityDays:N0} ימים)");

        // 2. Recent activity: 100 edits in the 90 days before the determining day.
        // The gadget stops counting as soon as the threshold is reached, but we show the number to the user,
        // so we count everything. Very active editors reach thousands of edits in 90 days, hence the high cap
        // (50 requests = 25,000 edits).
        progress?.Report($"סופר עריכות ב-{RecentWindowDays} הימים שלפני היום הקובע...");
        int recentAutoCap = (int)Math.Floor(MinRecentEdits * MaxAutoEditShare);
        var recent = await client.CountEditsAsync(
            site, user, CountedNamespaces, recentStart, determining, AutoEditTags, maxBatches: 50, stopWhen: null, ct);
        int recentCounted = recent.Manual + Math.Min(recent.Auto, recentAutoCap);
        int recentAdmin = 0;
        if (recentCounted < MinRecentEdits)
        {
            // Admin actions count as manual edits, but are only checked when edits fall short
            progress?.Report("משלים מפעולות מפעיל...");
            recentAdmin = await client.CountAdminActionsAsync(site, user, recentStart, determining, ct);
            recentCounted += recentAdmin;
        }
        if (recentCounted < MinRecentEdits)
            failures.Add($"רק {recentCounted:N0} עריכות נספרות ב-{RecentWindowDays} הימים שלפני היום הקובע (נדרשות {MinRecentEdits})");

        // 3. Total: 900 edits from registration until the determining day. Counting stops once the threshold is reached.
        progress?.Report("סופר את כלל העריכות...");
        int totalAutoCap = (int)Math.Floor(MinTotalEdits * MaxAutoEditShare);
        var total = await client.CountEditsAsync(
            site, user, CountedNamespaces, fromUtc: null, determining, AutoEditTags, maxBatches: 10,
            stopWhen: (manual, auto) => manual + Math.Min(auto, totalAutoCap) >= MinTotalEdits, ct);
        int totalCounted = total.Manual + Math.Min(total.Auto, totalAutoCap);
        int totalAdmin = 0;
        if (totalCounted < MinTotalEdits && !total.Truncated)
        {
            progress?.Report("משלים מפעולות מפעיל...");
            totalAdmin = await client.CountAdminActionsAsync(site, user, fromUtc: null, determining, ct);
            totalCounted += totalAdmin;
        }
        if (totalCounted < MinTotalEdits)
            failures.Add($"רק {totalCounted:N0} עריכות נספרות בסך הכל (נדרשות {MinTotalEdits})");

        // 4. Paid editor
        progress?.Report("בודק תבנית עורך בתשלום...");
        bool paid = await client.PageHasAnyTemplateAsync(site, "User:" + user, PaidEditorTemplates, ct);
        if (paid)
            failures.Add("בדף המשתמש מוצגת תבנית של עורך בתשלום");

        return new VoteEligibilityResult
        {
            Failures = failures,
            VoteStartUtc = voteStartUtc,
            DeterminingDateUtc = determining,
            FirstEditUtc = firstEdit,
            SeniorityDays = seniorityDays,
            RecentManual = recent.Manual,
            RecentAuto = recent.Auto,
            RecentAdminActions = recentAdmin,
            RecentCounted = recentCounted,
            RecentTruncated = recent.Truncated,
            TotalManual = total.Manual,
            TotalAuto = total.Auto,
            TotalAdminActions = totalAdmin,
            TotalCounted = totalCounted,
            TotalTruncated = total.Truncated,
            PaidEditor = paid,
        };
    }
}
