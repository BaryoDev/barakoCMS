using System.Diagnostics;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>The statement that reads the rows stops at the time the run states.</summary>
[Collection("Integration1")]
public class QueryTimeoutTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slow_query_is_cancelled_at_the_stated_timeout()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        await SeedAsync(store, tenant, Type(type, Field("Name")), [Entry(type, new() { ["Name"] = "a" })], Ct);

        var table = "public.mt_doc_contents";
        await using var holder = store.Storage.Database.CreateConnection();
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockTable = holder.CreateCommand())
        {
            lockTable.Transaction = transaction;
            lockTable.CommandText = $"lock table {table} in access exclusive mode";
            await lockTable.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            await using var session = store.QuerySession(tenant);
            var clock = Stopwatch.StartNew();
            var result = await new QueryRunner(session).RunAsync(
                new QueryDefinition { ContentType = type, Fields = ["Name"] },
                new QueryRunContext(null) { Timeout = TimeSpan.FromMilliseconds(400) }, Ct);
            clock.Stop();

            result.Ok.Should().BeFalse();
            result.Refusal.Should().Contain("took longer than 0.4 seconds");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the lock is held far longer than the limit");
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_timeout()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        await SeedAsync(store, tenant, Type(type, Field("Name")), [], Ct);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var session = store.QuerySession(tenant);

        var run = () => new QueryRunner(session).RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Name"] }, cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
