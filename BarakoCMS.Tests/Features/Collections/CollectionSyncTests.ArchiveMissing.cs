using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// <c>archiveMissing</c>: a sync that read its source completely archives the entries it owns that
/// the source no longer answers with.
/// </summary>
/// <remarks>
/// Every test that expects nothing to be archived also asserts the <c>archived</c> count, and the
/// ones where that would be a coincidence carry a control run that does archive, so "nothing
/// happened" cannot pass because the feature is missing.
/// </remarks>
public partial class CollectionSyncTests
{
    private static string IssueList(params string[] titles) => IssueList(titles.Select(t => (t, false)).ToArray());

    private static string IssueList(params (string Title, bool Assigned)[] items)
    {
        var data = new JsonArray();
        foreach (var (title, assigned) in items)
        {
            data.Add(new JsonObject
            {
                ["title"] = title,
                ["html_url"] = $"https://github.com/BaryoDev/barakoCMS/issues/{title}",
                ["labels"] = new JsonArray(),
                ["assignees"] = assigned ? new JsonArray(new JsonObject { ["login"] = "someone" }) : new JsonArray(),
            });
        }

        return new JsonObject { ["data"] = data }.ToJsonString();
    }

    private sealed class Source
    {
        public string Body { get; set; } = "{}";
    }

    private async Task<Setup> ArrangeArchivingAsync(
        Source source, bool archiveMissing = true, int maxEntries = 100, string? exclude = null)
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, source.Body), save: false, fields: IssueFields());

        var body = IssueSyncBody(setup, "{}", exclude, null);
        body["maxEntries"] = maxEntries;
        if (archiveMissing) body["archiveMissing"] = true;

        await PostAsync(await AdminAsync(), "/api/collection-syncs", body);
        return setup;
    }

    private static string StubPath(Setup setup) => "search" + setup.Type[3..];

    private async Task<Dictionary<string, ContentStatus>> StatusesAsync(string type) =>
        (await EntriesAsync(type)).ToDictionary(e => Value(e, "title")!, e => e.Status);

    [Fact]
    public async Task An_item_that_disappears_from_the_source_is_archived()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = IssueList("A", "B");
        var second = await RunAsync(setup);

        second.GetProperty("succeeded").GetBoolean().Should().BeTrue("got: {0}", second);
        second.GetProperty("archived").GetInt32().Should().Be(1);

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(3, "archiving is not erasing");
        statuses["C"].Should().Be(ContentStatus.Archived);
        statuses["A"].Should().Be(ContentStatus.Published);
        statuses["B"].Should().Be(ContentStatus.Published);
    }

    [Fact]
    public async Task An_item_an_exclude_rule_now_skips_is_archived()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source, exclude: """[ { "path": "assignees", "notEmpty": true } ]""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = IssueList(("A", false), ("B", false), ("C", true));
        var second = await RunAsync(setup);

        second.GetProperty("excluded").GetInt32().Should().Be(1);
        second.GetProperty("archived").GetInt32().Should().Be(1);

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(3);
        statuses["C"].Should().Be(ContentStatus.Archived, "an assigned issue is no longer up for grabs");
        statuses["A"].Should().Be(ContentStatus.Published);
    }

    [Fact]
    public async Task A_run_cut_short_by_max_entries_archives_nothing()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source, maxEntries: 3);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        // Four items and a cap of three: C is missing from what was read, but the source may well
        // still hold it past the cap, so this run cannot say it is gone.
        source.Body = IssueList("A", "B", "D", "E");
        var truncated = await RunAsync(setup);

        truncated.GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Published);

        // The control: the same sync, read completely, does archive C.
        source.Body = IssueList("A", "B", "D");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Archived);
    }

    [Fact]
    public async Task A_run_whose_source_has_a_next_page_archives_nothing()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = IssueList("A", "B");
        LinkHeaders[StubPath(setup)] = "<https://registry.example/search?page=2>; rel=\"next\"";
        try
        {
            var paged = await RunAsync(setup);

            paged.GetProperty("archived").GetInt32().Should().Be(0, "C may be on the next page");
            (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Published);
        }
        finally
        {
            LinkHeaders.TryRemove(StubPath(setup), out _);
        }

        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: one page, read completely");
    }

    [Fact]
    public async Task A_sync_without_archive_missing_archives_nothing()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source, archiveMissing: false);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = IssueList("A", "B");
        var second = await RunAsync(setup);

        second.GetProperty("archived").GetInt32().Should().Be(0);

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(3);
        statuses.Values.Should().OnlyContain(s => s == ContentStatus.Published);

        (await GetSyncAsync(setup)).GetProperty("archiveMissing").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Entries_written_by_hand_or_by_another_sync_are_never_archived()
    {
        var source = new Source { Body = IssueList("A", "B", "C", "Other") };
        var setup = await ArrangeArchivingAsync(
            source, exclude: """[ { "path": "title", "equalTo": "Other" } ]""");
        var client = await AdminAsync();

        // A second sync on the same collection and the same source, owning only "Other".
        var otherBody = IssueSyncBody(setup, "{}",
            """[ { "path": "title", "equalTo": "A" }, { "path": "title", "equalTo": "B" }, { "path": "title", "equalTo": "C" } ]""",
            null);
        otherBody["slug"] = setup.Slug + "-other";
        await PostAsync(client, "/api/collection-syncs", otherBody);
        var other = setup with { Slug = setup.Slug + "-other" };

        var hand = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = setup.Type,
            data = new Dictionary<string, object> { ["title"] = "Hand" },
            status = "Published",
        }, TestContext.Current.CancellationToken);
        hand.IsSuccessStatusCode.Should().BeTrue(await hand.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);
        (await RunAsync(other)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList("A");
        var second = await RunAsync(setup);

        second.GetProperty("archived").GetInt32().Should().Be(2, "the control: B and C were this sync's");

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(5);
        statuses["B"].Should().Be(ContentStatus.Archived);
        statuses["C"].Should().Be(ContentStatus.Archived);
        statuses["Other"].Should().Be(ContentStatus.Published, "another sync wrote it");
        statuses["Hand"].Should().Be(ContentStatus.Published, "somebody typed it");
        statuses["A"].Should().Be(ContentStatus.Published);
    }

    [Fact]
    public async Task An_item_that_comes_back_is_published_again_but_one_archived_by_hand_is_not()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        var a = (await EntriesAsync(setup.Type)).Single(e => Value(e, "title") == "A");
        var archivedByHand = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/contents/{a.Id}/status", new { id = a.Id, newStatus = "Archived" },
            TestContext.Current.CancellationToken);
        archivedByHand.IsSuccessStatusCode.Should().BeTrue(
            await archivedByHand.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        source.Body = IssueList("A", "B");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Archived);

        source.Body = IssueList("A", "B", "C");
        var third = await RunAsync(setup);

        third.GetProperty("archived").GetInt32().Should().Be(0);

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(3);
        statuses["C"].Should().Be(ContentStatus.Published, "the sync archived it, so the sync restores it");
        statuses["A"].Should().Be(ContentStatus.Archived, "an editor archived it, and that decision stands");
    }

    [Fact]
    public async Task A_run_that_skipped_an_item_archives_nothing()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        // An item with no title has no key, so this run cannot tell whether it was C.
        source.Body = IssueList("A", "B", "");
        var skipped = await RunAsync(setup);

        skipped.GetProperty("skipped").GetInt32().Should().Be(1);
        skipped.GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Published);

        source.Body = IssueList("A", "B");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: nothing skipped");
    }

    [Fact]
    public async Task An_empty_answer_archives_nothing()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = IssueList(Array.Empty<string>());
        var empty = await RunAsync(setup);

        empty.GetProperty("archived").GetInt32().Should().Be(0, "a provider having a bad moment answers with an empty list too");
        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(3);
        statuses.Values.Should().OnlyContain(s => s == ContentStatus.Published);

        source.Body = IssueList("A", "B");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: the keys survived the empty run");
    }

    [Fact]
    public async Task A_draft_the_source_dropped_is_left_a_draft()
    {
        var source = new Source { Body = IssueList("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        var c = (await EntriesAsync(setup.Type)).Single(e => Value(e, "title") == "C");
        var drafted = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/contents/{c.Id}/status", new { id = c.Id, newStatus = "Draft" },
            TestContext.Current.CancellationToken);
        drafted.IsSuccessStatusCode.Should().BeTrue(
            await drafted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        source.Body = IssueList("A");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "B was published, C a draft");

        var statuses = await StatusesAsync(setup.Type);
        statuses["B"].Should().Be(ContentStatus.Archived);
        statuses["C"].Should().Be(ContentStatus.Draft);
    }

    [Fact]
    public async Task Moving_a_sync_to_another_content_type_forgets_the_keys_it_owned()
    {
        var source = new Source { Body = IssueList("A", "B") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);
        (await StoredSyncAsync(setup)).SyncedKeys.Should().BeEquivalentTo(["A", "B"]);

        var moved = setup.Type + "b";
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = moved, DisplayName = "Issue", Fields = IssueFields(),
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var body = IssueSyncBody(setup, "{}", null, null);
        body["contentType"] = moved;
        body["archiveMissing"] = true;
        var put = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/collection-syncs/{setup.Slug}", body, TestContext.Current.CancellationToken);
        put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await StoredSyncAsync(setup)).SyncedKeys.Should().BeEmpty();
    }

    private async Task<CollectionSync> StoredSyncAsync(Setup setup)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<CollectionSync>()
            .FirstOrDefaultAsync(s => s.Slug == setup.Slug, TestContext.Current.CancellationToken))!;
    }
}
