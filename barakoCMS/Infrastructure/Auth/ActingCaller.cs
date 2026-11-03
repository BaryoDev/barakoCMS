using System.Security.Claims;
using barakoCMS.Features.Roles;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Who a request acts as: a person (the <c>UserId</c> claim) or a service principal (the
/// <c>principal_id</c> claim an API key bound to a principal carries). Content routes use this so a
/// principal key can do content work without a person behind it.
/// </summary>
/// <remarks>
/// A principal is not a <see cref="User"/> and is never stored as one. For the permission checks, which
/// take a user, <see cref="LoadAsync"/> builds a transient <see cref="User"/> in memory: its id is the
/// principal's own id (not a made-up one), its username is <c>principal:&lt;name&gt;</c>, and its roles
/// are the principal's roles that still count in this tenant. It has no membership, so the roles on the
/// principal are all it holds. Audit rows and the created-by and modified-by ids carry that id and name.
/// </remarks>
internal static class ActingCaller
{
    /// <summary>The service principal id of a principal key, or null for a person or a station.</summary>
    public static Guid? PrincipalIdOf(ClaimsPrincipal caller) =>
        Guid.TryParse(caller.FindFirst(ApiKeyAuthenticationHandler.PrincipalClaim)?.Value, out var id) ? id : null;

    /// <summary>The person's user id, or the principal's id for a principal key. Null when neither.</summary>
    public static Guid? IdOf(ClaimsPrincipal caller) =>
        PrincipalIdOf(caller)
        ?? (Guid.TryParse(caller.FindFirst("UserId")?.Value, out var id) ? id : null);

    /// <summary>
    /// The user the permission checks run for: the stored user for a person, a transient view of the
    /// principal for a principal key. Null when there is none, or the principal is gone or disabled.
    /// </summary>
    public static async Task<User?> LoadAsync(IQuerySession session, ClaimsPrincipal caller, CancellationToken ct)
    {
        if (PrincipalIdOf(caller) is not { } principalId)
            return Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
                ? await session.LoadAsync<User>(userId, ct)
                : null;

        var principal = await session.LoadAsync<ServicePrincipal>(principalId, ct);
        var slug = (caller.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        if (principal is null || principal.Disabled
            || !string.Equals(principal.TenantSlug, slug, StringComparison.OrdinalIgnoreCase))
            return null;

        var ids = principal.RoleIds.ToArray();
        var roles = ids.Length == 0
            ? []
            : await session.Query<Role>().Where(r => r.DeletedAt == null && ids.Contains(r.Id)).ToListAsync(ct);
        return new User
        {
            Id = principal.Id,
            Username = "principal:" + principal.Name,
            RoleIds = roles.Where(r => !r.IsPlatform && RoleScope.Reaches(r, slug)).Select(r => r.Id).ToList(),
        };
    }
}
