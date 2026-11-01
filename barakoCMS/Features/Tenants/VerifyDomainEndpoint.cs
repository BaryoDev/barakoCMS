using barakoCMS.Features.Accounts;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants;

/// <summary>Where a domain's DNS check stands after one run.</summary>
internal sealed class VerifyDomainResponse
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>Pending until both records are found, then Verified.</summary>
    public TenantDomainStatus Status { get; set; }

    /// <summary>The domain is a CNAME to <see cref="CnameTarget"/>.</summary>
    public bool CnameFound { get; set; }

    /// <summary>A TXT record at <see cref="TxtName"/> holds the value the tenant response shows.</summary>
    public bool TxtFound { get; set; }

    /// <summary>The lookups ran out of time, so retry: the records were not judged.</summary>
    public bool TimedOut { get; set; }

    /// <summary>The host the domain has to be a CNAME to.</summary>
    public string CnameTarget { get; set; } = string.Empty;

    public string TxtName { get; set; } = string.Empty;

    public DateTimeOffset? VerifiedAt { get; set; }
}

/// <summary>
/// POST /api/tenants/{handle}/domains/{domain}/verify: runs the DNS check for a pending domain and
/// moves it to verified when both records are there.
/// </summary>
/// <remarks>
/// Gated like the other tenant domain writes. A domain that is not the tenant's, or a tenant outside
/// the caller's account, answers 404. It never replays a stored answer: the result is what DNS says
/// now, and a replayed "pending" after the record was fixed would send the administrator in circles.
/// A failed check is a 200 that says what was missing, because the check itself worked.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class VerifyDomainEndpoint(
    IDocumentSession session,
    ITenantDomainSource domains,
    IDomainDnsChecker dns,
    IConfiguration configuration) : EndpointWithoutRequest<VerifyDomainResponse>
{
    /// <summary>The 409 text when the claim moved while the DNS check ran.</summary>
    public const string ClaimChangedMessage = "This domain claim changed while it was being checked. Check the tenant's domains and verify again.";

    public override void Configure()
    {
        Post("/api/tenants/{handle}/domains/{domain}/verify");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
        Definition.Metadata(new AccountScopedManagement());
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.DomainVerifyPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // A name that cannot be a stored domain is a bad request, answered before any lookup.
        if (TenantDomainLookup.BareHost(Route<string>("domain")) is null)
        {
            ThrowError("The domain in the route is not a valid domain name.", 400);
        }

        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is null) { await Send.ForbiddenAsync(ct); return; }

        var handle = Route<string>("handle")?.ToLowerInvariant();
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle, ct);
        if (tenant is null || !reach.Covers(tenant)) { await Send.NotFoundAsync(ct); return; }

        var host = TenantDomainMap.Normalise(Route<string>("domain"));
        var row = host is null ? null : await session.LoadAsync<TenantDomain>(host, ct);
        if (row is null || !string.Equals(row.TenantSlug, tenant.Slug, StringComparison.Ordinal))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var target = TenantDomains.DeploymentHost(configuration);
        if (target is null)
        {
            ThrowError($"{TenantDomains.DeploymentHostKey} is not set, so there is no host to point a CNAME at.", 409);
        }

        var response = new VerifyDomainResponse
        {
            Domain = row.Id,
            Status = row.Status,
            CnameTarget = target,
            TxtName = row.TxtName(),
            VerifiedAt = row.VerifiedAt,
        };

        if (row.Status == TenantDomainStatus.Verified)
        {
            response.CnameFound = response.TxtFound = true;
            await Send.OkAsync(response, ct);
            return;
        }

        var result = await dns.CheckAsync(row.Id, target, row.TxtName(), row.TxtValue(), ct);
        response.CnameFound = result.CnameOk;
        response.TxtFound = result.TxtOk;
        response.TimedOut = result.TimedOut;

        if (result.Passed)
        {
            // The DNS wait is long enough for the claim to change under this request: the tenant drops
            // the domain, or drops it and another tenant claims it. Writing the row loaded before the
            // wait would bring it back, or overwrite the new claim. So read it again, and write it
            // under the row's version (UseOptimisticConcurrency on TenantDomain), which refuses the
            // write if anything moved between this read and the save.
            var current = await session.LoadAsync<TenantDomain>(row.Id, ct);
            var listed = await session.Query<Tenant>().AnyAsync(x => x.Slug == tenant.Slug && x.Domains.Contains(row.Id), ct);
            if (current is null || !listed
                || !string.Equals(current.TenantSlug, tenant.Slug, StringComparison.Ordinal)
                || !string.Equals(current.Token, row.Token, StringComparison.Ordinal))
            {
                ThrowError(ClaimChangedMessage, 409);
            }

            TenantDomainClaims.Verify(current);
            session.Update(current);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("NonExistent"))
            {
                session.EjectAllPendingChanges();
                ThrowError(ClaimChangedMessage, 409);
            }

            domains.Invalidate();
            response.Status = current.Status;
            response.VerifiedAt = current.VerifiedAt;
        }

        await Send.OkAsync(response, ct);
    }
}
