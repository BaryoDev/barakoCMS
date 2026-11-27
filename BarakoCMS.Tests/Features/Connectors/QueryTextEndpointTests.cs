using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// The queries API takes a saved query as text, prints it back, and previews one that has
/// parameters from the values the caller types.
/// </summary>
[Collection("Integration1")]
public class QueryTextEndpointTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SeedStudentsAsync()
    {
        var type = TypeName();
        await QueryKit.SeedAsync(
            factory.Services.GetRequiredService<IDocumentStore>(), null,
            Type(type, Field("Number"), Field("Email"), Field("Name")),
            [
                Entry(type, new() { ["Number"] = "S-1", ["Email"] = "one@example.com", ["Name"] = "One" }),
                Entry(type, new() { ["Number"] = "S-2", ["Email"] = "two@example.com", ["Name"] = "Two" }),
            ], Ct);
        return type;
    }

    private static string Slug() => "qt-" + Guid.NewGuid().ToString("n")[..10];

    [Fact]
    public async Task A_query_saved_as_text_is_stored_and_the_response_prints_it_back()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();
        var slug = Slug();

        var saved = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "By number",
            slug,
            text = $"from {type} where Number = @data.StudentNo select Email, Name limit 1",
        }, Ct);
        var body = await saved.Content.ReadAsStringAsync(Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("contentType").GetString().Should().Be(type);
        doc.RootElement.GetProperty("limit").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("filters")[0].GetProperty("param").GetString().Should().Be("data.StudentNo");
        var text = doc.RootElement.GetProperty("text").GetString()!;
        text.Should().Contain("@data.StudentNo").And.Contain("limit 1");

        // What it prints is what it reads: saving that text gives the same text.
        var again = Slug();
        var second = await admin.PostAsJsonAsync("/api/queries", new { name = "Again", slug = again, text }, Ct);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        (await admin.GetFromJsonAsync<JsonElement>($"/api/queries/{again}", Ct)).GetProperty("text").GetString().Should().Be(text);
    }

    [Fact]
    public async Task Text_given_with_the_parts_of_a_query_is_refused()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();

        var refused = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "Both", slug = Slug(), text = $"from {type} select Email", fields = new[] { "Email" },
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("text or as its parts");
    }

    [Fact]
    public async Task Text_that_does_not_read_is_a_400_that_does_not_echo_it_back()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var long_ = new string('x', 200);

        var refused = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "Bad", slug = Slug(), text = $"from student where {long_} ~ 1 select Email",
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().NotContain(long_);
    }

    [Fact]
    public async Task Text_naming_a_field_the_type_does_not_have_is_refused_by_name()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();

        var refused = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "Missing", slug = Slug(), text = $"from {type} where NoSuchField = @id select Email",
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("NoSuchField");
    }

    [Fact]
    public async Task A_preview_of_a_query_with_a_parameter_runs_with_the_values_given_and_refuses_without()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();
        var slug = Slug();
        (await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "By number", slug, text = $"from {type} where Number = @data.StudentNo select Email limit 5",
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var withValue = await admin.PostAsJsonAsync($"/api/queries/{slug}/preview",
            new { parameters = new Dictionary<string, string> { ["data.StudentNo"] = "S-2" } }, Ct);
        withValue.StatusCode.Should().Be(HttpStatusCode.OK);
        var ok = await withValue.Content.ReadFromJsonAsync<JsonElement>(Ct);
        ok.GetProperty("ok").GetBoolean().Should().BeTrue(ok.ToString());
        ok.GetProperty("count").GetInt32().Should().Be(1);
        ok.GetProperty("rows")[0].GetProperty("Email").GetString().Should().Be("two@example.com");

        var without = await admin.PostAsync($"/api/queries/{slug}/preview", null, Ct);
        var refused = await without.Content.ReadFromJsonAsync<JsonElement>(Ct);
        refused.GetProperty("ok").GetBoolean().Should().BeFalse();
        refused.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_preview_takes_no_body_an_empty_body_or_whitespace_the_same_way()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();
        var slug = Slug();
        (await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "All", slug, fields = new[] { "Email" }, contentType = type, filters = Array.Empty<object>(), limit = 5,
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var bodies = new HttpContent?[]
        {
            null,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"),
            new StringContent("  \n", System.Text.Encoding.UTF8, "application/json"),
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        };

        foreach (var body in bodies)
        {
            var answer = await admin.PostAsync($"/api/queries/{slug}/preview", body, Ct);
            answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync(Ct));
            (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ok").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_preview_body_that_is_not_json_is_a_400_that_does_not_echo_it_back()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();
        var slug = Slug();
        (await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "All", slug, fields = new[] { "Email" }, contentType = type, filters = Array.Empty<object>(), limit = 5,
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var junk = "{ not json " + new string('y', 100);
        var answer = await admin.PostAsync(
            $"/api/queries/{slug}/preview", new StringContent(junk, System.Text.Encoding.UTF8, "application/json"), Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await answer.Content.ReadAsStringAsync(Ct)).Should().NotContain("yyyy");
    }

    [Fact]
    public async Task A_stored_query_on_status_reads_the_data_field_when_the_type_declares_one()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = TypeName();
        await QueryKit.SeedAsync(
            factory.Services.GetRequiredService<IDocumentStore>(), null,
            Type(type, Field("Status"), Field("Name")),
            [
                Entry(type, new() { ["Status"] = "open", ["Name"] = "a" }),
                Entry(type, new() { ["Status"] = "closed", ["Name"] = "b" }),
            ], Ct);
        var slug = Slug();

        var saved = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "Open", slug, contentType = type, fields = new[] { "Name", "Status" }, limit = 5,
            filters = new[] { new { field = "status", op = "eq", value = "open" } },
        }, Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));

        var preview = await (await admin.PostAsync($"/api/queries/{slug}/preview", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        preview.GetProperty("ok").GetBoolean().Should().BeTrue(preview.ToString());
        preview.GetProperty("count").GetInt32().Should().Be(1);
        preview.GetProperty("rows")[0].GetProperty("Name").GetString().Should().Be("a");
    }

    [Fact]
    public async Task Text_given_with_a_limit_or_descending_is_refused_and_not_overwritten()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();

        foreach (var extra in new object[] { new { limit = 7 }, new { descending = true } })
        {
            var body = new Dictionary<string, object?> { ["name"] = "Both", ["slug"] = Slug(), ["text"] = $"from {type} select Email limit 3" };
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);

            var refused = await admin.PostAsJsonAsync("/api/queries", body, Ct);

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("text or as its parts");
        }
    }

    [Fact]
    public async Task Filters_that_are_null_or_hold_a_null_and_fields_that_hold_a_null_are_a_400()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();

        var bodies = new[]
        {
            "\"filters\": null, \"fields\": [\"Email\"]",
            "\"filters\": [null], \"fields\": [\"Email\"]",
            "\"filters\": [], \"fields\": [null]",
            "\"filters\": [{\"field\": \"Number\", \"param\": \"id\", \"op\": \"eq\"}, null], \"fields\": [\"Email\"]",
        };

        foreach (var tail in bodies)
        {
            var json = $"{{ \"name\": \"n\", \"slug\": \"{Slug()}\", \"contentType\": \"{type}\", {tail} }}";
            var answer = await admin.PostAsync("/api/queries", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

            answer.StatusCode.Should().Be(HttpStatusCode.BadRequest, tail + " " + await answer.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_query_that_lists_more_than_fifty_fields_is_refused_at_save()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var type = await SeedStudentsAsync();

        var refused = await admin.PostAsJsonAsync("/api/queries", new
        {
            name = "Wide", slug = Slug(), contentType = type, filters = Array.Empty<object>(),
            fields = Enumerable.Range(0, 51).Select(i => "Email").ToArray(),
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("at most 50 fields, filters or measures");
    }
}
