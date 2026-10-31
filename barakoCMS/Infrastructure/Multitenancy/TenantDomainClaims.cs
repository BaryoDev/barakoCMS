using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>Stages the <see cref="TenantDomain"/> rows that go with a change to a tenant's domain list.</summary>
internal static class TenantDomainClaims
{
    /// <summary>
    /// Brings a tenant's rows in line with the domains it now lists, in the caller's unit of work.
    /// A new domain is inserted pending, or verified when <paramref name="trusted"/> is true. A domain
    /// the tenant no longer lists loses its row. True when anything was staged.
    /// </summary>
    /// <remarks>
    /// The insert is what makes a domain belong to one tenant: it fails at <c>SaveChangesAsync</c>
    /// with <see cref="DocumentAlreadyExistsException"/> when another tenant's row holds the host, and
    /// the tenant document goes back with it. <paramref name="trusted"/> is for a seed file, which an
    /// operator wrote into the deployment's own configuration.
    /// </remarks>
    public static async Task<bool> StageAsync(
        IDocumentSession session, string slug, IReadOnlyCollection<string> domains, bool trusted, CancellationToken ct)
    {
        var held = await session.Query<TenantDomain>().Where(d => d.TenantSlug == slug).ToListAsync(ct);

        var changed = false;
        foreach (var row in held.Where(row => !domains.Contains(row.Id)))
        {
            session.Delete(row);
            changed = true;
        }

        foreach (var host in domains)
        {
            var row = held.FirstOrDefault(r => r.Id == host);
            if (row is null)
            {
                var claim = TenantDomain.Claim(host, slug);
                if (trusted)
                    Verify(claim);
                session.Insert(claim);
                changed = true;
            }
            else if (trusted && row.Status != TenantDomainStatus.Verified)
            {
                Verify(row);
                session.Store(row);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Saves the unit of work. False when a domain row already existed for another tenant.</summary>
    public static async Task<bool> TrySaveAsync(IDocumentSession session, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsDuplicate(ex))
        {
            session.EjectAllPendingChanges();
            return false;
        }
    }

    /// <summary>A second insert of one id, which Marten reports itself or wraps from Npgsql (SQLSTATE 23505).</summary>
    private static bool IsDuplicate(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JasperFx.DocumentAlreadyExistsException or Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }

    public static void Verify(TenantDomain row)
    {
        row.Status = TenantDomainStatus.Verified;
        row.VerifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The rows of these tenants, by tenant slug.</summary>
    public static async Task<ILookup<string, TenantDomain>> ForAsync(
        IQuerySession session, IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        var rows = await session.Query<TenantDomain>().Where(d => d.TenantSlug.IsOneOf(slugs.ToArray())).ToListAsync(ct);
        return rows.ToLookup(r => r.TenantSlug, StringComparer.OrdinalIgnoreCase);
    }
}
