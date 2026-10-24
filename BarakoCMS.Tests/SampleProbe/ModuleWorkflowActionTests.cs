using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.SampleProbe;

/// <summary>
/// An action type a module added: a workflow can use it, an unknown one is refused, and the action
/// catalog lists it with the metadata of the class the module wrote.
/// </summary>
[Collection("Integration1")]
public class ModuleWorkflowActionTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string actionType) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"probe-{Guid.NewGuid():N}",
            triggerContentType = $"ticket{Guid.NewGuid():N}"[..20],
            triggerEvent = SampleProbeModule.TicketCalledTrigger,
            actions = new[] { new { type = actionType, parameters = new Dictionary<string, string>() } },
        }, Ct);

    [Fact]
    public async Task A_workflow_can_use_a_module_action()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);

        var saved = await SaveAsync(client, SampleProbeModule.EchoAction);

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_workflow_with_an_unregistered_module_action_is_400()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);

        var refused = await SaveAsync(client, "SampleProbe.Missing");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_catalog_lists_the_module_action()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ManageWorkflows]);

        var res = await client.GetAsync("/api/workflows/actions", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(SampleProbeModule.EchoAction);
    }

    [Fact]
    public void Metadata_comes_from_the_wrapped_class()
    {
        var registry = new WorkflowPluginRegistry(
            [new ModuleWorkflowAction(new ProbeEchoAction(), SampleProbeModule.EchoAction)]);

        var metadata = registry.GetActionMetadata(SampleProbeModule.EchoAction);

        metadata.Should().NotBeNull();
        metadata!.Description.Should().Be("Echo, from the sample probe");
    }
}
