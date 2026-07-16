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

    public bool Apply(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext)
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
        var definition = LoadDefinition(contentType);
        if (definition != null)
        {
            foreach (var field in definition.Fields)
            {
                if (field.Sensitivity == SensitivityLevel.Public || !data.ContainsKey(field.Name))
                    continue;
                if (CallerMaySee(field, user))
                    continue;
                ApplyMask(data, field);
            }
        }

        return false;
    }

    public void ApplyWrite(string contentType, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext)
    {
        if (_mode == SensitivityMode.Off)
            return;

        var user = httpContext.User;
        if (user.IsInRole("SuperAdmin"))
            return;

        var definition = LoadDefinition(contentType);
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
            if (existing != null && existing.TryGetValue(field.Name, out var current))
                incoming[field.Name] = current;
            else
                incoming.Remove(field.Name);
        }
    }

    private static bool CallerMaySee(FieldDefinition field, System.Security.Claims.ClaimsPrincipal user)
    {
        IEnumerable<string> allowed = field.VisibleToRoles.Count > 0
            ? field.VisibleToRoles
            : DefaultRolesFor(field.Sensitivity);
        return RoleAllowed(user, allowed);
    }

    private ContentTypeDefinition? LoadDefinition(string contentType)
    {
        if (_schemaCache.TryGetValue(contentType, out var cached))
            return cached;
        var def = _session.Query<ContentTypeDefinition>().FirstOrDefault(d => d.Name == contentType);
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

    private static void ApplyMask(IDictionary<string, object> data, FieldDefinition field)
    {
        var mask = field.Mask;
        if (mask == FieldMask.Default)
            mask = field.Sensitivity == SensitivityLevel.Hidden ? FieldMask.Remove : FieldMask.Redact;

        switch (mask)
        {
            case FieldMask.Remove:
                data.Remove(field.Name);
                break;
            case FieldMask.Last4:
                var s = data[field.Name]?.ToString() ?? string.Empty;
                data[field.Name] = s.Length <= 4 ? "****" : new string('*', s.Length - 4) + s[^4..];
                break;
            default: // Redact
                data[field.Name] = "***";
                break;
        }
    }
}
