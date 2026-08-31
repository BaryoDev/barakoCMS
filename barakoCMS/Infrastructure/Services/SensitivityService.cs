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
                if (field.Sensitivity == SensitivityLevel.Public)
                    continue;
                if (CallerMaySee(field, user))
                    continue;
                foreach (var key in MatchingKeys(data, field.Name))
                    ApplyMask(data, key, field);
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
            // Drop every casing the caller sent, then put the stored value back under the casing it
            // was stored as. Removing first matters: the caller may have sent "salary" where the
            // store holds "Salary", and leaving theirs behind would keep their value in the document.
            foreach (var key in MatchingKeys(incoming, field.Name))
                incoming.Remove(key);

            // Restore unconditionally, not only when the caller sent the field. Omitting a field they
            // cannot see must not be a way to delete it.
            var stored = existing is null ? [] : MatchingKeys(existing, field.Name);
            if (stored.Count > 0)
                incoming[stored[0]] = existing![stored[0]];
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

    /// <summary>
    /// Every stored key that matches <paramref name="name"/> ignoring case.
    /// </summary>
    /// <remarks>
    /// Content data is a plain case-sensitive dictionary and nothing at the write boundary rejects a
    /// key that only differs from a schema field by case, so "Salary" and "salary" can both be
    /// stored. Masking one and leaving the other would hand the value to a caller who may not see the
    /// field. <c>ToPublic</c> already treats the two as the same field (its allowlist is
    /// OrdinalIgnoreCase); this is the same rule on the authenticated path.
    /// Materialised, because callers mutate the dictionary while walking the result.
    /// </remarks>
    private static List<string> MatchingKeys(IEnumerable<KeyValuePair<string, object>> data, string name) =>
        data.Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

    private static void ApplyMask(IDictionary<string, object> data, string key, FieldDefinition field)
    {
        var mask = field.Mask;
        if (mask == FieldMask.Default)
            mask = field.Sensitivity == SensitivityLevel.Hidden ? FieldMask.Remove : FieldMask.Redact;

        // Keyed by the record's own spelling, passed in by the caller, or a Redact would add a
        // second key beside the one holding the value and leave the original in place.
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
