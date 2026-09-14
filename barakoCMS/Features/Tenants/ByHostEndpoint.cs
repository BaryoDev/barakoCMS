using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;

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
/// It reads the same cached map request resolution reads, so a lookup costs no query and can never
/// disagree with how the API itself would route that host. Only active tenants resolve, for the same
/// reason.
/// </remarks>
internal sealed class TenantByHostEndpoint : EndpointWithoutRequest<TenantByHostResponse>
{
    private readonly ITenantDomainSource _domains;

    public TenantByHostEndpoint(ITenantDomainSource domains) => _domains = domains;

    public override void Configure()
    {
        Get("/api/tenants/by-host/{host}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var map = await _domains.GetAsync(ct);
        var slug = map.Find(Route<string>("host"));

        if (slug is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new TenantByHostResponse(slug), ct);
    }
}
