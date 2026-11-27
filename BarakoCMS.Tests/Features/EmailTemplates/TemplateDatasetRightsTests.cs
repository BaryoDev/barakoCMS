using System.Security.Claims;
using BarakoCMS.Tests.Features.Connectors;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// A template's datasets follow the rule a Query step follows: whoever saves the template, or a
/// workflow that sends it, must be able to read every field the saved query reads, and the run reads
/// the rows as the event's actor, so the stricter of the two holds.
/// </summary>
[Collection("Integration1")]
public class TemplateDatasetRightsTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Datasets(QueryFlow flow) => $"{{\"lines\":\"{flow.QuerySlug}\"}}";

    private async Task<(QueryFlow Flow, Guid Narrow, Guid Full)> SeededAsync()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync(limit: 5);
        var narrow = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, ["Name", "Number"]));
        var full = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, null));
        return (flow, narrow, full);
    }

    private async Task<IReadOnlyList<string>> SaveErrorsAsync(string? datasets, Guid saver)
    {
        using var scope = factory.Services.CreateScope();
        var data = new Dictionary<string, object> { ["Subject"] = "s", ["Body"] = "b" };
        if (datasets is not null) data["Datasets"] = datasets;

        return await scope.ServiceProvider.GetRequiredService<IContentLifecycleRunner>()
            .RunBeforeSaveAsync(EmailTemplateRenderer.TemplateType, null, data, null, saver, Ct);
    }

    [Fact]
    public async Task A_saver_without_read_on_a_dataset_field_cannot_save_the_template_and_is_told_the_field()
    {
        var (flow, narrow, _) = await SeededAsync();

        var errors = await SaveErrorsAsync(Datasets(flow), narrow);

        errors.Should().ContainSingle().Which.Should().Contain("lines").And.Contain(flow.QuerySlug).And.Contain("Email");
    }

    [Fact]
    public async Task A_saver_who_reads_every_dataset_field_saves_the_template()
    {
        var (flow, _, full) = await SeededAsync();

        (await SaveErrorsAsync(Datasets(flow), full)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_with_no_dataset_is_not_checked_and_an_unreadable_declaration_is_refused()
    {
        var (_, narrow, _) = await SeededAsync();

        (await SaveErrorsAsync(null, narrow)).Should().BeEmpty();
        (await SaveErrorsAsync("[1,2]", narrow)).Should().ContainSingle().Which.Should().Contain("Datasets");
    }

    [Fact]
    public async Task A_dataset_key_of_five_thousand_characters_is_refused_without_being_echoed_whole()
    {
        var (flow, _, full) = await SeededAsync();
        var key = new string('k', 5_000);

        var errors = await SaveErrorsAsync($"{{\"{key}\":\"{flow.QuerySlug}\"}}", full);

        var refusal = errors.Should().ContainSingle().Which;
        refusal.Should().Contain(new string('k', 40) + "...").And.Contain("cannot be a dataset name");
        refusal.Should().NotContain(new string('k', 41));
    }

    [Fact]
    public async Task A_publish_is_checked_like_a_save()
    {
        var (flow, narrow, _) = await SeededAsync();
        using var scope = factory.Services.CreateScope();
        var data = new Dictionary<string, object> { ["Datasets"] = Datasets(flow) };

        var errors = await scope.ServiceProvider.GetRequiredService<IContentLifecycleRunner>().RunBeforeSaveAsync(
            EmailTemplateRenderer.TemplateType, Guid.NewGuid(), data, new Dictionary<string, object>(), narrow,
            new ContentTransition("Publish", "Draft", "Published"), Ct);

        errors.Should().ContainSingle().Which.Should().Contain("Email");
    }

    [Fact]
    public async Task A_template_holding_Datasets_under_two_spellings_is_refused_at_save_and_at_render()
    {
        var (flow, _, full) = await SeededAsync();
        var data = new Dictionary<string, object>
        {
            ["Subject"] = "s", ["Body"] = "b", ["Datasets"] = Datasets(flow), ["datasets"] = "{\"lines\":\"another-query\"}",
        };

        using var scope = factory.Services.CreateScope();
        var errors = await scope.ServiceProvider.GetRequiredService<IContentLifecycleRunner>()
            .RunBeforeSaveAsync(EmailTemplateRenderer.TemplateType, null, data, null, full, Ct);
        errors.Should().ContainSingle().Which.Should().Contain("Datasets").And.Contain("case");

        var stored = EmailTemplateData.Template("s", "b");
        stored.Data = data;
        var (rendered, error) = await EmailTemplateRenderer.RenderAsync(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), stored, Ct);
        rendered.Should().BeNull();
        error.Should().Contain("Datasets").And.Contain("case");
    }

    private async Task<Guid> StoreTemplateAsync(QueryFlow flow)
    {
        var template = EmailTemplateData.Template("s", "b", datasets: Datasets(flow));
        await using var session = flow.Store.LightweightSession();
        session.Store(template);
        await session.SaveChangesAsync(Ct);
        return template.Id;
    }

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

    private static WorkflowDefinition Sending(string template, bool inBranch = false)
    {
        var email = new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b", ["Template"] = template } };
        return new WorkflowDefinition
        {
            Name = "Invoice", TriggerContentType = "invoice", TriggerEvent = "Created",
            Actions = inBranch
                ? [new WorkflowAction
                {
                    Type = "Conditional",
                    Parameters = new()
                    {
                        ["Condition"] = "{{status}} == Published",
                        ["ThenActions"] = System.Text.Json.JsonSerializer.Serialize(new[] { new { Type = "Email", Parameters = email.Parameters } }),
                    },
                }]
                : [email],
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workflow_saved_by_someone_who_cannot_read_a_dataset_field_is_refused_naming_it(bool inBranch)
    {
        var (flow, narrow, _) = await SeededAsync();
        var template = await StoreTemplateAsync(flow);

        var missing = await MissingAsync(narrow, Sending(template.ToString(), inBranch));

        missing.Should().ContainSingle().Which.Should().Contain("lines").And.Contain("Email").And.Contain(template.ToString());
    }

    [Fact]
    public async Task A_workflow_saved_by_someone_who_reads_every_dataset_field_is_not_refused()
    {
        var (flow, _, full) = await SeededAsync();
        var template = await StoreTemplateAsync(flow);

        (await MissingAsync(full, Sending(template.ToString()))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_named_by_a_placeholder_or_not_found_is_left_to_the_run()
    {
        var (_, narrow, _) = await SeededAsync();

        (await MissingAsync(narrow, Sending("{{data.Template}}"))).Should().BeEmpty();
        (await MissingAsync(narrow, Sending(Guid.NewGuid().ToString()))).Should().BeEmpty();
    }

    private async Task<(TemplateVariableExtractor Extractor, Content Entry)> PreparedAsync(QueryFlow flow, Guid actor, Func<Content, Content>? shape = null)
    {
        var entryId = await flow.EnrollAsync("S-1", actor);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<IDocumentSession>();
        var entry = shape?.Invoke((await session.LoadAsync<Content>(entryId, Ct))!) ?? (await session.LoadAsync<Content>(entryId, Ct))!;

        var extractor = new TemplateVariableExtractor(
            session, sp.GetRequiredService<IPermissionResolver>(), sp.GetRequiredService<ISensitivityService>(), null)
        {
            Queries = new QueryRunner(session, sp.GetRequiredService<IPermissionResolver>(), sp.GetRequiredService<ISensitivityService>()),
        };
        const string Loop = "{{#each lines}}{{row.Name}};{{/each}}";
        await extractor.PreparePreviewAsync(entry, actor, [Loop], Ct);
        await extractor.PrepareDatasetsAsync(entry, new Dictionary<string, string> { ["lines"] = flow.QuerySlug }, Ct);
        return (extractor, entry);
    }

    private const string Loop = "{{#each lines}}{{row.Name}};{{/each}}";

    [Fact]
    public async Task A_run_by_an_actor_without_read_on_a_dataset_field_fails_the_step_by_name()
    {
        var (flow, narrow, _) = await SeededAsync();
        var (extractor, entry) = await PreparedAsync(flow, narrow);

        var render = () => extractor.ResolveVariables(Loop, entry);

        render.Should().Throw<TemplateRenderException>().Which.Message.Should().Contain("lines").And.Contain("Email");
    }

    [Fact]
    public async Task A_run_by_an_actor_who_reads_every_field_renders_the_rows()
    {
        var (flow, _, full) = await SeededAsync();
        var (extractor, entry) = await PreparedAsync(flow, full);

        extractor.ResolveVariables(Loop, entry).Should().Be("One;");
    }

    [Fact]
    public async Task An_erased_entry_reads_no_dataset_and_the_render_fails_with_that_reason()
    {
        var (flow, _, full) = await SeededAsync();
        var (extractor, entry) = await PreparedAsync(flow, full, e => new ErasedContent(e.Id, e.ContentType));

        var render = () => extractor.ResolveVariables(Loop, entry);

        render.Should().Throw<TemplateRenderException>().Which.Message.Should().Contain("An erased entry reads no dataset.");
    }
}
