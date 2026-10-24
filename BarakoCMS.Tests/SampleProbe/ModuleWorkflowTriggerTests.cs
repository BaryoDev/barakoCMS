using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.SampleProbe;

/// <summary>
/// A trigger a module registered: a workflow can be saved on it, the module fires it, and a run
/// starts. Firing stays inside the scope's tenant, and a module cannot fire core's own triggers.
/// </summary>
[Collection("Integration1")]
public class ModuleWorkflowTriggerTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_workflow_saved_on_a_module_trigger_starts_a_run_when_the_module_fires_it()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];

        var saved = await SaveAsync(client, contentType, SampleProbeModule.TicketCalledTrigger);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var workflowId = await IdOfAsync(saved);

        var entry = await StoreEntryAsync(contentType, tenant: null);

        (await FireAsync(null, SampleProbeModule.TicketCalledTrigger, entry)).Should().Be(1);

        var runs = await RunsOfAsync(workflowId, tenant: null);
        runs.Should().ContainSingle();
        runs[0].TriggerEvent.Should().Be(SampleProbeModule.TicketCalledTrigger);
        runs[0].ContentId.Should().Be(entry.Id);
        runs[0].Actions.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_workflow_on_a_trigger_nobody_registered_is_refused()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];

        var positive = await SaveAsync(client, contentType, SampleProbeModule.TicketCalledTrigger);
        positive.StatusCode.Should().Be(HttpStatusCode.OK, "the same request on a registered trigger saves");

        var refused = await SaveAsync(client, contentType, "probe.nothing");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(SampleProbeModule.TicketCalledTrigger);
    }

    [Fact]
    public async Task Saving_a_workflow_on_a_module_trigger_needs_the_workflow_capability()
    {
        var holder = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);
        var other = await host.ClientHoldingAsync(["read_probe_tickets"]);
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];

        (await SaveAsync(holder, contentType, SampleProbeModule.TicketCalledTrigger)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await SaveAsync(other, contentType, SampleProbeModule.TicketCalledTrigger)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_module_fires_only_into_its_own_tenant()
    {
        var tenant = await host.CreateTenantAsync(ct: Ct);
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];

        var here = await StoreWorkflowAsync(contentType, tenant: null);
        var there = await StoreWorkflowAsync(contentType, tenant);
        var entry = await StoreEntryAsync(contentType, tenant: null);

        (await FireAsync(null, SampleProbeModule.TicketCalledTrigger, entry)).Should().Be(1);

        (await RunsOfAsync(here, tenant: null)).Should().ContainSingle("the positive control");
        (await RunsOfAsync(there, tenant)).Should().BeEmpty("the other tenant's workflow is not reached");
    }

    [Theory]
    [InlineData(WorkflowEvents.Published)]
    [InlineData(WorkflowEvents.Deleted)]
    [InlineData("transition:Approve")]
    [InlineData("probe.nothing")]
    public async Task A_module_cannot_fire_a_core_trigger_or_one_nobody_registered(string trigger)
    {
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];
        var workflowId = await StoreWorkflowAsync(contentType, tenant: null, trigger);
        var entry = await StoreEntryAsync(contentType, tenant: null);

        var act = () => FireAsync(null, trigger, entry);

        await act.Should().ThrowAsync<ArgumentException>();
        (await RunsOfAsync(workflowId, tenant: null)).Should().BeEmpty();
    }

    private async Task<int> FireAsync(string? tenant, string trigger, Content entry)
    {
        using var scope = host.Services.CreateScopeForTenant(tenant);
        return await scope.ServiceProvider.GetRequiredService<IWorkflowTriggers>().FireAsync(trigger, entry, Ct);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string contentType, string trigger) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"probe-{Guid.NewGuid():N}",
            triggerContentType = contentType,
            triggerEvent = trigger,
            actions = new[]
            {
                new { type = "Email", parameters = new Dictionary<string, string> { ["To"] = "desk@example.com", ["Subject"] = "Called", ["Body"] = "Ticket {{id}}" } },
            },
        }, Ct);

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> StoreWorkflowAsync(string contentType, string? tenant, string trigger = SampleProbeModule.TicketCalledTrigger)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            TriggerContentType = contentType,
            TriggerEvent = trigger,
            Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "desk@example.com", ["Subject"] = "s", ["Body"] = "b" } }],
        };

        await using var session = host.OpenSession(tenant);
        session.Store(workflow);
        await session.SaveChangesAsync(Ct);
        return workflow.Id;
    }

    private async Task<Content> StoreEntryAsync(string contentType, string? tenant)
    {
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = contentType,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Counter"] = "2" },
        };

        await using var session = host.OpenSession(tenant);
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry;
    }

    private async Task<IReadOnlyList<WorkflowRun>> RunsOfAsync(Guid workflowId, string? tenant)
    {
        await using var session = host.OpenSession(tenant);
        return await session.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync(Ct);
    }
}
