using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Query definitions: what an operator may fetch, and everything they may not.
/// </summary>
/// <remarks>
/// This is the piece most likely to grow teeth. It exists so a workflow can email every subscriber
/// without anyone writing code, and the same shape one step further is a query engine editable by
/// somebody configuring a marketing campaign.
///
/// The three that matter: a field the type does not declare cannot be filtered on, a field that is
/// not Public cannot leave, and no query returns an unbounded set. Each is paired with a control, so
/// a runner that refused everything cannot pass.
/// </remarks>
[Collection("Sequential")]
public class QueryTests
{
    private readonly IntegrationTestFixture _factory;

    public QueryTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_filter_on_a_field_the_type_does_not_declare_is_refused()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 3);

        var res = await SaveAsync(client, type,
            filters: [new { field = "NoSuchField", op = "eq", value = "x" }],
            fields: ["Email"]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("NoSuchField");
    }

    /// <summary>
    /// A filter on a Sensitive field is refused, and the message does not say it is Sensitive.
    /// </summary>
    /// <remarks>
    /// Filtering on a field the result cannot show is an oracle: a workflow author could binary
    /// search a Sensitive salary by watching how many rows come back, without the value ever
    /// appearing in a payload. The refusal reads the same as for a field that does not exist,
    /// because saying which would let somebody enumerate a type's Sensitive fields from here.
    /// </remarks>
    [Fact]
    public async Task A_filter_on_a_sensitive_field_is_refused_without_saying_why()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 3);

        var res = await SaveAsync(client, type,
            filters: [new { field = "Salary", op = "gt", value = "1" }],
            fields: ["Email"]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Salary", "the operator has to know which field was refused");
        body.Should().NotContain("Sensitive",
            "but not that it exists and is hidden, which is how a type's Sensitive fields get enumerated");
    }

    [Fact]
    public async Task A_sensitive_field_cannot_be_projected()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 3);

        var res = await SaveAsync(client, type, filters: [], fields: ["Email", "Salary"]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The control for all three refusals: a Public field filters, sorts and projects.
    /// </summary>
    /// <remarks>
    /// Without it, a validator that refused everything would pass every test above while making the
    /// feature unusable, which is the failure this file exists to rule out.
    /// </remarks>
    [Fact]
    public async Task A_public_field_filters_sorts_and_projects()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 3);

        var slug = await SaveOkAsync(client, type,
            filters: [new { field = "Active", op = "eq", value = "true" }],
            fields: ["Email", "Name"],
            sortField: "Email");

        var preview = await PreviewAsync(client, slug);

        preview.GetProperty("ok").GetBoolean().Should().BeTrue(
            "got refusal: {0}", preview.TryGetProperty("refusal", out var r) ? r.ToString() : "none");
        preview.GetProperty("count").GetInt32().Should().Be(2, "one of the three is not Active");

        var first = preview.GetProperty("rows")[0];

        // The value, not just the key. Reading row zero and checking a field exists passes with
        // OrderBySql removed entirely, so it proved the projection and nothing about the sort.
        first.GetProperty("Email").GetString().Should().Be("sub0@example.com",
            "sorted ascending by Email, so the lowest comes first");
        first.TryGetProperty("Salary", out _).Should().BeFalse(
            "the projection names what leaves, and Salary is not on it");
    }

    /// <summary>
    /// The projection is an allowlist: a field the query does not name does not leave.
    /// </summary>
    /// <remarks>
    /// Even a Public one. That is the point of naming them: a schema change that adds a field later
    /// does not silently start including it in every payload built from this query.
    /// </remarks>
    [Fact]
    public async Task A_public_field_the_query_does_not_name_does_not_leave()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 2);

        var slug = await SaveOkAsync(client, type, filters: [], fields: ["Email"]);
        var preview = await PreviewAsync(client, slug);

        var first = preview.GetProperty("rows")[0];
        first.TryGetProperty("Email", out _).Should().BeTrue();
        first.TryGetProperty("Name", out _).Should().BeFalse(
            "Name is Public and was not named, so it stays out");
    }

    [Fact]
    public async Task A_limit_above_the_ceiling_is_refused()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 2);

        var res = await SaveAsync(client, type, filters: [], fields: ["Email"], limit: 5000);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(QueryDefinition.MaxLimit.ToString());
    }

    /// <summary>
    /// A query stored with an over-ceiling limit is refused when it runs, not silently truncated.
    /// </summary>
    /// <remarks>
    /// This asserted that the run was capped, and could not fail: validation refuses an over-ceiling
    /// limit before the query is built, so the assertion read a refusal with zero rows and found
    /// zero less than the ceiling. There was a Math.Clamp behind it that nothing could reach.
    ///
    /// Refusing is the better behaviour anyway. Returning a thousand rows to a query that asked for
    /// a hundred thousand is a lie the caller cannot see, and the caller here is a workflow about to
    /// email all of them.
    ///
    /// The save path refuses this too, so reaching it means a document written another way. That is
    /// exactly why the check is repeated at run time rather than trusted from the endpoint.
    /// </remarks>
    [Fact]
    public async Task A_stored_limit_above_the_ceiling_is_refused_when_it_runs()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 5);
        var slug = await SaveOkAsync(client, type, filters: [], fields: ["Email"], limit: 2);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var stored = await session.Query<QueryDefinition>()
                .FirstOrDefaultAsync(q => q.Slug == slug, TestContext.Current.CancellationToken);
            stored!.Limit = 99999;
            session.Store(stored);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var preview = await PreviewAsync(client, slug);

        preview.GetProperty("ok").GetBoolean().Should().BeFalse(
            "a stored limit past the ceiling is refused rather than quietly truncated");
        preview.GetProperty("refusal").GetString().Should().Contain(QueryDefinition.MaxLimit.ToString());
        preview.GetProperty("count").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// The control: a limit inside the ceiling returns exactly that many rows.
    /// </summary>
    /// <remarks>
    /// Without this, a runner that refused every query would pass the test above.
    /// </remarks>
    [Fact]
    public async Task A_limit_inside_the_ceiling_bounds_the_rows()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 5);
        var slug = await SaveOkAsync(client, type, filters: [], fields: ["Email"], limit: 2);

        var preview = await PreviewAsync(client, slug);

        preview.GetProperty("ok").GetBoolean().Should().BeTrue();
        preview.GetProperty("count").GetInt32().Should().Be(2, "five rows exist and the query asked for two");
    }

    /// <summary>
    /// A query with no projection is refused rather than defaulting to every field.
    /// </summary>
    /// <remarks>
    /// "All fields" is how a personal-data field added next year ends up in a payload nobody
    /// revisited.
    /// </remarks>
    [Fact]
    public async Task A_query_with_no_projection_is_refused()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 2);

        var res = await SaveAsync(client, type, filters: [], fields: []);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A field raised to Sensitive after the query was saved stops being returned.
    /// </summary>
    /// <remarks>
    /// The save-time check cannot see the future. A field that was Public when somebody wrote this
    /// query can be raised afterwards, and without a run-time check the query would go on feeding it
    /// into third-party payloads with nothing anywhere saying so. This is the reason validation runs
    /// twice rather than once.
    /// </remarks>
    [Fact]
    public async Task A_field_raised_to_sensitive_after_saving_stops_being_returned()
    {
        var client = AdminClient();
        var type = await SeedAsync(rows: 2);
        var slug = await SaveOkAsync(client, type, filters: [], fields: ["Email"]);

        (await PreviewAsync(client, slug)).GetProperty("ok").GetBoolean().Should().BeTrue(
            "it works before the schema changes, which is what makes the change below the cause");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var def = await session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(d => d.Name == type, TestContext.Current.CancellationToken);
            def!.Fields.First(f => f.Name == "Email").Sensitivity = SensitivityLevel.Sensitive;
            session.Store(def);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var preview = await PreviewAsync(client, slug);

        preview.GetProperty("ok").GetBoolean().Should().BeFalse(
            "the field is not Public any more, so it cannot keep leaving");
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["SuperAdmin", "Admin"], Guid.NewGuid().ToString()));
        return client;
    }

    private static string NewSlug(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private Task<HttpResponseMessage> SaveAsync(
        HttpClient client, string type, object[] filters, string[] fields,
        int limit = 100, string? sortField = null, string? slug = null) =>
        client.PostAsJsonAsync("/api/queries", new
        {
            name = "Subscribers",
            slug = slug ?? NewSlug("q"),
            contentType = type,
            filters,
            fields,
            limit,
            sortField,
        }, TestContext.Current.CancellationToken);

    private async Task<string> SaveOkAsync(
        HttpClient client, string type, object[] filters, string[] fields,
        int limit = 100, string? sortField = null)
    {
        var slug = NewSlug("q");
        var res = await SaveAsync(client, type, filters, fields, limit, sortField, slug);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return slug;
    }

    private async Task<JsonElement> PreviewAsync(HttpClient client, string slug)
    {
        var res = await client.PostAsync($"/api/queries/{slug}/preview", null, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    /// <summary>A subscriber type with Public Email, Name and Active, and a Sensitive Salary.</summary>
    private async Task<string> SeedAsync(int rows)
    {
        var type = NewSlug("sub");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Subscriber",
            Fields =
            [
                new FieldDefinition { Name = "Email", Type = "email" },
                new FieldDefinition { Name = "Name", Type = "string" },
                new FieldDefinition { Name = "Active", Type = "bool" },
                new FieldDefinition { Name = "Salary", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        for (var i = 0; i < rows; i++)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>
                {
                    ["Email"] = $"sub{i}@example.com",
                    ["Name"] = $"Subscriber {i}",
                    // The last one is inactive, so a filter that does nothing returns a different
                    // count from one that works.
                    ["Active"] = i < rows - 1,
                    ["Salary"] = 50000 + i,
                },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }
}
