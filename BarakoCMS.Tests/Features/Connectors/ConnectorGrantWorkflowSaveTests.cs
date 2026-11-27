using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static BarakoCMS.Tests.Features.Connectors.GrantKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A Request step sends what its connector was granted as the system, so a workflow that runs one is
/// saved only by someone who may read each granted field the request names (#1423).
/// </summary>
[Collection("Integration1")]
public class ConnectorGrantWorkflowSaveTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Workflow(string trigger, string request) => new()
    {
        Name = "Sends " + request, TriggerContentType = trigger, TriggerEvent = "Created",
        Actions = [new WorkflowAction { Type = "Request", Parameters = new() { ["Request"] = request } }],
    };

    private async Task<List<string>> MissingAsync(Guid saver, WorkflowDefinition workflow)
    {
        using var scope = fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", saver.ToString())], "test"));
        return await WorkflowSaveRights.MissingAsync(
            sp.GetRequiredService<IDocumentSession>(), sp.GetRequiredService<IPermissionResolver>(),
            sp.GetRequiredService<IWorkflowPluginRegistry>(), principal, workflow, Ct,
            sp.GetRequiredService<ISensitivityService>());
    }

    private async Task<(RequestKit.Subject Subject, string Slug)> PlainAsync(string body, string[] granted)
    {
        var subject = await GrantKit.SeedAsync(fixture, granted);
        var definition = Definition(subject, body);
        await StoreAsync(fixture, definition);
        return (subject, definition.Slug);
    }

    private async Task<(GrantKit.LineSeed Seed, string Slug)> QueryAsync(
        Func<string, string, string[]> grants, string[] fields)
    {
        var seed = await GrantKit.SeedLinesAsync(fixture, grants);
        var query = new QueryDefinition
        {
            Id = Guid.NewGuid(), Slug = Name("wsq"), Name = "Workflow query", ContentType = seed.Lines, Fields = [.. fields],
        };
        var definition = RequestKit.Definition(seed.Subject, "{{query.rows}}");
        definition.QuerySlug = query.Slug;
        await StoreAsync(fixture, query, definition);
        return (seed, definition.Slug);
    }

    [Fact]
    public async Task A_saver_who_may_not_read_a_granted_field_a_template_names_is_refused_by_name()
    {
        var (subject, slug) = await PlainAsync("{\"v\":\"{{Ssn}}\"}", ["Ssn"]);
        var (saver, _) = await UserAsync(fixture, [], subject.Type);

        var missing = await MissingAsync(saver, Workflow(subject.Type, slug));

        missing.Should().ContainSingle().Which.Should().Contain(subject.Type + ".Ssn").And.NotContain(SsnValue);
    }

    [Fact]
    public async Task A_saver_who_may_read_the_granted_field_is_not_refused()
    {
        var (subject, slug) = await PlainAsync("{\"v\":\"{{Ssn}}\"}", ["Ssn"]);
        var (saver, _) = await UserAsync(fixture, [SystemCapabilities.ViewSensitive], subject.Type);

        (await MissingAsync(saver, Workflow(subject.Type, slug))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_saver_is_not_asked_about_a_field_the_connector_is_not_granted()
    {
        var (subject, slug) = await PlainAsync("{\"v\":\"{{Title}}\"}", ["Ssn"]);
        var (saver, _) = await UserAsync(fixture, [], subject.Type);

        (await MissingAsync(saver, Workflow(subject.Type, slug))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_saver_who_may_not_read_a_granted_field_in_a_header_or_the_path_is_refused()
    {
        var subject = await GrantKit.SeedAsync(fixture, ["Ssn"]);
        var definition = Definition(subject, "{}", "/people/{{Ssn}}", new() { ["X-Ref"] = "{{Ssn}}" });
        await StoreAsync(fixture, definition);
        var (saver, _) = await UserAsync(fixture, [], subject.Type);

        var missing = await MissingAsync(saver, Workflow(subject.Type, definition.Slug));

        missing.Should().ContainSingle().Which.Should().Contain(subject.Type + ".Ssn");
    }

    [Fact]
    public async Task A_saver_who_may_not_read_a_granted_field_a_query_row_projects_is_refused_by_name()
    {
        var (seed, slug) = await QueryAsync((lines, _) => [lines + ".Note"], ["Qty", "Note"]);
        var (saver, _) = await UserAsync(fixture, [], seed.Lines);

        var missing = await MissingAsync(saver, Workflow(seed.Lines, slug));

        missing.Should().ContainSingle().Which.Should().Contain("Note").And.NotContain(NoteValue);
    }

    [Fact]
    public async Task A_saver_who_may_read_the_field_a_query_row_projects_is_not_refused()
    {
        var (seed, slug) = await QueryAsync((lines, _) => [lines + ".Note"], ["Qty", "Note"]);
        var (saver, _) = await UserAsync(fixture, [SystemCapabilities.ViewSensitive], seed.Lines);

        (await MissingAsync(saver, Workflow(seed.Lines, slug))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_saver_who_may_not_read_a_followed_reference_field_is_refused_by_name()
    {
        var (seed, slug) = await QueryAsync((_, products) => [products + ".Cost"], ["Qty", "Product.Cost"]);
        var (saver, _) = await UserAsync(fixture, [], seed.Lines, seed.Products);

        var missing = await MissingAsync(saver, Workflow(seed.Lines, slug));

        missing.Should().ContainSingle().Which.Should().Contain("Cost").And.NotContain(CostValue);
    }

    [Fact]
    public async Task A_saver_who_may_read_the_followed_reference_field_is_not_refused()
    {
        var (seed, slug) = await QueryAsync((_, products) => [products + ".Cost"], ["Qty", "Product.Cost"]);
        var (saver, _) = await UserAsync(fixture, [SystemCapabilities.ViewSensitive], seed.Lines, seed.Products);

        (await MissingAsync(saver, Workflow(seed.Lines, slug))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_the_request_names_that_does_not_exist_refuses_the_save()
    {
        var seed = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);
        var definition = RequestKit.Definition(seed.Subject, "{{query.rows}}");
        definition.QuerySlug = Name("missing");
        await StoreAsync(fixture, definition);
        var (saver, _) = await UserAsync(fixture, [SystemCapabilities.ViewSensitive], seed.Lines);

        var missing = await MissingAsync(saver, Workflow(seed.Lines, definition.Slug));

        missing.Should().ContainSingle().Which.Should().Contain("could not be found");
    }

    [Fact]
    public async Task A_request_whose_connector_has_no_grants_adds_no_requirement_for_query_rows()
    {
        var (seed, slug) = await QueryAsync((_, _) => [], ["Qty"]);
        var (saver, _) = await UserAsync(fixture, [], seed.Lines);

        (await MissingAsync(saver, Workflow(seed.Lines, slug))).Should().BeEmpty();
    }
}
