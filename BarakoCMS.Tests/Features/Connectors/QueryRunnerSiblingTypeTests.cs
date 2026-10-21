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

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(Type(lower), Type(upper));
            seed.Store(Row(lower, "mine"), Row(upper, "theirs"));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session = store.QuerySession(tenant);
        var result = await new QueryRunner(session).RunAsync(
            new QueryDefinition { ContentType = lower, Fields = ["Title"], Limit = 50 }, TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Rows.Select(r => r["Title"]?.ToString()).Should().BeEquivalentTo(["mine"]);
    }
}
