using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>Two stored types that differ only by case keep their rows apart in a saved query.</summary>
[Collection("Sequential")]
public class QueryRunnerSiblingTypeTests(IntegrationTestFixture factory)
{
    private static Content Row(string type, string title) => new()
    {
        Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
        Data = new() { ["Title"] = title }, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static ContentTypeDefinition Type(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name,
        Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_query_for_one_spelling_does_not_return_the_rows_of_the_other()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = "qr-sib-" + Guid.NewGuid().ToString("n")[..8];
        var lower = "item" + Guid.NewGuid().ToString("n")[..6];
        var upper = lower.ToUpperInvariant();

        // The name index ignores case now (#1158), so a database that has been migrated cannot hold
        // both spellings. Only data from before that index can, so the index is dropped for this
        // test and put back from its own definition.
        var ct = TestContext.Current.CancellationToken;
        var restore = await DropNameIndexAsync(store, ct);
        try
        {
            await using (var seed = store.LightweightSession(tenant))
            {
                seed.Store(Type(lower), Type(upper));
                seed.Store(Row(lower, "mine"), Row(upper, "theirs"));
                await seed.SaveChangesAsync(ct);
            }

            await using var session = store.QuerySession(tenant);
            var result = await new QueryRunner(session).RunAsync(
                new QueryDefinition { ContentType = lower, Fields = ["Title"], Limit = 50 }, ct);

            result.Ok.Should().BeTrue(result.Refusal);
            result.Rows.Select(r => r["Title"]?.ToString()).Should().BeEquivalentTo(["mine"]);
        }
        finally
        {
            await using var cleanup = store.LightweightSession(tenant);
            cleanup.DeleteWhere<ContentTypeDefinition>(d => d.Name.ToLower() == lower);
            cleanup.DeleteWhere<Content>(c => c.ContentType.ToLower() == lower);
            await cleanup.SaveChangesAsync(CancellationToken.None);
            await RunAsync(store, restore, CancellationToken.None);
        }
    }

    private const string IndexName = "mt_doc_contenttypedefinition_uidx_name";

    private static async Task<string> DropNameIndexAsync(IDocumentStore store, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        await using var read = session.Connection.CreateCommand();
        read.CommandText = $"select indexdef from pg_indexes where indexname = '{IndexName}'";
        var definition = (string?)await read.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("The content type name index is missing.");
        await RunAsync(store, $"drop index public.{IndexName}", ct);
        return definition;
    }

    private static async Task RunAsync(IDocumentStore store, string sql, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
