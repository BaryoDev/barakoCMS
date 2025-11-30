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

            if (!IsValidType(value, expectedType))
            {
                var actualType = GetActualTypeName(value);
                errors.Add($"Field '{fieldName}' expects type '{expectedType}' but received '{actualType}' ({GetValuePreview(value)})");
            }
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Checks if a value matches the expected type
    /// </summary>
    private static bool IsValidType(object value, string expectedType)
    {
        return expectedType switch
        {
            "string" => IsString(value),
            "int" => IsInt(value),
            "bool" => IsBool(value),
            "datetime" => IsDateTime(value),
            "decimal" => IsDecimal(value),
            "array" => IsArray(value),
            "object" => IsObject(value),
            _ => false
        };
    }

    private static bool IsString(object value)
    {
        if (value is string)
            return true;
            
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
            return true;
            
        return false;
    }

    private static bool IsInt(object value)
    {
        if (value is int || value is long || value is short || value is byte)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
        {
            try
            {
                je.GetInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Try parsing string
        if (value is string str)
            return int.TryParse(str, out _);

        return false;
    }

    private static bool IsBool(object value)
    {
        if (value is bool)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False))
            return true;

        // Try parsing string
        if (value is string str)
            return bool.TryParse(str, out _);

        return false;
    }

    private static bool IsDateTime(object value)
    {
        if (value is DateTime)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
        {
            try
            {
                je.GetDateTime();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Try parsing string (ISO 8601)
        if (value is string str)
            return DateTime.TryParse(str, out _);

        return false;
    }

    private static bool IsDecimal(object value)
    {
        if (value is decimal || value is double || value is float)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
        {
            try
            {
                je.GetDecimal();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Try parsing string
        if (value is string str)
            return decimal.TryParse(str, out _);

        return false;
    }

    private static bool IsArray(object value)
    {
        // Check if it's any enumerable except string
        if (value is string)
            return false;

        // Dictionaries are not arrays (they are objects)
        if (value is System.Collections.IDictionary)
            return false;

        if (value is System.Collections.IEnumerable)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return true;

        return false;
    }

    private static bool IsObject(object value)
    {
        // Primitives are not objects
        if (value is string || value is int || value is bool || value is DateTime || value is decimal)
            return false;

        // Arrays are not objects (in our context)
        if (IsArray(value))
            return false;

        // Dictionary or complex type
        if (value is Dictionary<string, object> || value is IDictionary<string, object>)
            return true;

        // JSON deserialization might give us JsonElement
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return true;

        // Any other complex type
        return value.GetType().IsClass;
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
