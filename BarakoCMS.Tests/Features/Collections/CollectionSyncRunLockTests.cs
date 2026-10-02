using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Sync;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// One run at a time fills a collection: a second one waits a bounded time and is then refused with
/// 409, not left to fail on the entry stream both would create.
/// </summary>
/// <remarks>
/// No test here races two runs and hopes they overlap. The overlap is built: either the lock is held
/// by the test, or a run is stopped inside its fetch until the other side has been observed. Where a
/// test needs a run to be waiting, it reads that from <c>pg_locks</c> and does not sleep.
///
/// The wait is 300 milliseconds here, not the five seconds a deployment gets, so a test that
/// expects a refusal does not sit through the default. The one test about waiting raises it.
/// </remarks>
[Collection("Sequential")]
public class CollectionSyncRunLockTests : IDisposable
{
    private const string TwoPackages = CollectionSyncTests.TwoPackages;

    private readonly IntegrationTestFixture _factory;

    /// <summary>The sync tests' own arrangement and stubbed source, so both classes share one host.</summary>
    private readonly CollectionSyncTests _syncs;

    private readonly CollectionSyncRunOptions _options;
    private readonly TimeSpan _configuredWait;

    public CollectionSyncRunLockTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _syncs = new CollectionSyncTests(factory);
        _options = _syncs.Host.Services.GetRequiredService<CollectionSyncRunOptions>();
        _configuredWait = _options.LockWait;
        _options.LockWait = TimeSpan.FromMilliseconds(300);
    }

    public void Dispose() => _options.LockWait = _configuredWait;

    [Fact]
    public void A_deployment_waits_five_seconds_unless_it_says_otherwise_and_never_more_than_thirty()
    {
        _configuredWait.Should().Be(TimeSpan.FromSeconds(5), "the test host configures nothing");
        Configured(null).LockWait.Should().Be(TimeSpan.FromSeconds(5));
        Configured("").LockWait.Should().Be(TimeSpan.FromSeconds(5), "blank is unset");
        Configured("0").LockWait.Should().Be(TimeSpan.Zero);
        Configured("2.5").LockWait.Should().Be(TimeSpan.FromSeconds(2.5));
        Configured("600").LockWait.Should().Be(TimeSpan.FromSeconds(30));
        Configured("-1").LockWait.Should().Be(TimeSpan.Zero);
    }

    /// <remarks>
    /// The options are built while the services are registered, so this is a deployment that does
    /// not start, not one that starts and fails every run.
    /// </remarks>
    [Theory]
    [InlineData("abc")]
    [InlineData("5,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_wait_that_is_not_a_number_is_refused_naming_the_setting(string value)
    {
        var reading = () => Configured(value);

        reading.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{CollectionSyncRunOptions.LockWaitKey}*'{value}'*");
    }

    private static CollectionSyncRunOptions Configured(string? value) => CollectionSyncRunOptions.FromConfiguration(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(value is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [CollectionSyncRunOptions.LockWaitKey] = value })
            .Build());

    [Fact]
    public async Task A_manual_run_is_refused_with_409_and_Retry_After_when_the_collection_stays_locked_past_the_wait()
    {
        var setup = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        await using (var held = await HoldAsync(setup.Type))
        {
            held.Should().NotBeNull("the control: nothing else is filling this collection");

            var refused = await PostRunAsync(setup);
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "got {0}", body);
            body.Should().Contain(setup.Type, "the answer names the collection that is busy");
            refused.Headers.RetryAfter.Should().NotBeNull("a caller is told when to come back");
            refused.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1), "the wait, rounded up to a whole second");

            (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty("a refused run writes nothing");
            (await _syncs.GetSyncAsync(setup)).GetProperty("lastRunAt").ValueKind.Should().Be(
                JsonValueKind.Null, "and it does not count as a run");
        }

        (await _syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2, "the lock is free again");
    }

    /// <remarks>
    /// The lock is let go only after Postgres shows the run queued behind it, so a run that did not
    /// wait has already answered 409 by then, and one that arrived late cannot pass by finding the
    /// lock free.
    /// </remarks>
    [Fact]
    public async Task A_manual_run_waits_for_a_collection_that_is_freed_inside_the_wait_and_then_runs()
    {
        _options.LockWait = TimeSpan.FromSeconds(30);
        var setup = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        var answered = await RunWhileQueuedAsync(setup, async () =>
            (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty("it has not run while it waits"));
        var body = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        answered.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(2);
    }

    /// <remarks>
    /// The run read the sync, then queued for the lock. By the time it holds the lock the sync is
    /// gone, and a run that trusted its first read would fetch, write entries and save the sync back.
    /// </remarks>
    [Fact]
    public async Task A_manual_run_answers_404_when_the_sync_was_deleted_while_it_waited()
    {
        _options.LockWait = TimeSpan.FromSeconds(30);
        var source = new ParkedSource();
        var setup = await _syncs.ArrangeAsync(source.Answer);

        var answered = await RunWhileQueuedAsync(setup, async () =>
        {
            var deleted = await (await _syncs.AdminAsync()).DeleteAsync(
                $"/api/collection-syncs/{setup.Slug}", TestContext.Current.CancellationToken);
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        });

        answered.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "got {0}", await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        source.Calls.Should().Be(0, "a sync that no longer exists is not fetched");
        (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty();
    }

    /// <remarks>
    /// The lock the run holds is the old collection's, so it does not cover the one the sync now
    /// fills. It is refused without <c>Retry-After</c>, since nothing is busy: asking again works.
    /// </remarks>
    [Fact]
    public async Task A_manual_run_answers_409_when_the_sync_was_pointed_at_another_collection_while_it_waited()
    {
        _options.LockWait = TimeSpan.FromSeconds(30);
        var source = new ParkedSource();
        var setup = await _syncs.ArrangeAsync(source.Answer);
        var other = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), save: false);

        var answered = await RunWhileQueuedAsync(setup, () => RepointAsync(setup, other.Type));
        var body = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        answered.StatusCode.Should().Be(HttpStatusCode.Conflict, "got {0}", body);
        body.Should().Contain(other.Type, "the answer names where the sync points now");
        answered.Headers.RetryAfter.Should().BeNull("nothing is busy, so there is nothing to wait out");
        source.Calls.Should().Be(0, "nothing was fetched under a lock on the wrong collection");
        (await _syncs.EntriesAsync(other.Type)).Should().BeEmpty();

        (await _syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2, "asking again runs it");
        (await _syncs.EntriesAsync(other.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// The first run is stopped inside its fetch, which is after it took the lock, so the second
    /// request arrives while a run is in flight every time.
    /// </remarks>
    [Fact]
    public async Task A_second_manual_run_is_refused_with_409_while_the_first_stays_in_flight_past_the_wait()
    {
        var source = new ParkedSource { Parks = true };
        var setup = await _syncs.ArrangeAsync(source.Answer);

        var first = PostRunAsync(setup);

        try
        {
            (await source.EnteredAsync()).Should().BeTrue("the first run has to reach its source for the second to overlap it");

            var second = await PostRunAsync(setup);

            second.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "got {0}", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            source.Release();
        }

        var finished = await first;
        var body = await finished.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        finished.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(2);

        source.Calls.Should().Be(1, "the refused run never reached the source");
        (await _syncs.EntriesAsync(setup.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// The sync has never run, so it is due. The first sweep has to leave it alone and the second
    /// has to run it, or the first proves only that this sweep runs nothing.
    /// </remarks>
    [Fact]
    public async Task The_sweep_leaves_a_due_sync_for_later_while_a_manual_run_holds_the_collection()
    {
        var source = new ParkedSource();
        var setup = await _syncs.ArrangeAsync(source.Answer);

        await using (var held = await HoldAsync(setup.Type))
        {
            held.Should().NotBeNull("the control: nothing else is filling this collection");

            await SweepAsync();

            source.Calls.Should().Be(0, "the sweep must not fetch for a collection another run is filling");
            (await _syncs.EntriesAsync(setup.Type)).Should().BeEmpty();
            (await _syncs.GetSyncAsync(setup)).GetProperty("lastRunAt").ValueKind.Should().Be(
                JsonValueKind.Null, "the sync stays due, so a later tick runs it");
        }

        await SweepAsync();

        source.Calls.Should().Be(1, "the lock is free, so the sweep runs the sync it left");
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
        var ct = TestContext.Current.CancellationToken;

        await using var holder = Store.Storage.Database.CreateConnection();
        await holder.OpenAsync(ct);

        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "select pg_advisory_lock(hashtextextended(@name, 0))";
            take.Parameters.AddWithValue("name", CollectionSyncLock.NameFor(DefaultTenantId(), setup.Type));
            await take.ExecuteScalarAsync(ct);
        }

        (await PostRunAsync(setup)).StatusCode.Should().Be(
            HttpStatusCode.Conflict, "the control: the holder's connection is still up");

        await KillAsync((await LockBackendAsync(setup.Type, granted: true))!.Value);

        (await _syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(
            2, "nothing unlocked it, the lost connection did");
    }

    /// <remarks>
    /// The run has taken the lock and is inside its fetch when the lock's connection goes. It writes
    /// its entries all the same, and the unlock that then fails is not its outcome.
    /// </remarks>
    [Fact]
    public async Task A_manual_run_whose_lock_connection_is_lost_still_answers_what_it_did()
    {
        var source = new ParkedSource { Parks = true };
        var setup = await _syncs.ArrangeAsync(source.Answer);

        var running = PostRunAsync(setup);

        try
        {
            (await source.EnteredAsync()).Should().BeTrue("the run has to be past the lock and inside its fetch");

            var backend = await LockBackendAsync(setup.Type, granted: true);
            backend.Should().NotBeNull("the control: the run holds the lock on a backend of its own");

            await KillAsync(backend!.Value);
        }
        finally
        {
            source.Release();
        }

        var answered = await running;
        var body = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        answered.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(2);
        (await _syncs.EntriesAsync(setup.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// Two due syncs. The sweep is inside the fetch of the first when that sync's lock connection
    /// goes. The second being fetched is what shows the tick carried on.
    /// </remarks>
    [Fact]
    public async Task The_sweep_goes_on_to_the_next_sync_when_a_lock_connection_is_lost()
    {
        var (first, firstSource, _, secondSource) = await ArrangeTwoInSlugOrderAsync();
        firstSource.Parks = true;

        var sweep = SweepAsync();

        try
        {
            (await firstSource.EnteredAsync()).Should().BeTrue("the sweep has to be inside the first sync's fetch");
            secondSource.Calls.Should().Be(0, "the control: the sweep has not reached the second sync yet");

            var backend = await LockBackendAsync(first.Type, granted: true);
            backend.Should().NotBeNull("the control: the sweep holds the first sync's lock on a backend of its own");

            await KillAsync(backend!.Value);
        }
        finally
        {
            firstSource.Release();
        }

        await sweep;

        secondSource.Calls.Should().Be(1, "one sync's lost lock must not stop the syncs after it");
        (await _syncs.EntriesAsync(first.Type)).Should().HaveCount(2, "and the first sync still wrote what it fetched");
    }

    /// <remarks>
    /// The sweep reads its due list once, then is held inside the first sync. Meanwhile the second
    /// sync is run from the API and then edited. The sweep's copy of it says it is due and carries
    /// the old name, so a sweep that trusted that copy fetches it again and saves over the edit.
    /// </remarks>
    [Fact]
    public async Task The_sweep_does_not_rerun_or_overwrite_a_sync_that_ran_and_was_edited_while_it_was_busy()
    {
        var (_, firstSource, second, secondSource) = await ArrangeTwoInSlugOrderAsync();
        firstSource.Parks = true;

        var sweep = SweepAsync();

        try
        {
            (await firstSource.EnteredAsync()).Should().BeTrue("the sweep has to be inside the first sync's fetch");

            (await _syncs.RunAsync(second)).GetProperty("created").GetInt32().Should().Be(2);
            secondSource.Calls.Should().Be(1, "the control: the manual run fetched once");

            var edit = CollectionSyncTests.SyncBody(second);
            edit["name"] = "Edited while the sweep was busy";

            var saved = await (await _syncs.AdminAsync()).PutAsJsonAsync(
                $"/api/collection-syncs/{second.Slug}", edit, TestContext.Current.CancellationToken);
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            firstSource.Release();
        }

        await sweep;

        secondSource.Calls.Should().Be(1, "it ran a moment ago, so it is not due, whatever the sweep's older copy says");
        (await _syncs.GetSyncAsync(second)).GetProperty("name").GetString().Should().Be(
            "Edited while the sweep was busy", "the sweep must not save its older copy over the edit");
    }

    /// <remarks>
    /// Skipped quietly. A sweep that used its older copy without looking would fail on it and log a
    /// defect for what is an ordinary delete.
    /// </remarks>
    [Fact]
    public async Task The_sweep_skips_a_sync_that_was_deleted_while_it_was_busy_without_reporting_a_fault()
    {
        var (_, firstSource, second, secondSource) = await ArrangeTwoInSlugOrderAsync();
        firstSource.Parks = true;

        var log = new RecordingLogger();
        var sweep = SweepAsync(log);

        try
        {
            (await firstSource.EnteredAsync()).Should().BeTrue("the sweep has to be inside the first sync's fetch");

            var deleted = await (await _syncs.AdminAsync()).DeleteAsync(
                $"/api/collection-syncs/{second.Slug}", TestContext.Current.CancellationToken);
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            firstSource.Release();
        }

        await sweep;

        firstSource.Calls.Should().Be(1, "the control: the sweep went on past the first sync");
        secondSource.Calls.Should().Be(0);
        log.Errors.Where(e => e.Contains(second.Slug)).Should().BeEmpty("a deleted sync is not a fault");
    }

    /// <remarks>
    /// The sweep's lock is on the collection its older copy names. Running the sync as it is now
    /// would write to the new collection without holding that one's lock.
    /// </remarks>
    [Fact]
    public async Task The_sweep_leaves_a_sync_that_was_pointed_at_another_collection_while_it_was_busy_for_the_next_tick()
    {
        var (_, firstSource, second, secondSource) = await ArrangeTwoInSlugOrderAsync();
        var other = await _syncs.ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), save: false);
        firstSource.Parks = true;

        var sweep = SweepAsync();

        try
        {
            (await firstSource.EnteredAsync()).Should().BeTrue("the sweep has to be inside the first sync's fetch");
            await RepointAsync(second, other.Type);
        }
        finally
        {
            firstSource.Release();
        }

        await sweep;

        secondSource.Calls.Should().Be(0, "its lock was taken for the collection it no longer fills");
        (await _syncs.EntriesAsync(other.Type)).Should().BeEmpty();

        await SweepAsync();

        secondSource.Calls.Should().Be(1, "it is still due, and the next tick locks the right collection");
        (await _syncs.EntriesAsync(other.Type)).Should().HaveCount(2);
    }

    /// <remarks>
    /// A budget of one and two due syncs. The first cannot run, and a sweep that had already cut
    /// its list down to the budget would then have nothing else to look at.
    /// </remarks>
    [Fact]
    public async Task A_sync_the_sweep_leaves_for_later_does_not_use_up_the_budget_of_the_one_behind_it()
    {
        await SweepUntilNothingIsDueAsync();

        var (first, firstSource, second, secondSource) = await ArrangeTwoInSlugOrderAsync();
        (await DueSlugsAsync()).Should().Equal(first.Slug, second.Slug);

        await using (var held = await HoldAsync(first.Type))
        {
            held.Should().NotBeNull("the control: nothing else is filling the first sync's collection");

            (await SweepAsync(budget: 1)).Should().Be(1, "one sync ran");
        }

        firstSource.Calls.Should().Be(0, "the control: its collection was locked");
        secondSource.Calls.Should().Be(1, "the locked sync ahead of it took none of the budget");
        (await DueSlugsAsync()).Should().Equal(new[] { first.Slug }, "and the first is still due for a later tick");
    }

    /// <remarks>
    /// A budget of two and three due syncs. While the sweep is inside the first, the second is run
    /// from the API, so the sweep finds it no longer due once it holds its lock. The third must get
    /// the slot the second did not use.
    /// </remarks>
    [Fact]
    public async Task A_sync_that_is_no_longer_due_when_the_sweep_reaches_it_does_not_use_up_the_budget_either()
    {
        await SweepUntilNothingIsDueAsync();

        var ordered = await ArrangeInSlugOrderAsync(3);
        (await DueSlugsAsync()).Should().Equal(ordered.Select(o => o.Setup.Slug));

        ordered[0].Source.Parks = true;
        var sweep = SweepAsync(budget: 2);

        try
        {
            (await ordered[0].Source.EnteredAsync()).Should().BeTrue("the sweep has to be inside the first sync's fetch");

            (await _syncs.RunAsync(ordered[1].Setup)).GetProperty("created").GetInt32().Should().Be(2);
        }
        finally
        {
            ordered[0].Source.Release();
        }

        (await sweep).Should().Be(2, "the first and the third ran");

        ordered[0].Source.Calls.Should().Be(1);
        ordered[1].Source.Calls.Should().Be(1, "the control: only the run from the API fetched it");
        ordered[2].Source.Calls.Should().Be(1, "the sync that was skipped ahead of it took none of the budget");
        (await DueSlugsAsync()).Should().BeEmpty();
    }

    /// <remarks>
    /// A connection whose command timeout is shorter than the wait. The wait has to end as a
    /// refusal when the server's lock timeout fires, not as the client giving up on the command.
    /// </remarks>
    [Fact]
    public async Task A_wait_longer_than_the_connections_command_timeout_still_ends_as_a_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "timeout" + Guid.NewGuid().ToString("n")[..10];

        await using var impatient = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_factory.ConnectionString) { CommandTimeout = 1 }.ToString());

        await using var held = await HoldAsync(type);
        held.Should().NotBeNull("the control: nothing else holds this lock");

        var second = await CollectionSyncLock.TryAcquireAsync(
            impatient.CreateConnection, DefaultTenantId(), type, TimeSpan.FromSeconds(2), NullLogger.Instance, ct);

        second.Should().BeNull("the lock is held for the whole wait, which is a refusal and not an error");
    }

    /// <remarks>
    /// The nearest thing to the server granting the lock while the client fails: the connection
    /// already holds it, and taking it throws because the connection is already open. On a pooled
    /// data source, as a deployment has, since a pooled connection closed with a session lock keeps
    /// it until the pool reuses that connection.
    /// </remarks>
    [Fact]
    public async Task An_acquire_that_throws_on_a_connection_holding_the_lock_leaves_the_lock_free()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "throws" + Guid.NewGuid().ToString("n")[..10];
        var name = CollectionSyncLock.NameFor(DefaultTenantId(), type);

        await using var pool = PooledDataSource();
        var connection = pool.CreateConnection();
        await connection.OpenAsync(ct);

        await using (var take = connection.CreateCommand())
        {
            take.CommandText = "select pg_advisory_lock(hashtextextended(@name, 0))";
            take.Parameters.AddWithValue("name", name);
            await take.ExecuteScalarAsync(ct);
        }

        (await IsFreeAsync(name)).Should().BeFalse("the control: the connection holds the lock");

        var acquiring = () => CollectionSyncLock.TryAcquireAsync(
            () => connection, DefaultTenantId(), type, TimeSpan.Zero, NullLogger.Instance, ct);

        await acquiring.Should().ThrowAsync<InvalidOperationException>("the connection is already open");

        (await IsFreeAsync(name)).Should().BeTrue("the connection went back to the pool without the lock");
    }

    /// <remarks>
    /// The lock's connection is left in a failed transaction, where the unlock is refused like any
    /// other statement. On a pooled data source, for the reason above.
    /// </remarks>
    [Fact]
    public async Task A_lock_whose_unlock_fails_is_still_free_once_it_is_disposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "aborted" + Guid.NewGuid().ToString("n")[..10];
        var name = CollectionSyncLock.NameFor(DefaultTenantId(), type);

        await using var pool = PooledDataSource();
        NpgsqlConnection? connection = null;

        var held = await CollectionSyncLock.TryAcquireAsync(
            () => connection = pool.CreateConnection(), DefaultTenantId(), type, TimeSpan.Zero, NullLogger.Instance, ct);

        held.Should().NotBeNull();
        (await IsFreeAsync(name)).Should().BeFalse("the control: the lock is held while it is alive");

        await using (var begin = connection!.CreateCommand())
        {
            begin.CommandText = "begin";
            await begin.ExecuteNonQueryAsync(ct);
        }

        await using (var fail = connection.CreateCommand())
        {
            fail.CommandText = "select 1 / 0";
            var failing = () => fail.ExecuteScalarAsync(ct);
            await failing.Should().ThrowAsync<PostgresException>();
        }

        await held!.DisposeAsync();

        (await IsFreeAsync(name)).Should().BeTrue("the connection went back to the pool without the lock");
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

    /// <summary>
    /// A stubbed source that counts its calls and can hold the first one until the test lets it go.
    /// </summary>
    /// <remarks>
    /// The events are never disposed: the stub's routes are static and outlive the test, and a later
    /// sweep may call this one again.
    /// </remarks>
    private sealed class ParkedSource
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _calls;
        private volatile bool _parks;

        public bool Parks
        {
            get => _parks;
            set => _parks = value;
        }

        public int Calls => Volatile.Read(ref _calls);

        public (HttpStatusCode, string) Answer()
        {
            if (Interlocked.Increment(ref _calls) == 1 && _parks)
            {
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(60));
            }

            return (HttpStatusCode.OK, TwoPackages);
        }

        public Task<bool> EnteredAsync() =>
            Task.Run(() => _entered.Wait(TimeSpan.FromSeconds(60)), TestContext.Current.CancellationToken);

        public void Release() => _release.Set();
    }

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    /// <summary>Two syncs that have never run, so both are due, returned in the order the sweep takes them.</summary>
    private async Task<(CollectionSyncTests.Setup First, ParkedSource FirstSource, CollectionSyncTests.Setup Second, ParkedSource SecondSource)>
        ArrangeTwoInSlugOrderAsync()
    {
        var ordered = await ArrangeInSlugOrderAsync(2);
        return (ordered[0].Setup, ordered[0].Source, ordered[1].Setup, ordered[1].Source);
    }

    /// <summary>Syncs that have never run, so all are due, in the order the sweep takes them.</summary>
    private async Task<List<(CollectionSyncTests.Setup Setup, ParkedSource Source)>> ArrangeInSlugOrderAsync(int count)
    {
        var arranged = new List<(CollectionSyncTests.Setup Setup, ParkedSource Source)>();

        for (var i = 0; i < count; i++)
        {
            var source = new ParkedSource();
            arranged.Add((await _syncs.ArrangeAsync(source.Answer), source));
        }

        return arranged.OrderBy(a => a.Setup.Slug, StringComparer.Ordinal).ToList();
    }

    private Task<int> SweepAsync(RecordingLogger? log = null, int budget = 100) =>
        (log is null
            ? ActivatorUtilities.CreateInstance<CollectionSyncService>(_syncs.Host.Services)
            : ActivatorUtilities.CreateInstance<CollectionSyncService>(
                _syncs.Host.Services, (Microsoft.Extensions.Logging.ILogger<CollectionSyncService>)log))
            .SweepTenantAsync(null, DateTime.UtcNow, budget, TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs whatever earlier tests left due, so a test about the budget starts from none.
    /// </summary>
    private async Task SweepUntilNothingIsDueAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (await SweepAsync(budget: CollectionSyncService.MaxSyncsPerSweep) > 0)
        {
            (DateTime.UtcNow < deadline).Should().BeTrue("the syncs earlier tests left due should run out within two minutes");
        }
    }

    /// <summary>The slugs of the syncs a sweep starting now would find due, in the order it takes them.</summary>
    private async Task<List<string>> DueSlugsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var enabled = await session.Query<barakoCMS.Models.CollectionSync>()
            .Where(s => s.Enabled)
            .OrderBy(s => s.Slug)
            .ToListAsync(TestContext.Current.CancellationToken);

        return enabled.Where(s => s.IsDue(DateTime.UtcNow)).Select(s => s.Slug).ToList();
    }

    /// <summary>What the sweep logged as an error, as the text an operator would read.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<CollectionSyncService>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) _errors.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>
    /// Holds the collection's lock, starts a run, and once Postgres shows that run queued behind the
    /// lock does <paramref name="meanwhile"/> and lets the lock go.
    /// </summary>
    private async Task<HttpResponseMessage> RunWhileQueuedAsync(CollectionSyncTests.Setup setup, Func<Task> meanwhile)
    {
        Task<HttpResponseMessage> waiting;

        await using (var held = await HoldAsync(setup.Type))
        {
            held.Should().NotBeNull("the control: nothing else is filling this collection");

            waiting = PostRunAsync(setup);

            await UntilAsync(
                async () => await LockBackendAsync(setup.Type, granted: false) is not null,
                "the run should be queued behind the lock");

            await meanwhile();
        }

        return await waiting;
    }

    private async Task RepointAsync(CollectionSyncTests.Setup setup, string contentType)
    {
        var edit = CollectionSyncTests.SyncBody(setup);
        edit["contentType"] = contentType;

        var saved = await (await _syncs.AdminAsync()).PutAsJsonAsync(
            $"/api/collection-syncs/{setup.Slug}", edit, TestContext.Current.CancellationToken);

        saved.StatusCode.Should().Be(HttpStatusCode.OK,
            "got {0}", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A pooled data source of its own, as a deployment has; the test host's is unpooled.</summary>
    private NpgsqlDataSource PooledDataSource() => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(_factory.ConnectionString) { Pooling = true }.ToString());

    private async Task<HttpResponseMessage> PostRunAsync(CollectionSyncTests.Setup setup) =>
        await (await _syncs.AdminAsync()).PostAsync(
            $"/api/collection-syncs/{setup.Slug}/run", null, TestContext.Current.CancellationToken);

    /// <summary>Takes the lock a run takes, the way the sweep does.</summary>
    private Task<CollectionSyncLock?> HoldAsync(string type) =>
        CollectionSyncLock.TryAcquireAsync(
            () => Store.Storage.Database.CreateConnection(),
            DefaultTenantId(), type, TimeSpan.Zero, NullLogger.Instance, TestContext.Current.CancellationToken);

    /// <summary>The tenant id a session in the default partition reports, which is what a run locks on.</summary>
    private string DefaultTenantId()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDocumentSession>().TenantId;
    }

    /// <summary>The backend holding a collection's lock, or the one queued for it.</summary>
    private async Task<int?> LockBackendAsync(string type, bool granted)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        // A bigint advisory key is stored as its high half in classid and its low half in objid.
        await using var find = connection.CreateCommand();
        find.CommandText = """
            select pid from pg_locks
            where locktype = 'advisory' and objsubid = 1 and granted = @granted
              and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
              and objid = (hashtextextended(@name, 0) & 4294967295)::oid
            limit 1
            """;
        find.Parameters.AddWithValue("granted", granted);
        find.Parameters.AddWithValue("name", CollectionSyncLock.NameFor(DefaultTenantId(), type));

        return (int?)await find.ExecuteScalarAsync(ct);
    }

    private async Task<bool> IsFreeAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        await using var take = connection.CreateCommand();
        take.CommandText = "select pg_try_advisory_lock(hashtextextended(@name, 0))";
        take.Parameters.AddWithValue("name", name);

        // Closing this unpooled connection is what lets it go again.
        return (bool)(await take.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Ends a backend the way a lost connection does, and returns once it is gone.</summary>
    private async Task KillAsync(int pid)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        await using (var kill = connection.CreateCommand())
        {
            kill.CommandText = "select pg_terminate_backend(@pid)";
            kill.Parameters.AddWithValue("pid", pid);
            ((bool?)await kill.ExecuteScalarAsync(ct)).Should().BeTrue();
        }

        // Terminating is a signal, so the backend is gone a moment later, not at once.
        await UntilAsync(
            async () =>
            {
                await using var alive = connection.CreateCommand();
                alive.CommandText = "select count(*) from pg_stat_activity where pid = @pid";
                alive.Parameters.AddWithValue("pid", pid);
                return (long)(await alive.ExecuteScalarAsync(ct))! == 0;
            },
            "the terminated backend should be gone");
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!await condition())
        {
            (DateTime.UtcNow < deadline).Should().BeTrue("within 30 seconds, " + because);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
