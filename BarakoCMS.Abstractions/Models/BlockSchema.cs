using System.Text.Json;

namespace barakoCMS.Models;

/// <summary>
/// The blocks a tenant's renderer draws, and the properties each takes: what a <c>blocks</c> field
/// is checked against. One per tenant.
/// </summary>
/// <remarks>
/// The renderer registers it, so the console reads the blocks of the tenant it edits from the API
/// rather than from one renderer address per console. Two tenants on one deployment can have
/// different blocks, since plugins are enabled per tenant.
///
/// A tenant with none takes any block a <c>blocks</c> field's shape allows, as a <c>json</c> field
/// would, so a page type moved to <c>blocks</c> before its renderer registers keeps saving.
/// </remarks>
public sealed class BlockSchema
{
    /// <summary>The one row a tenant holds.</summary>
    public static readonly Guid RowId = new("b10c5000-0000-4000-8000-000000000001");

    public Guid Id { get; set; } = RowId;

    /// <summary>
    /// Each block type by name, with the JSON Schema of its properties: an object schema in the
    /// subset a <c>json</c> field's <c>jsonSchema</c> takes.
    /// </summary>
    public Dictionary<string, JsonElement> Blocks { get; set; } = new();

    /// <summary>Who registered it: <c>renderer</c> for the renderer key, or the user's id.</summary>
    public string RegisteredBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
