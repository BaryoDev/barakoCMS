using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Pages.Features.BlockSchema;

internal sealed record BlockFieldItem(
    string Name,
    string Kind,
    string Label,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max);

internal sealed record BlockItem(string Type, string Label, bool PerViewer, IReadOnlyList<BlockFieldItem> Fields);

/// <summary>The block schema as barakoPress publishes it, so an editor builds its forms from either.</summary>
internal sealed record BlockSchemaResponse(int Version, IReadOnlyList<BlockItem> Blocks);

/// <summary>
/// GET /api/pages/block-schema: the tenant's block schema, as <c>{ version, blocks: [{ type, label,
/// perViewer, fields: [{ name, kind, label, required, options, min, max }] }] }</c>.
/// </summary>
/// <remarks>
/// Any signed-in caller, as <c>GET /api/blocks/schema</c>: it says which blocks a page may hold and
/// nothing about any entry. A tenant whose renderer has registered no schema gets an empty
/// <c>blocks</c> list, not a 404. <see cref="BlockSchemaTranslation"/> says how the stored JSON
/// Schema becomes this shape.
/// </remarks>
internal sealed class Endpoint(IQuerySession session) : EndpointWithoutRequest<BlockSchemaResponse>
{
    public override void Configure() => Get("/api/pages/block-schema");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var stored = await session.LoadAsync<barakoCMS.Models.BlockSchema>(barakoCMS.Models.BlockSchema.RowId, ct);
        await Send.OkAsync(BlockSchemaTranslation.From(stored), ct);
    }
}

/// <summary>
/// Stored per-tenant JSON Schema to the block schema shape. A hint on the stored schema
/// (<see cref="BlockSchemaHints"/>) wins. Without one:
/// <list type="bullet">
/// <item>Block <c>label</c> and field <c>label</c> are the name, split on <c>-</c>, <c>_</c> and case
/// changes, lowercased after the first letter: <c>heroBanner</c> becomes <c>Hero banner</c>.</item>
/// <item><c>perViewer</c> is false.</item>
/// <item>Kind is <c>select</c> for a string <c>enum</c>, <c>url</c> for a string with format
/// <c>uri</c>, <c>url</c> or <c>uri-reference</c>, <c>text</c> for any other string, <c>number</c> for
/// number or integer, <c>boolean</c> for boolean, <c>slots</c> for an array of arrays, and <c>text</c>
/// for anything else.</item>
/// <item><c>options</c> are a string enum's values. <c>min</c> and <c>max</c> are <c>minimum</c> and
/// <c>maximum</c> of a number, and <c>minItems</c> and <c>maxItems</c> of slots.</item>
/// <item><c>required</c> is membership in the schema's <c>required</c> list.</item>
/// </list>
/// Blocks are ordered by type and fields by name, because the stored schema keeps no declaration order.
/// </summary>
internal static class BlockSchemaTranslation
{
    public const int Version = 1;

    public static BlockSchemaResponse From(barakoCMS.Models.BlockSchema? stored) =>
        new(Version, stored is null
            ? []
            : stored.Blocks
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => Block(b.Key, b.Value))
                .ToList());

    private static BlockItem Block(string type, JsonElement schema)
    {
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

        var fields = new List<BlockFieldItem>();
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            // Ordered by name: the schema is stored as jsonb, which keeps no declaration order.
            fields.AddRange(properties.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => Field(p.Name, p.Value, required.Contains(p.Name))));
        }

        return new BlockItem(
            type,
            Hint(schema, BlockSchemaHints.Label) ?? Humanize(type),
            schema.TryGetProperty(BlockSchemaHints.PerViewer, out var pv) && pv.ValueKind == JsonValueKind.True,
            fields);
    }

    private static BlockFieldItem Field(string name, JsonElement schema, bool required)
    {
        var kind = Hint(schema, BlockSchemaHints.Kind) ?? KindOf(schema);
        IReadOnlyList<string>? options = null;
        if (kind == "select" && schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            options = values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        }

        var (min, max) = kind switch
        {
            "number" => (Number(schema, "minimum"), Number(schema, "maximum")),
            "slots" => (Number(schema, "minItems"), Number(schema, "maxItems")),
            _ => ((double?)null, (double?)null),
        };

        return new BlockFieldItem(name, kind, Hint(schema, BlockSchemaHints.Label) ?? Humanize(name), required, options, min, max);
    }

    private static string KindOf(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            && values.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String))
        {
            return "select";
        }

        return TypeOf(schema) switch
        {
            "string" => schema.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String
                && f.GetString() is "uri" or "url" or "uri-reference" ? "url" : "text",
            "number" or "integer" => "number",
            "boolean" => "boolean",
            "array" when schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                && TypeOf(items) == "array" => "slots",
            _ => "text",
        };
    }

    /// <summary>The schema's type, or the first one that is not null when it lists several.</summary>
    private static string? TypeOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return null;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => type.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String && t.GetString() != "null")
                .Select(t => t.GetString()).FirstOrDefault(),
            _ => null,
        };
    }

    private static double? Number(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Hint(JsonElement schema, string key) =>
        schema.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary><c>heroBanner</c>, <c>hero-banner</c> and <c>hero_banner</c> all become <c>Hero banner</c>.</summary>
    internal static string Humanize(string name)
    {
        var words = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '-' or '_')
            {
                if (words.Length > 0 && words[^1] != ' ')
                {
                    words.Append(' ');
                }
            }
            else if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]) && name[i - 1] is not ('-' or '_'))
            {
                words.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }
        }

        return words.ToString().Trim();
    }
}
