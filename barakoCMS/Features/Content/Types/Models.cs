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
    // A computation that reads a field the caller cannot see would name it, so it is left out. A
    // formula goes whole or not at all, and one that does not parse is read as naming every field.
    internal static ComputedDefinition? ReadableComputed(ComputedDefinition? c, ISet<string> hidden) =>
        c is null || (hidden.Count > 0 && barakoCMS.Core.Validation.ComputedFields.ReadsAny(c, hidden)) ? null : c;

    /// <summary>Where the block schema of a <c>blocks</c> field is served.</summary>
    public const string BlockSchemaRoute = "/api/blocks/schema";

    public static ReadableField From(FieldDefinition f, ISet<string> hidden, ISet<string> unreadableTypes) => new(
        f.Name,
        f.Type,
        f.IsRequired,
        f.ValidationRules,
        f.Options,
        f.Multiple,
        f.ItemType,
        f.ReferenceType is { } target && unreadableTypes.Contains(target) ? null : f.ReferenceType,
        f.DefaultValue,
        f.JsonSchema,
        f.Currency,
        f.Scale,
        ReadableComputed(f.Computed, hidden),
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
        barakoCMS.Infrastructure.Services.IPermissionResolver permissions,
        Marten.IQuerySession session,
        barakoCMS.Models.User caller,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken ct)
    {
        var visible = new List<FieldDefinition>();
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in d.Fields ?? [])
        {
            if (await sensitivity.MayReadFieldAsync(d.Name, field, null, user, ct))
                visible.Add(field);
            else
                hidden.Add(field.Name);
        }

        // A reference is shown only when the caller may read the type it points at, so the shape
        // never names one that GET /types/{name} answers with 404. A reference to a type that does
        // not exist is hidden too. Each name is resolved the way the get route resolves it, so the
        // permission check runs on the definition that route would return; a name that cannot be
        // resolved to one type counts as unreadable. ItemType is a scalar type and is not touched.
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = visible.Select(f => f.ReferenceType)
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct(StringComparer.Ordinal);
        foreach (var name in targets)
        {
            try
            {
                var target = await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, name, ct);
                if (target is null || !await permissions.CanPerformActionAsync(caller, target.Name, "read", null, ct))
                    unreadable.Add(name);
            }
            catch (barakoCMS.Core.AmbiguousContentTypeException)
            {
                unreadable.Add(name);
            }
        }

        // Nothing the caller cannot read is named anywhere in the shape. Copies, so the stored
        // definition is not touched.
        var lifecycle = d.Lifecycle is null ? null : new LifecycleDefinition
        {
            States = [.. d.Lifecycle.States],
            InitialState = d.Lifecycle.InitialState,
            Transitions = d.Lifecycle.Transitions.Select(t => new StateTransition
            {
                Name = t.Name,
                From = t.From,
                To = t.To,
                RequiredFields = t.RequiredFields.Where(n => !hidden.Contains(n)).ToList(),
                OptionalFields = t.OptionalFields.Where(n => !hidden.Contains(n)).ToList(),
            }).ToList(),
        };
        // A rule that covers a hidden field is dropped whole, since a partial rule would mislead.
        var uniqueness = d.Uniqueness?.Where(r => !r.Fields.Any(hidden.Contains)).ToList();

        return new ReadableTypeResponse(
            d.Name, d.DisplayName, d.Description, d.IsSingleton,
            visible.Select(f => ReadableField.From(f, hidden, unreadable)).ToList(),
            lifecycle, uniqueness);
    }
}
