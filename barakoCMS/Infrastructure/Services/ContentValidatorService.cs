using Marten;
using barakoCMS.Models;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

public interface IContentValidatorService
{
    Task<(bool IsValid, List<string> Errors)> ValidateAsync(string contentType, Dictionary<string, object> data);
}

public class ContentValidatorService : IContentValidatorService
{
    private readonly IQuerySession _session;

    public ContentValidatorService(IQuerySession session)
    {
        _session = session;
    }

    public async Task<(bool IsValid, List<string> Errors)> ValidateAsync(string contentType, Dictionary<string, object> data)
    {
        var errors = new List<string>();
        
        // 1. Load Schema
        var schema = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == contentType);

        if (schema == null)
        {
            // If no schema exists, we assume "Loose Mode" (Hybrid) - Allow anything.
            // Or should we fail? For Phase 2.6, if the user Defined a Type, we enforce it. 
            // If they didn't, we act like the old system (allow anything).
            return (true, errors);
        }

        // 2. Validate Fields
        foreach (var field in schema.Fields)
        {
            var keyDetails = data.FirstOrDefault(k => k.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase));

            // Check Required
            if (field.IsRequired)
            {
                if (keyDetails.Key == null || keyDetails.Value == null || string.IsNullOrWhiteSpace(keyDetails.Value.ToString()))
                {
                    errors.Add($"Field '{field.DisplayName}' ({field.Name}) is required.");
                    continue;
                }
            }

            // Check Type - Validate data type matches field definition
            if (keyDetails.Key != null && keyDetails.Value != null)
            {
                var value = keyDetails.Value;
                var expectedType = field.Type.ToLower();

                if (!IsValidType(value, expectedType))
                {
                    var actualType = GetActualTypeName(value);
                    errors.Add($"Field '{field.DisplayName}' expects type '{expectedType}' but received '{actualType}'");
                }
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Checks if a value matches the expected type
    /// </summary>
    private bool IsValidType(object value, string expectedType)
    {
        return expectedType switch
        {
            "text" or "string" => IsString(value),
            "number" or "int" or "integer" => IsNumber(value),
            "boolean" or "bool" => IsBoolean(value),
            "datetime" or "date" => IsDateTime(value),
            "decimal" => IsDecimal(value),
            "array" => IsArray(value),
            "object" => IsObject(value),
            _ => false
        };
    }

    private bool IsString(object value)
    {
        if (value is string) return true;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String) return true;
        return false;
    }

    private bool IsNumber(object value)
    {
        if (value is int or long or short or byte) return true;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
        {
            try { je.GetInt32(); return true; }
            catch { return false; }
        }

        if (value is string str) return int.TryParse(str, out _);
        return false;
    }

    private bool IsBoolean(object value)
    {
        // Direct boolean type
        if (value is bool) return true;

        // JsonElement boolean
        if (value is JsonElement je && (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False))
            return true;

        // String representations: "true", "false", "True", "False"
        if (value is string str)
            return bool.TryParse(str, out _);

        return false;
    }

    private bool IsDateTime(object value)
    {
        if (value is DateTime) return true;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
        {
            try { je.GetDateTime(); return true; }
            catch { return false; }
        }

        if (value is string str)
            return DateTime.TryParse(str, out _);

        return false;
    }

    private bool IsDecimal(object value)
    {
        if (value is decimal or double or float) return true;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
        {
            try { je.GetDecimal(); return true; }
            catch { return false; }
        }

        if (value is string str)
            return decimal.TryParse(str, out _);

        return false;
    }

    private bool IsArray(object value)
    {
        if (value is string) return false;
        if (value is System.Collections.IDictionary) return false;
        if (value is System.Collections.IEnumerable) return true;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array) return true;
        return false;
    }

    private bool IsObject(object value)
    {
        if (value is string or int or bool or DateTime or decimal) return false;
        if (IsArray(value)) return false;
        if (value is Dictionary<string, object> or IDictionary<string, object>) return true;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object) return true;
        return value.GetType().IsClass;
    }

    private string GetActualTypeName(object value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Array => "array",
                JsonValueKind.Object => "object",
                JsonValueKind.Null => "null",
                _ => "unknown"
            };
        }

        return value.GetType().Name.ToLower();
    }
}
