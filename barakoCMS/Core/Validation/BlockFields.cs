using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a <c>blocks</c> field holds: a list of blocks, each an object naming its <c>type</c>, with
/// the block's properties beside it. Checked against the tenant's <see cref="BlockSchema"/>.
/// </summary>
/// <remarks>
/// The registry checks the shape alone, since it cannot reach the database: a list of objects, each
/// with a <c>type</c> that is text. With a registered schema, an entry write also refuses a block
/// type the schema does not name, a property its block does not declare, and a property value that
/// fails the block's schema, each naming the path, such as <c>Blocks[2].colour</c>. A misspelled
/// property used to save and render nothing.
///
/// The keys in <see cref="Envelope"/> belong to the block itself rather than to its properties, so
/// no block declares them and every block may carry them.
/// </remarks>
internal static class BlockFields
{
    public const string TypeName = "blocks";

    /// <summary>The most blocks one field holds.</summary>
    public const int MaxBlocks = 200;

    /// <summary>The most block types one schema names.</summary>
    public const int MaxBlockTypes = 200;

    /// <summary>The keys every block may carry, which are not its properties.</summary>
    /// <remarks>
    /// <c>preset</c> is the id of the <see cref="BlockPreset"/> a block was made from, kept for the
    /// console. <c>binding</c> is the id of the <see cref="BlockBinding"/> whose entries page resolve
    /// returns beside the block. Both are ids or absent.
    /// </remarks>
    public static IReadOnlyList<string> Envelope { get; } = ["type", "id", "preset", "binding"];

    private static readonly Regex BlockName = new(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public static bool Is(string? type) => string.Equals(type, TypeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The shape every <c>blocks</c> value has, whatever the tenant registered.</summary>
    public static bool IsShape(object value)
    {
        var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
        return element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().All(b => b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString()!.Length > 0);
    }

    /// <summary>What is wrong with a schema a renderer registers.</summary>
    public static List<string> SchemaErrors(IReadOnlyDictionary<string, JsonElement>? blocks)
    {
        var errors = new List<string>();
        if (blocks is null || blocks.Count == 0)
        {
            return ["A block schema names at least one block."];
        }

        if (blocks.Count > MaxBlockTypes)
        {
            return [$"A block schema names at most {MaxBlockTypes} blocks, and this one names {blocks.Count}."];
        }

        foreach (var (name, schema) in blocks)
        {
            if (!BlockName.IsMatch(name))
            {
                errors.Add($"Block name '{name}' starts with a letter and holds letters, digits, '-' and '_', at most 64.");
            }
            else if (schema.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Block '{name}' has to be described by an object schema.");
            }
            else if (schema.TryGetProperty("properties", out var properties)
                     && properties.ValueKind == JsonValueKind.Object
                     && properties.EnumerateObject().Select(p => p.Name).FirstOrDefault(Envelope.Contains) is { } taken)
            {
                errors.Add($"Block '{name}' declares '{taken}', which every block carries and none declares.");
            }
            else
            {
                errors.AddRange(JsonFieldSchemas.SchemaErrors(schema).Select(e => $"Block '{name}' schema {e}"));
            }
        }

        return errors;
    }

    /// <summary>What is wrong with a value written to a <c>blocks</c> field, against the tenant's schema.</summary>
    public static async Task<IEnumerable<string>> ValueErrorsAsync(
        IQuerySession session, FieldDefinition field, object value, Content? existing)
    {
        var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
        if (element.GetArrayLength() > MaxBlocks)
        {
            return [$"Field '{field.DisplayName}' ({field.Name}) holds at most {MaxBlocks} blocks."];
        }

        if (StoredValues.IsUnchanged(field.Name, value, existing))
        {
            return [];
        }

        if (await session.LoadAsync<BlockSchema>(BlockSchema.RowId) is not { Blocks.Count: > 0 } schema)
        {
            return [];
        }

        var errors = new List<string>();
        var index = 0;
        foreach (var block in element.EnumerateArray())
        {
            errors.AddRange(BlockErrors(schema, block, $"{field.Name}[{index++}]"));
            if (errors.Count >= 5)
            {
                break;
            }
        }

        return errors.Take(5).Select(e => $"Field '{field.DisplayName}' ({field.Name}): {e}");
    }

    /// <summary>
    /// What is wrong with a block's properties against the tenant's schema, for a writer other than a
    /// page, such as a preset. Nothing when the tenant has registered no schema.
    /// </summary>
    public static async Task<List<string>> PropsErrorsAsync(
        IQuerySession session, string type, JsonElement props, string at, CancellationToken ct) =>
        await session.LoadAsync<BlockSchema>(BlockSchema.RowId, ct) is { Blocks.Count: > 0 } schema
            ? PropsErrors(schema, type, props, at).ToList()
            : [];

    private static IEnumerable<string> BlockErrors(BlockSchema schema, JsonElement block, string at)
    {
        var badIds = new[] { "preset", "binding" }
            .Where(key => block.TryGetProperty(key, out var v)
                && !(v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _)))
            .Select(key => $"{at}.{key} is the id of a {key}.")
            .ToList();

        return badIds.Count > 0 ? badIds : PropsErrors(schema, block.GetProperty("type").GetString()!, Properties(block), at);
    }

    private static IEnumerable<string> PropsErrors(BlockSchema schema, string type, JsonElement props, string at)
    {
        if (!schema.Blocks.TryGetValue(type, out var blockSchema))
        {
            return [$"{at}.type '{type}' is not a block this tenant's renderer registered."];
        }

        // A block that says what its other properties may be leaves them to its schema. One that
        // says nothing takes only the properties it names, so a misspelled one is refused.
        if (blockSchema.TryGetProperty("additionalProperties", out _))
        {
            return JsonFieldSchemas.Errors(blockSchema, props, at);
        }

        blockSchema.TryGetProperty("properties", out var declared);
        var unknown = props.EnumerateObject()
            .Where(p => !(declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(p.Name, out _)))
            .Select(p => $"{at}.{p.Name} is not a property of '{type}'.")
            .ToList();

        return unknown.Count > 0 ? unknown : JsonFieldSchemas.Errors(blockSchema, props, at);
    }

    /// <summary>The block without its envelope, which is what its schema describes.</summary>
    private static JsonElement Properties(JsonElement block)
    {
        var properties = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in block.EnumerateObject().Where(p => !Envelope.Contains(p.Name)))
        {
            properties[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        }

        return JsonSerializer.SerializeToElement(properties);
    }
}
