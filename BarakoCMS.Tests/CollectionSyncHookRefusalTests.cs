using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>A sync row a hook refuses leaves nothing the hook staged for the next row to commit.</summary>
[Collection("Integration1")]
public class CollectionSyncHookRefusalTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FeedFetcher(string body) : IConnectorFetcher
    {
        public Task<ConnectorFetchResult> FetchAsync(Connector? connector, ComposedRequest request, int maxBytes, CancellationToken ct) =>
            Task.FromResult(new ConnectorFetchResult(true, 200, 1, null, body));
    }

    [Fact]
    public async Task A_refused_row_does_not_leave_its_hooks_stored_document_for_the_next_row_to_commit()
    {
        var type = "syncrefuse" + Guid.NewGuid().ToString("n")[..10];
        var first = "a" + Guid.NewGuid().ToString("n")[..8];
        var second = "b" + Guid.NewGuid().ToString("n")[..8];
        var staged = Guid.NewGuid();

        using (var arrange = fixture.Services.CreateScope())
        {
            var session = arrange.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Code", Type = "string" },
                    new FieldDefinition { Name = "Title", Type = "string" },
                ],
            });
            foreach (var code in new[] { first, second })
            {
                session.Store(new Content
                {
                    Id = CollectionSyncRunner.EntryId(type, code),
                    ContentType = type,
                    Status = ContentStatus.Published,
                    Data = new Dictionary<string, object> { ["Code"] = code, ["Title"] = "old" },
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var feed = "<rss><channel>"
            + $"<item><guid>{first}</guid><title>new</title></item>"
            + $"<item><guid>{second}</guid><title>new</title></item>"
            + "</channel></rss>";
        var sync = new CollectionSync
        {
            Id = Guid.NewGuid(),
            Name = "Rows",
            Slug = "sync" + first,
            ContentType = type,
            Source = SyncSource.Feed,
            FeedUrl = "https://feed.example/rss",
            KeyField = "Code",
            FieldMap = new Dictionary<string, string> { ["Code"] = "id", ["Title"] = "title" },
        };

        using var run = fixture.Services.CreateScope();
        var session2 = run.ServiceProvider.GetRequiredService<IDocumentSession>();
        var lifecycle = new ContentLifecycleRunner([new NoMartenStoreThenRefuseHook(type, staged, first)], session2);
        var runner = ActivatorUtilities.CreateInstance<CollectionSyncRunner>(
            run.ServiceProvider, new FeedFetcher(feed), (IContentLifecycleRunner)lifecycle);

        await runner.RunAsync(sync, Ct);

        using var check = fixture.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<Content>(CollectionSyncRunner.EntryId(type, second), Ct))!
            .Data["Title"].ToString().Should().Be("new", "the second row was accepted and saved");
        (await query.LoadAsync<Content>(CollectionSyncRunner.EntryId(type, first), Ct))!
            .Data["Title"].ToString().Should().Be("old", "the first row was refused");
        (await query.LoadAsync<ProbeDocument>(staged, Ct)).Should().BeNull(
            "what the refused row's hook staged is dropped, not committed with the next row");
    }
}
