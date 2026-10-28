using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace WikiEdits;

public partial class MainWindow : Window
{
    private readonly WikipediaClient _client = new();
    private readonly QueryHistory _history = new();
    private CancellationTokenSource? _cts;

    /// <summary>The active diff request. Cancelled when another row is selected.</summary>
    private CancellationTokenSource? _diffCts;

    /// <summary>
    /// Short delay between a selection change in the grid and sending the diff request.
    /// Arrowing quickly through ten rows then sends one request instead of ten.
    /// </summary>
    private readonly DispatcherTimer _diffDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // Paging state of the current query.
    // Pages already fetched are kept in memory, so going back does not require another server request.
    private readonly List<List<WikiEdit>> _pages = new();
    private readonly List<string?> _pageTokens = new(); // _pageTokens[i] = token for fetching page i+1, or null if i is the last page
    private int _pageIndex = -1;
    private string _querySite = "";
    private string _queryName = "";      // User name or page title, depending on the mode
    private bool _queryPageMode;         // Mode the last query ran in (does not change if the user flips the switch afterwards)
    private int? _queryNs;
    private int _pageSize;

    /// <summary>Revision to select automatically after the first page loads, when switching between user and page mode from the menu.</summary>
    private long? _selectAfterLoad;

    // Diff panel state: the row selected in the grid, and the revision currently shown (which may differ after navigating the history).
    private WikiEdit? _selectedEdit;
    private long _navRevisionId;
    private long _navParentId;
    private string _navTitle = "";

    /// <summary>True while the panel shows the full article instead of the diff.</summary>
    private bool _showingArticle;

    /// <summary>The browser control initializes asynchronously. Until it is ready, the latest content is kept here and shown once initialization completes.</summary>
    private bool _webReady;
    private string? _pendingHtml;

    // Countdown shown while the client waits out a server rate limit (429 Retry-After).
    private readonly DispatcherTimer _throttleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _throttleUntil;
    private string _statusBeforeThrottle = "";

    private static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0, 120, 0));
    private static readonly Brush BadBrush = Brushes.Firebrick;
    private static readonly Brush NormalBrush = SystemColors.ControlTextBrush;

    /// <summary>Translation of common user groups. Groups not listed here are shown by their internal name.</summary>
    private static readonly Dictionary<string, string> GroupNames = new()
    {
        ["sysop"] = "מפעיל מערכת",
        ["bureaucrat"] = "ביורוקרט",
        ["bot"] = "בוט",
        ["interface-admin"] = "מנהל ממשק",
        ["checkuser"] = "בודק",
        ["suppress"] = "מסתיר",
        ["patroller"] = "מנטר",
        ["autopatrolled"] = "עורך אוטומטי",
        ["editor"] = "עורך ותיק",
        ["reviewer"] = "בודק שינויים",
        ["rollbacker"] = "משחזר",
        ["extendedconfirmed"] = "משתמש ותיק",
        ["founder"] = "מייסד",
        ["steward"] = "דייל",
    };

    /// <summary>Default groups every account gets, so not worth displaying.</summary>
    private static readonly HashSet<string> DefaultGroups = new() { "*", "user", "autoconfirmed" };

    /// <summary>A Wikipedia edition offered in the site dropdown. ToString returns the domain so the editable box shows it.</summary>
    private sealed record WikiSite(string Name, string Domain)
    {
        public string Label => $"{Name} ({Domain})";
        public override string ToString() => Domain;
    }

    /// <summary>Hebrew first (the default), then the ten largest Wikipedias.</summary>
    private static readonly WikiSite[] Sites =
    {
        new("עברית", "he.wikipedia.org"),
        new("אנגלית", "en.wikipedia.org"),
        new("גרמנית", "de.wikipedia.org"),
        new("צרפתית", "fr.wikipedia.org"),
        new("ספרדית", "es.wikipedia.org"),
        new("רוסית", "ru.wikipedia.org"),
        new("יפנית", "ja.wikipedia.org"),
        new("איטלקית", "it.wikipedia.org"),
        new("סינית", "zh.wikipedia.org"),
        new("פורטוגזית", "pt.wikipedia.org"),
        new("ערבית", "ar.wikipedia.org"),
    };

    /// <summary>The switch at the top of the window: false = by user, true = by page.</summary>
    private bool PageMode => cmbMode.SelectedIndex == 1;

    /// <summary>True while a server operation started by the fetch or vote-check button is running.</summary>
    private bool Busy => _cts is not null;

    /// <summary>Which button started the running operation. Only that button turns into a "stop" button.</summary>
    private enum BusyKind { Fetch, VoteCheck }
    private BusyKind _busyKind;

    public MainWindow()
    {
        InitializeComponent();

        // Reopen where the window was last closed (size, position, maximized / minimized state)
        WindowPlacement.Load()?.ApplyTo(this);

        cmbSite.ItemsSource = Sites;
        cmbSite.SelectedIndex = 0;
        cmbPageSize.ItemsSource = new[] { "100", "250", "500", "1000", "2000", "5000" };
        cmbPageSize.Text = "500";
        cmbNamespace.SelectedIndex = 0;
        cmbDiffMode.SelectedIndex = 0;
        dtpVoteStart.SelectedDate = DateTime.Today;
        cmbWaitDays.ItemsSource = Enumerable.Range(0, VoteEligibility.DeterminingOffsetDays + 1).ToList();
        cmbWaitDays.SelectedItem = VoteEligibility.DeterminingOffsetDays;

        _history.Load();
        cmbMode.SelectedIndex = 0;
        ApplyMode();
        UpdatePagingControls();
        UpdateRevisionNavButtons();

        _diffDebounce.Tick += (_, _) =>
        {
            _diffDebounce.Stop();
            _ = LoadDiffAsync();
        };

        _client.Throttled += OnThrottled;
        _throttleTimer.Tick += (_, _) => UpdateThrottleNotice();

        Loaded += async (_, _) => await InitWebViewAsync();
    }

    /// <summary>Shows a countdown in the status bar (and in the diff panel if a diff is pending) while the client waits out a rate limit.</summary>
    private void OnThrottled(TimeSpan delay)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnThrottled(delay));
            return;
        }

        if (!_throttleTimer.IsEnabled)
            _statusBeforeThrottle = lblStatus.Text;
        _throttleUntil = DateTime.Now + delay;

        if (_diffCts is not null)
            ShowDiffMessage($"ויקיפדיה מגבילה את קצב הבקשות. ממתין {(int)Math.Ceiling(delay.TotalSeconds)} שניות לפני ניסיון חוזר...");

        UpdateThrottleNotice();
        _throttleTimer.Start();
    }

    private void UpdateThrottleNotice()
    {
        int seconds = (int)Math.Ceiling((_throttleUntil - DateTime.Now).TotalSeconds);
        if (seconds <= 0)
        {
            _throttleTimer.Stop();
            lblStatus.Text = _statusBeforeThrottle;
            return;
        }
        lblStatus.Text = $"ויקיפדיה מגבילה את קצב הבקשות. ממתין {seconds} שניות לפני ניסיון חוזר...";
    }

    /// <summary>
    /// Initializes the browser control. Its data folder lives in the user's profile rather than next to the executable,
    /// so the program also works when installed in a folder without write permission.
    /// </summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WikiEdits", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await webDiff.EnsureCoreWebView2Async(env);

            var core = webDiff.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            webDiff.AllowExternalDrop = false;

            // Links inside the diff open in the default browser, not inside the control.
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenInBrowser(e.Uri);
                }
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenInBrowser(e.Uri);
            };

            _webReady = true;
            if (_pendingHtml is not null)
            {
                webDiff.NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }
        }
        catch (Exception ex)
        {
            // Usually: the WebView2 Runtime is not installed.
            webDiff.Visibility = Visibility.Collapsed;
            lblWebError.Text =
                "לא ניתן להפעיל את פקד הדפדפן להצגת ההפרש.\n\n" +
                "יש לוודא שרכיב Microsoft Edge WebView2 Runtime מותקן:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "פרטי השגיאה: " + ex.Message;
            lblWebError.Visibility = Visibility.Visible;
        }
    }

    private static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ----------------------------------------------------------------------
    // Mode: by user / by page
    // ----------------------------------------------------------------------

    private void cmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded || lblUser is not null)
            ApplyMode();
    }

    /// <summary>Adapts the UI to the selected mode: field label, history list, grid columns and the vote-check row.</summary>
    private void ApplyMode()
    {
        lblUser.Text = PageMode ? "שם הערך:" : "שם משתמש:";
        RefreshNameList();

        colTitle.Visibility = PageMode ? Visibility.Collapsed : Visibility.Visible; // In a page history every row has the same page
        colUser.Visibility = PageMode ? Visibility.Visible : Visibility.Collapsed;  // In a user query every row has the same editor
        lblNamespace.Visibility = PageMode ? Visibility.Collapsed : Visibility.Visible;
        cmbNamespace.Visibility = PageMode ? Visibility.Collapsed : Visibility.Visible;
        rowVote.Visibility = PageMode ? Visibility.Collapsed : Visibility.Visible;  // Voting eligibility is a property of a user
    }

    /// <summary>Fills the dropdown from the history of the current mode without disturbing the text being typed.</summary>
    private void RefreshNameList()
    {
        string current = cmbUser.Text;
        cmbUser.ItemsSource = (PageMode ? _history.Pages : _history.Users).ToList();
        cmbUser.Text = current;
    }

    // ----------------------------------------------------------------------
    // Fetching edits and paging
    // ----------------------------------------------------------------------

    /// <summary>Starts a fetch, or stops the running one: the same button reads "stop" while busy.</summary>
    private void btnRun_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
            _cts?.Cancel();
        else
            _ = RunQueryAsync();
    }

    private async Task RunQueryAsync()
    {
        string name = cmbUser.Text.Trim();
        string site = cmbSite.Text.Trim();

        if (name.Length == 0)
        {
            MessageBox.Show(this, PageMode ? "יש להזין שם ערך." : "יש להזין שם משתמש.", "חסר שם",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            cmbUser.Focus();
            return;
        }
        if (site.Length == 0)
        {
            MessageBox.Show(this, "יש להזין דומיין של אתר ויקי.", "חסר אתר", MessageBoxButton.OK, MessageBoxImage.Warning);
            cmbSite.Focus();
            return;
        }

        _querySite = site;
        _queryName = name;
        _queryPageMode = PageMode;
        _queryNs = cmbNamespace.SelectedIndex switch
        {
            1 => 0,   // Articles
            2 => 1,   // Talk
            3 => 2,   // User
            _ => null // All
        };
        _pageSize = ReadPageSize();

        _pages.Clear();
        _pageTokens.Clear();
        _pageIndex = -1;
        grid.ItemsSource = null;
        pnlUserInfo.Visibility = Visibility.Collapsed;

        await RunBusyAsync(BusyKind.Fetch, async ct =>
        {
            if (_queryPageMode)
            {
                pnlUserInfo.Visibility = Visibility.Visible;
                await FetchPageAsync(continueToken: null, ct);
                if (_pages[0].Count > 0)
                {
                    _history.AddPage(_pages[0][0].Title); // The normalized title, after resolving redirects
                    RefreshNameList();
                }
                return;
            }

            lblStatus.Text = "שולף פרטי חשבון...";
            var info = await _client.GetUserInfoAsync(site, name, ct);
            ShowUserInfo(info);
            pnlUserInfo.Visibility = Visibility.Visible;

            await FetchPageAsync(continueToken: null, ct);

            // Only names that produced something are saved to the history, so typos are not remembered.
            if (info.Exists || _pages[0].Count > 0)
            {
                _history.AddUser(name);
                RefreshNameList();
            }
        });
    }

    /// <summary>Page size from the combo box. An invalid value falls back to the default of 500.</summary>
    private int ReadPageSize()
    {
        string text = cmbPageSize.Text.Replace(",", "").Trim();
        int size = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Compat.Clamp(n, 1, 50000)
            : 500;
        cmbPageSize.Text = size.ToString(CultureInfo.InvariantCulture);
        return size;
    }

    private async void btnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex < 0)
            return;

        // Next page already in memory? Show it without a server request.
        if (_pageIndex + 1 < _pages.Count)
        {
            ShowPage(_pageIndex + 1);
            return;
        }

        string? token = _pageTokens[_pageIndex];
        if (token is null)
            return;

        await RunBusyAsync(BusyKind.Fetch, ct => FetchPageAsync(token, ct));
    }

    private void btnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0)
            ShowPage(_pageIndex - 1);
    }

    /// <summary>Fetches a page from the server, adds it to the cache and shows it.</summary>
    private async Task FetchPageAsync(string? continueToken, CancellationToken ct)
    {
        string what = _queryPageMode ? "גרסאות" : "עריכות";
        lblStatus.Text = $"שולף {what}...";
        var progress = new Progress<int>(count => lblStatus.Text = $"התקבלו {count} {what}...");

        var page = _queryPageMode
            ? await _client.GetPageRevisionsPageAsync(_querySite, _queryName, _pageSize, continueToken, progress, ct)
            : await _client.GetUserContributionsPageAsync(_querySite, _queryName, _queryNs, _pageSize, continueToken, progress, ct);

        _pages.Add(page.Edits);
        _pageTokens.Add(page.ContinueToken);
        ShowPage(_pages.Count - 1);
    }

    /// <summary>Shows a page from the cache and updates the statistics and the status bar.</summary>
    private void ShowPage(int index)
    {
        _pageIndex = index;
        var page = _pages[index];

        // Assigning ItemsSource builds a fresh collection view, which drops the sort the user chose.
        // Capture it first and put it back, so paging keeps the current column sort.
        var sort = grid.Items.SortDescriptions.ToList();
        var directions = grid.Columns.Select(c => c.SortDirection).ToList();

        grid.ItemsSource = page;

        foreach (var description in sort)
            grid.Items.SortDescriptions.Add(description);
        for (int i = 0; i < directions.Count && i < grid.Columns.Count; i++)
            grid.Columns[i].SortDirection = directions[i];

        if (_queryPageMode)
            ShowPageInfo();
        ShowEditStats(_pages.SelectMany(p => p).ToList());

        string what = _queryPageMode ? "גרסאות" : "עריכות";
        if (page.Count == 0)
        {
            lblStatus.Text = index == 0
                ? $"לא נמצאו {what} עבור \"{_queryName}\"."
                : $"אין {what} נוספות.";
        }
        else
        {
            int first = index * _pageSize + 1;
            int last = first + page.Count - 1;
            string tail = _pageTokens[index] is null ? " (סוף הרשימה)" : "";
            lblStatus.Text = $"עמוד {index + 1}: {what} {first:N0} עד {last:N0}{tail}";
        }

        // Switched from the context menu: select the same revision we came from, if it is on this page.
        if (_selectAfterLoad is long wanted)
        {
            _selectAfterLoad = null;
            var row = page.Find(e => e.RevisionId == wanted);
            if (row is not null)
                SelectEdit(row);
        }

        UpdatePagingControls();
    }

    /// <summary>Selects a row by its item, which stays correct whatever order the grid is sorted in.</summary>
    private void SelectEdit(WikiEdit edit)
    {
        grid.SelectedItem = edit;
        grid.ScrollIntoView(edit);
    }

    private void UpdatePagingControls()
    {
        bool hasNext = _pageIndex >= 0 &&
                       (_pageIndex + 1 < _pages.Count || _pageTokens[_pageIndex] is not null);

        btnPrev.IsEnabled = !Busy && _pageIndex > 0;
        btnNext.IsEnabled = !Busy && hasNext;
        lblPage.Text = _pageIndex < 0 ? "" : $"עמוד {_pageIndex + 1}";
    }

    /// <summary>Runs a server operation with the controls locked, cancellation support and error handling.</summary>
    private async Task RunBusyAsync(BusyKind kind, Func<CancellationToken, Task> work)
    {
        // The grid is about to be replaced, so a pending or running diff request is no longer relevant.
        // Without this the query would wait behind it, including any retry back-off delay.
        _diffDebounce.Stop();
        _diffCts?.Cancel();

        _cts = new CancellationTokenSource();
        _busyKind = kind;
        SetBusy(true);
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "בוטל.";
        }
        catch (Exception ex)
        {
            lblStatus.Text = "שגיאה.";
            MessageBox.Show(this, ex.Message, "השאילתה נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    // ----------------------------------------------------------------------
    // Context menu: switching between user and page
    // ----------------------------------------------------------------------

    private void grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A right click selects the row, so the menu refers to it
        if (FindRow(e.OriginalSource) is DataGridRow row)
            grid.SelectedItem = row.Item;
    }

    private void grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (grid.SelectedItem is not WikiEdit edit || Busy)
        {
            e.Handled = true;
            return;
        }

        menuOpenPage.Visibility = _queryPageMode ? Visibility.Collapsed : Visibility.Visible;
        menuOpenUser.Visibility = _queryPageMode ? Visibility.Visible : Visibility.Collapsed;
        menuOpenPage.Header = $"הצגת היסטוריית הערך \"{edit.Title}\"";
        menuOpenUser.Header = $"הצגת העריכות של \"{edit.User}\"";
    }

    private void menuOpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not WikiEdit edit)
            return;

        _selectAfterLoad = edit.RevisionId;
        cmbMode.SelectedIndex = 1;
        cmbUser.Text = edit.Title;
        _ = RunQueryAsync();
    }

    private void menuOpenUser_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not WikiEdit edit)
            return;

        _selectAfterLoad = edit.RevisionId;
        cmbMode.SelectedIndex = 0;
        cmbUser.Text = edit.User;
        _ = RunQueryAsync();
    }

    /// <summary>Finds the grid row that a clicked element belongs to.</summary>
    private static DataGridRow? FindRow(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null && d is not DataGridRow)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return d as DataGridRow;
    }

    // ----------------------------------------------------------------------
    // Info panel
    // ----------------------------------------------------------------------

    private void ShowUserInfo(WikiUserInfo info)
    {
        if (!info.Exists)
        {
            lblInfoEdits.Text = "חשבון לא רשום (ייתכן שזו כתובת IP)";
            lblInfoReg.Text = "";
            lblInfoGroups.Text = "";
            lblInfoBlock.Text = "";
            lblInfoBlock.Foreground = NormalBrush;
            lblInfoSurvived.Text = "";
            lblInfoReverted.Text = "";
            return;
        }

        lblInfoEdits.Text = $"עריכות בסך הכל: {info.EditCount:N0}";

        lblInfoReg.Text = info.Registration is DateTime reg
            ? $"נרשם: {reg:yyyy-MM-dd} ({DescribeAge(reg)})"
            : "נרשם: לפני שנשמרו תאריכי הרשמה";

        var groups = info.Groups
            .Where(g => !DefaultGroups.Contains(g))
            .Select(g => GroupNames.TryGetValue(g, out var name) ? name : g)
            .ToList();
        lblInfoGroups.Text = groups.Count == 0
            ? "קבוצות: משתמש רגיל"
            : "קבוצות: " + string.Join(", ", groups);

        if (info.IsBlocked)
        {
            string expiry = info.BlockExpiry is null or "infinite" or "infinity" or "never"
                ? "ללא הגבלת זמן"
                : "עד " + FormatExpiry(info.BlockExpiry);
            string reason = string.IsNullOrWhiteSpace(info.BlockReason) ? "" : $". סיבה: {info.BlockReason}";
            lblInfoBlock.Text = $"חסימה: חסום {expiry} על ידי {info.BlockedBy}{reason}";
            lblInfoBlock.Foreground = BadBrush;
        }
        else
        {
            lblInfoBlock.Text = "חסימה: אין";
            lblInfoBlock.Foreground = NormalBrush;
        }

        lblInfoSurvived.Text = "גרסה נוכחית: ...";
        lblInfoReverted.Text = "שוחזרו: ...";
    }

    /// <summary>In page mode the panel shows the page title and the number of fetched revisions instead of account details.</summary>
    private void ShowPageInfo()
    {
        string title = _pages.Count > 0 && _pages[0].Count > 0 ? _pages[0][0].Title : _queryName;
        int fetched = _pages.Sum(p => p.Count);
        int editors = _pages.SelectMany(p => p).Select(e => e.User).Distinct(StringComparer.Ordinal).Count();

        lblInfoEdits.Text = $"ערך: {title}";
        lblInfoReg.Text = $"גרסאות שנשלפו: {fetched:N0}";
        lblInfoGroups.Text = $"עורכים שונים: {editors:N0}";
        lblInfoBlock.Text = "";
        lblInfoBlock.Foreground = NormalBrush;
    }

    /// <summary>Statistics computed from all edits fetched so far (not from the account's full history).</summary>
    private void ShowEditStats(IReadOnlyCollection<WikiEdit> edits)
    {
        if (pnlUserInfo.Visibility != Visibility.Visible || edits.Count == 0)
        {
            lblInfoSurvived.Text = "";
            lblInfoReverted.Text = "";
            return;
        }

        int reverted = edits.Count(e => e.IsReverted);

        if (_queryPageMode)
        {
            // In a page history exactly one revision is current, so this metric is meaningless
            lblInfoSurvived.Text = "";
        }
        else
        {
            int top = edits.Count(e => e.IsTop);
            lblInfoSurvived.Text = $"עדיין הגרסה הנוכחית: {top:N0} מתוך {edits.Count:N0} שנשלפו ({Percent(top, edits.Count)})";
        }

        lblInfoReverted.Text = $"שוחזרו: {reverted:N0} ({Percent(reverted, edits.Count)})";
        lblInfoReverted.Foreground = reverted > 0 && reverted * 5 >= edits.Count
            ? BadBrush                   // A fifth or more of the edits were reverted
            : NormalBrush;
    }

    // ----------------------------------------------------------------------
    // Diff panel
    // ----------------------------------------------------------------------

    private void grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedEdit = grid.SelectedItem as WikiEdit;
        _navRevisionId = _selectedEdit?.RevisionId ?? 0;
        _navParentId = _selectedEdit?.ParentId ?? 0;
        _navTitle = _selectedEdit?.Title ?? "";
        ScheduleDiffLoad();
    }

    private void cmbDiffMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (lblDiffTitle is not null)
            ScheduleDiffLoad();
    }

    /// <summary>Cancels a running request, updates the header immediately, and defers the actual send until the selection settles.</summary>
    private void ScheduleDiffLoad()
    {
        CancelDiffRequest();
        _showingArticle = false;

        if (_selectedEdit is WikiEdit edit && _navRevisionId == edit.RevisionId)
        {
            SetDiffHeader(edit.Title, edit.Timestamp, edit.User, edit.Comment);
            ShowDiffMessage("...");
        }
        else if (_selectedEdit is null)
        {
            lblDiffTitle.Text = "יש לבחור שורה בטבלה כדי לראות את ההפרש.";
            lblDiffComment.Text = "";
            ShowDiffMessage("");
        }

        UpdateRevisionNavButtons();
        _diffDebounce.Stop();
        _diffDebounce.Start();
    }

    private void CancelDiffRequest()
    {
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = null;
    }

    private void SetDiffHeader(string title, DateTime? timestamp, string user, string comment)
    {
        lblDiffTitle.Text = timestamp is DateTime ts
            ? $"{title}  |  {ts:yyyy-MM-dd HH:mm}  |  {user}"
            : title;
        lblDiffComment.Text = comment.Length == 0 ? "" : "תקציר: " + comment;
    }

    private async Task LoadDiffAsync()
    {
        if (_selectedEdit is null || _navRevisionId == 0)
            return;

        var cts = new CancellationTokenSource();
        _diffCts = cts;
        await LoadDiffCoreAsync(cts);
    }

    /// <summary>Fetches and shows the diff of the revision in the panel (_navRevisionId) according to the selected comparison mode.</summary>
    private async Task LoadDiffCoreAsync(CancellationTokenSource cts)
    {
        if (_selectedEdit is null || _navRevisionId == 0)
            return;

        var target = cmbDiffMode.SelectedIndex == 1 ? DiffTarget.Current : DiffTarget.Previous;
        bool isSelected = _navRevisionId == _selectedEdit.RevisionId;

        // Cases that need no server request
        if (target == DiffTarget.Previous && _navParentId == 0)
        {
            ShowDiffMessage("זו הגרסה הראשונה של הדף: אין גרסה קודמת להשוואה.");
            return;
        }
        if (target == DiffTarget.Current && isSelected && _selectedEdit.IsTop)
        {
            ShowDiffMessage("עריכה זו היא הגרסה הנוכחית של הדף. אין הבדל.");
            return;
        }

        ShowDiffMessage("שולף הפרש...");

        try
        {
            var diff = await _client.GetDiffAsync(cmbSite.Text.Trim(), _navRevisionId, target, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            // When navigating the history, revision details come from the compare response, not from the grid
            if (!isSelected && diff.RevisionTimestamp is not null)
                SetDiffHeader(diff.Title.Length > 0 ? diff.Title : _navTitle, diff.RevisionTimestamp, diff.RevisionUser, diff.RevisionComment);

            if (diff.Html.Length == 0)
                ShowDiffMessage(target == DiffTarget.Current
                    ? "זו הגרסה הנוכחית של הדף. אין הבדל."
                    : "אין הבדל בין הגרסאות.");
            else
                RenderDiff(diff.Html);
        }
        catch (OperationCanceledException)
        {
            // Another row was selected meanwhile. The result is no longer relevant.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                ShowDiffMessage("שגיאה בשליפת ההפרש: " + ex.Message);
        }
    }

    // Navigating the page history from the diff panel

    private async void btnRevPrev_Click(object sender, RoutedEventArgs e) => await NavigateRevisionAsync(newer: false);

    private async void btnRevNext_Click(object sender, RoutedEventArgs e) => await NavigateRevisionAsync(newer: true);

    private void btnRevBack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEdit is null)
            return;

        CancelDiffRequest();
        _navRevisionId = _selectedEdit.RevisionId;
        _navParentId = _selectedEdit.ParentId;
        _navTitle = _selectedEdit.Title;
        SetDiffHeader(_selectedEdit.Title, _selectedEdit.Timestamp, _selectedEdit.User, _selectedEdit.Comment);
        UpdateRevisionNavButtons();
        _ = LoadDiffAsync();
    }

    /// <summary>Moves to the adjacent revision in the page history (even if someone else made it) and shows its diff.</summary>
    private async Task NavigateRevisionAsync(bool newer)
    {
        if (_selectedEdit is null || _navRevisionId == 0)
            return;

        CancelDiffRequest();
        _diffDebounce.Stop();
        var cts = new CancellationTokenSource();
        _diffCts = cts;
        SetRevisionNavEnabled(false);
        ShowDiffMessage(newer ? "מחפש את הגרסה הבאה..." : "מחפש את הגרסה הקודמת...");

        try
        {
            var adjacent = await _client.GetAdjacentRevisionAsync(cmbSite.Text.Trim(), _navTitle, _navRevisionId, newer, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (adjacent is null)
            {
                ShowDiffMessage(newer ? "זו הגרסה האחרונה של הדף." : "זו הגרסה הראשונה של הדף.");
                return;
            }

            _navRevisionId = adjacent.Value.RevisionId;
            _navParentId = adjacent.Value.ParentId;
            await LoadDiffCoreAsync(cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                ShowDiffMessage("שגיאה בחיפוש הגרסה: " + ex.Message);
        }
        finally
        {
            UpdateRevisionNavButtons();
        }
    }

    private void UpdateRevisionNavButtons()
    {
        bool has = _selectedEdit is not null && _navRevisionId != 0;
        btnRevPrev.IsEnabled = has;
        btnRevNext.IsEnabled = has;
        btnRevBack.IsEnabled = has && _navRevisionId != _selectedEdit!.RevisionId;
        btnArticle.IsEnabled = has;
        btnArticle.Content = _showingArticle ? "הפרש" : "ערך";
    }

    private void SetRevisionNavEnabled(bool enabled)
    {
        btnRevPrev.IsEnabled = enabled;
        btnRevNext.IsEnabled = enabled;
        btnRevBack.IsEnabled = enabled;
        btnArticle.IsEnabled = enabled;
    }

    /// <summary>Switches the panel between the diff and the full article at the revision being shown.</summary>
    private async void btnArticle_Click(object sender, RoutedEventArgs e)
    {
        if (_showingArticle)
        {
            _showingArticle = false;
            UpdateRevisionNavButtons();
            ScheduleDiffLoad();
            return;
        }

        if (_selectedEdit is null || _navRevisionId == 0)
            return;

        CancelDiffRequest();
        _diffDebounce.Stop();
        var cts = new CancellationTokenSource();
        _diffCts = cts;

        _showingArticle = true;
        UpdateRevisionNavButtons();
        ShowDiffMessage("שולף את הערך...");

        try
        {
            string site = cmbSite.Text.Trim();
            var article = await _client.GetArticleAsync(site, _navRevisionId, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            lblDiffTitle.Text = article.Title;
            lblDiffComment.Text = $"הערך כפי שהיה בגרסה {article.RevisionId:N0}";
            RenderArticle(site, article.Html);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                _showingArticle = false;
                UpdateRevisionNavButtons();
                ShowDiffMessage("שגיאה בשליפת הערך: " + ex.Message);
            }
        }
    }

    /// <summary>NavigateToString is limited to about 2 million characters. A larger diff is cut at a row boundary.</summary>
    private const int MaxDiffHtmlLength = 1_500_000;

    private void ShowDiffMessage(string message)
    {
        string body = message.Length == 0
            ? ""
            : $"<p class=\"msg\">{WebUtility.HtmlEncode(message)}</p>";
        NavigateDiff(BuildDiffPage(body));
    }

    /// <summary>Shows Wikipedia's diff table inside the browser control, with the site's styling and text direction.</summary>
    private void RenderDiff(string rowsHtml)
    {
        bool truncated = rowsHtml.Length > MaxDiffHtmlLength;
        if (truncated)
        {
            int cut = rowsHtml.LastIndexOf("</tr>", MaxDiffHtmlLength, StringComparison.OrdinalIgnoreCase);
            rowsHtml = cut > 0 ? rowsHtml[..(cut + 5)] : rowsHtml[..MaxDiffHtmlLength];
        }

        string body =
            "<table class=\"diff\">" +
            "<colgroup><col class=\"diff-marker\"><col class=\"diff-content\">" +
            "<col class=\"diff-marker\"><col class=\"diff-content\"></colgroup>" +
            "<tbody>" + rowsHtml + "</tbody></table>";

        if (truncated)
            body += "<p class=\"msg\">... ההפרש גדול מאוד וקוצר. לחיצה כפולה על השורה בטבלה תפתח אותו במלואו בדפדפן.</p>";

        NavigateDiff(BuildDiffPage(body));
    }

    /// <summary>Shows the parsed article in the panel, styled by the wiki's own content stylesheet.</summary>
    private void RenderArticle(string site, string articleHtml)
    {
        bool truncated = articleHtml.Length > MaxDiffHtmlLength;
        if (truncated)
        {
            int cut = articleHtml.LastIndexOf("</p>", MaxDiffHtmlLength, StringComparison.OrdinalIgnoreCase);
            articleHtml = (cut > 0 ? articleHtml[..(cut + 4)] : articleHtml[..MaxDiffHtmlLength]) +
                          "<p class=\"msg\">... הערך ארוך מאוד וקוצר. לחיצה כפולה על השורה בטבלה תפתח אותו בדפדפן.</p>";
        }

        NavigateDiff(BuildArticlePage(site, articleHtml));
    }

    /// <summary>
    /// Wraps parsed article HTML in a page that can render it: a base URL so the wiki's relative links and
    /// images resolve, and the wiki's own content stylesheet so tables and infoboxes look as they do on the site.
    /// The article body carries its own lang and dir attributes, so the page does not impose a direction.
    /// </summary>
    private static string BuildArticlePage(string site, string articleHtml) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        $"<base href=\"https://{site}/\">" +
        $"<link rel=\"stylesheet\" href=\"https://{site}/w/load.php?modules=mediawiki.skinning.content.parsoid%7Csite.styles&only=styles&skin=vector-2022\">" +
        "<style>" +
        "html,body{margin:0;padding:0;background:#fff;color:#202122;font-family:'Segoe UI',Arial,sans-serif;font-size:14px;line-height:1.6}" +
        "body{padding:10px 14px}" +
        ".msg{color:#54595d;margin:1em 0}" +
        "img{max-width:100%;height:auto}" +
        "table{max-width:100%}" +
        "a{color:#36c;text-decoration:none}" +
        "h1,h2,h3{font-family:'Linux Libertine','Georgia',serif;font-weight:normal;border-bottom:1px solid #a2a9b1;padding-bottom:0.17em}" +
        "h3{border-bottom:none;font-weight:bold;font-size:1.1em}" +
        "</style></head><body>" + articleHtml + "</body></html>";

    private void NavigateDiff(string html)
    {
        if (_webReady)
            webDiff.NavigateToString(html);
        else
            _pendingHtml = html; // Shown as soon as the browser control finishes loading
    }

    /// <summary>
    /// An HTML page with the styling of Wikipedia's diff table (mediawiki.diff.styles) in right-to-left direction.
    /// The browser applies the full Unicode bidirectional algorithm, including bracket mirroring,
    /// so Hebrew wikitext is rendered exactly as on the site.
    /// </summary>
    public static string BuildDiffPage(string body) =>
        "<!DOCTYPE html><html lang=\"he\" dir=\"rtl\"><head><meta charset=\"utf-8\"><style>" +
        "html,body{margin:0;padding:0;background:#fff;color:#202122;font-family:'Segoe UI',Arial,sans-serif;font-size:14px}" +
        "body{padding:6px}" +
        ".msg{color:#54595d;padding:12px;margin:0}" +
        // The rules below follow MediaWiki's diff.less: separate cells with 4px spacing, each changed line in a
        // rounded box with a thick border on the inline-start side (the side facing the +/- marker).
        "table.diff{border:0;border-spacing:4px;margin:0;width:100%;table-layout:fixed}" +
        "table.diff td{padding:0.33em 0.5em}" +
        "table.diff col.diff-marker{width:2.2em}" +
        "td.diff-marker{text-align:end;font-weight:bold;font-size:1.25em;line-height:1.2;padding:0.25em 0;vertical-align:top;" +
        "font-family:'Segoe UI',Arial,sans-serif;color:#202122}" +
        "td.diff-marker[data-marker]::before{content:attr(data-marker)}" +
        "td.diff-lineno{font-weight:bold;font-family:'Segoe UI',Arial,sans-serif;font-size:13px;padding:0.5em 0.25em 0.25em;unicode-bidi:normal}" +
        "td.diff-addedline,td.diff-deletedline,td.diff-context{font-family:Consolas,'Courier New',monospace;font-size:13px;line-height:1.5;" +
        "vertical-align:top;white-space:pre-wrap;word-wrap:break-word;overflow-wrap:anywhere;unicode-bidi:plaintext;" +
        "border-style:solid;border-width:1px;border-inline-start-width:4px;border-radius:0.33em}" +
        "td.diff-addedline{border-color:#a3d3ff}" +
        "td.diff-deletedline{border-color:#ffe49c}" +
        "td.diff-context{background:#f8f9fa;border-color:#eaecf0}" +
        "td.diff-empty{background:#eaecf0;border:1px solid #eaecf0;border-radius:0.33em}" +
        "table.diff td div{display:block}" +
        ".diffchange{font-weight:bold;text-decoration:none}" +
        "td.diff-addedline .diffchange,td.diff-deletedline .diffchange{border-radius:0.33em;padding:0.25em 0}" +
        "td.diff-addedline .diffchange{background:#d8ecff}" +
        "td.diff-deletedline .diffchange{background:#feeec8}" +
        "a{color:#36c;text-decoration:none}" +
        "</style></head><body>" + body + "</body></html>";

    // ----------------------------------------------------------------------
    // Voting eligibility check
    // ----------------------------------------------------------------------

    /// <summary>Starts the eligibility check, or stops the running one: the button reads "stop" while busy.</summary>
    private async void btnCheckVote_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
        {
            _cts?.Cancel();
            return;
        }

        string user = cmbUser.Text.Trim();
        string site = cmbSite.Text.Trim();

        if (user.Length == 0)
        {
            MessageBox.Show(this, "יש להזין שם משתמש.", "חסר שם משתמש", MessageBoxButton.OK, MessageBoxImage.Warning);
            cmbUser.Focus();
            return;
        }
        if (site.Length == 0)
        {
            MessageBox.Show(this, "יש להזין דומיין של אתר ויקי.", "חסר אתר", MessageBoxButton.OK, MessageBoxImage.Warning);
            cmbSite.Focus();
            return;
        }

        // Vote start: UTC midnight of the chosen date. The Wikipedia gadget treats the entered date and time
        // as UTC (its dialog says so) without converting from local time, so we do the same.
        var voteStartUtc = DateTime.SpecifyKind((dtpVoteStart.SelectedDate ?? DateTime.Today).Date, DateTimeKind.Utc);
        int waitDays = cmbWaitDays.SelectedItem is int days ? days : VoteEligibility.DeterminingOffsetDays;

        lblVoteResult.Text = "";
        lblVoteDetails.Text = "";

        await RunBusyAsync(BusyKind.VoteCheck, async ct =>
        {
            var progress = new Progress<string>(message => lblStatus.Text = message);
            var result = await VoteEligibility.CheckAsync(_client, site, user, voteStartUtc, progress, ct, waitDays);
            ShowVoteResult(result);
            lblStatus.Text = "בדיקת זכות ההצבעה הושלמה.";

            if (result.FirstEditUtc is not null)
            {
                _history.AddUser(user);
                RefreshNameList();
            }
        });
    }

    private void ShowVoteResult(VoteEligibilityResult r)
    {
        if (r.Eligible)
        {
            lblVoteResult.Text = "✔ יש זכות הצבעה";
            lblVoteResult.Foreground = GoodBrush;
        }
        else
        {
            lblVoteResult.Text = "✘ אין זכות הצבעה: " + string.Join("; ", r.Failures);
            lblVoteResult.Foreground = BadBrush;
        }

        var parts = new List<string>();

        string recentPlus = r.RecentTruncated ? "+" : "";
        var windowStart = r.DeterminingDateUtc.AddDays(-VoteEligibility.RecentWindowDays);
        string recent = $"עריכות במרחב הערכים והמשיקים לו בין {windowStart:yyyy-MM-dd} ל-{r.DeterminingDateUtc:yyyy-MM-dd} " +
                        $"({VoteEligibility.RecentWindowDays} הימים שלפני היום הקובע): {r.RecentManual + r.RecentAuto:N0}{recentPlus}";
        if (r.RecentAuto > 0) recent += $", מתוכן {r.RecentAuto:N0} אוטומטיות";
        if (r.RecentAdminActions > 0) recent += $", ועוד {r.RecentAdminActions:N0} פעולות מפעיל";
        recent += $" (נספרות: {r.RecentCounted:N0})";
        parts.Add(recent);

        string totalPlus = r.TotalTruncated ? "+" : "";
        string total = $"סך הכל נספרות: {r.TotalCounted:N0}{totalPlus}";
        if (r.TotalAuto > 0) total += $" (כולל {Math.Min(r.TotalAuto, (int)(VoteEligibility.MinTotalEdits * VoteEligibility.MaxAutoEditShare)):N0} אוטומטיות)";
        parts.Add(total);

        parts.Add(r.FirstEditUtc is DateTime first
            ? $"עריכה ראשונה: {first.ToLocalTime():yyyy-MM-dd} ({r.SeniorityDays:N0} ימי ותק)"
            : "אין עריכות");

        lblVoteDetails.Text = string.Join("  |  ", parts);
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static string Percent(int part, int total) =>
        total == 0 ? "0%" : $"{Math.Round(100.0 * part / total)}%";

    private static string DescribeAge(DateTime since)
    {
        var span = DateTime.Now - since;
        if (span.TotalDays >= 365) return $"לפני {(int)(span.TotalDays / 365.25)} שנים";
        if (span.TotalDays >= 30) return $"לפני {(int)(span.TotalDays / 30.44)} חודשים";
        return $"לפני {Math.Max(1, (int)span.TotalDays)} ימים";
    }

    private static string FormatExpiry(string expiry) =>
        DateTime.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : expiry;

    private void cmbUser_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Busy)
        {
            e.Handled = true;
            _ = RunQueryAsync();
        }
    }

    /// <summary>Double-clicking a row opens the diff in the browser.</summary>
    private void grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource)?.Item is not WikiEdit edit)
            return;

        OpenInBrowser(WikipediaClient.DiffUrl(cmbSite.Text.Trim(), edit));
    }

    private void SetBusy(bool busy)
    {
        // The button that started the operation becomes its stop button; the other one is just disabled.
        bool fetching = busy && _busyKind == BusyKind.Fetch;
        bool checking = busy && _busyKind == BusyKind.VoteCheck;
        btnRun.Content = fetching ? "עצירת שליפה" : "שליפת נתונים";
        btnRun.IsEnabled = !checking;
        btnCheckVote.Content = checking ? "עצירת בדיקה" : "בדיקת זכות הצבעה";
        btnCheckVote.IsEnabled = !fetching;

        cmbMode.IsEnabled = !busy;
        cmbUser.IsEnabled = !busy;
        cmbSite.IsEnabled = !busy;
        cmbNamespace.IsEnabled = !busy;
        cmbPageSize.IsEnabled = !busy;
        dtpVoteStart.IsEnabled = !busy;
        cmbWaitDays.IsEnabled = !busy;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        UpdatePagingControls();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        WindowPlacement.Capture(this).Save();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _diffDebounce.Stop();
        _throttleTimer.Stop();
        _cts?.Cancel();
        _diffCts?.Cancel();
        _client.Dispose();
        base.OnClosed(e);
    }
}
