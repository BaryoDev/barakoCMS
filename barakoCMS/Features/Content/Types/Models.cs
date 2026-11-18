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
    object? DefaultValue,
    System.Text.Json.JsonElement? JsonSchema,
    string? Currency,
    int? Scale,
    ComputedDefinition? Computed,
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
        f.DefaultValue,
        f.JsonSchema,
        f.Currency,
        f.Scale,
        f.Computed,
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
    /// <summary>
    /// The shape for this caller. A field is listed only when the caller reads it the way an entry
    /// read would show it, which is the question <see cref="barakoCMS.Core.Interfaces.ISensitivityService"/>
    /// already answers for filtering and searching, so the shape never names a field an entry hides.
    /// </summary>
    public static async Task<ReadableTypeResponse> ForAsync(
        ContentTypeDefinition d,
        barakoCMS.Core.Interfaces.ISensitivityService sensitivity,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken ct)
    {
        var visible = new List<FieldDefinition>();
        foreach (var field in d.Fields ?? [])
        {
            if (await sensitivity.MayReadFieldAsync(d.Name, field, null, user, ct))
                visible.Add(field);
        }

        return From(d, visible);
    }

    private static ReadableTypeResponse From(ContentTypeDefinition d, List<FieldDefinition> fields) => new(
        d.Name,
        d.DisplayName,
        d.Description,
        d.IsSingleton,
        fields.Select(ReadableField.From).ToList(),
        d.Lifecycle,
        d.Uniqueness);
}
