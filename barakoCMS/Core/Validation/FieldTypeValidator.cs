using System.Text.RegularExpressions;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Validates field types and names against documented standards.
/// See DEVELOPMENT_STANDARDS.md for complete reference.
/// </summary>
public static class FieldTypeValidator
{
    /// <summary>
    /// Validates if a field type is allowed. Delegates to <see cref="FieldTypeRegistry"/>,
    /// the single source of truth, so this helper can never diverge from the two
    /// runtime validators again.
    /// </summary>
    public static bool IsValidFieldType(string type) => FieldTypeRegistry.IsKnownType(type);

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
        return $"Invalid field type '{type}'. Allowed types: {string.Join(", ", FieldTypeRegistry.AllowedTypeNames)}. " +
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
    /// Gets all allowed field types (from the shared registry).
    /// </summary>
    public static IReadOnlySet<string> GetAllowedTypes() =>
        FieldTypeRegistry.AllowedTypeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
