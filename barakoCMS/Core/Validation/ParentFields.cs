using barakoCMS.Core.Hooks;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// A reference field a type declares as its entries' parent, in <see cref="FieldDefinition.Parent"/>:
/// checked when the type is saved, and enforced by <see cref="ParentReferenceHook"/> on every write.
/// </summary>
/// <remarks>
/// Before this, a guarded tree needed code: a module registering the hook for one type and field.
/// The declaration is tenant data, so two tenants on one deployment can each have their own trees.
/// </remarks>
internal static class ParentFields
{
    /// <summary>What is wrong with the parent declarations of a type's fields, for the type validator.</summary>
    public static IEnumerable<string> DefinitionErrors(string typeName, IReadOnlyCollection<FieldDefinition> fields)
    {
        var declared = fields.Where(f => f?.Parent is not null).ToList();

        if (declared.Count > 1)
            yield return $"Only one field may be declared the parent, and {string.Join(", ", declared.Select(f => $"'{f.Name}'"))} are.";

        foreach (var field in declared)
        {
            if (!string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase) || field.Multiple)
                yield return $"Field '{field.Name}' declares parent but is not a single reference.";
            else if (!string.Equals(field.ReferenceType, typeName, StringComparison.OrdinalIgnoreCase))
                yield return $"Field '{field.Name}' declares parent, so it has to point at '{typeName}' itself, "
                           + $"and points at '{field.ReferenceType}'.";

            if (field.Parent!.MaxDepth is < 1 or > ParentDeclaration.MostDepth)
                yield return $"Field '{field.Name}' declares a parent maxDepth of {field.Parent.MaxDepth}, "
                           + $"and it may be 1 to {ParentDeclaration.MostDepth}.";
        }
    }

    /// <summary>
    /// Runs <see cref="ParentReferenceHook"/> for the field the entry's type declares a parent, if any.
    /// </summary>
    /// <remarks>
    /// The hook skips a create, because a new entry cannot be part of a loop. It can still be hung
    /// too deep, or under a loop some other write path stored, so a create is walked too, under an
    /// id minted for the walk that cannot appear anywhere in the tree.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        ContentTypeDefinition? definition, ContentLifecycleContext context, CancellationToken ct)
    {
        if (definition?.Fields.FirstOrDefault(f => f.Parent is not null && IsParentShaped(definition, f)) is not { } field)
            return [];

        var walked = context.EntryId is null
            ? new ContentLifecycleContext
            {
                ContentType = context.ContentType,
                Data = context.Data,
                Existing = context.Existing,
                EntryId = Guid.NewGuid(),
                Session = context.Session,
                UserId = context.UserId,
            }
            : context;

        var depth = Math.Clamp(field.Parent!.MaxDepth, 1, ParentDeclaration.MostDepth);
        return await new ParentReferenceHook(definition.Name, field.Name, depth).OnBeforeSaveAsync(walked, ct);
    }

    /// <summary>Whether a stored declaration is one the type validator would accept.</summary>
    /// <remarks>
    /// A definition stored some other way may carry one on a field that cannot be a parent. Walking
    /// it would refuse writes for a reason the type's owner cannot see, so it is read as none.
    /// </remarks>
    private static bool IsParentShaped(ContentTypeDefinition definition, FieldDefinition field) =>
        string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase)
        && !field.Multiple
        && string.Equals(field.ReferenceType, definition.Name, StringComparison.OrdinalIgnoreCase);
}
