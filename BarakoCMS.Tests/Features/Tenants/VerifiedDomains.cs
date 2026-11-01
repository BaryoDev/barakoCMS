using barakoCMS.Infrastructure.Multitenancy;
using Marten;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>Test setup: the verified claim rows that make a stored tenant's domains route.</summary>
internal static class VerifiedDomains
{
    public static void Stage(IDocumentSession session, string slug, IEnumerable<string> domains)
    {
        foreach (var domain in domains)
        {
            var row = TenantDomain.Claim(TenantDomainMap.Normalise(domain)!, slug);
            row.Status = TenantDomainStatus.Verified;
            row.VerifiedAt = DateTimeOffset.UtcNow;
            session.Store(row);
        }
    }

    /// <summary>
    /// Marks the domains verified when the tenant already claimed them through the API (a pending row,
    /// updated under its version), and inserts a verified row for one that has no claim yet.
    /// </summary>
    public static async Task PromoteAsync(IDocumentSession session, string slug, IEnumerable<string> domains, CancellationToken ct)
    {
        foreach (var domain in domains)
        {
            var host = TenantDomainMap.Normalise(domain)!;
            var row = await session.LoadAsync<TenantDomain>(host, ct);
            if (row is null)
            {
                Stage(session, slug, [host]);
                continue;
            }

            row.Status = TenantDomainStatus.Verified;
            row.VerifiedAt = DateTimeOffset.UtcNow;
            session.Update(row);
        }
    }
}
