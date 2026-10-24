using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A leader has to notice when it stopped being one (#856). Postgres frees a session lock when its
/// backend dies and tells nobody.
/// </summary>
[Collection("Integration1")]
public class AdvisoryLeaseTests
{
    private readonly IntegrationTestFixture _fixture;

    public AdvisoryLeaseTests(IntegrationTestFixture fixture) => _fixture = fixture;

    // Far from the 8_242_026_xxx production keys and the migration ledger's.
    private static long NewKey() => 7_100_000_000L + Random.Shared.Next(1, 1_000_000);

    private static async Task TerminateHolderAsync(IDocumentStore store, long key, CancellationToken ct)
    {
        await using var admin = store.Storage.Database.CreateConnection();
        await admin.OpenAsync(ct);
        await using var kill = admin.CreateCommand();
        kill.CommandText =
            "select count(*) from (select pg_terminate_backend(pid) from pg_locks "
            + "where locktype = 'advisory' and granted "
            + "and ((classid::bigint << 32) | objid::bigint) = @key) t";
        kill.Parameters.AddWithValue("key", key);
        Convert.ToInt32(await kill.ExecuteScalarAsync(ct)).Should().Be(1, "one backend held the lock");
    }

    /// <summary>
    /// Revert to see it red: make <c>AdvisoryLease.IsHeldAsync</c> return true without the pg_locks query.
    /// </summary>
    [Fact]
    public async Task A_lease_reports_it_is_no_longer_held_once_its_backend_is_terminated()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var key = NewKey();

        var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync();
        await using var lease = await AdvisoryLease.TryAcquireAsync(connection, key, default);

        lease.Should().NotBeNull();
        (await lease!.IsHeldAsync(default)).Should().BeTrue("the lock was just taken");
        lease.PoolerSuspected.Should().BeFalse("the test connects directly");

        await TerminateHolderAsync(store, key, default);

        (await lease.IsHeldAsync(default)).Should().BeFalse("the backend that owned the lock is gone");
    }

    /// <summary>
    /// Revert to see it red: delete the <c>lease.IsHeldAsync</c> check at the top of the tenant loop
    /// in <c>ScheduledContentService.SweepHeldAsync</c> (the tenant stays Draft check), or change its
    /// <c>return false</c> to <c>return true</c> (the return value check).
    /// </summary>
    [Fact]
    public async Task A_scheduled_sweep_that_loses_its_lock_stops_before_the_next_tenant()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var slug = "lease-" + Guid.NewGuid().ToString("N")[..8];
        var defaultId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var due = DateTime.UtcNow.AddMinutes(-5);

        await using (var session = store.LightweightSession())
        {
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            session.Store(new Content
            {
                Id = defaultId, ContentType = "lease-probe", Status = ContentStatus.Draft, ScheduledPublishAt = due,
            });
            await session.SaveChangesAsync();
        }

        await using (var session = store.LightweightSession(slug))
        {
            session.Store(new Content
            {
                Id = tenantId, ContentType = "lease-probe", Status = ContentStatus.Draft, ScheduledPublishAt = due,
            });
            await session.SaveChangesAsync();
        }

        // The default partition is swept first and holds a due item, so the hook fires there, once
        // that tenant's batch is loaded and before the tenant after it is visited.
        var sweeper = new ScheduledContentService(store, logger)
        {
            AfterLoad = ct => TerminateHolderAsync(store, 8_242_026_001L, ct),
        };

        (await sweeper.TrySweepAllTenantsAsync(DateTime.UtcNow, default)).Should().BeFalse(
            "a sweep that lost its lock partway did not complete and must not report it did");

        await using var query = store.QuerySession(slug);
        var left = await query.LoadAsync<Content>(tenantId);
        left.Should().NotBeNull();
        left!.Status.Should().Be(ContentStatus.Draft, "a sweep that lost leadership must not carry on into the next tenant");
    }

    [Fact]
    public void Refusing_to_run_behind_a_pooler_is_off_unless_configured()
    {
        AdvisoryLease.RefuseBehindPooler(null).Should().BeFalse();
        AdvisoryLease.RefuseBehindPooler(new ConfigurationBuilder().Build()).Should().BeFalse();

        var on = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdvisoryLease.RefuseBehindPoolerKey] = "true" })
            .Build();
        AdvisoryLease.RefuseBehindPooler(on).Should().BeTrue();
        AdvisoryLease.PoolerMessage(8_242_026_003L).Should().Contain("transaction mode").And.Contain("8242026003");
    }

    private static async Task<bool> KeyIsFreeAsync(IDocumentStore store, long key)
    {
        await using var probe = store.Storage.Database.CreateConnection();
        await probe.OpenAsync();
        await using var take = probe.CreateCommand();
        take.CommandText = "select pg_try_advisory_lock(@key)";
        take.Parameters.AddWithValue("key", key);
        var got = (bool?)await take.ExecuteScalarAsync() ?? false;
        if (got)
        {
            await using var release = probe.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", key);
            await release.ExecuteScalarAsync();
        }

        return got;
    }

    /// <summary>
    /// Revert to see it red: remove the <c>catch { await connection.DisposeAsync(); throw; }</c> in
    /// <c>AdvisoryLease.AcquireAsync</c>.
    /// </summary>
    [Fact]
    public async Task A_throw_after_the_lock_is_taken_does_not_leave_it_held()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var key = NewKey();

        AdvisoryLease.AfterLockTaken = _ => throw new InvalidOperationException("boom");
        try
        {
            var act = () => AdvisoryLease.AcquireAsync(store, key, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            AdvisoryLease.AfterLockTaken = null;
        }

        (await KeyIsFreeAsync(store, key)).Should().BeTrue("the connection that took the lock was disposed");
    }

    private sealed class KillingDeclaration(IDocumentStore store, long key, List<string> visited) : barakoCMS.Infrastructure.Retention.RetentionDeclaration
    {
        public override string Name => "probe";

        public override Task<bool> HasRowsAsync(IQuerySession session, CancellationToken ct) => Task.FromResult(true);

        public override async Task<int> SweepAsync(
            IDocumentSession session, string tenantId, DateTimeOffset nowUtc, CancellationToken ct)
        {
            visited.Add(tenantId);
            if (visited.Count == 1)
            {
                await TerminateHolderAsync(store, key, ct);
            }

            return 0;
        }
    }

    /// <summary>
    /// Revert to see it red: delete the <c>lease.IsHeldAsync</c> check in
    /// <c>RetentionSweeper.TrySweepAllTenantsAsync</c> (two partitions visited), or the
    /// <c>held = false</c> beside it (LockHeld stays true).
    /// </summary>
    [Fact]
    public async Task A_retention_sweep_that_loses_its_lock_stops_and_reports_the_lock_not_held()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var key = NewKey();
        var visited = new List<string>();
        var config = new ConfigurationBuilder().Build();

        var result = await barakoCMS.Infrastructure.Retention.RetentionSweeper.TrySweepAllTenantsAsync(
            store, config, key, "select 'lease-a' union all select 'lease-b'",
            [new KillingDeclaration(store, key, visited)], DateTimeOffset.UtcNow,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "probe", default);

        visited.Should().HaveCount(1, "the second partition must not be visited after the lock was lost");
        result.LockHeld.Should().BeFalse("the pass was aborted");
    }

    /// <summary>
    /// Revert to see it red: delete the <c>if (AdvisoryLease.RefuseBehindPooler(...)) return ...</c>
    /// block in <c>RetentionSweeper</c> (declaration is visited) or in <c>ScheduledContentService.TrySweepAllTenantsAsync</c>
    /// (second assertion).
    /// </summary>
    [Fact]
    public async Task With_the_flag_on_a_suspected_pooler_makes_the_sweeps_decline()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();
        var on = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdvisoryLease.RefuseBehindPoolerKey] = "true" })
            .Build();
        var key = NewKey();
        var visited = new List<string>();

        AdvisoryLease.ForcePoolerSuspected = true;
        try
        {
            var result = await barakoCMS.Infrastructure.Retention.RetentionSweeper.TrySweepAllTenantsAsync(
                store, on, key, "select 'lease-a'",
                [new KillingDeclaration(store, key, visited)], DateTimeOffset.UtcNow,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "probe", default);

            result.LockHeld.Should().BeFalse();
            visited.Should().BeEmpty("nothing is swept behind a pooler when the flag is on");

            var scheduled = new ScheduledContentService(store, logger, on);
            (await scheduled.TrySweepAllTenantsAsync(DateTime.UtcNow, default)).Should().BeFalse();
        }
        finally
        {
            AdvisoryLease.ForcePoolerSuspected = null;
        }

        (await KeyIsFreeAsync(store, key)).Should().BeTrue("a declined sweep releases its lock");
    }

    /// <summary>
    /// Revert to see it red: delete the <c>throw new InvalidOperationException(AdvisoryLease.PoolerMessage(Key))</c>
    /// block in <c>SchemaApplyLock.RunAsync</c>.
    /// </summary>
    [Fact]
    public async Task With_the_flag_on_a_suspected_pooler_the_schema_lock_refuses_to_apply()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var on = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdvisoryLease.RefuseBehindPoolerKey] = "true" })
            .Build();

        var ran = false;
        AdvisoryLease.ForcePoolerSuspected = true;
        try
        {
            var act = () => SchemaApplyLock.RunAsync(
                store, () => { ran = true; return Task.CompletedTask; },
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), on, default);

            var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
            thrown.Which.Message.Should().Contain("transaction mode");
        }
        finally
        {
            AdvisoryLease.ForcePoolerSuspected = null;
        }

        ran.Should().BeFalse("the apply must not run behind a pooler when refused");
        (await KeyIsFreeAsync(store, SchemaApplyLock.Key)).Should().BeTrue("the refused host released the schema lock");
    }
}
