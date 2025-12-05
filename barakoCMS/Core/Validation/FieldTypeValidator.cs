using System.Text.RegularExpressions;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Validates field types and names against documented standards.
/// See DEVELOPMENT_STANDARDS.md for complete reference.
/// </summary>
public static class FieldTypeValidator
{
    /// <summary>
    /// Allowed field types as documented in DEVELOPMENT_STANDARDS.md
    /// </summary>
    private static readonly HashSet<string> AllowedTypes = new()
    {
        "string",
        "int",
        "bool",
        "datetime",
        "decimal",
        "array",
        "object"
    };

    /// <summary>
    /// Validates if a field type is allowed
    /// </summary>
    public static bool IsValidFieldType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        return AllowedTypes.Contains(type.ToLower());
    }

    /// <summary>
    /// Validates if a field name follows PascalCase convention
    /// </summary>
    public static bool IsValidFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return false;

        // PascalCase pattern: Starts with uppercase letter, contains only letters and numbers
        var pascalCasePattern = @"^[A-Z][a-zA-Z0-9]*$";
        return Regex.IsMatch(fieldName, pascalCasePattern);
    }

    /// <summary>
    /// Gets a detailed error message for invalid field type
    /// </summary>
    public static string GetFieldTypeError(string type)
    {
        return $"Invalid field type '{type}'. Allowed types: {string.Join(", ", AllowedTypes)}. " +
               "See DEVELOPMENT_STANDARDS.md for details.";
    }

    /// <summary>
    /// Gets a detailed error message for invalid field name
    /// </summary>
    public static string GetFieldNameError(string fieldName)
    {
        var suggestion = FixFieldName(fieldName);
        return $"Field name '{fieldName}' must be PascalCase. " +
               $"Expected: '{suggestion}'. " +
               "See DEVELOPMENT_STANDARDS.md for naming conventions.";
    }

    /// <summary>
    /// Gets all invalid field types from a fields dictionary
    /// </summary>
    public static List<string> GetInvalidFieldTypes(Dictionary<string, string> fields)
    {
        return fields
            .Where(f => !IsValidFieldType(f.Value))
            .Select(f => $"{f.Key}: {GetFieldTypeError(f.Value)}")
            .ToList();
    }

    /// <summary>
    /// Gets all invalid field names from a fields dictionary
    /// </summary>
    public static List<string> GetInvalidFieldNames(Dictionary<string, string> fields)
    {
        return fields
            .Where(f => !IsValidFieldName(f.Key))
            .Select(f => GetFieldNameError(f.Key))
            .ToList();
    }

    /// <summary>
    /// Attempts to fix a field name to PascalCase
    /// </summary>
    private static string FixFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return fieldName;

        // Remove invalid characters and split by common separators
        var parts = fieldName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Capitalize first letter of each part
        var fixedName = string.Join("", parts.Select(p => 
            char.ToUpper(p[0]) + p.Substring(1).ToLower()));

        return fixedName;
    }

    /// <summary>
    /// Gets all allowed field types
    /// </summary>
    public static IReadOnlySet<string> GetAllowedTypes() => AllowedTypes;
}
