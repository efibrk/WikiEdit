using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WikiEdits;

/// <summary>
/// History of the user names and page titles that were queried, stored as a JSON file in the user's application data folder.
/// The most recently queried name comes first in each list.
/// </summary>
public sealed class QueryHistory
{
    private const int MaxEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Keeps Hebrew readable instead of \uXXXX escapes
    };

    private readonly List<string> _users = new();
    private readonly List<string> _pages = new();

    public QueryHistory()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WikiEdits");
        FilePath = Path.Combine(dir, "history.json");
    }

    /// <summary>Full path of the history file.</summary>
    public string FilePath { get; }

    /// <summary>User names, most recently queried first.</summary>
    public IReadOnlyList<string> Users => _users;

    /// <summary>Page titles, most recently queried first.</summary>
    public IReadOnlyList<string> Pages => _pages;

    /// <summary>Loads the history from the file. A missing or corrupt file is treated as an empty history.</summary>
    public void Load()
    {
        _users.Clear();
        _pages.Clear();
        try
        {
            if (!File.Exists(FilePath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Legacy format: a list of user names only
                Fill(_users, root);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("users", out var users)) Fill(_users, users);
                if (root.TryGetProperty("pages", out var pages)) Fill(_pages, pages);
            }
        }
        catch (Exception)
        {
            // The history is a convenience only. If the file is unreadable, start over.
            _users.Clear();
            _pages.Clear();
        }
    }

    public void AddUser(string name) => Add(_users, name);

    public void AddPage(string name) => Add(_pages, name);

    private static void Fill(List<string> target, JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            string name = item.GetString()?.Trim() ?? "";
            if (name.Length > 0 && !target.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                target.Add(name);
        }
    }

    /// <summary>Adds a name to the top of the list (or moves it to the top if already present) and saves the file.</summary>
    private void Add(List<string> list, string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            return;

        list.RemoveAll(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, trimmed);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);

        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var model = new Dictionary<string, List<string>> { ["users"] = _users, ["pages"] = _pages };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(model, JsonOptions));
        }
        catch (Exception)
        {
            // A failed save must not crash the program. The in-memory lists stay valid.
        }
    }
}
