using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.SampleProbe;

/// <summary>
/// A module fires a trigger with a key; a second fire with the same key for the same entry and
/// workflow queues nothing, so a retried call does not start the work twice.
/// </summary>
[Collection("Integration3")]
public class TriggerKeyTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_key_queues_one_run()
    {
        var (workflowId, entry) = await WorkflowAndEntryAsync();

        (await FireAsync(entry, "order-1")).Should().Be(1);
        (await FireAsync(entry, "order-1")).Should().Be(0);

        var runs = await RunsOfAsync(workflowId);
        runs.Should().ContainSingle().Which.TriggerKey.Should().Be("order-1");
    }

    [Fact]
    public async Task A_different_key_queues_another_run()
    {
        var (workflowId, entry) = await WorkflowAndEntryAsync();

        await FireAsync(entry, "order-1");
        (await FireAsync(entry, "order-2")).Should().Be(1);

        (await RunsOfAsync(workflowId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task No_key_queues_a_run_each_time()
    {
        var (workflowId, entry) = await WorkflowAndEntryAsync();

        await FireAsync(entry, null);
        await FireAsync(entry, null);

        (await RunsOfAsync(workflowId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task NotifyRoleOnFailure_round_trips_through_the_workflow_api()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);

        var saved = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"probe-{Guid.NewGuid():N}",
            triggerContentType = $"ticket{Guid.NewGuid():N}"[..20],
            triggerEvent = SampleProbeModule.TicketCalledTrigger,
            notifyRoleOnFailure = "Accountant",
            actions = new[]
            {
                new { type = "Email", parameters = new Dictionary<string, string> { ["To"] = "desk@example.com", ["Subject"] = "s", ["Body"] = "b" } },
            },
        }, Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        using var doc = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct));
        var id = doc.RootElement.GetProperty("id").GetGuid();

        // There is no read of one workflow by id, and the list is paged across every test's
        // workflows, so the answer to the save and the stored definition stand for the read.
        doc.RootElement.GetProperty("notifyRoleOnFailure").GetString().Should().Be("Accountant");
        await using var session = host.OpenSession(null);
        (await session.LoadAsync<WorkflowDefinition>(id, Ct))!.NotifyRoleOnFailure.Should().Be("Accountant");
    }

    private async Task<int> FireAsync(Content entry, string? key)
    {
        using var scope = host.Services.CreateScopeForTenant(null);
        return await scope.ServiceProvider.GetRequiredService<IWorkflowTriggers>()
            .FireAsync(SampleProbeModule.TicketCalledTrigger, entry, key, Ct);
    }

    private async Task<(Guid WorkflowId, Content Entry)> WorkflowAndEntryAsync()
    {
        var contentType = $"ticket{Guid.NewGuid():N}"[..20];
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            TriggerContentType = contentType,
            TriggerEvent = SampleProbeModule.TicketCalledTrigger,
            Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "desk@example.com", ["Subject"] = "s", ["Body"] = "b" } }],
        };
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = contentType,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Counter"] = "2" },
        };

        await using var session = host.OpenSession(null);
        session.Store(workflow);
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return (workflow.Id, entry);
    }

    private async Task<IReadOnlyList<WorkflowRun>> RunsOfAsync(Guid workflowId)
    {
        await using var session = host.OpenSession(null);
        return await session.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync(Ct);
    }
}
