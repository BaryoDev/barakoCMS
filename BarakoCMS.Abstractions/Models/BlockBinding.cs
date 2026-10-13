namespace barakoCMS.Models;

/// <summary>
/// Where a block's entries come from: a published, publicly deliverable content type, ordered and
/// capped. A page block names one by id in its <c>binding</c>, and page resolve returns the entries
/// it selects beside the page, so every renderer draws from the same answer.
/// </summary>
public sealed class BlockBinding
{
    /// <summary>The most entries one binding selects.</summary>
    public const int MaxLimit = 20;

    public Guid Id { get; set; }

    /// <summary>What an editor sees it as, for example "Latest three posts".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The content type the entries are of.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>A field to order by, newest first when it starts with <c>-</c>, or null for newest created first.</summary>
    public string? Sort { get; set; }

    /// <summary>How many entries, from 1 to <see cref="MaxLimit"/>.</summary>
    public int Limit { get; set; } = 3;

    /// <summary>Public fields the entries have to hold exactly, compared ignoring case, or null for none.</summary>
    public Dictionary<string, string>? Where { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
