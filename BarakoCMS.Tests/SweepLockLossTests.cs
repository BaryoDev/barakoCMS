using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Filters;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A sweep that holds a session advisory lock through <see cref="AdvisoryLease"/> stops when the
/// backend that owns the lock is gone, instead of carrying on without it.
/// </summary>
/// <remarks>
/// Each test terminates the lock's backend from the sweep's own "lock held" hook, so the loss
/// happens after the lock was taken and before the first unit of work. Revert the
/// <c>IsHeldAsync</c> check in the sweep under test to see its test go red.
/// </remarks>
[Collection("Integration1")]
public class SweepLockLossTests
{
    private const long IdempotencySweepKey = 8_242_026_612L;
    private const long RedactionPassKey = 8_242_026_608L;
    private const long CollectionSyncSweepKey = 8_242_026_794L;

    private readonly IntegrationTestFixture _factory;

    public SweepLockLossTests(IntegrationTestFixture factory) => _factory = factory;

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

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

    [Fact]
    public async Task The_idempotency_sweep_deletes_nothing_once_its_lock_is_gone()
    {
        var store = Store;
        var prefix = $"lost-{Guid.NewGuid():N}-";
        var now = DateTime.UtcNow;
        await using (var seed = store.LightweightSession())
        {
            seed.Store(
                new IdempotencyRecord { Key = prefix + "old", Completed = true, CreatedAt = now.AddDays(-10) },
                new IdempotencyRecord { Key = prefix + "older", Completed = true, CreatedAt = now.AddDays(-20) });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var service = new IdempotencyRetentionService(
            store, new IdempotencyOptions { KeyHours = 1 }, NullLogger<IdempotencyRetentionService>.Instance)
        {
            AfterLockHeld = ct => TerminateHolderAsync(store, IdempotencySweepKey, ct),
        };

        var result = await service.TrySweepAsync(now, TestContext.Current.CancellationToken);

        result.Should().NotBeNull("the lock was free when the sweep started");
        result!.Behind.Should().BeTrue("a sweep that lost its lock leaves the rest for the next tick");
        result.Removed.Should().Be(0);

        await using var check = store.QuerySession();
        var surviving = await check.Query<IdempotencyRecord>()
            .Where(r => r.Key.StartsWith(prefix))
            .Select(r => r.Key)
            .ToListAsync(TestContext.Current.CancellationToken);
        surviving.Should().HaveCount(2);
        surviving.Should().BeEquivalentTo([prefix + "old", prefix + "older"]);
    }

    [Fact]
    public async Task The_log_redaction_pass_rewrites_nothing_once_its_lock_is_gone()
    {
        var store = Store;
        var logId = Guid.NewGuid();
        await using (var seed = store.LightweightSession())
        {
            seed.Store(new WorkflowExecutionLog
            {
                Id = logId,
                WorkflowId = Guid.NewGuid(),
                ContentId = Guid.NewGuid(),
                ExecutedAt = DateTime.UtcNow,
                Success = false,
                Actions =
                [
                    new ActionExecutionLog
                    {
                        ActionType = "Email",
                        Success = false,
                        ErrorMessage = "SMTP refused lost-lock-person@example.test",
                        ResolvedParameters = new Dictionary<string, string> { ["To"] = "lost-lock-person@example.test" },
                    },
                ],
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var conn = store.Storage.Database.CreateConnection())
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"update {store.Options.Schema.For<WorkflowExecutionLog>()} set data = data - 'Redacted' where id = @id";
            cmd.Parameters.AddWithValue("id", logId);
            (await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }

        var pass = new WorkflowExecutionLogRedactionService(
            store, NullLogger<WorkflowExecutionLogRedactionService>.Instance)
        {
            AfterLockHeld = ct => TerminateHolderAsync(store, RedactionPassKey, ct),
        };

        var redacted = await pass.RedactAllTenantsAsync(TestContext.Current.CancellationToken);

        redacted.Should().Be(0, "the lock was lost before the first partition");

        await using var check = store.QuerySession();
        var json = await check.Json.FindByIdAsync<WorkflowExecutionLog>(logId, TestContext.Current.CancellationToken);
        json.Should().NotBeNull();
        json.Should().Contain("lost-lock-person@example.test", "the row must still hold what it captured");
    }

    [Fact]
    public async Task The_collection_sync_sweep_stops_before_the_first_tenant_once_its_lock_is_gone()
    {
        var store = Store;
        var service = new CollectionSyncService(
            _factory.Services,
            store,
            new ConfigurationBuilder().Build(),
            NullLogger<CollectionSyncService>.Instance)
        {
            AfterLockHeld = ct => TerminateHolderAsync(store, CollectionSyncSweepKey, ct),
        };

        var swept = await service.TrySweepAsync(DateTime.UtcNow, TestContext.Current.CancellationToken);

        swept.Should().BeFalse("a sweep that lost its lock did not cover every tenant");
    }
}
