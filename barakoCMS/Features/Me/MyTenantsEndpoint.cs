using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Me;

internal sealed record TenantSummary(string Slug, string Name, string? LogoUrl, Dictionary<string, string> Branding);

/// <summary>
/// GET /api/me/tenants — the tenants the signed-in user belongs to (their active memberships joined
/// with the tenant registry). Powers a "switch tenant" experience across a multi-tenant deployment.
/// </summary>
/// <remarks>
/// <c>LogoUrl</c> is the <c>Logo</c> of each tenant's published site entry, which costs two reads
/// for every tenant on the page and none for the rest, so at most two hundred for a full page.
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

        var items = new List<TenantSummary>(page.Items.Count);
        foreach (var tenant in page.Items)
        {
            var profile = await barakoCMS.Features.Tenants.TenantProfiles.ReadAsync(store, tenant, ct);
#pragma warning disable CS0618 // still returned until the member goes, since nothing replaced it
            items.Add(new TenantSummary(tenant.Slug, tenant.Name, profile.LogoUrl, tenant.Branding));
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
