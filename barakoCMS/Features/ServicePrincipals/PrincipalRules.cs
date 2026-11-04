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

    /// <summary>The first reason the caller cannot hand these roles to a principal, or null (see <see cref="RoleGrantRules"/>).</summary>
    public static Task<string?> NotHeldAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        IEnumerable<Guid> roleIds, CancellationToken ct) =>
        RoleGrantRules.NotHeldAsync(session, callers, permissions, user, roleIds, TenantOf(user), ct);

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
