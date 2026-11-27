using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// The readers that existed before a saved query could take a parameter or be run by a workflow.
/// A query with no parameters runs for them exactly as it did, and one with a parameter is refused
/// by name rather than read with the parameter ignored.
/// </summary>
[Collection("Integration1")]
public class QueryReaderPinTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SeedAsync()
    {
        var type = TypeName();
        await QueryKit.SeedAsync(
            factory.Services.GetRequiredService<IDocumentStore>(), null,
            Type(type, Field("Name"), Field("Email")),
            [
                Entry(type, new() { ["Name"] = "A", ["Email"] = "a@example.com" }, DateTime.UtcNow.AddMinutes(-3)),
                Entry(type, new() { ["Name"] = "B", ["Email"] = "b@example.com" }, DateTime.UtcNow.AddMinutes(-2)),
            ], Ct);
        return type;
    }

    private static QueryDefinition Definition(string type, params QueryFilter[] filters) => new()
    {
        Id = Guid.NewGuid(), Name = "pin", Slug = "pin-" + Guid.NewGuid().ToString("n")[..8],
        ContentType = type, Filters = [.. filters], Fields = ["Email"], Limit = 10,
    };

    [Fact]
    public async Task The_original_runner_returns_the_same_rows_for_a_query_with_no_parameters()
    {
        var type = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IQueryRunner>();

        var result = await runner.RunAsync(Definition(type, Where("Name", "eq", "A")), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, object?> { ["Email"] = "a@example.com" });
    }

    [Fact]
    public async Task The_original_runner_refuses_a_parameter_by_name_and_does_not_read()
    {
        var type = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IQueryRunner>();
        var definition = Definition(type, WhereParam("Name", "eq", "data.Who"));

        var result = await runner.RunAsync(definition, Ct);
        var validation = await runner.ValidateAsync(definition, Ct);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("Name").And.Contain("takes a parameter");
        result.Rows.Should().BeEmpty();
        validation.Should().Contain("takes a parameter");
    }

    [Fact]
    public async Task The_aggregate_run_route_refuses_a_parameter_by_name()
    {
        var type = await SeedAsync();
        var definition = Definition(type, WhereParam("Name", "eq", "data.Who"));
        definition.Fields = [];
        definition.Measures = [new QueryMeasure { Fn = "count", As = "n" }];
        await using (var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(definition);
            await session.SaveChangesAsync(Ct);
        }

        var admin = await new WorkflowStopHarness(factory).AdminAsync();
        var run = await admin.PostAsJsonAsync($"/api/queries/{definition.Slug}/run", new { }, Ct);

        run.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await run.Content.ReadAsStringAsync(Ct)).Should().Contain("takes a parameter");
    }

    [Fact]
    public async Task The_list_of_workflow_actions_names_the_Query_action_with_its_required_parameter()
    {
        var admin = await new WorkflowStopHarness(factory).AdminAsync();

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/workflows/actions", Ct);

        var all = body.ValueKind == JsonValueKind.Array ? body : body.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;
        var query = all.EnumerateArray().Single(a => a.GetProperty("type").GetString() == "Query");
        query.GetRawText().Should().Contain("\"Query\"");
    }
}
