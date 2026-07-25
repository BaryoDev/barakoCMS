using System.Text.Json;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Validates content data against ContentType field definitions
/// </summary>
public static class ContentDataValidator
{
    /// <summary>
    /// Validation result containing success status and error messages
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();

        public static ValidationResult Success() => new() { IsValid = true };
        
        public static ValidationResult Failure(params string[] errors) => new()
        {
            IsValid = false,
            Errors = errors.ToList()
        };
    }

    /// <summary>
    /// Validates that data values match the declared field types
    /// </summary>
    public static ValidationResult ValidateData(
        Dictionary<string, object> data,
        Dictionary<string, string> fieldDefinitions)
    {
        if (data == null || fieldDefinitions == null)
            return ValidationResult.Failure("Data and field definitions cannot be null");

        var errors = new List<string>();

        foreach (var field in fieldDefinitions)
        {
            var fieldName = field.Key;
            var expectedType = field.Value.ToLower();

            // Field is optional - if not present, skip validation
            if (!data.ContainsKey(fieldName))
                continue;

            var value = data[fieldName];

            // Null values are allowed for all types
            if (value == null)
                continue;

            // Validate the value against the field type via the shared registry —
            // the same source of truth the runtime validators use.
            if (!FieldTypeRegistry.IsValidValue(expectedType, value))
            {
                var actualType = GetActualTypeName(value);
                errors.Add($"Field '{fieldName}' expects type '{expectedType}' but received '{actualType}' ({GetValuePreview(value)})");
            }
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    private static string GetActualTypeName(object value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "bool",
                JsonValueKind.Array => "array",
                JsonValueKind.Object => "object",
                JsonValueKind.Null => "null",
                _ => "unknown"
            };
        }

        return value.GetType().Name.ToLower();
    }

    private static string GetValuePreview(object value)
    {
        if (value == null)
            return "null";

        var str = value.ToString();
        if (str != null && str.Length > 50)
            return str.Substring(0, 47) + "...";

        return str ?? "null";
    }
}
