namespace WikiEdits;

/// <summary>One page of edits, together with the continuation token for the next page.</summary>
/// <param name="Edits">The edits in this page, newest first.</param>
/// <param name="ContinueToken">Token for fetching the next page, or null when this is the last page.</param>
public sealed record ContributionsPage(List<WikiEdit> Edits, string? ContinueToken);

/// <summary>A single edit, from either a user's contributions or a page's revision history.</summary>
public sealed class WikiEdit
{
    public long RevisionId { get; init; }
    public long ParentId { get; init; }
    public string Title { get; init; } = "";

    /// <summary>The editor's name. For a user query it is always the queried user; in a page history it varies per row.</summary>
    public string User { get; init; } = "";

    public DateTime Timestamp { get; init; }
    public string Comment { get; init; } = "";
    public int Size { get; init; }

    /// <summary>Size difference from the previous revision. Settable because in a page history it is computed after fetching.</summary>
    public int SizeDiff { get; set; }
    public bool Minor { get; init; }
    public bool IsNew { get; init; }
    public bool IsTop { get; init; }

    /// <summary>Internal tag names as returned by the API, e.g. mw-reverted.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>The tags by their localized display names, comma separated, for showing in the grid.</summary>
    public string TagsDisplay { get; init; } = "";

    /// <summary>The edit was marked by the system as reverted.</summary>
    public bool IsReverted => Tags.Contains("mw-reverted");
}
