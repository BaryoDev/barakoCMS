using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants;

internal sealed record TenantByHostResponse(string Handle);

/// <summary>GET /api/tenants/by-host/{host}: the tenant a domain belongs to.</summary>
/// <remarks>
/// For a renderer serving several sites from one container, which has to know the tenant before it
/// can read anything else.
///
/// Anonymous, because that question is asked before anyone signs in, and it answers with the handle
/// and nothing more. Which tenant answers on a public domain is already public, since the domain
/// serves it; the rest of the tenant record is not.
///
/// It resolves the host the way request resolution does: a registered domain from the same cached
/// map first, then the leading subdomain. The map holds only active tenants, but the subdomain rule
/// names a handle without looking it up, so that branch checks the tenant exists and is active
/// before answering. Otherwise any made-up subdomain would come back as a handle.
/// </remarks>
internal sealed class TenantByHostEndpoint : EndpointWithoutRequest<TenantByHostResponse>
{
    private readonly ITenantDomainSource _domains;
    private readonly IQuerySession _session;

    public TenantByHostEndpoint(ITenantDomainSource domains, IQuerySession session)
    {
        _domains = domains;
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/tenants/by-host/{host}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var host = Route<string>("host");
        var map = await _domains.GetAsync(ct);
        var resolved = TenantResolutionMiddleware.Resolve(host, map);
        var slug = resolved.Slug;

        if (slug is not null && map.Find(host) is null)
        {
            slug = await _session.Query<Tenant>()
                .Where(t => t.Slug == slug && t.IsActive)
                .Select(t => t.Slug)
                .FirstOrDefaultAsync(ct);
        }

        if (slug is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new TenantByHostResponse(slug), ct);
    }
}
