using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Me;

public sealed record TenantSummary(string Slug, string Name, string? LogoUrl, Dictionary<string, string> Branding);

/// <summary>
/// GET /api/me/tenants — the tenants the signed-in user belongs to (their active memberships joined
/// with the tenant registry). Powers a "switch tenant" experience across a multi-tenant deployment.
/// </summary>
public class MyTenantsEndpoint : EndpointWithoutRequest<List<TenantSummary>>
{
    private readonly IQuerySession _session;

    public MyTenantsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/me/tenants"); // authenticated by default
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        var memberships = await _session.Query<Membership>()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .ToListAsync(ct);

        var slugs = memberships.Select(m => m.TenantSlug).ToList();
        var tenants = await _session.Query<Tenant>()
            .Where(t => slugs.Contains(t.Slug) && t.IsActive)
            .ToListAsync(ct);

        var result = tenants
            .Select(t => new TenantSummary(t.Slug, t.Name, t.LogoUrl, t.Branding))
            .OrderBy(t => t.Name)
            .ToList();

        await SendOkAsync(result, ct);
    }
}
