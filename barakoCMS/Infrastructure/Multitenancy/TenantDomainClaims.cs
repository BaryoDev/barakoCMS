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

    /// <summary>The table a domain claim lives in, which a unique violation names.</summary>
    internal const string Table = "mt_doc_tenant_domains";

    /// <summary>
    /// A second insert of one host: Marten reports it as <see cref="JasperFx.DocumentAlreadyExistsException"/>
    /// or it comes through as Npgsql's unique violation (SQLSTATE 23505). Only a violation on the claims
    /// table counts. A duplicate tenant handle is also a 23505, and answering it as a domain clash
    /// would blame the wrong thing.
    /// </summary>
    internal static bool IsDuplicate(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" } pg)
            {
                return string.Equals(pg.TableName, Table, StringComparison.Ordinal);
            }

            if (current is JasperFx.DocumentAlreadyExistsException exists
                && exists.DocumentType?.ToString()?.Contains(nameof(TenantDomain), StringComparison.Ordinal) == true)
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
