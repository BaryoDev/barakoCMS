using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Schema-driven sensitivity: document-level (Public/Sensitive/Hidden) plus per-field masking read
/// from the content type's <see cref="FieldDefinition.Sensitivity"/>. SuperAdmin sees everything.
/// Scoped, so its schema lookups are cached for the duration of one request (cheap for List).
/// </summary>
public class SensitivityService : ISensitivityService
{
    private readonly IQuerySession _session;
    private readonly SensitivityMode _mode;
    private readonly Dictionary<string, ContentTypeDefinition?> _schemaCache = new(StringComparer.OrdinalIgnoreCase);

    public SensitivityService(IQuerySession session, IConfiguration configuration)
    {
        _session = session;
        _mode = Enum.TryParse<SensitivityMode>(configuration["Sensitivity:Mode"], ignoreCase: true, out var m)
            ? m
            : SensitivityMode.SensitiveOnly;
    }

    public async ValueTask<bool> ApplyAsync(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext, CancellationToken ct = default)
    {
        if (_mode == SensitivityMode.Off)
            return false;

        var user = httpContext.User;
        if (user.IsInRole("SuperAdmin"))
            return false; // SuperAdmin sees everything.

        // 1. Document-level.
        if (level == SensitivityLevel.Hidden)
        {
            data.Clear();
            return true; // whole document hidden
        }
        if (level == SensitivityLevel.Sensitive && !RoleAllowed(user, DefaultRolesFor(SensitivityLevel.Sensitive)))
        {
            data.Clear();
            return false;
        }

        // 2. Field-level, from the content type's schema.
        var definition = await LoadDefinitionAsync(contentType, ct);
        if (definition != null)
        {
            foreach (var field in definition.Fields)
            {
                // Case-insensitive, like validation and public delivery. A record holding "salary"
                // against a schema field named "Salary" is validated as that field and delivered as
                // that field; masking matched ordinally and did not, so a Sensitive value escaped
                // exactly the mismatch DeliveryQuery documents as normal and expected.
                if (field.Sensitivity == SensitivityLevel.Public || StoredKey(data, field.Name) is null)
                    continue;
                if (CallerMaySee(field, user))
                    continue;
                ApplyMask(data, field);
            }
        }

        return false;
    }

    public async ValueTask ApplyWriteAsync(string contentType, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext, CancellationToken ct = default)
    {
        if (_mode == SensitivityMode.Off)
            return;

        var user = httpContext.User;
        if (user.IsInRole("SuperAdmin"))
            return;

        var definition = await LoadDefinitionAsync(contentType, ct);
        if (definition == null)
            return;

        foreach (var field in definition.Fields)
        {
            if (field.Sensitivity == SensitivityLevel.Public)
                continue;
            if (CallerMaySee(field, user))
                continue;

            // The caller cannot see this field, so they cannot set it. Revert to the stored value
            // on update, or drop it entirely on create.
            // Same lookup as the read path, for the same reason. A caller who cannot see a field
            // could otherwise set it by spelling it differently from the schema, and the value would
            // still be validated and delivered as that field.
            var incomingKey = StoredKey(incoming, field.Name);
            var existingKey = existing is null ? null : StoredKey(existing, field.Name);

            if (existingKey is not null)
                incoming[incomingKey ?? field.Name] = existing![existingKey];
            else if (incomingKey is not null)
                incoming.Remove(incomingKey);
        }
    }

    private static bool CallerMaySee(FieldDefinition field, System.Security.Claims.ClaimsPrincipal user)
    {
        IEnumerable<string> allowed = field.VisibleToRoles.Count > 0
            ? field.VisibleToRoles
            : DefaultRolesFor(field.Sensitivity);
        return RoleAllowed(user, allowed);
    }

    private async ValueTask<ContentTypeDefinition?> LoadDefinitionAsync(string contentType, CancellationToken ct)
    {
        if (_schemaCache.TryGetValue(contentType, out var cached))
            return cached;
        // Marten 9 removed synchronous LINQ execution; this was the last sync query.
        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == contentType, ct);
        _schemaCache[contentType] = def;
        return def;
    }

    private static bool RoleAllowed(System.Security.Claims.ClaimsPrincipal user, IEnumerable<string> roles)
        => roles.Any(user.IsInRole);

    // Default policy when a field does not list explicit VisibleToRoles. SuperAdmin is always
    // allowed (handled above), so it need not be repeated here.
    private static string[] DefaultRolesFor(SensitivityLevel level) => level switch
    {
        SensitivityLevel.Sensitive => new[] { "HR" },
        SensitivityLevel.Hidden => Array.Empty<string>(), // only SuperAdmin by default
        _ => Array.Empty<string>(),
    };

    /// <summary>The key a record actually stores a field under, whatever casing it used.</summary>
    /// <remarks>
    /// Validation matches the schema with OrdinalIgnoreCase and so does public delivery, which
    /// documents the mismatch as normal. Masking is the third reader of the same data and has to
    /// agree with the other two, or a field is the same field to two of them and a different one to
    /// the third, and the third is the one deciding whether to hide it.
    /// </remarks>
    private static string? StoredKey<T>(IEnumerable<KeyValuePair<string, T>> data, string fieldName)
    {
        foreach (var kv in data)
        {
            if (string.Equals(kv.Key, fieldName, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        return null;
    }

    private static void ApplyMask(IDictionary<string, object> data, FieldDefinition field)
    {
        var mask = field.Mask;
        if (mask == FieldMask.Default)
            mask = field.Sensitivity == SensitivityLevel.Hidden ? FieldMask.Remove : FieldMask.Redact;

        // The record's own spelling, not the schema's, or a Redact would add a second key beside the
        // one holding the value and leave the original in place.
        var key = StoredKey(data, field.Name) ?? field.Name;

        switch (mask)
        {
            case FieldMask.Remove:
                data.Remove(key);
                break;
            case FieldMask.Last4:
                var s = data[key]?.ToString() ?? string.Empty;
                data[key] = s.Length <= 4 ? "****" : new string('*', s.Length - 4) + s[^4..];
                break;
            default: // Redact
                data[key] = "***";
                break;
        }
    }
}
