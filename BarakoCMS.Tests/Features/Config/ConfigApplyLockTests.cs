using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Npgsql;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// The type locks of a config apply: a <c>contents</c> section reads the types its entries are
/// checked against after it holds their lock, and an apply that Postgres aborts for waiting on
/// another answers as a conflict.
/// </summary>
/// <remarks>
/// The lock test is not a race. A second session holds the type's lock exclusively, standing in for
/// the edit that is being made, and the apply is started and waits on it. Nothing sleeps: the one
/// wait is a bounded poll of pg_locks for the queued apply.
/// </remarks>
[Collection("Integration1")]
public class ConfigApplyLockTests(IntegrationTestFixture fixture)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private const int Rounds = 6;

    private readonly ConfigCallers _callers = new(fixture);

    private IDocumentStore Store => fixture.Services.GetRequiredService<IDocumentStore>();

    private static string Tag() => Guid.NewGuid().ToString("n")[..10];

    private async Task<long> QueuedAsync(string tenant, string type)
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            select count(*) from pg_locks
            where locktype = 'advisory' and objsubid = 1 and granted = false
              and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
              and objid = (hashtextextended(@name, 0) & 4294967295)::oid
            """;
        count.Parameters.AddWithValue("name", $"barakocms:field-change:{tenant}:{barakoCMS.Core.ContentTypeName.Normalize(type)}");
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task WaitForQueuedAsync(string tenant, string type)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(Limit);
        while (await QueuedAsync(tenant, type) < 1)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, limit.Token);
        }
    }

    private static KeyValuePair<string, object> Entry(string type)
    {
        var id = Guid.NewGuid();
        return new(id.ToString(), new { id, contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = "x" } });
    }

    private static object TypeDocument(string name, string extraField) => new
    {
        name,
        displayName = name,
        fields = new object[] { new { name = "Title", type = "string" }, new { name = extraField, type = "string" } },
    };

    [Fact]
    public async Task A_contents_apply_checks_its_entries_against_a_type_edit_that_committed_while_it_waited_for_the_lock()
    {
        var tenant = await _callers.TenantAsync();
        var type = "lk" + Tag();
        var stored = Type(type, ("Title", "string"), ("Note", "string"));
        await _callers.StoreInTenantAsync(tenant, stored);
        var client = await _callers.SuperAdminAsync(tenant);
        var document = new { version = 1, contents = new Dictionary<string, object>([Entry(type)]) };

        Task<(HttpResponseMessage Response, System.Text.Json.JsonElement Body)> apply;
        await using (var editor = Store.LightweightSession(tenant))
        {
            // The edit holds the type's lock until it commits, as a save of the type does.
            await FieldChanges.LockTypeAsync(editor, type, Ct);
            apply = Task.Run(() => ApplyAsync(client, document), Ct);
            await WaitForQueuedAsync(tenant, type);

            stored.Fields.Single(f => f.Name == "Note").IsRequired = true;
            editor.Store(stored);
            await editor.SaveChangesAsync(Ct);
        }

        var (response, body) = await apply.WaitAsync(Limit, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("(Note) is required");
        (await _callers.InTenantAsync(tenant, s => s.Query<Content>().Where(c => c.ContentType == type).CountAsync(Ct)))
            .Should().Be(0, "the entry was refused against the type as it was committed, so nothing is stored");
    }

    [Fact]
    public void A_deadlock_Postgres_aborts_a_save_for_is_a_commit_conflict()
    {
        var deadlock = new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");
        var wrapped = new InvalidOperationException("the batch failed", new Marten.Exceptions.MartenCommandException(new NpgsqlCommand("select 1"), deadlock));

        ConfigEngine.IsCommitConflict(deadlock).Should().BeTrue();
        ConfigEngine.IsCommitConflict(wrapped).Should().BeTrue("Marten wraps the exception from Postgres, so the chain is walked, as for a unique violation");
        ConfigEngine.IsCommitConflict(new InvalidOperationException("the save failed", new PostgresException("syntax error", "ERROR", "ERROR", "42601")))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Two_applies_that_cross_in_the_type_locks_they_take_never_answer_500()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await _callers.TenantAsync();
            var a = "a" + Tag();
            var b = "b" + Tag();
            await _callers.StoreInTenantAsync(tenant, Type(a, ("Title", "string")), Type(b, ("Title", "string")));

            // The first holds b for its types and then asks for a and b for its entries. The second holds
            // a and then asks for b, so each can wait on a type the other holds.
            var first = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object> { [b] = TypeDocument(b, "More") },
                contents = new Dictionary<string, object>([Entry(a), Entry(b)]),
            };
            var second = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object> { [a] = TypeDocument(a, "More") },
                contents = new Dictionary<string, object>([Entry(b)]),
            };
            var clientOne = await _callers.SuperAdminAsync(tenant);
            var clientTwo = await _callers.SuperAdminAsync(tenant);

            using var barrier = new Barrier(2);
            var one = Task.Run(() => { barrier.SignalAndWait(Limit, Ct).Should().BeTrue(); return ApplyAsync(clientOne, first); }, Ct);
            var two = Task.Run(() => { barrier.SignalAndWait(Limit, Ct).Should().BeTrue(); return ApplyAsync(clientTwo, second); }, Ct);
            var results = await Task.WhenAll(one, two).WaitAsync(Limit, Ct);

            foreach (var (response, body) in results)
            {
                response.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Conflict], body.ToString());
            }

            results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1,
                "Postgres aborts one of two applies that wait on each other and never both");
        }
    }
}
