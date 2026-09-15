using JasperFx;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The tenant partitions a background pass visits.
/// </summary>
/// <remarks>
/// Two sources, chosen by whether Postgres enforces the tenant filter (#877).
///
/// With enforcement off, the rows themselves, through the <c>select distinct tenant_id</c> the
/// caller names. That cannot miss a partition, including one written under an <c>X-Tenant</c>
/// header naming a slug that has no <see cref="Tenant"/> document, which nothing refuses.
///
/// With enforcement on, that query cannot work. The policy compares <c>tenant_id</c> with
/// <c>app.tenant_id</c>, a bare connection never sets it, and <c>FORCE</c> binds the owner too, so
/// it errors on a fresh connection and sees a single tenant on a reused one. The registry is read
/// instead: every tenant, active or not, because a run queued before a tenant was deactivated still
/// has to execute, plus the default partition. <see cref="Tenant"/> is single tenanted, so reading
/// it needs no tenant. It is read on every call, so a tenant created after startup is visited on the
/// next pass. The caller runs its own filter inside a session opened for each partition, which does
/// set <c>app.tenant_id</c>.
///
/// What enforcement costs here: one query per registered tenant per pass rather than one query in
/// total, and a partition holding rows with no registry entry is not reached.
/// </remarks>
internal static class TenantPartitions
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        IDocumentStore store, IConfiguration configuration, string distinctTenantIdsSql, CancellationToken ct)
    {
        return configuration.GetValue(DatabaseTenancy.EnabledKey, false)
            ? await FromRegistryAsync(store, ct)
            : await FromRowsAsync(store, distinctTenantIdsSql, ct);
    }

    private static async Task<IReadOnlyList<string>> FromRegistryAsync(IDocumentStore store, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var slugs = await session.Query<Tenant>().Select(t => t.Slug).ToListAsync(ct);

        return slugs
            .Prepend(StorageConstants.DefaultTenantId)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> FromRowsAsync(
        IDocumentStore store, string distinctTenantIdsSql, CancellationToken ct)
    {
        var partitions = new List<string>();

        await using var conn = store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = distinctTenantIdsSql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }
}
