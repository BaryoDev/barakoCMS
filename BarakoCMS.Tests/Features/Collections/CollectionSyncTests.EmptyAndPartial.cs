using System.Net.Http.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// <c>emptyIsComplete</c> lets an empty answer archive the last entry, and <c>incompleteWhen</c>
/// refuses a body that says it is a partial list.
/// </summary>
/// <remarks>
/// Each test that expects nothing to happen carries a control that does, so "nothing happened"
/// cannot pass because the option is missing.
/// </remarks>
public partial class CollectionSyncTests
{
    private static string Partial(params string[] titles)
    {
        var body = JsonNode.Parse(IssueList(titles))!.AsObject();
        body["incomplete_results"] = true;
        return body.ToJsonString();
    }

    private static string Complete(params string[] titles)
    {
        var body = JsonNode.Parse(IssueList(titles))!.AsObject();
        body["incomplete_results"] = false;
        return body.ToJsonString();
    }

    private static readonly Action<Dictionary<string, object?>> EmptyComplete =
        body => body["emptyIsComplete"] = true;

    private static void RefuseIncomplete(Dictionary<string, object?> body) =>
        body["incompleteWhen"] = new JsonObject { ["path"] = "incomplete_results", ["equalTo"] = "true" };

    [Fact]
    public async Task An_empty_answer_with_empty_is_complete_archives_the_last_entry()
    {
        var source = new Source { Body = IssueList("A") };
        var setup = await ArrangeArchivingAsync(source, tweak: EmptyComplete);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList(Array.Empty<string>());
        var empty = await RunAsync(setup);

        empty.GetProperty("succeeded").GetBoolean().Should().BeTrue("got: {0}", empty);
        empty.GetProperty("archived").GetInt32().Should().Be(1);

        var statuses = await StatusesAsync(setup.Type);
        statuses.Should().HaveCount(1, "archiving is not erasing");
        statuses["A"].Should().Be(ContentStatus.Archived);

        var stored = await GetSyncAsync(setup);
        stored.GetProperty("emptyIsComplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_answer_without_empty_is_complete_archives_nothing()
    {
        var source = new Source { Body = IssueList("A") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList(Array.Empty<string>());
        var empty = await RunAsync(setup);

        empty.GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["A"].Should().Be(ContentStatus.Published);

        (await GetSyncAsync(setup)).GetProperty("emptyIsComplete").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_answer_with_empty_is_complete_but_no_archive_missing_archives_nothing()
    {
        var source = new Source { Body = IssueList("A") };
        var setup = await ArrangeArchivingAsync(source, archiveMissing: false, tweak: EmptyComplete);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList(Array.Empty<string>());
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["A"].Should().Be(ContentStatus.Published);
    }

    [Fact]
    public async Task An_empty_answer_cut_by_a_next_page_still_archives_nothing_with_empty_is_complete()
    {
        var source = new Source { Body = IssueList("A") };
        var setup = await ArrangeArchivingAsync(source, tweak: EmptyComplete);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList(Array.Empty<string>());
        LinkHeaders[StubPath(setup)] = "<https://registry.example/search?page=2>; rel=\"next\"";
        try
        {
            (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(0, "A may be on the next page");
            (await StatusesAsync(setup.Type))["A"].Should().Be(ContentStatus.Published);
        }
        finally
        {
            LinkHeaders.TryRemove(StubPath(setup), out _);
        }

        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: one page, empty, read completely");
    }

    [Fact]
    public async Task A_body_that_says_it_is_partial_fails_the_run_and_archives_nothing()
    {
        var source = new Source { Body = Complete("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source, tweak: RefuseIncomplete);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = Partial("A", "B");
        var partial = await RunAsync(setup);

        partial.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        partial.GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Published);

        var stored = await StoredSyncAsync(setup);
        stored.LastError.Should().Contain("incomplete_results").And.Contain("partial");
        stored.ConsecutiveFailures.Should().Be(1);

        // The control: the same list, answered as complete, does archive C.
        source.Body = Complete("A", "B");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1);
        (await StatusesAsync(setup.Type))["C"].Should().Be(ContentStatus.Archived);
    }

    [Fact]
    public async Task A_partial_body_is_read_as_before_when_incomplete_when_is_not_set()
    {
        var source = new Source { Body = Complete("A", "B", "C") };
        var setup = await ArrangeArchivingAsync(source);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(3);

        source.Body = Partial("A", "B");
        var run = await RunAsync(setup);

        run.GetProperty("succeeded").GetBoolean().Should().BeTrue("got: {0}", run);
        run.GetProperty("archived").GetInt32().Should().Be(1, "today's behaviour: the flag in the body means nothing");
    }

    [Fact]
    public async Task A_partial_empty_body_does_not_archive_even_with_empty_is_complete()
    {
        var source = new Source { Body = Complete("A") };
        var setup = await ArrangeArchivingAsync(source, tweak: body =>
        {
            EmptyComplete(body);
            RefuseIncomplete(body);
        });

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = Partial(Array.Empty<string>());
        var partial = await RunAsync(setup);

        partial.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        partial.GetProperty("archived").GetInt32().Should().Be(0);
        (await StatusesAsync(setup.Type))["A"].Should().Be(ContentStatus.Published);

        source.Body = Complete(Array.Empty<string>());
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: a complete empty answer");
    }

    [Fact]
    public async Task Incomplete_when_needs_a_path_and_a_value_and_is_refused_on_a_feed()
    {
        var source = new Source { Body = Complete("A") };
        var setup = await ArrangeAsync(() => (System.Net.HttpStatusCode.OK, source.Body), save: false, fields: IssueFields());

        var blank = IssueSyncBody(setup, "{}", null, null);
        blank["incompleteWhen"] = new JsonObject { ["path"] = "", ["equalTo"] = "true" };
        (await (await AdminAsync()).PostAsJsonAsync("/api/collection-syncs", blank, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        var noValue = IssueSyncBody(setup, "{}", null, null);
        noValue["incompleteWhen"] = new JsonObject { ["path"] = "incomplete_results", ["equalTo"] = "" };
        (await (await AdminAsync()).PostAsJsonAsync("/api/collection-syncs", noValue, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        var feed = IssueSyncBody(setup, "{}", null, null);
        feed["source"] = "Feed";
        feed["feedUrl"] = "https://example.com/feed.xml";
        RefuseIncomplete(feed);
        (await (await AdminAsync()).PostAsJsonAsync("/api/collection-syncs", feed, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        var good = IssueSyncBody(setup, "{}", null, null);
        RefuseIncomplete(good);
        (await (await AdminAsync()).PostAsJsonAsync("/api/collection-syncs", good, TestContext.Current.CancellationToken))
            .IsSuccessStatusCode.Should().BeTrue("the control: a path and a value save");
    }
}

public partial class CollectionSyncTests
{
    [Fact]
    public async Task An_empty_answer_without_the_incomplete_field_archives_nothing_when_both_options_are_set()
    {
        var source = new Source { Body = Complete("A") };
        var setup = await ArrangeArchivingAsync(source, tweak: body =>
        {
            EmptyComplete(body);
            RefuseIncomplete(body);
        });

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        source.Body = IssueList(Array.Empty<string>());
        var bare = await RunAsync(setup);

        bare.GetProperty("succeeded").GetBoolean().Should().BeTrue("got: {0}", bare);
        bare.GetProperty("archived").GetInt32().Should().Be(0, "the marker field is missing, so the empty answer is not known to be complete");
        (await StatusesAsync(setup.Type))["A"].Should().Be(ContentStatus.Published);

        source.Body = Complete(Array.Empty<string>());
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1, "the control: the field says complete");
    }

    [Fact]
    public async Task A_non_empty_answer_without_the_incomplete_field_is_read_as_usual()
    {
        var source = new Source { Body = Complete("A", "B") };
        var setup = await ArrangeArchivingAsync(source, tweak: RefuseIncomplete);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        source.Body = IssueList("A");
        (await RunAsync(setup)).GetProperty("archived").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Equal_to_is_trimmed_on_save_and_still_refuses_a_partial_body()
    {
        var source = new Source { Body = Complete("A", "B") };
        var setup = await ArrangeArchivingAsync(source, tweak: body =>
            body["incompleteWhen"] = new JsonObject { ["path"] = " incomplete_results ", ["equalTo"] = " true " });

        var stored = await StoredSyncAsync(setup);
        stored.IncompleteWhen.Should().NotBeNull();
        stored.IncompleteWhen!.EqualTo.Should().Be("true");
        stored.IncompleteWhen.Path.Should().Be("incomplete_results");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        source.Body = Partial("A");
        var partial = await RunAsync(setup);
        partial.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        partial.GetProperty("archived").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("""{"n":1.0,"data":[]}""", "1", true)]
    [InlineData("""{"n":1,"data":[]}""", "1.0", true)]
    [InlineData("""{"n":2,"data":[]}""", "1", false)]
    [InlineData("""{"n":"1","data":[]}""", "1", true)]
    public void A_json_number_is_compared_as_a_number(string body, string equalTo, bool refused)
    {
        var read = barakoCMS.Infrastructure.Sync.SyncPayloadReader.ReadJson(
            body, "data", 10, null, new SyncIncompleteWhen { Path = "n", EqualTo = equalTo });

        read.Ok.Should().Be(!refused);
    }
}
