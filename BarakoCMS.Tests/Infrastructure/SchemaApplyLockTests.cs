using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Marten;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// Two hosts applying the schema at once (#609).
/// </summary>
/// <remarks>
/// <para>
/// Marten checks whether an object exists and then creates it, and those two steps are not atomic.
/// Two hosts starting together both saw an object missing, both created it, and the loser's whole
/// DDL batch failed with <c>42P07</c>. It failed a <c>Backend (.NET)</c> job twice on 5 September,
/// and because the merge queue merges a group only when the whole group is green, one racing host
/// sent every entry in that batch back to its branch.
/// </para>
/// <para>
/// Proven by starting several at once, as the issue asks, rather than by the absence of the failure
/// for a while. Each test uses its own schema, so the objects genuinely do not exist when the run
/// begins: against the shared schema every apply would be a no-op and would pass whether the lock
/// worked or not.
/// </para>
/// </remarks>
[Collection("Sequential")]
public class SchemaApplyLockTests
{
    private readonly IntegrationTestFixture _fixture;

    public SchemaApplyLockTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>A document type of this test's own, so nothing else in the suite shares its tables.</summary>
    private sealed class Racer
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Rank { get; set; }
    }

    private DocumentStore StoreOn(string schema) =>
        DocumentStore.For(opts =>
        {
            opts.Connection(_fixture.ConnectionString);
            opts.DatabaseSchemaName = schema;
            opts.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOnly;

            // Indexes, not just the table. The object that actually failed in #609 was an index
            // (mt_doc_jobs_idx_queue_id), because a table create is one statement and the indexes
            // after it are where a second host's batch lands on something already there.
            opts.Schema.For<Racer>().Index(x => x.Name).Index(x => x.Rank);
        });

    private static string FreshSchema() => "lock_test_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Six hosts applying the same brand new schema at once all succeed.
    /// </summary>
    [Fact]
    public async Task Several_hosts_applying_a_new_schema_at_once_all_succeed()
    {
        var schema = FreshSchema();
        var stores = Enumerable.Range(0, 6).Select(_ => StoreOn(schema)).ToList();

        try
        {
            var applies = stores.Select(s => Task.Run(async () =>
                await SchemaApplyLock.RunAsync(s, () => s.Storage.ApplyAllConfiguredChangesToDatabaseAsync())));

            // Not Should().NotThrowAsync() on the whole set: WhenAll surfaces one exception and hides
            // the rest, and 42P07 is the one this has to name if it comes back.
            var act = async () => await Task.WhenAll(applies);
            await act.Should().NotThrowAsync("no host should lose a DDL batch to another host's create");

            // The control. Six applies that all quietly did nothing would pass the assertion above,
            // so prove the schema was really built.
            await using var session = stores[0].LightweightSession();
            session.Store(new Racer { Id = Guid.NewGuid(), Name = "first", Rank = 1 });
            await session.SaveChangesAsync();

            var stored = await stores[0].QuerySession().Query<Racer>().ToListAsync();
            stored.Should().HaveCount(1, "the table and its indexes exist and take writes");
        }
        finally
        {
            foreach (var store in stores) await store.DisposeAsync();
        }
    }

    /// <summary>
    /// The lock is a real lock: a second caller waits for the first to finish rather than running
    /// alongside it.
    /// </summary>
    /// <remarks>
    /// The paired deterministic test. The one above races on purpose and could pass by luck on a
    /// quiet machine even with no lock at all. This one cannot: it holds the lock open and checks
    /// that nothing else got in, which is false the moment the lock is removed.
    /// </remarks>
    [Fact]
    public async Task A_second_caller_waits_for_the_first_to_finish()
    {
        var schema = FreshSchema();
        await using var first = StoreOn(schema);
        await using var second = StoreOn(schema);

        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGotIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = Task.Run(() => SchemaApplyLock.RunAsync(first, async () =>
        {
            firstIsInside.SetResult();
            await releaseFirst.Task;
        }));

        await firstIsInside.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var waiter = Task.Run(() => SchemaApplyLock.RunAsync(second, () =>
        {
            secondGotIn.TrySetResult();
            return Task.CompletedTask;
        }));

        // Long enough that a missing lock shows up as a pass here rather than as a timing question.
        var raced = await Task.WhenAny(secondGotIn.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        raced.Should().NotBe(
            secondGotIn.Task,
            "the second caller must wait while the first holds the lock, not run beside it");

        releaseFirst.SetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(30));

        // And it does get in once the first is done, so this is a lock and not a deadlock.
        await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        secondGotIn.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>
    /// A held lock does not hang startup. The wait ends at the timeout with a message that names the
    /// key and the pid holding it.
    /// </summary>
    [Fact]
    public async Task A_held_lock_fails_with_the_key_and_the_holder_after_the_timeout()
    {
        var schema = FreshSchema();
        await using var held = StoreOn(schema);
        await using var waiting = StoreOn(schema);

        await using var holder = held.Storage.Database.CreateConnection();
        await holder.OpenAsync();

        int holderPid;
        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "select pg_backend_pid(), pg_try_advisory_lock(@key)";
            take.Parameters.AddWithValue("key", SchemaApplyLock.Key);
            await using var reader = await take.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            holderPid = reader.GetInt32(0);
            reader.GetBoolean(1).Should().BeTrue("the test holds the lock before the host asks for it");
        }

        try
        {
            var ran = false;
            var act = () => SchemaApplyLock.RunAsync(
                waiting, () => { ran = true; return Task.CompletedTask; },
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), null, CancellationToken.None);

            var thrown = await act.Should().ThrowAsync<TimeoutException>();
            thrown.Which.Message.Should().Contain(SchemaApplyLock.Key.ToString());
            thrown.Which.Message.Should().Contain(holderPid.ToString());
            ran.Should().BeFalse("the apply must not run without the lock");
        }
        finally
        {
            await using var release = holder.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SchemaApplyLock.Key);
            await release.ExecuteScalarAsync();
        }
    }
}
