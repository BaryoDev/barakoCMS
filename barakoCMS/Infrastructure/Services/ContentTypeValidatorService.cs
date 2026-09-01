using System.Linq;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Services;

public interface IContentTypeValidatorService
{
    (bool IsValid, List<string> Errors) Validate(string name, string displayName, List<FieldDefinition> fields);

    /// <summary>Checks a lifecycle declaration on its own. Null is valid and means the default three states.</summary>
    (bool IsValid, List<string> Errors) ValidateLifecycle(LifecycleDefinition? lifecycle);
}

public class ContentTypeValidatorService : IContentTypeValidatorService
{
    public (bool IsValid, List<string> Errors) Validate(string name, string displayName, List<FieldDefinition> fields)
    {
        var errors = new List<string>();

        // Validate ContentType name
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("ContentType name is required.");
        }

        // Validate DisplayName
        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors.Add("ContentType displayName is required.");
        }

        // Validate fields
        if (fields == null || fields.Count == 0)
        {
            errors.Add("At least one field is required.");
        }
        else
        {
            foreach (var field in fields)
            {
                // Validate field name
                if (string.IsNullOrWhiteSpace(field.Name))
                {
                    errors.Add("Field name cannot be empty.");
                    continue;
                }

                // Validate PascalCase
                if (!IsPascalCase(field.Name))
                {
                    var suggestion = ToPascalCase(field.Name);
                    errors.Add($"Field name '{field.Name}' must be in PascalCase. Did you mean '{suggestion}'?");
                }

                // Validate field type against the shared registry (single source of truth).
                if (string.IsNullOrWhiteSpace(field.Type))
                {
                    errors.Add($"Field '{field.Name}' must have a type.");
                }
                else if (!FieldTypeRegistry.IsKnownType(field.Type))
                {
                    var allowedList = string.Join(", ", FieldTypeRegistry.AllowedTypeNames);
                    errors.Add($"Field '{field.Name}' has invalid type '{field.Type}'. Allowed types: {allowedList}");
                }
                else if (string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(field.ReferenceType))
                {
                    // Refused at definition time rather than at write time. A reference with no
                    // target is an untyped uuid: nothing validates what it points at and delivery
                    // cannot resolve it, so accepting the type and discovering the gap later would
                    // leave every entry already written unvalidatable.
                    errors.Add($"Field '{field.Name}' is a reference and must name the content type it points at, in referenceType.");
                }
                else if (!string.IsNullOrWhiteSpace(field.ReferenceType)
                         && !string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Field '{field.Name}' declares referenceType but is of type '{field.Type}', not reference.");
                }
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <inheritdoc />
    public (bool IsValid, List<string> Errors) ValidateLifecycle(LifecycleDefinition? lifecycle)
    {
        var errors = new List<string>();

        // Null is not an error. Every content type that exists has none, and a type without a
        // lifecycle has to keep behaving exactly as it did before lifecycles existed.
        if (lifecycle is null)
            return (true, errors);

        if (lifecycle.States.Count == 0)
            errors.Add("A lifecycle must declare at least one state.");

        var duplicateStates = lifecycle.States
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var duplicate in duplicateStates)
            errors.Add($"State '{duplicate}' is declared more than once.");

        foreach (var state in lifecycle.States.Where(s => !IsPascalCase(s)))
            errors.Add($"State '{state}' must be in PascalCase, letters and digits only.");

        if (string.IsNullOrWhiteSpace(lifecycle.InitialState))
        {
            errors.Add("A lifecycle must name the state a new entry starts in.");
        }
        else if (!lifecycle.States.Contains(lifecycle.InitialState, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"The initial state '{lifecycle.InitialState}' is not one of the declared states.");
        }

        // Names are checked for uniqueness because a permission and a workflow trigger both key on
        // them in the issues that follow. Two transitions sharing a name would make either
        // ambiguous, and the ambiguity would surface as a permission that sometimes applies.
        var duplicateNames = lifecycle.Transitions
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var duplicate in duplicateNames)
            errors.Add($"Transition '{duplicate}' is declared more than once.");

        foreach (var transition in lifecycle.Transitions)
        {
            if (string.IsNullOrWhiteSpace(transition.Name))
            {
                errors.Add("Every transition needs a name, because a permission and a workflow both key on it.");
                continue;
            }

            if (!IsPascalCase(transition.Name))
                errors.Add($"Transition '{transition.Name}' must be in PascalCase, letters and digits only.");

            if (!lifecycle.States.Contains(transition.From, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Transition '{transition.Name}' moves from '{transition.From}', which is not a declared state.");

            if (!lifecycle.States.Contains(transition.To, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Transition '{transition.Name}' moves to '{transition.To}', which is not a declared state.");

            if (string.Equals(transition.From, transition.To, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Transition '{transition.Name}' moves '{transition.From}' to itself, which changes nothing.");
        }

        return (errors.Count == 0, errors);
    }

    private static bool IsPascalCase(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return false;

        // PascalCase: starts with uppercase letter, contains only letters and digits, no underscores or hyphens
        return char.IsUpper(fieldName[0]) &&
               fieldName.All(c => char.IsLetterOrDigit(c)) &&
               !fieldName.Contains('_') &&
               !fieldName.Contains('-');
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        // Handle snake_case, kebab-case, or space-separated
        var words = Regex.Split(input, @"[_\-\s]+");

        return string.Concat(words.Select(word =>
            string.IsNullOrEmpty(word) ? "" : char.ToUpper(word[0]) + word.Substring(1).ToLower()));
    }
}
