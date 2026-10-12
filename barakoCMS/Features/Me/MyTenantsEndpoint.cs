using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Me;

/// <param name="Slug">The tenant's slug.</param>
/// <param name="Name">The tenant's display name.</param>
/// <param name="LogoUrl">The logo of the tenant's published site entry, if it has one.</param>
/// <param name="Branding">The tenant's branding values.</param>
/// <param name="Roles">
/// The roles the caller holds in that tenant, read the way <c>GET /api/me</c> reads them there: the
/// membership's roles and the caller's global roles, sorted by name. A role can differ per tenant,
/// which is why a switcher shows it beside each one.
/// </param>
internal sealed record TenantSummary(
    string Slug,
    string Name,
    string? LogoUrl,
    Dictionary<string, string> Branding,
    IReadOnlyList<MyRole> Roles);

/// <summary>
/// GET /api/me/tenants — the tenants the signed-in user belongs to (their active memberships joined
/// with the tenant registry). Powers a "switch tenant" experience across a multi-tenant deployment.
/// </summary>
/// <remarks>
/// <c>LogoUrl</c> is the <c>Logo</c> of each tenant's published site entry, which costs two reads
/// for every tenant on the page and none for the rest, so at most two hundred for a full page.
/// The roles cost one more read for the whole page, since the memberships are already loaded.
/// </remarks>
internal class MyTenantsEndpoint(IQuerySession session, IDocumentStore store) : Endpoint<ListRequest, PaginatedResponse<TenantSummary>>
{
    public override void Configure()
    {
        Get("/api/me/tenants"); // authenticated by default
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        var memberships = await session.Query<Membership>()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .ToListAsync(ct);

        var slugs = memberships.Select(m => m.TenantSlug).ToList();
        var tenants = await session.Query<Tenant>()
            .Where(t => slugs.Contains(t.Slug) && t.IsActive)
            .ToListAsync(ct);

        // Paged in memory: the tenants are already filtered by the caller's memberships, which is
        // a join the query cannot express, so the set is small and known by this point.
        var page = tenants.OrderBy(t => t.Name).ToList().ToPagedResponse(req);

        var user = await session.LoadAsync<User>(userId, ct);
        var global = user?.RoleIds ?? [];
        var roleIdsBySlug = memberships
            .GroupBy(m => m.TenantSlug, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(m => m.RoleIds ?? []).Union(global).ToList(),
                StringComparer.Ordinal);
        var onPage = page.Items
            .SelectMany(t => roleIdsBySlug.GetValueOrDefault(t.Slug) ?? [])
            .Distinct()
            .ToList();
        var roles = onPage.Count == 0
            ? new Dictionary<Guid, Role>()
            : (await session.Query<Role>().Where(r => r.Id.In(onPage)).ToListAsync(ct)).ToDictionary(r => r.Id);

        var items = new List<TenantSummary>(page.Items.Count);
        foreach (var tenant in page.Items)
        {
            var profile = await barakoCMS.Features.Tenants.TenantProfiles.ReadAsync(store, tenant, ct);
#pragma warning disable CS0618 // still returned until the member goes, since nothing replaced it
            var held = (roleIdsBySlug.GetValueOrDefault(tenant.Slug) ?? [])
                .Select(id => roles.GetValueOrDefault(id))
                .OfType<Role>()
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new MyRole(r.Id, r.Name))
                .ToList();
            items.Add(new TenantSummary(tenant.Slug, tenant.Name, profile.LogoUrl, tenant.Branding, held));
#pragma warning restore CS0618
        }

        await Send.OkAsync(new PaginatedResponse<TenantSummary>
        {
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
