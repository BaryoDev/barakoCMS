using System.Reflection;
using barakoCMS.Modules;
using JasperFx.Events.Projections;
using Marten.Events.Aggregation;

namespace BarakoCMS.Tests.SampleProbe;

// A stand-in for a private shop module: owns its events, document and projection, and nothing
// else. Generic on purpose; barakoCMS never learns what a shop is.
public sealed record ProbeTicketIssued(Guid TicketId, string Label);

public sealed record ProbeTicketCalled(Guid TicketId, string Counter);

public sealed record ProbeSecretNoted(Guid Id, string Note);

public sealed class ProbeTicket
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Counter { get; set; }
}

/// <summary>A document with Marten numeric revisions, whose writes read their own result set.</summary>
public sealed class ProbeRevisioned
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;

    [Marten.Schema.Version]
    public long Version { get; set; }
}

/// <summary>A document with optimistic concurrency, whose writes read a guid version back.</summary>
public sealed class ProbeGuarded
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class ProbeTicketProjection : SingleStreamProjection<ProbeTicket, Guid>
{
    public ProbeTicket Create(ProbeTicketIssued e) => new() { Id = e.TicketId, Label = e.Label };

    public void Apply(ProbeTicketCalled e, ProbeTicket ticket) => ticket.Counter = e.Counter;
}

internal sealed class SampleProbeModule : IBarakoModule
{
    public string Name => "SampleProbe";

    // No endpoints: the probe is exercised through core's surfaces only.
    public IEnumerable<Assembly> EndpointAssemblies => [];

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Events are conjoined per tenant, so the projected document is too.
        schema.For<ProbeTicket>().MultiTenanted();
        schema.For<ProbeRevisioned>().MultiTenanted().UseNumericRevisions(true);
        schema.For<ProbeGuarded>().MultiTenanted().UseOptimisticConcurrency(true);
        schema.AddEventType<ProbeTicketIssued>();
        schema.AddEventType<ProbeTicketCalled>();
        schema.AddEventType<ProbeSecretNoted>();
        schema.AddProjection<ProbeTicketProjection>(ProjectionLifecycle.Inline);
    }

    public void ConfigureTopics(IModuleTopics topics)
    {
        topics.Add("probe.tickets", "read_probe_tickets", typeof(ProbeTicketIssued), typeof(ProbeTicketCalled));
        topics.Add("probe.secrets", "read_probe_secrets", typeof(ProbeSecretNoted));
    }

    public const string TicketCalledTrigger = "probe.ticket-called";

    public void ConfigureWorkflowTriggers(IModuleWorkflowTriggers triggers) =>
        triggers.Add(TicketCalledTrigger, "A ticket was called to a counter.");

    public const string EchoAction = "SampleProbe.Echo";

    public void ConfigureWorkflowActions(IModuleWorkflowActions actions) => actions.Add<ProbeEchoAction>(EchoAction);
}

/// <summary>A module's own workflow action, which does nothing and succeeds.</summary>
[barakoCMS.Infrastructure.Attributes.WorkflowActionMetadata(
    Description = "Echo, from the sample probe",
    Group = barakoCMS.Infrastructure.Attributes.WorkflowActionGroup.Content,
    RequiredParameters = new string[0],
    OptionalParameters = new string[0],
    ExampleJson = @"{""Type"":""SampleProbe.Echo"",""Parameters"":{}}"
)]
internal sealed class ProbeEchoAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public string Type => SampleProbeModule.EchoAction;

    [Obsolete("Use RunAsync.")]
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success());
}
