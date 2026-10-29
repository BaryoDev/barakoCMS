#pragma warning disable CS0618 // Accounting is obsolete in 5.5 and is removed in #1285
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>A collection sync goes through the lifecycle hooks, so it cannot rewrite what a hook keeps fixed.</summary>
[Collection("Integration1")]
public class CollectionSyncJournalTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FeedFetcher(string body) : IConnectorFetcher
    {
        public Task<ConnectorFetchResult> FetchAsync(Connector? connector, ComposedRequest request, int maxBytes, CancellationToken ct) =>
            Task.FromResult(new ConnectorFetchResult(true, 200, 1, null, body));
    }

    [Fact]
    public async Task A_sync_cannot_change_the_reference_of_a_posted_journal_entry()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var key = Guid.NewGuid().ToString("N");
        var entryId = CollectionSyncRunner.EntryId(AccountingContentTypes.JournalEntry, key);
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = entryId,
                ContentType = AccountingContentTypes.JournalEntry,
                LifecycleState = "Posted",
                Data = new Dictionary<string, object>
                {
                    ["Date"] = "2026-10-05",
                    ["Memo"] = key,
                    ["Reference"] = "original",
                    ["Amount"] = 10m,
                    ["Status"] = "Posted",
                    ["EntryNumber"] = "JE-000001",
                    ["Lines"] = new List<object>
                    {
                        new Dictionary<string, object> { ["AccountCode"] = cash, ["Debit"] = 10m, ["Credit"] = 0m },
                        new Dictionary<string, object> { ["AccountCode"] = income, ["Debit"] = 0m, ["Credit"] = 10m },
                    },
                },
            });
            await session.SaveChangesAsync(Ct);
        }

        var feed = $"<rss><channel><item><guid>{key}</guid><title>changed</title></item></channel></rss>";
        var sync = new CollectionSync
        {
            Id = Guid.NewGuid(),
            Name = "Entries",
            Slug = "sync" + key[..8],
            ContentType = AccountingContentTypes.JournalEntry,
            Source = SyncSource.Feed,
            FeedUrl = "https://feed.example/rss",
            KeyField = "Memo",
            FieldMap = new Dictionary<string, string> { ["Memo"] = "id", ["Reference"] = "title" },
        };

        using var run = fixture.Services.CreateScope();
        var runner = ActivatorUtilities.CreateInstance<CollectionSyncRunner>(run.ServiceProvider, new FeedFetcher(feed));
        var outcome = await runner.RunAsync(sync, Ct);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("Reference");
        AccountingTestData.Text(await AccountingTestData.LoadAsync(fixture, entryId), "Reference").Should().Be("original");
    }
}
