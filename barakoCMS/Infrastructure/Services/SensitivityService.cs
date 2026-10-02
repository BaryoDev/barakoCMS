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
/// <remarks>
/// Who may see a value is decided from the caller's stored roles, not from the role names in the
/// token. The holder of the seeded SuperAdmin role sees everything. A field that lists
/// <see cref="FieldDefinition.VisibleToRoles"/> is seen by the holders of those roles and nobody
/// else. A field that lists none is seen by a role holding
/// <see cref="SystemCapabilities.ViewSensitive"/> or <see cref="SystemCapabilities.ViewHidden"/>,
/// whichever its level asks for, and so is an entry whose own level is not Public.
/// </remarks>
public class SensitivityService : ISensitivityService
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly SensitivityMode _mode;
    private readonly Dictionary<string, ContentTypeDefinition?> _schemaCache = new(StringComparer.OrdinalIgnoreCase);
    private System.Security.Claims.ClaimsPrincipal? _storedCaller;
    private string? _storedCallerFor;

    /// <summary>
    /// Throws when the configured mode cannot do what its name says. Called at startup.
    /// </summary>
    public static void ValidateMode(IConfiguration configuration)
    {
        var raw = configuration["Sensitivity:Mode"];
        if (Enum.TryParse<SensitivityMode>(raw, ignoreCase: true, out var mode) && mode == SensitivityMode.All)
        {
            throw new InvalidOperationException(
                "Sensitivity:Mode is All, which is declared but not implemented: scrubbing branches "
                + "on Off only, so All behaves exactly as SensitiveOnly. Use SensitiveOnly, which is "
                + "the default and scrubs every field marked Sensitive or Hidden.");
        }
    }

    public SensitivityService(
        IQuerySession session,
        IConfiguration configuration,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
        _mode = Enum.TryParse<SensitivityMode>(configuration["Sensitivity:Mode"], ignoreCase: true, out var m)
            ? m
            : SensitivityMode.SensitiveOnly;
    }

    // Claim types of the stored caller. Private to this class, and not the role claim type a token
    // uses, so nothing a token carries can be read as one of them.
    private const string StoredRoleId = "barako:stored-role-id";
    private const string StoredRoleName = "barako:stored-role-name";
    private const string StoredCapability = "barako:stored-capability";

    /// <summary>
    /// The caller as the store describes them: the roles they hold in this tenant, by id and by
    /// name, and which of the two sensitivity capabilities those roles carry. Read once per request.
    /// </summary>
    /// <remarks>
    /// The token's role claims are not used. A claim carries a role's name and no id, so the names
    /// "HR" and "SuperAdmin" used to decide who saw a Sensitive or Hidden value, and a claim is as
    /// old as its token, so a capability taken off a role would keep working until the token
    /// expired. A caller with no <c>UserId</c> claim, or one naming no stored user, holds no role
    /// and sees only Public values.
    /// </remarks>
    private async ValueTask<System.Security.Claims.ClaimsPrincipal> StoredCallerAsync(
        System.Security.Claims.ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.FindFirst("UserId")?.Value ?? string.Empty;
        if (_storedCaller is not null && string.Equals(_storedCallerFor, userId, StringComparison.Ordinal))
            return _storedCaller;

        IReadOnlyList<Role> roles = [];
        if (Guid.TryParse(userId, out var id) && await _session.LoadAsync<User>(id, ct) is { } stored)
        {
            var roleIds = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
                .EffectiveRoleIdsAsync(_session, stored, _tenant.Slug, ct);

            if (roleIds.Count > 0)
                roles = await _session.Query<Role>().Where(r => r.Id.In(roleIds)).ToListAsync(ct);
        }

        var claims = new List<System.Security.Claims.Claim>();
        foreach (var role in roles)
        {
            claims.Add(new(StoredRoleId, role.Id.ToString()));
            claims.Add(new(StoredRoleName, role.Name ?? string.Empty));
        }

        foreach (var capability in new[] { SystemCapabilities.ViewSensitive, SystemCapabilities.ViewHidden })
        {
            if (roles.Any(r => SystemCapabilities.Satisfies(r.SystemCapabilities ?? [], capability)))
                claims.Add(new(StoredCapability, capability));
        }

        _storedCallerFor = userId;
        _storedCaller = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "stored-roles", nameType: null, roleType: StoredRoleName));
        return _storedCaller;
    }

    /// <summary>Whether the stored caller holds the seeded SuperAdmin role, by its id.</summary>
    private static bool IsSuperAdmin(System.Security.Claims.ClaimsPrincipal storedCaller) =>
        storedCaller.HasClaim(StoredRoleId, SystemRoles.SuperAdminRoleId.ToString());

    public async ValueTask<bool> ApplyAsync(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext, CancellationToken ct = default)
    {
        if (_mode == SensitivityMode.Off)
            return false;

        // A Public entry of a type with no restricted field has nothing to decide, so the caller's
        // roles are not read for it.
        if (level == SensitivityLevel.Public
            && (await LoadDefinitionAsync(contentType, ct))?.Fields.Any(f => f.Sensitivity != SensitivityLevel.Public) != true)
            return false;

        var user = await StoredCallerAsync(httpContext.User, ct);
        if (IsSuperAdmin(user))
            return false; // SuperAdmin sees everything.

        // 1. Document-level. An entry's own level is judged the way a field of that level with no
        // role list of its own is.
        if (level != SensitivityLevel.Public && !CallerMaySee(new FieldDefinition { Sensitivity = level }, user))
        {
            data.Clear();
            return level == SensitivityLevel.Hidden; // only a Hidden entry reports itself as withheld
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

        var user = await StoredCallerAsync(httpContext.User, ct);
        if (IsSuperAdmin(user))
            return;

        var definition = await LoadDefinitionAsync(contentType, ct);
        if (definition == null)
            return;

        DropUnwritable(definition, incoming, existing, user);
    }

    public async ValueTask ApplyWriteAsync(ContentTypeDefinition definition, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext, CancellationToken ct = default)
    {
        if (_mode == SensitivityMode.Off)
            return;

        var user = await StoredCallerAsync(httpContext.User, ct);
        if (IsSuperAdmin(user))
            return;

        DropUnwritable(definition, incoming, existing, user);
    }

    private static void DropUnwritable(
        ContentTypeDefinition definition,
        IDictionary<string, object> incoming,
        IReadOnlyDictionary<string, object>? existing,
        System.Security.Claims.ClaimsPrincipal user)
    {
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

    /// <summary>
    /// A field's own list decides when it has one, and the capability for its level when not.
    /// </summary>
    /// <remarks>
    /// The list replaces the default, it does not add to it: a role holding the capability and not
    /// on the list does not see the field. SuperAdmin is handled by the callers.
    /// </remarks>
    private static bool CallerMaySee(FieldDefinition field, System.Security.Claims.ClaimsPrincipal user)
    {
        if (field.VisibleToRoles is { Count: > 0 })
            return field.VisibleToRoles.Any(entry => HoldsRole(user, entry));

        return field.Sensitivity switch
        {
            SensitivityLevel.Sensitive => user.HasClaim(StoredCapability, SystemCapabilities.ViewSensitive),
            SensitivityLevel.Hidden => user.HasClaim(StoredCapability, SystemCapabilities.ViewHidden),
            _ => true,
        };
    }

    private async ValueTask<ContentTypeDefinition?> LoadDefinitionAsync(string contentType, CancellationToken ct)
    {
        if (_schemaCache.TryGetValue(contentType, out var cached))
            return cached;
        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == contentType, ct);
        _schemaCache[contentType] = def;
        return def;
    }

    /// <summary>Whether the stored caller holds the role a list entry names.</summary>
    /// <remarks>
    /// An entry that reads as an id is a role id, which is what a list is stored as. Anything else
    /// is a role name, from a definition stored before ids were or naming a role that did not
    /// exist when it was written, and is matched exactly against the names of the caller's roles.
    /// </remarks>
    private static bool HoldsRole(System.Security.Claims.ClaimsPrincipal user, string? entry) =>
        entry is not null
        && (barakoCMS.Core.RoleReferences.IsId(entry, out var id)
            ? user.HasClaim(StoredRoleId, id.ToString())
            : user.HasClaim(StoredRoleName, entry));

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
