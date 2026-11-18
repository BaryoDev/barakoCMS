using barakoCMS.Models;

namespace barakoCMS.Features.Content.Types;

internal sealed record ReadableTypeSummary(string Name, string DisplayName);

internal sealed record ReadableTypeList(IReadOnlyList<ReadableTypeSummary> Items);

/// <summary>
/// A field as a content reader sees it: what an entry may hold, and nothing about who may see it.
/// Role lists and sensitivity are left out on purpose.
/// </summary>
internal sealed record ReadableField(
    string Name,
    string Type,
    bool IsRequired,
    Dictionary<string, object> ValidationRules,
    List<FieldOption>? Options,
    bool Multiple,
    string? ItemType,
    string? ReferenceType,
    string? BlockSchema)
{
    /// <summary>Where the block schema of a <c>blocks</c> field is served.</summary>
    public const string BlockSchemaRoute = "/api/blocks/schema";

    public static ReadableField From(FieldDefinition f) => new(
        f.Name,
        f.Type,
        f.IsRequired,
        f.ValidationRules,
        f.Options,
        f.Multiple,
        f.ItemType,
        f.ReferenceType,
        string.Equals(f.Type, "blocks", StringComparison.OrdinalIgnoreCase) ? BlockSchemaRoute : null);
}

internal sealed record ReadableTypeResponse(
    string Name,
    string DisplayName,
    string Description,
    bool IsSingleton,
    IReadOnlyList<ReadableField> Fields,
    LifecycleDefinition? Lifecycle,
    List<UniquenessRule>? Uniqueness)
{
    public static ReadableTypeResponse From(ContentTypeDefinition d) => new(
        d.Name,
        d.DisplayName,
        d.Description,
        d.IsSingleton,
        (d.Fields ?? []).Select(ReadableField.From).ToList(),
        d.Lifecycle,
        d.Uniqueness);
}
