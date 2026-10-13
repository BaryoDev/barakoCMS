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
