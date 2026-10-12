using System.Reflection;
using barakoCMS.Modules;
using JasperFx.Events.Projections;
using Marten.Events.Aggregation;

namespace BarakoCMS.Tests.Clinic;

// A stand-in for a private clinic module: owns its events, document and projection, and nothing
// else. Generic on purpose; barakoCMS never learns what a clinic is.
public sealed record ProbeTicketIssued(Guid TicketId, string Label);

public sealed record ProbeTicketCalled(Guid TicketId, string Counter);

public sealed record ProbeSecretNoted(Guid Id, string Note);

public sealed class ProbeTicket
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Counter { get; set; }
}

public sealed class ProbeTicketProjection : SingleStreamProjection<ProbeTicket, Guid>
{
    public ProbeTicket Create(ProbeTicketIssued e) => new() { Id = e.TicketId, Label = e.Label };

    public void Apply(ProbeTicketCalled e, ProbeTicket ticket) => ticket.Counter = e.Counter;
}

internal sealed class ClinicProbeModule : IBarakoModule
{
    public string Name => "ClinicProbe";

    // No endpoints: the probe is exercised through core's surfaces only.
    public IEnumerable<Assembly> EndpointAssemblies => [];

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Events are conjoined per tenant, so the projected document is too.
        schema.For<ProbeTicket>().MultiTenanted();
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
}
