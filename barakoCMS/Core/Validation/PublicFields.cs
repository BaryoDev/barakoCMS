using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The rule for which fields anonymous readers and public indexes may take from an entry. Anonymous
/// delivery has no owner, so a field its owner can hide (<see cref="FieldDefinition.ShownWhenListedIn"/>)
/// is never among them, whatever its sensitivity.
/// </summary>
public static class PublicFields
{
    /// <summary>Whether the field is one its owner can hide from other readers.</summary>
    public static bool IsOwnerHidden(FieldDefinition field) => !string.IsNullOrEmpty(field.ShownWhenListedIn);

    /// <summary>Whether the field's value goes into public search text.</summary>
    public static bool InSearchText(FieldDefinition field) => InSearchText(field, field.Sensitivity);

    /// <summary>The same, with the field taken to another sensitivity level.</summary>
    public static bool InSearchText(FieldDefinition field, SensitivityLevel level) =>
        level == SensitivityLevel.Public && !InlineImageFields.Is(field.Type) && !IsOwnerHidden(field);

    /// <summary>The names of the fields that go into public search text.</summary>
    public static HashSet<string> SearchTextNames(IEnumerable<FieldDefinition> fields, string? changed = null, SensitivityLevel? changedTo = null) =>
        fields
            .Where(f => changed is not null && changedTo is { } to && string.Equals(f.Name, changed, StringComparison.OrdinalIgnoreCase)
                ? InSearchText(f, to)
                : InSearchText(f))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
