namespace WikiEdits;

/// <summary>Which revision a given edit is compared against.</summary>
public enum DiffTarget
{
    /// <summary>Against the revision before the edit: what the edit itself changed.</summary>
    Previous,

    /// <summary>Against the page's current revision: what has changed since the edit.</summary>
    Current,
}

/// <summary>Reference to a revision in a page history, with its parent revision (0 for the first revision).</summary>
public readonly record struct RevisionRef(long RevisionId, long ParentId);

/// <summary>A rendered article, as the wiki itself renders it.</summary>
/// <param name="Title">The page title as stored on the wiki.</param>
/// <param name="RevisionId">The revision the HTML was rendered from.</param>
/// <param name="Html">The parsed article body (a single div), without the surrounding skin.</param>
public sealed record WikiArticle(string Title, long RevisionId, string Html);

/// <summary>Result of comparing two revisions, as an HTML table in Wikipedia's own format.</summary>
public sealed class WikiDiff
{
    /// <summary>The revision the request referred to.</summary>
    public long RevisionId { get; init; }

    public long FromRevisionId { get; init; }
    public long ToRevisionId { get; init; }

    public string Title { get; init; } = "";

    /// <summary>Who made the revision the request referred to.</summary>
    public string RevisionUser { get; init; } = "";
    public DateTime? RevisionTimestamp { get; init; }
    public string RevisionComment { get; init; } = "";

    /// <summary>Size of the diff in bytes, as reported by the server.</summary>
    public int DiffSize { get; init; }

    /// <summary>
    /// The table rows (tr) of the diff as returned by the API, without the table wrapper.
    /// Empty when the revisions are identical.
    /// </summary>
    public string Html { get; init; } = "";
}
