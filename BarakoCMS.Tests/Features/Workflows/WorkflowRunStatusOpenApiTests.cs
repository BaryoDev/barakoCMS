using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The OpenAPI document names every status a run and an action can have, so a client can check its
/// own list against the server's.
/// </summary>
/// <remarks>
/// The names are written out here and not read from the enum alone. A client holds the same list,
/// so a status added or renamed is a change to what it reads, and this is where that shows up.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunStatusOpenApiTests
{
    private static readonly string[] RunStatuses =
        ["Pending", "Running", "Succeeded", "Failed", "PartiallyFailed", "Cancelled"];

    private static readonly string[] AttemptStatuses =
        ["Pending", "Running", "Succeeded", "Failed", "Unknown", "Skipped", "Cancelled"];

    private readonly IntegrationTestFixture _factory;

    public WorkflowRunStatusOpenApiTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_status_of_a_run_is_an_enum_listing_every_run_status()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var status = OpenApiSchemaReader.Property(doc, Run(doc), "status");

        status.TryGetProperty("enum", out _).Should().BeTrue("a bare string tells a client nothing about the values: {0}", status.GetRawText());
        var values = OpenApiSchemaReader.EnumValues(status);
        values.Should().HaveCount(6);
        values.Should().Equal(RunStatuses);
        values.Should().Equal(Enum.GetNames<RunStatus>());
    }

    [Fact]
    public async Task The_status_of_an_action_is_an_enum_listing_every_attempt_status()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var actions = Run(doc).GetProperty("properties").GetProperty("actions");
        var attempt = OpenApiSchemaReader.Resolve(doc, actions.GetProperty("items"));
        var status = OpenApiSchemaReader.Property(doc, attempt, "status");

        status.TryGetProperty("enum", out _).Should().BeTrue("a bare string tells a client nothing about the values: {0}", status.GetRawText());
        var values = OpenApiSchemaReader.EnumValues(status);
        values.Should().HaveCount(7);
        values.Should().Equal(AttemptStatuses);
        values.Should().Equal(Enum.GetNames<AttemptStatus>());
    }

    [Fact]
    public async Task The_status_filter_of_the_run_list_is_an_optional_enum_listing_every_run_status()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var parameters = OpenApiSchemaReader.Operation(doc, "/api/workflow-runs", "get")
            .GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("name").GetString() == "status")
            .ToList();

        parameters.Should().HaveCount(1);
        parameters[0].GetProperty("in").GetString().Should().Be("query");
        (parameters[0].TryGetProperty("required", out var required) && required.GetBoolean())
            .Should().BeFalse("the list is served without the filter, so the document must not ask for it");

        var status = OpenApiSchemaReader.Resolve(doc, parameters[0].GetProperty("schema"));

        status.TryGetProperty("enum", out _).Should().BeTrue("a bare string tells a client nothing about the values: {0}", status.GetRawText());
        var values = OpenApiSchemaReader.EnumValues(status);
        values.Should().HaveCount(6);
        values.Should().Equal(RunStatuses);
    }

    /// <summary>
    /// The list and the single read describe the same run, so a status declared on one and left a
    /// string on the other would be a client reading two shapes.
    /// </summary>
    [Fact]
    public async Task The_run_list_describes_its_items_with_the_same_status_enum()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var page = OpenApiSchemaReader.OkResponse(doc, OpenApiSchemaReader.Operation(doc, "/api/workflow-runs", "get"));
        var item = OpenApiSchemaReader.Resolve(doc, page.GetProperty("properties").GetProperty("items").GetProperty("items"));
        var status = OpenApiSchemaReader.Property(doc, item, "status");

        status.TryGetProperty("enum", out _).Should().BeTrue("a bare string tells a client nothing about the values: {0}", status.GetRawText());
        var values = OpenApiSchemaReader.EnumValues(status);
        values.Should().HaveCount(6);
        values.Should().Equal(RunStatuses);
    }

    private static JsonElement Run(JsonDocument doc) =>
        OpenApiSchemaReader.OkResponse(doc, OpenApiSchemaReader.Operation(doc, "/api/workflow-runs/{id}", "get"));
}
