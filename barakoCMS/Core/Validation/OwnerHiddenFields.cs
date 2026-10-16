using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// A field its owner can hide from other callers, through <see cref="FieldDefinition.ShownWhenListedIn"/>:
/// checked when the type is saved.
/// </summary>
public static class OwnerHiddenFields
{
    /// <summary>What is wrong with the ShownWhenListedIn declarations of a type's fields.</summary>
    public static IEnumerable<string> DefinitionErrors(IReadOnlyList<FieldDefinition> fields)
    {
        foreach (var field in fields.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.ShownWhenListedIn)))
        {
            var listName = field.ShownWhenListedIn!;

            if (string.Equals(listName, field.Name, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"ShownWhenListedIn on '{field.Name}' cannot name the field itself.";
                continue;
            }

            var list = fields.FirstOrDefault(f => f is not null
                && string.Equals(f.Name, listName, StringComparison.OrdinalIgnoreCase));

            if (list is null)
            {
                yield return $"ShownWhenListedIn on '{field.Name}' names a field that does not exist: '{listName}'.";
            }
            else if (!string.Equals(list.Type, "array", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"ShownWhenListedIn on '{field.Name}' must name an array field, and '{list.Name}' is '{list.Type}'.";
            }
        }
    }
}
