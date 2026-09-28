using System.Security.Claims;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Answers "how far does this caller reach" for an endpoint that reads or writes a document stored
/// once for the whole deployment, where the conjoined session filters nothing.
/// </summary>
/// <remarks>
/// The capability gate answers from the caller's effective roles in the current tenant, which
/// include their membership roles there. That is right for a tenant's own data and wrong for a
/// global table: whoever creates a tenant holds Admin there through a membership, and Admin holds
/// the capability for settings, flags, client errors and the rest. So an endpoint over a global
/// table asks one of the two questions here as well:
/// <list type="bullet">
/// <item><see cref="IsSuperAdminAsync"/> for data that belongs to one tenant but is stored globally
/// with the tenant kept as data. A SuperAdmin sees every tenant's rows; anyone else sees their
/// current tenant's. This is the rule <c>GET /api/audit</c> already follows.</item>
/// <item><see cref="HoldsGloballyAsync"/> for data that configures the whole deployment. The
/// capability has to come from one of the caller's global roles (<see cref="User.RoleIds"/>), which
/// only a platform administrator grants, not from a tenant membership. A single-tenant deployment's
/// Admin holds the role globally, so it keeps these screens.</item>
/// </list>
/// Both read the stored user rather than the token's role claims, which carry membership roles and
/// are fifteen minutes stale.
/// </remarks>
public static class PlatformScope
{
    public static async Task<bool> IsSuperAdminAsync(
        IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        (await CallerAsync(session, principal, ct))?.RoleIds.Contains(SystemRoles.SuperAdminRoleId) == true;

    /// <summary>
    /// Whether one of the caller's global roles grants <paramref name="capability"/>, or, with
    /// <see cref="CapabilityGateProcessor.LegacyRoleFallbackKey"/> on, carries one of the legacy role
    /// names the endpoint's gate honours.
    /// </summary>
    public static async Task<bool> HoldsGloballyAsync(
        IQuerySession session,
        ClaimsPrincipal principal,
        IConfiguration configuration,
        string capability,
        IReadOnlyCollection<string> legacyRoles,
        CancellationToken ct)
    {
        var caller = await CallerAsync(session, principal, ct);
        if (caller is null || caller.RoleIds.Count == 0) return false;
        if (caller.RoleIds.Contains(SystemRoles.SuperAdminRoleId)) return true;

        var ids = caller.RoleIds;
        var roles = await session.Query<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var legacy = configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false);

        return roles.Any(r => SystemCapabilities.Satisfies(r.SystemCapabilities, capability)
                              || (legacy && legacyRoles.Contains(r.Name)));
    }

    /// <summary>The message a caller refused by <see cref="HoldsGloballyAsync"/> is given.</summary>
    public const string DeploymentWideMessage =
        "This setting applies to every tenant on the deployment, so only a platform administrator can use it.";

    private static async Task<User?> CallerAsync(IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirst("UserId")?.Value, out var id)
            ? await session.LoadAsync<User>(id, ct)
            : null;
}
