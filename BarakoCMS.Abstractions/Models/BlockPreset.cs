using System.Text.Json;

namespace barakoCMS.Models;

/// <summary>
/// A saved block: a block type with its properties filled in, which an editor drops onto a page as
/// a starting point. Tenant data, so every console and renderer of the tenant reads the same ones.
/// </summary>
public sealed class BlockPreset
{
    public Guid Id { get; set; }

    /// <summary>What an editor sees it as, for example "Hero with photo".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The block type it fills in, one the tenant's <see cref="BlockSchema"/> names.</summary>
    public string BlockType { get; set; } = string.Empty;

    /// <summary>The block's properties, an object checked against that block's schema.</summary>
    public JsonElement Props { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

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
