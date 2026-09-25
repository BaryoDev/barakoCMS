using System.Collections;
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
/// Field rules and exclusions in a collection sync (#988), and a datetime settling to unchanged (#987).
/// </summary>
/// <remarks>
/// The source is shaped like a GitHub search: issues from two repositories, labels as an array of
/// objects, an assignees array that is empty on the ones still up for grabs, and a milestone's
/// closed and open counts. That is the shape barakocms.com's roadmap and up-for-grabs pages need,
/// and none of it could be produced by a plain field map.
///
/// Each item differs from the others in the value under test, so a rule that ignored the item and
/// wrote one value everywhere fails as visibly as a rule that wrote nothing.
/// </remarks>
public partial class CollectionSyncTests
{
    private const string Issues = """
    {
      "data": [
        { "title": "Fix the importer",
          "html_url": "https://github.com/BaryoDev/barakoCMS/issues/1",
          "repository_url": "https://api.github.com/repos/BaryoDev/barakoCMS",
          "labels": [ { "name": "bug" }, { "name": "good first issue" } ],
          "assignees": [],
          "closed_issues": 3, "open_issues": 1,
          "updated_at": "2026-09-21T10:12:29Z" },
        { "title": "Write the guide",
          "html_url": "https://github.com/BaryoDev/barakoPress/issues/7",
          "repository_url": "https://api.github.com/repos/BaryoDev/barakoPress",
          "labels": [ { "name": "docs" } ],
          "assignees": [],
          "closed_issues": 2, "open_issues": 1,
          "updated_at": "2026-09-22T08:00:00.5Z" },
        { "title": "Plan the release",
          "html_url": "https://github.com/BaryoDev/barakoCMS/issues/9",
          "repository_url": "https://api.github.com/repos/BaryoDev/barakoCMS",
          "labels": [],
          "assignees": [],
          "closed_issues": 0, "open_issues": 0,
          "updated_at": "2026-09-23T00:00:00Z" },
        { "title": "Taken already",
          "html_url": "https://github.com/BaryoDev/barakoCMS/issues/4",
          "repository_url": "https://api.github.com/repos/BaryoDev/barakoCMS",
          "labels": [ { "name": "good first issue" } ],
          "assignees": [ { "login": "someone" } ],
          "closed_issues": 1, "open_issues": 1,
          "updated_at": "2026-09-20T00:00:00Z" },
        { "title": "BarakoCMS",
          "html_url": "https://github.com/BaryoDev/BarakoCMS",
          "repository_url": "https://api.github.com/repos/BaryoDev/BarakoCMS",
          "labels": [],
          "assignees": [],
          "closed_issues": 5, "open_issues": 5,
          "updated_at": "2026-09-19T00:00:00Z" }
      ]
    }
    """;

    private static List<FieldDefinition> IssueFields() =>
    [
        new FieldDefinition { Name = "title", Type = "string" },
        new FieldDefinition { Name = "product", Type = "string" },
        new FieldDefinition { Name = "path", Type = "string" },
        new FieldDefinition { Name = "repo", Type = "string" },
        new FieldDefinition { Name = "tags", Type = "string" },
        new FieldDefinition { Name = "labels", Type = "array" },
        new FieldDefinition { Name = "firstIssue", Type = "bool" },
        new FieldDefinition { Name = "percent", Type = "int" },
        new FieldDefinition { Name = "total", Type = "int" },
        new FieldDefinition { Name = "published", Type = "datetime" },
    ];

    [Fact]
    public async Task A_const_rule_fills_a_field_the_item_does_not_hold()
    {
        var setup = await ArrangeIssuesAsync("""{ "product": { "const": "cms" } }""");

        var outcome = await RunAsync(setup);
        outcome.GetProperty("created").GetInt32().Should().Be(5, "got: {0}", outcome);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        entries.Select(e => Value(e, "product")).Should().OnlyContain(v => v == "cms");
    }

    [Fact]
    public async Task A_prefix_strip_rule_keeps_what_follows_the_prefix()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "path": { "path": "html_url", "prefixStrip": "https://github.com/" } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "path").Should().Be("BaryoDev/barakoCMS/issues/1");
        Value(Titled(entries, "Write the guide"), "path").Should().Be("BaryoDev/barakoPress/issues/7");
    }

    [Fact]
    public async Task A_regex_rule_writes_its_capture_group()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "repo": { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$" } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "repo").Should().Be("barakoCMS");
        Value(Titled(entries, "Write the guide"), "repo").Should().Be("barakoPress");
    }

    [Fact]
    public async Task A_join_rule_writes_every_element_of_an_array()
    {
        var setup = await ArrangeIssuesAsync("""{ "tags": { "path": "labels[].name", "join": ", " } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "tags").Should().Be("bug, good first issue");
        Value(Titled(entries, "Write the guide"), "tags").Should().Be("docs");
        Value(Titled(entries, "Plan the release"), "tags").Should().BeNull("an empty array joins to no value");
    }

    [Fact]
    public async Task An_array_path_onto_an_array_field_writes_the_list_and_settles_on_a_second_run()
    {
        var setup = await ArrangeIssuesAsync("""{ "labels": { "path": "labels[].name" } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        List(Titled(entries, "Fix the importer"), "labels").Should().Equal("bug", "good first issue");
        List(Titled(entries, "Write the guide"), "labels").Should().Equal("docs");

        var second = await RunAsync(setup);
        second.GetProperty("updated").GetInt32().Should().Be(0, "the same lists were fetched again");
        second.GetProperty("unchanged").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task A_contains_rule_writes_whether_any_element_matches()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "firstIssue": { "path": "labels[].name", "contains": "Good First Issue" } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "firstIssue").Should().Be("True");
        Value(Titled(entries, "Write the guide"), "firstIssue").Should().Be("False");
        Value(Titled(entries, "Plan the release"), "firstIssue").Should().Be("False",
            "an empty array contains nothing, which is an answer rather than a missing value");
    }

    [Fact]
    public async Task A_ratio_rule_writes_closed_over_closed_plus_open_as_a_rounded_percent()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "percent": { "ratio": ["closed_issues", "open_issues"] } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "percent").Should().Be("75");
        Value(Titled(entries, "Write the guide"), "percent").Should().Be("67", "two of three rounds to 67");
        Value(Titled(entries, "Plan the release"), "percent").Should().Be("0", "a milestone with no issues is not done");
    }

    [Fact]
    public async Task A_sum_rule_writes_its_paths_added()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "total": { "sum": ["closed_issues", "open_issues"] } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "total").Should().Be("4");
        Value(Titled(entries, "Write the guide"), "total").Should().Be("3");
        Value(Titled(entries, "Plan the release"), "total").Should().Be("0");
        Value(Titled(entries, "BarakoCMS"), "total").Should().Be("10");
    }

    [Fact]
    public async Task A_replace_rule_changes_each_text_it_names_after_the_prefix_is_stripped()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "path": { "path": "html_url", "prefixStrip": "https://github.com/BaryoDev/", "replace": { "/issues/": " #", "/": " · " } } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "path").Should().Be("barakoCMS #1");
        Value(Titled(entries, "Write the guide"), "path").Should().Be("barakoPress #7");
        Value(Titled(entries, "BarakoCMS"), "path").Should().Be("BarakoCMS");
    }

    [Fact]
    public async Task A_sum_of_decimals_with_a_whole_total_fills_an_int_field()
    {
        const string milestones = """
        {
          "data": [
            { "title": "One", "closed_issues": 3.0, "open_issues": 1 },
            { "title": "Two", "closed_issues": 2.5, "open_issues": 0.5 }
          ]
        }
        """;

        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, milestones), save: false, fields: IssueFields());
        await PostAsync(await AdminAsync(), "/api/collection-syncs",
            IssueSyncBody(setup, """{ "total": { "sum": ["closed_issues", "open_issues"] } }""", null, null));

        var outcome = await RunAsync(setup);
        outcome.GetProperty("created").GetInt32().Should().Be(2, "got: {0}", outcome);
        outcome.GetProperty("skipped").GetInt32().Should().Be(0);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(2);
        Value(Titled(entries, "One"), "total").Should().Be("4");
        Value(Titled(entries, "Two"), "total").Should().Be("3");
    }

    [Fact]
    public async Task A_rule_that_throws_is_recorded_on_the_sync_as_a_failed_run()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "repo": { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$" } }""");

        // Stored past the save check, as a sync saved before a check existed would be.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var sync = await session.Query<CollectionSync>()
                .FirstAsync(s => s.Slug == setup.Slug, TestContext.Current.CancellationToken);
            sync.FieldRules["repo"].Regex = "([a-z";
            session.Store(sync);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var outcome = await RunAsync(setup);
        outcome.GetProperty("succeeded").GetBoolean().Should().BeFalse("got: {0}", outcome);

        var after = await GetSyncAsync(setup);
        after.GetProperty("lastRunAt").ValueKind.Should().Be(JsonValueKind.String, "the sync is not left due");
        after.GetProperty("lastError").GetString().Should().Contain("rule");
    }

    [Fact]
    public async Task A_map_rule_writes_the_entry_a_value_names_and_nothing_for_one_it_does_not()
    {
        var setup = await ArrangeIssuesAsync(
            """{ "repo": { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$", "map": { "barakoCMS": "API", "barakoPress": "Renderer" } } }""");

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        Value(Titled(entries, "Fix the importer"), "repo").Should().Be("API");
        Value(Titled(entries, "Write the guide"), "repo").Should().Be("Renderer");
        Value(Titled(entries, "BarakoCMS"), "repo").Should().BeNull("the map compares exactly, and names barakoCMS, not BarakoCMS");
    }

    [Fact]
    public async Task A_sum_rule_cannot_be_the_key()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, Issues), save: false, fields: IssueFields());

        var body = IssueSyncBody(setup, """{ "total": { "sum": ["closed_issues", "open_issues"] } }""", null, null);
        body["keyField"] = "total";

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("cannot be a const, ratio, sum or contains rule");
    }

    [Fact]
    public async Task Exclude_rules_skip_an_item_with_a_non_empty_path_or_an_equal_value()
    {
        var setup = await ArrangeIssuesAsync(
            rules: "{}",
            exclude: """[ { "path": "assignees", "notEmpty": true }, { "path": "title", "equalTo": "BarakoCMS" } ]""");

        var outcome = await RunAsync(setup);

        outcome.GetProperty("succeeded").GetBoolean().Should().BeTrue("got: {0}", outcome);
        outcome.GetProperty("created").GetInt32().Should().Be(3);
        outcome.GetProperty("excluded").GetInt32().Should().Be(2);
        outcome.GetProperty("skipped").GetInt32().Should().Be(0, "an excluded item is a rule doing its job, not a bad row");

        var titles = (await EntriesAsync(setup.Type)).Select(e => Value(e, "title")).ToList();
        titles.Should().HaveCount(3);
        titles.Should().BeEquivalentTo(["Fix the importer", "Write the guide", "Plan the release"]);
    }

    [Fact]
    public async Task A_run_that_excludes_every_item_succeeds_with_no_entries()
    {
        var setup = await ArrangeIssuesAsync(
            rules: "{}",
            exclude: """[ { "path": "html_url", "notEmpty": true } ]""");

        var outcome = await RunAsync(setup);

        outcome.GetProperty("succeeded").GetBoolean().Should().BeTrue(
            "nothing matching is an answer, where nothing mapping is a broken sync; got: {0}", outcome);
        outcome.GetProperty("excluded").GetInt32().Should().Be(5);
        (await EntriesAsync(setup.Type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_sync_using_every_rule_is_unchanged_on_a_second_run()
    {
        var setup = await ArrangeIssuesAsync("""
        {
          "product":    { "const": "cms" },
          "path":       { "path": "html_url", "prefixStrip": "https://github.com/" },
          "repo":       { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$" },
          "tags":       { "path": "labels[].name", "join": ", " },
          "labels":     { "path": "labels[].name" },
          "firstIssue": { "path": "labels[].name", "contains": "good first issue" },
          "percent":    { "ratio": ["closed_issues", "open_issues"] }
        }
        """);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        // The control: every rule wrote something, or an unchanged second run proves nothing.
        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(5);
        var importer = Titled(entries, "Fix the importer");
        importer.Data.Keys.Should().BeEquivalentTo(
            ["title", "product", "path", "repo", "tags", "labels", "firstIssue", "percent"]);

        var second = await RunAsync(setup);

        second.GetProperty("updated").GetInt32().Should().Be(0);
        second.GetProperty("unchanged").GetInt32().Should().Be(5);
    }

    /// <summary>
    /// #987. The stored value comes back as the serializer wrote it and the fresh one is formatted
    /// with seven fractional digits, so a datetime compared as text never matched.
    /// </summary>
    [Fact]
    public async Task A_datetime_field_is_unchanged_on_a_second_run_against_the_same_source()
    {
        var setup = await ArrangeIssuesAsync(
            rules: "{}",
            fieldMap: new() { ["title"] = "title", ["published"] = "updated_at" });

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(5);

        var second = await RunAsync(setup);

        second.GetProperty("updated").GetInt32().Should().Be(0, "nothing in the source moved");
        second.GetProperty("unchanged").GetInt32().Should().Be(5);
    }

    /// <summary>
    /// A definition saved without any of the new properties writes exactly the fields it wrote
    /// before, and reads back with the new properties empty.
    /// </summary>
    [Fact]
    public async Task A_definition_without_rules_writes_the_same_entries_as_before()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(2);
        foreach (var entry in entries)
        {
            entry.Data.Keys.Should().BeEquivalentTo(["packageId", "downloads", "summary"]);
        }

        var files = entries.Single(c => Value(c, "packageId") == "BarakoCMS.Files");
        Value(files, "downloads").Should().Be("310");
        Value(files, "summary").Should().Be("File storage");

        var saved = await GetSyncAsync(setup);
        saved.GetProperty("fieldRules").EnumerateObject().Should().BeEmpty();
        saved.GetProperty("exclude").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Rules_and_exclusions_read_back_as_they_were_saved()
    {
        var setup = await ArrangeIssuesAsync(
            rules: """{ "repo": { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$" } }""",
            exclude: """[ { "path": "assignees", "notEmpty": true } ]""");

        var saved = await GetSyncAsync(setup);

        var repo = saved.GetProperty("fieldRules").GetProperty("repo");
        repo.GetProperty("path").GetString().Should().Be("repository_url");
        repo.GetProperty("regex").GetString().Should().Be("repos/[^/]+/([^/]+)$");

        var exclude = saved.GetProperty("exclude");
        exclude.GetArrayLength().Should().Be(1);
        exclude[0].GetProperty("path").GetString().Should().Be("assignees");
        exclude[0].GetProperty("notEmpty").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "product": {} }""", null, "needs exactly one of const, path, ratio or sum")]
    [InlineData("""{ "product": { "const": "cms", "path": "title" } }""", null, "needs exactly one of const, path, ratio or sum")]
    [InlineData("""{ "product": { "const": "cms", "join": ", " } }""", null, "only apply to a path")]
    [InlineData("""{ "repo": { "path": "repository_url", "regex": "([a-z" } }""", null, "does not compile")]
    [InlineData("""{ "repo": { "path": "repository_url", "regex": "repos/.+" } }""", null, "exactly one capture group")]
    [InlineData("""{ "repo": { "path": "repository_url", "regex": "(a)(b)" } }""", null, "exactly one capture group")]
    [InlineData("""{ "tags": { "path": "labels[].name", "join": ",", "contains": "bug" } }""", null, "join or contains")]
    [InlineData("""{ "percent": { "ratio": ["closed_issues"] } }""", null, "exactly two paths")]
    [InlineData("""{ "total": { "sum": ["closed_issues"] } }""", null, "sum needs 2 to 10 paths")]
    [InlineData("""{ "total": { "sum": ["closed_issues", ""] } }""", null, "sum needs 2 to 10 paths")]
    [InlineData("""{ "total": { "sum": ["labels[].n", "open_issues"] } }""", null, "cannot add or divide an array path")]
    [InlineData("""{ "percent": { "ratio": ["closed_issues", "labels[].n"] } }""", null, "cannot add or divide an array path")]
    [InlineData("""{ "product": { "const": "cms", "replace": { "c": "k" } } }""", null, "only apply to a path")]
    [InlineData("""{ "tags": { "sum": ["closed_issues", "open_issues"] } }""", null, "a sum writes a number")]
    [InlineData("""{ "total": { "sum": ["closed_issues", "open_issues"], "path": "title" } }""", null, "needs exactly one of const, path, ratio or sum")]
    [InlineData("""{ "product": { "const": "cms", "map": { "cms": "API" } } }""", null, "only apply to a path")]
    [InlineData("""{ "repo": { "path": "repository_url", "replace": {} } }""", null, "replace needs 1 to 20 replacements")]
    [InlineData("""{ "repo": { "path": "repository_url", "replace": { "": "x" } } }""", null, "replace needs 1 to 20 replacements")]
    [InlineData("""{ "repo": { "path": "repository_url", "map": {} } }""", null, "map needs 1 to 500 values")]
    [InlineData("""{ "firstIssue": { "path": "labels[].name", "contains": "bug", "map": { "bug": "x" } } }""", null, "map or contains")]
    [InlineData("""{ "title": { "path": "html_url" } }""", null, "both fieldMap and fieldRules")]
    [InlineData("""{ "nosuchfield": { "const": "cms" } }""", null, "nosuchfield")]
    [InlineData("""{ "tags": { "path": "labels[].name", "contains": "bug" } }""", null, "needs a bool field")]
    [InlineData("""{ "tags": { "ratio": ["closed_issues", "open_issues"] } }""", null, "int or decimal field")]
    [InlineData("""{ "tags": { "path": "labels[].name" } }""", null, "needs join, contains, or an array field")]
    [InlineData("{}", """[ { "path": "assignees" } ]""", "exactly one of notEmpty or equalTo")]
    [InlineData("{}", """[ { "path": "", "notEmpty": true } ]""", "exactly one of notEmpty or equalTo")]
    [InlineData("{}", """[ { "path": "id", "notEmpty": true, "equalTo": "x" } ]""", "exactly one of notEmpty or equalTo")]
    public async Task A_malformed_rule_is_refused_when_it_is_saved(string rules, string? exclude, string expected)
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, Issues), save: false, fields: IssueFields());

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", IssueSyncBody(setup, rules, exclude, null), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}", body);
        body.Should().Contain(expected);
    }

    [Fact]
    public async Task A_const_rule_cannot_be_the_key()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, Issues), save: false, fields: IssueFields());

        var body = IssueSyncBody(setup, """{ "product": { "const": "cms" } }""", null, null);
        body["keyField"] = "product";

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("cannot be a const, ratio, sum or contains rule");
    }

    [Fact]
    public async Task An_array_path_without_join_cannot_be_the_key()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, Issues), save: false, fields: IssueFields());

        var body = IssueSyncBody(setup, """{ "labels": { "path": "labels[].name" } }""", null, null);
        body["keyField"] = "labels";

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("array path without join");
    }

    [Fact]
    public async Task A_rule_value_that_does_not_convert_is_not_echoed_into_the_error()
    {
        var setup = await ArrangeIssuesAsync("""{ "percent": { "path": "title" } }""");

        var outcome = await RunAsync(setup);

        outcome.GetProperty("succeeded").GetBoolean().Should().BeFalse("got: {0}", outcome);
        var error = outcome.GetProperty("error").GetString();
        error.Should().Contain("percent");
        error.Should().NotContain("Fix the importer", "provider text stays out of the stored and logged error");
    }

    [Theory]
    [InlineData("<https://api.example/x?page=2>; rel=\"next\"", true)]
    [InlineData("<https://api.example/x?page=2>; rel=next", true)]
    [InlineData("<https://api.example/x?page=2>; rel=\"next prefetch\"", true)]
    [InlineData("<https://api.example/x?page=1>; rel=\"prev\", <https://api.example/x?page=3>; REL=\"Next\"", true)]
    [InlineData("<https://api.example/x?page=1>; rel=\"prev\"", false)]
    [InlineData("<https://api.example/x?page=9>; rel=\"nextish\"", false)]
    public void A_link_header_names_a_next_page_only_by_its_rel(string link, bool expected) =>
        barakoCMS.Infrastructure.Connectors.ConnectorSender.NamesNextPage(link).Should().Be(expected);

    private async Task<Setup> ArrangeIssuesAsync(
        string rules, string? exclude = null, Dictionary<string, string>? fieldMap = null)
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, Issues), save: false, fields: IssueFields());
        await PostAsync(await AdminAsync(), "/api/collection-syncs", IssueSyncBody(setup, rules, exclude, fieldMap));
        return setup;
    }

    private static Dictionary<string, object?> IssueSyncBody(
        Setup setup, string rules, string? exclude, Dictionary<string, string>? fieldMap)
    {
        var body = SyncBody(setup);
        body["fieldMap"] = fieldMap ?? new Dictionary<string, string> { ["title"] = "title" };
        body["keyField"] = "title";
        body["fieldRules"] = JsonNode.Parse(rules);
        if (exclude is not null) body["exclude"] = JsonNode.Parse(exclude);
        return body;
    }

    private static Content Titled(IEnumerable<Content> entries, string title) =>
        entries.Single(e => Value(e, "title") == title);

    private static List<string?> List(Content entry, string field) =>
        entry.Data.TryGetValue(field, out var value) && value is IEnumerable items and not string
            ? items.Cast<object?>().Select(i => i is JsonElement e ? e.GetString() : i?.ToString()).ToList()
            : [];
}
