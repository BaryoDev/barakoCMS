using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ServicePrincipals;

// A service principal is an identity for a machine caller. Its API keys take their roles from the
// principal, so they keep working when the person who made them leaves. Managed by a person holding
// manage_api_keys, in that person's tenant. A principal key cannot reach these routes: managing keys
// and principals is on the cap: deny list.

internal static class PrincipalRules
{
    public const string PlatformRoleMessage =
        "A service principal cannot hold a platform role or a role that carries a platform capability.";

    /// <summary>
    /// The first reason these roles cannot go on a principal, or null. It reuses the rule a person
    /// follows when assigning roles to a membership (<see cref="Members.IsAssignable"/> and
    /// <see cref="RoleScope.AllReachableAsync"/>, so another tenant's role answers as one that does
    /// not exist) and is stricter in one way: a role carrying a platform capability is refused for
    /// every caller, SuperAdmin included, because a key must never hold platform reach.
    /// </summary>
    public static async Task<string?> RefusalAsync(IQuerySession session, List<Guid> roleIds, string tenant, CancellationToken ct)
    {
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            return "SuperAdmin is a platform role and cannot be granted inside a tenant.";
        if (!await RoleScope.AllReachableAsync(session, roleIds, tenant, ct))
            return "One or more roles do not exist.";
        if (roleIds.Count == 0)
            return null;

        var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && roleIds.Contains(r.Id)).ToListAsync(ct);
        return roles.Any(r => r.IsPlatform || PlatformRoles.CarriesPlatformCapability(r)) ? PlatformRoleMessage : null;
    }

    /// <summary>
    /// The first reason the caller cannot hand these roles to a principal, or null. A person may
    /// only grant what they hold, as for stations: otherwise a key steward could mint a principal
    /// with a capability they lack and use it through a key. A SuperAdmin holds every capability
    /// the roles can carry, since platform capabilities are already refused above.
    /// </summary>
    public static async Task<string?> NotHeldAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        IEnumerable<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.ToList();
        if (ids.Count == 0 || await PlatformRoles.IsSuperAdminAsync(session, user, ct))
            return null;

        var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var capability in roles.SelectMany(r => r.SystemCapabilities).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await callers.HoldsAsync(user, capability, ct))
                return $"You cannot grant '{capability}' because you do not hold it.";
        }

        // Content rights too. A key acts through the principal's roles on the content routes, so a
        // role with wider rights than the caller's own would widen the caller. Judged by the
        // resolver those routes use, with no entry, so a rule's conditions are not compared.
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var callerId);
        var caller = callerId == Guid.Empty ? null : await session.LoadAsync<User>(callerId, ct);
        foreach (var (type, action) in roles.SelectMany(ContentRights).Distinct())
        {
            if (caller is null || !await permissions.CanPerformActionAsync(caller, type, action, null, ct))
                return $"You cannot grant '{action}' on '{type}' because you do not hold it.";
        }

        return null;
    }

    /// <summary>Every enabled content right a role carries: each type with create, read, update, delete and named transitions.</summary>
    private static IEnumerable<(string Type, string Action)> ContentRights(Role role)
    {
        foreach (var permission in role.Permissions ?? [])
        {
            if (permission is null || string.IsNullOrWhiteSpace(permission.ContentTypeSlug)) continue;
            var type = permission.ContentTypeSlug;
            if (permission.Create?.Enabled == true) yield return (type, "create");
            if (permission.Read?.Enabled == true) yield return (type, "read");
            if (permission.Update?.Enabled == true) yield return (type, "update");
            if (permission.Delete?.Enabled == true) yield return (type, "delete");
            foreach (var (name, rule) in permission.Transitions ?? [])
                if (rule?.Enabled == true) yield return (type, PermissionResolver.TransitionActionPrefix + name);
        }
    }

    /// <summary>Whether these roles may go on a principal, then whether the caller holds what they carry.</summary>
    public static async Task<string?> CheckAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        List<Guid> roleIds, string tenant, CancellationToken ct) =>
        await RefusalAsync(session, roleIds, tenant, ct) ?? await NotHeldAsync(session, callers, permissions, user, roleIds, ct);

    /// <summary>The trimmed name, or the reason it is refused.</summary>
    public static string? NameProblem(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
            return "Name is required, up to 100 characters.";
        return name.Any(char.IsControl) ? "Name cannot contain control characters." : null;
    }

    /// <summary>
    /// Saves, and answers 409 when the principal changed since it was read (another admin edited or
    /// disabled it at the same time). Returns false when it answered.
    /// </summary>
    public static async Task<bool> SaveAsync(IDocumentSession session, IEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            endpoint.HttpContext.Response.StatusCode = 409;
            await endpoint.HttpContext.Response.WriteAsJsonAsync(
                new { message = "The service principal changed while it was being updated. Try again." }, ct);
            return false;
        }
    }

    /// <summary>The principal's roles, loaded for the hold check on bind and move.</summary>
    public static Task<string?> NotHeldByPrincipalAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        ServicePrincipal principal, CancellationToken ct) =>
        NotHeldAsync(session, callers, permissions, user, principal.RoleIds, ct);

    public static string TenantOf(System.Security.Claims.ClaimsPrincipal user) =>
        (user.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

    public static Guid? ActorOf(System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var id) ? id : null;

    /// <summary>The principal in this tenant, or null when it is missing or belongs to another tenant.</summary>
    public static Task<ServicePrincipal?> InTenantAsync(IQuerySession session, Guid id, string tenant, CancellationToken ct) =>
        session.Query<ServicePrincipal>().FirstOrDefaultAsync(p => p.Id == id && p.TenantSlug == tenant, ct);
}

internal sealed record ServicePrincipalResponse(
    Guid Id, string Name, string TenantSlug, List<Guid> RoleIds, bool Disabled, DateTime? DisabledAt,
    DateTime CreatedAt, Guid CreatedBy)
{
    public static ServicePrincipalResponse Of(ServicePrincipal p) =>
        new(p.Id, p.Name, p.TenantSlug, p.RoleIds, p.Disabled, p.DisabledAt, p.CreatedAt, p.CreatedBy);
}
