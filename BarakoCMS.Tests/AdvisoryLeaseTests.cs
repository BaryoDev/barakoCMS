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
[Collection("Sequential")]
public class AdvisoryLeaseTests
{
    private readonly IntegrationTestFixture _fixture;

    public AdvisoryLeaseTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static long NewKey() => 8_242_026_900L + Random.Shared.Next(1, 90_000);

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
    /// in <c>ScheduledContentService.SweepHeldAsync</c>.
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

        (await sweeper.TrySweepAllTenantsAsync(DateTime.UtcNow, default)).Should().BeTrue();

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
}
