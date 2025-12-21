using barakoCMS.Models;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Services;

public interface IContentTypeValidatorService
{
    (bool IsValid, List<string> Errors) Validate(string name, string displayName, List<FieldDefinition> fields);
}

public class ContentTypeValidatorService : IContentTypeValidatorService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "text",
        "int", "integer", "number",
        "bool", "boolean",
        "datetime", "date",
        "decimal",
        "array",
        "object"
    };

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

                // Validate field type
                if (string.IsNullOrWhiteSpace(field.Type))
                {
                    errors.Add($"Field '{field.Name}' must have a type.");
                }
                else if (!AllowedTypes.Contains(field.Type))
                {
                    var allowedList = string.Join(", ", AllowedTypes.OrderBy(t => t));
                    errors.Add($"Field '{field.Name}' has invalid type '{field.Type}'. Allowed types: {allowedList}");
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
