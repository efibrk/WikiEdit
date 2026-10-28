namespace WikiEdits;

/// <summary>User account details as returned by the list=users query.</summary>
public sealed class WikiUserInfo
{
    public string Name { get; init; } = "";

    /// <summary>False when the account does not exist or the name is an IP address.</summary>
    public bool Exists { get; init; }

    public long EditCount { get; init; }

    /// <summary>Null for very old accounts whose registration date was not recorded.</summary>
    public DateTime? Registration { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    public bool IsBlocked { get; init; }
    public string? BlockedBy { get; init; }
    public string? BlockReason { get; init; }

    /// <summary>"infinite" for an indefinite block, otherwise a timestamp.</summary>
    public string? BlockExpiry { get; init; }
}
