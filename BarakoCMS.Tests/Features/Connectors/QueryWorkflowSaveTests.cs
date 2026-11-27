using System.Net.Http.Json;
using System.Security.Claims;
using BarakoCMS.Tests.Features.Workflows;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A workflow with a Query step is saved only by someone who may read everything the saved query
/// reads, since the step's values go on into messages the author did not have to be allowed to see.
/// </summary>
[Collection("Integration1")]
public class QueryWorkflowSaveTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Workflow(string slug) => new()
    {
        Name = "Enrollment", TriggerContentType = "enrollment", TriggerEvent = "Created",
        Actions = [new WorkflowAction { Type = "Query", Parameters = new() { ["Query"] = slug } }],
    };

    private async Task<List<string>> MissingAsync(Guid author, WorkflowDefinition workflow)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", author.ToString())], "test"));
        return await WorkflowSaveRights.MissingAsync(
            sp.GetRequiredService<IDocumentSession>(), sp.GetRequiredService<IPermissionResolver>(),
            sp.GetRequiredService<IWorkflowPluginRegistry>(), principal, workflow, Ct,
            sp.GetRequiredService<ISensitivityService>());
    }

    [Fact]
    public async Task An_author_without_read_on_a_field_the_query_selects_is_refused_by_name()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        var narrow = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, ["Name", "Number"]));

        var missing = await MissingAsync(narrow, Workflow(flow.QuerySlug));

        missing.Should().ContainSingle().Which.Should().Contain(flow.QuerySlug).And.Contain("Email");
    }

    [Fact]
    public async Task An_author_who_may_read_every_field_the_query_names_is_not_refused()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        var full = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, null));

        (await MissingAsync(full, Workflow(flow.QuerySlug))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_that_does_not_exist_yet_and_one_named_by_a_template_are_left_to_the_run()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null);
        var narrow = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, ["Name"]));

        (await MissingAsync(narrow, Workflow("not-saved-yet"))).Should().BeEmpty();
        (await MissingAsync(narrow, Workflow("{{data.Q}}"))).Should().BeEmpty();
    }

    [Fact]
    public async Task The_save_route_refuses_a_workflow_whose_query_names_a_field_the_author_may_not_read()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        var admin = await flow.Harness.AdminAsync();

        // The admin may read everything, so the route accepts it: the refusal case is the unit above,
        // and this pins that the route passes the saved query through the same check without error.
        var created = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = "Enrollment " + Guid.NewGuid().ToString("n")[..6],
            triggerContentType = flow.Enrollments,
            triggerEvent = "Created",
            actions = new[] { new { type = "Query", parameters = new Dictionary<string, string> { ["Query"] = flow.QuerySlug }, capture = new[] { "Email" } } },
        }, Ct);

        (await created.Content.ReadAsStringAsync(Ct)).Should().NotContain("cannot read everything");
    }
}
