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
}
