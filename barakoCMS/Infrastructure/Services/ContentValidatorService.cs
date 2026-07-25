using Marten;
using barakoCMS.Core.Validation;
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

            // Check Type - validate the value against the field type via the shared
            // registry (same source of truth the content-type validator uses).
            if (keyDetails.Key != null && keyDetails.Value != null)
            {
                var value = keyDetails.Value;
                var expectedType = field.Type.ToLower();

                if (!FieldTypeRegistry.IsValidValue(expectedType, value))
                {
                    var actualType = GetActualTypeName(value);
                    errors.Add($"Field '{field.DisplayName}' expects type '{expectedType}' but received '{actualType}'");
                }
            }
        }

        return (errors.Count == 0, errors);
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
