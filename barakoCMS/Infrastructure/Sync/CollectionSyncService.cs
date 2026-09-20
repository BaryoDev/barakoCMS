using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>
/// Runs every collection sync that is due, across every tenant.
/// </summary>
/// <remarks>
/// The same shape as <see cref="Services.ScheduledContentService"/>, deliberately: a
/// <see cref="BackgroundService"/> on a fixed tick, a Postgres advisory lock so one instance sweeps,
/// a session per tenant, and a bound on how much one tick will do. Two instances both sweeping would
/// mean every source called twice as often as configured and two writers on the same entry.
///
/// Each sync is run inside a tenant-bound scope rather than against a session opened off the store,
/// because the work needs the request composer, the connector fetcher and the content writer, and
/// all three are scoped services that have to see the same session and the same partition.
/// </remarks>
internal sealed class CollectionSyncService(
    IServiceProvider services,
    IDocumentStore store,
    ILogger<CollectionSyncService> logger) : BackgroundService
{
    /// <summary>
    /// How often due syncs are looked for, which is not how often any sync runs.
    /// </summary>
    /// <remarks>
    /// A sync's own <c>IntervalMinutes</c> decides that, and the shortest one allowed is five
    /// minutes. The tick only has to be finer than the shortest interval for a sync to run close to
    /// when it is due.
    /// </remarks>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A Postgres advisory lock key, arbitrary but fixed, identifying this sweep across instances.
    /// </summary>
    /// <remarks>
    /// Its own key, not shared with the scheduled content sweep. Sharing one would make the two
    /// exclude each other, so a long sync would hold up scheduled publishing for no reason.
    /// </remarks>
    private const long SweepLockKey = 8_242_026_794L;

    /// <summary>How many syncs one tick will run before leaving the rest to the next one.</summary>
    /// <remarks>
    /// Each one is an outbound call and up to <c>MaxEntries</c> writes, so an instance that has just
    /// started, with every sync in every tenant due at once, must not try to do all of them in one
    /// tick while holding the lock.
    /// </remarks>
    public const int MaxSyncsPerSweep = 20;

    /// <summary>How many sync definitions one tenant's query loads.</summary>
    private const int MaxSyncsPerTenant = 200;

    /// <summary>Whether the scheduled sweep runs at all.</summary>
    /// <remarks>
    /// True by default and by omission, so a deployment that upgrades and configures a sync gets the
    /// schedule it configured. Set false where the schedule is not wanted: a staging copy of a
    /// production database would otherwise call every one of production's providers on production's
    /// interval. A sync can still be run from the API with this off, which is the difference between
    /// turning the schedule off and disabling the sync.
    /// </remarks>
    public const string EnabledKey = "CollectionSyncs:Enabled";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue(EnabledKey, true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Collection sync service started. Sweep interval: {Interval}", SweepInterval);

        // Let the app, the Marten schema and the projection daemon warm up before the first sweep,
        // the same delay the scheduled content sweep takes for the same reason.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySweepAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during collection sync sweep");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }

        logger.LogInformation("Collection sync service stopped");
    }

    /// <summary>Runs the due syncs in every partition, if this instance can take the lock.</summary>
    /// <returns>True if this instance swept, false if another one was already sweeping.</returns>
    public async Task<bool> TrySweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        // The connection object, not a new one built from its ConnectionString: Npgsql redacts the
        // password out of ConnectionString unless Persist Security Info is set.
        await using var lockConnection = store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", SweepLockKey);
            var acquired = (bool?)await acquire.ExecuteScalarAsync(ct) ?? false;
            if (!acquired)
            {
                logger.LogDebug("Another instance is running collection syncs; skipping this tick.");
                return false;
            }
        }

        try
        {
            await SweepHeldAsync(nowUtc, ct);
            return true;
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SweepLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task SweepHeldAsync(DateTime nowUtc, CancellationToken ct)
    {
        // null is the default partition, where a single-deployment site keeps its content; named
        // slugs are the path-based tenants.
        var partitions = new List<string?> { null };
        await using (var query = store.QuerySession())
        {
            var tenants = await query.Query<Tenant>().Where(t => t.IsActive).ToListAsync(ct);
            partitions.AddRange(tenants.Select(t => (string?)t.Slug));
        }

        var budget = MaxSyncsPerSweep;

        foreach (var slug in partitions.Distinct())
        {
            if (budget <= 0) break;

            budget -= await SweepTenantAsync(slug, nowUtc, budget, ct);
        }
    }

    /// <summary>Runs up to <paramref name="budget"/> due syncs in one partition.</summary>
    /// <returns>How many were run.</returns>
    public async Task<int> SweepTenantAsync(string? martenTenantId, DateTime nowUtc, int budget, CancellationToken ct)
    {
        using var scope = services.CreateScopeForTenant(martenTenantId);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var due = (await session.Query<CollectionSync>()
                .Where(s => s.Enabled)
                .OrderBy(s => s.Slug)
                .Take(MaxSyncsPerTenant)
                .ToListAsync(ct))
            .Where(s => s.IsDue(nowUtc))
            .Take(budget)
            .ToList();

        if (due.Count == 0) return 0;

        var runner = scope.ServiceProvider.GetRequiredService<ICollectionSyncRunner>();
        var run = 0;

        foreach (var sync in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await runner.RunAsync(sync, ct);
                run++;

                logger.LogInformation(
                    "Collection sync {Slug} for tenant {Tenant}: {Created} created, {Updated} updated, "
                  + "{Unchanged} unchanged, {Skipped} skipped",
                    sync.Slug, martenTenantId ?? "(default)",
                    outcome.Created, outcome.Updated, outcome.Unchanged, outcome.Skipped);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One sync that throws must not stop the others in the same tenant. The runner
                // already records an expected failure on the sync itself; reaching here means a
                // defect rather than a provider being down, so it is logged with the exception.
                logger.LogError(ex, "Collection sync {Slug} threw", sync.Slug);

                // Nothing of this sync's is left staged, or the next one's save would carry it.
                session.EjectAllPendingChanges();
                run++;
            }
        }

        return run;
    }
}
