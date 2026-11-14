using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Resolves a caller's capabilities from storage, with no request. It reads the same roles the
/// capability gate reads for a request by the same caller, through the same rules.
/// </summary>
internal sealed class CapabilityResolver(IDocumentStore store) : ICapabilityResolver
{
    public async Task<IReadOnlySet<string>> ForUserAsync(Guid userId, string tenant, CancellationToken cancellationToken = default)
    {
        var slug = TenantScopes.SlugFor(tenant);
        await using var session = Open(slug);

        if (await session.LoadAsync<User>(userId, cancellationToken) is not { } user
            || !await TenantIsActiveAsync(session, slug, cancellationToken))
        {
            return Empty();
        }

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(session, user, slug, cancellationToken);
        if (roleIds.Count == 0)
        {
            return Empty();
        }

        var ids = roleIds.ToArray();
        var roles = await session.Query<Role>()
            .Where(r => r.DeletedAt == null && ids.Contains(r.Id))
            .ToListAsync(cancellationToken);

        if (roles.Any(r => r.Id == SystemRoles.SuperAdminRoleId))
        {
            return new HashSet<string>([SystemCapabilities.All], StringComparer.OrdinalIgnoreCase);
        }

        return Union(roles);
    }

    public async Task<IReadOnlySet<string>> ForPrincipalAsync(Guid principalId, string tenant, CancellationToken cancellationToken = default)
    {
        var slug = TenantScopes.SlugFor(tenant);
        await using var session = Open(slug);

        var principal = await session.LoadAsync<ServicePrincipal>(principalId, cancellationToken);
        if (principal is null || principal.Disabled
            || !string.Equals(principal.TenantSlug, slug, StringComparison.OrdinalIgnoreCase)
            || !await TenantIsActiveAsync(session, slug, cancellationToken))
        {
            return Empty();
        }

        var ids = principal.RoleIds.ToArray();
        var roles = await session.Query<Role>()
            .Where(r => r.DeletedAt == null && ids.Contains(r.Id))
            .ToListAsync(cancellationToken);

        // A role of another tenant, or a platform role, never counts, and a principal never holds
        // the all-capabilities marker or a platform capability, as at the gate.
        var usable = roles.Where(r => !r.IsPlatform && RoleScope.Reaches(r, slug));
        var held = Union(usable);
        held.RemoveWhere(c => c == SystemCapabilities.All || PlatformRoles.PlatformCapabilities.Contains(c));
        return held;
    }

    private IQuerySession Open(string slug) =>
        slug == Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(slug);

    private static async Task<bool> TenantIsActiveAsync(IQuerySession session, string slug, CancellationToken ct)
    {
        if (slug == Tenant.DefaultSlug)
        {
            return true;
        }

        var row = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        return row is null || row.Status == TenantStatus.Active;
    }

    private static HashSet<string> Empty() => new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Union(IEnumerable<Role> roles) =>
        new(roles.SelectMany(r => r.SystemCapabilities ?? []), StringComparer.OrdinalIgnoreCase);
}
