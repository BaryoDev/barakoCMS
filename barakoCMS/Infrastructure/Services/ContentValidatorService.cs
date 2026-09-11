using Marten;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

public interface IContentValidatorService
{
    /// <summary>Checks a data bag against its content type's schema.</summary>
    /// <param name="existing">
    /// The entry being changed, or null when one is being created. Only the singleton cap reads it,
    /// and null is the answer that enforces the cap, so a create path that passes nothing still gets
    /// the check.
    /// </param>
    Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing = null);
}

public class ContentValidatorService : IContentValidatorService
{
    private readonly IQuerySession _session;

    public ContentValidatorService(IQuerySession session)
    {
        _session = session;
    }

    public async Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing = null)
    {
        var errors = new List<string>();
        
        // 1. Load Schema
        var schema = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == contentType);

        if (schema == null)
        {
            // No content type definition, so there is no schema to check against and the entry is
            // accepted as-is. Validation is opt-in: defining a type is what turns it on.
            return (true, errors);
        }

        // 2. The singleton cap. Creating only: an update is not a second entry, and refusing it
        // would make the flag unusable, since the one entry a singleton type is for could never be
        // edited. Counting case-insensitively for the same reason the create endpoint does: names
        // have only been normalised since 4.0, so an entry written by a 3.x import carries whatever
        // the caller typed and matching exactly would count none of them.
        if (schema.IsSingleton && existing is null)
        {
            var typeName = schema.Name.ToLower();

            var taken = await _session.Query<Models.Content>()
                .AnyAsync(c => c.ContentType.ToLower() == typeName);

            if (taken)
            {
                errors.Add(
                    $"'{schema.DisplayName}' holds a single entry and already has one. "
                  + "Edit that entry rather than creating another.");

                // No point reporting field errors on a request that cannot be created either way.
                return (false, errors);
            }
        }

        // 3. Validate Fields
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
                else if (expectedType == "reference")
                {
                    // The registry checked the shape. Whether the target exists, and is the type
                    // this field declares, needs the database, which is why it is here and not
                    // there. Checked on write rather than on read: a reference that pointed at
                    // nothing would otherwise be stored happily and fail for whoever renders it.
                    var error = await ValidateReferenceAsync(field, value);
                    if (error is not null)
                        errors.Add(error);
                }
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Checks that a reference points at something, and at the right kind of something.
    /// </summary>
    /// <remarks>
    /// Pointing at a real entry of the wrong type is the more interesting failure of the two. It
    /// looks correct in the data bag, passes any shape check, and produces a resolved value the
    /// consumer did not ask for. Naming the target type in the error matters for the same reason:
    /// "not found" and "wrong type" are different mistakes and the caller can only fix the one they
    /// are told about.
    /// </remarks>
    private async Task<string?> ValidateReferenceAsync(FieldDefinition field, object value)
    {
        var raw = value is JsonElement je ? je.ToString() : value.ToString();
        if (!Guid.TryParse(raw, out var targetId))
            return $"Field '{field.DisplayName}' expects a reference id.";

        var target = await _session.LoadAsync<Models.Content>(targetId);

        if (target is null)
            return $"Field '{field.DisplayName}' references {targetId}, which does not exist.";

        if (!string.Equals(target.ContentType, field.ReferenceType, StringComparison.OrdinalIgnoreCase))
            return $"Field '{field.DisplayName}' references a '{target.ContentType}' "
                 + $"but is declared to point at '{field.ReferenceType}'.";

        return null;
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
