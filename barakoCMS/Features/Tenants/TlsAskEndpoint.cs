using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using FastEndpoints;

namespace barakoCMS.Features.Tenants;

internal sealed record TlsAskResponse(bool Allowed);

/// <summary>
/// GET /api/tenants/tls-ask?domain={host}: whether a reverse proxy may get a certificate for a host.
/// </summary>
/// <remarks>
/// The <c>ask</c> endpoint of Caddy's on-demand TLS, which sends the server name as <c>domain</c> and
/// issues a certificate only on a 200. It answers 200 for a domain an active tenant holds and 404 for
/// anything else, and says nothing about which tenant: <c>by-host</c> already answers that for a
/// mapped domain, and this one has no reason to.
///
/// Anonymous, because the proxy asks during a TLS handshake with nobody signed in. Only the cached
/// domain map is read, so a made-up name costs no query, and the <c>tls-ask</c> rate limit holds a
/// flood of them to 60 a minute per address. The leading-subdomain rule is not consulted: a tenant
/// reached as a subdomain of the deployment's own domain is covered by that domain's certificate,
/// and answering yes for any subdomain would let anyone have certificates issued for names nobody
/// registered.
/// </remarks>
internal sealed class TlsAskEndpoint(ITenantDomainSource domains) : EndpointWithoutRequest<TlsAskResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/tls-ask");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.TlsAskPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var host = TenantDomainLookup.BareHost(Query<string>("domain", isRequired: false));
        if (host is null || (await domains.GetAsync(ct)).Find(host) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new TlsAskResponse(true), ct);
    }
}
