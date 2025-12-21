using Marten;
using barakoCMS.Models;

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

            // Check Type (Basic)
            if (keyDetails.Key != null && keyDetails.Value != null)
            {
                var val = keyDetails.Value.ToString(); // Simplified for now
                if (field.Type == "number" && !double.TryParse(val, out _))
                {
                    errors.Add($"Field '{field.DisplayName}' must be a number.");
                }
                else if (field.Type == "boolean" && !bool.TryParse(val, out _))
                {
                     errors.Add($"Field '{field.DisplayName}' must be a boolean.");
                }
                // Add more types as needed (date, etc.)
            }
        }

        return (errors.Count == 0, errors);
    }
}
