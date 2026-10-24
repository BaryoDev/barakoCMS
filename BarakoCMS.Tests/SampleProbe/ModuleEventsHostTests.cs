using FluentAssertions;
using Marten;
using Xunit;

namespace BarakoCMS.Tests.SampleProbe;

[Collection("Integration4")]
public class ModuleEventsHostTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    [Fact]
    public async Task ModuleProjection_InlineEvent_WritesDocumentInSameTransaction()
    {
        var id = Guid.NewGuid();

        await host.AppendAsync(new ProbeTicketIssued(id, "A-001"), id);
        await host.AppendAsync(new ProbeTicketCalled(id, "Counter 2"), id);

        await using var session = host.OpenSession();
        var ticket = await session.LoadAsync<ProbeTicket>(id);
        ticket.Should().NotBeNull();
        ticket!.Label.Should().Be("A-001");
        ticket.Counter.Should().Be("Counter 2");
    }

    [Fact]
    public async Task ModuleEvent_StoredUnderModuleAlias()
    {
        var id = Guid.NewGuid();
        await host.AppendAsync(new ProbeTicketIssued(id, "A-002"), id);

        await using var session = host.OpenSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Single().EventTypeName.Should().Be("sample-probe.probe_ticket_issued");
    }

    [Fact]
    public async Task ModuleProjection_OtherTenant_DoesNotSeeDocument()
    {
        var tenant = await host.CreateTenantAsync();
        var id = Guid.NewGuid();
        await host.AppendAsync(new ProbeTicketIssued(id, "T-001"), id, tenant);

        await using var defaultSession = host.OpenSession();
        (await defaultSession.LoadAsync<ProbeTicket>(id)).Should().BeNull();

        await using var tenantSession = host.OpenSession(tenant);
        (await tenantSession.LoadAsync<ProbeTicket>(id)).Should().NotBeNull();
    }
}
