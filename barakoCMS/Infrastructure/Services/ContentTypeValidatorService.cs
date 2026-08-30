using barakoCMS.Core.Validation;
using barakoCMS.Models;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Services;

public interface IContentTypeValidatorService
{
    (bool IsValid, List<string> Errors) Validate(string name, string displayName, List<FieldDefinition> fields);
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
