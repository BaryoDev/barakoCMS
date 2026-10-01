using System.Net;
using System.Text.Json;
using barakoCMS.Infrastructure.Sync;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// One run at a time fills a collection: a second one is refused with 409, not left to fail on the
/// entry stream both would create.
/// </summary>
/// <remarks>
/// No test here races two runs and hopes they overlap. The overlap is built: either the lock is held
/// by the test, or the first run is stopped inside its fetch until the second has been answered.
/// Each refusal is followed by a run that succeeds once the lock is free, so a 409 cannot pass
/// because the route refuses everything.
/// </remarks>
[Collection("Sequential")]
public class CollectionSyncRunLockTests
{
    private const string TwoPackages = CollectionSyncTests.TwoPackages;

    private readonly IntegrationTestFixture _factory;

    /// <summary>The sync tests' own arrangement and stubbed source, so both classes share one host.</summary>
    private readonly CollectionSyncTests _syncs;

    public CollectionSyncRunLockTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _syncs = new CollectionSyncTests(factory);
    }

    [Fact]
    public async Task A_manual_run_is_refused_with_409_while_the_sweep_holds_the_collection()
    {
        var setup = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        await using (var held = await HoldAsync(setup))
        {
            held.Should().NotBeNull("the control: nothing else is filling this collection");

            var refused = await PostRunAsync(setup);
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "got {0}", body);
            body.Should().Contain(setup.Type, "the answer names the collection that is busy");

            (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty("a refused run writes nothing");
            (await _syncs.GetSyncAsync(setup)).GetProperty("lastRunAt").ValueKind.Should().Be(
                JsonValueKind.Null, "and it does not count as a run");
        }

        (await _syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2, "the lock is free again");
    }

    /// <remarks>
    /// The first run is stopped inside its fetch, which is after it took the lock, so the second
    /// request arrives while a run is in flight every time.
    /// </remarks>
    [Fact]
    public async Task A_second_manual_run_is_refused_with_409_while_the_first_is_in_flight()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;

        var setup = await _syncs.ArrangeAsync(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }

            return (HttpStatusCode.OK, TwoPackages);
        });

        var first = PostRunAsync(setup);

        try
        {
            (await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(60)), TestContext.Current.CancellationToken))
                .Should().BeTrue("the first run has to reach its source for the second to overlap it");

            var second = await PostRunAsync(setup);

            second.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "got {0}", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
        }

        var finished = await first;
        var body = await finished.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        finished.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(2);

        Volatile.Read(ref calls).Should().Be(1, "the refused run never reached the source");
        (await _syncs.EntriesAsync(setup.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// The sync has never run, so it is due. The first sweep has to leave it alone and the second
    /// has to run it, or the first proves only that this sweep runs nothing.
    /// </remarks>
    [Fact]
    public async Task The_sweep_leaves_a_due_sync_for_later_while_a_manual_run_holds_the_collection()
    {
        var calls = 0;
        var setup = await _syncs.ArrangeAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return (HttpStatusCode.OK, TwoPackages);
        });

        var sweeper = ActivatorUtilities.CreateInstance<CollectionSyncService>(_syncs.Host.Services);

        await using (var held = await HoldAsync(setup))
        {
            held.Should().NotBeNull("the control: nothing else is filling this collection");

            await sweeper.SweepTenantAsync(null, DateTime.UtcNow, 100, TestContext.Current.CancellationToken);

            Volatile.Read(ref calls).Should().Be(0, "the sweep must not fetch for a collection another run is filling");
            (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty();
            (await _syncs.GetSyncAsync(setup)).GetProperty("lastRunAt").ValueKind.Should().Be(
                JsonValueKind.Null, "the sync stays due, so a later tick runs it");
        }

        await sweeper.SweepTenantAsync(null, DateTime.UtcNow, 100, TestContext.Current.CancellationToken);

        Volatile.Read(ref calls).Should().Be(1, "the lock is free, so the sweep runs the sync it left");
        (await _syncs.EntriesAsync(setup.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// What a process dying mid-run looks like to Postgres: the backend holding the lock goes away
    /// without ever unlocking. The lock is session scoped, so it goes with it.
    /// </remarks>
    [Fact]
    public async Task A_lock_whose_holder_lost_its_connection_is_free_again()
    {
        var setup = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var ct = TestContext.Current.CancellationToken;

        await using var holder = store.Storage.Database.CreateConnection();
        await holder.OpenAsync(ct);

        int pid;
        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "select pg_advisory_lock(hashtextextended(@name, 0)), pg_backend_pid()";
            take.Parameters.AddWithValue("name", CollectionSyncLock.NameFor(DefaultTenantId(), setup.Type));

            await using var reader = await take.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).Should().BeTrue();
            pid = reader.GetInt32(1);
        }

        (await PostRunAsync(setup)).StatusCode.Should().Be(
            HttpStatusCode.Conflict, "the control: the holder's connection is still up");

        await using (var killer = store.Storage.Database.CreateConnection())
        {
            await killer.OpenAsync(ct);

            await using (var kill = killer.CreateCommand())
            {
                kill.CommandText = "select pg_terminate_backend(@pid)";
                kill.Parameters.AddWithValue("pid", pid);
                ((bool?)await kill.ExecuteScalarAsync(ct)).Should().BeTrue();
            }

            // Terminating is a signal, so the backend is gone a moment later rather than at once.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                await using var alive = killer.CreateCommand();
                alive.CommandText = "select count(*) from pg_stat_activity where pid = @pid";
                alive.Parameters.AddWithValue("pid", pid);

                if ((long)(await alive.ExecuteScalarAsync(ct))! == 0) break;

                (DateTime.UtcNow < deadline).Should().BeTrue("the terminated backend should be gone within 30 seconds");
                await Task.Delay(100, ct);
            }
        }

        (await _syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(
            2, "nothing unlocked it, the lost connection did");
    }

    [Fact]
    public void The_lock_is_one_per_tenant_and_collection_whatever_the_case_of_the_type()
    {
        CollectionSyncLock.NameFor("acme", "Package").Should().Be(CollectionSyncLock.NameFor("acme", "package"),
            "an entry's id lower-cases the type, so both spellings write the same entries");

        CollectionSyncLock.NameFor("acme", "package").Should().NotBe(CollectionSyncLock.NameFor("other", "package"),
            "one tenant's run must not refuse another tenant's");

        CollectionSyncLock.NameFor("acme", "package").Should().NotBe(CollectionSyncLock.NameFor("acme", "release"));
    }

    private async Task<HttpResponseMessage> PostRunAsync(CollectionSyncTests.Setup setup) =>
        await (await _syncs.AdminAsync()).PostAsync(
            $"/api/collection-syncs/{setup.Slug}/run", null, TestContext.Current.CancellationToken);

    /// <summary>Takes the lock a run takes, the way the sweep and the run route do.</summary>
    private Task<CollectionSyncLock?> HoldAsync(CollectionSyncTests.Setup setup) =>
        CollectionSyncLock.TryAcquireAsync(
            _factory.Services.GetRequiredService<IDocumentStore>(),
            DefaultTenantId(), setup.Type, TestContext.Current.CancellationToken);

    /// <summary>The tenant id a session in the default partition reports, which is what a run locks on.</summary>
    private string DefaultTenantId()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDocumentSession>().TenantId;
    }
}
