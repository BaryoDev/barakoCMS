using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

public class TopicRegistryTests
{
    private sealed class Declares(string name, Action<IModuleTopics> topics) : IBarakoModule
    {
        public string Name => name;
        public void ConfigureTopics(IModuleTopics t) => topics(t);
    }

    [Fact]
    public void Registry_ProbeModule_FindsTopicsByNameAndEventType()
    {
        var registry = new TopicRegistry([new SampleProbeModule()]);

        registry.Find("probe.tickets")!.Capability.Should().Be("read_probe_tickets");
        registry.Find("probe.tickets")!.Module.Should().Be("SampleProbe");
        registry.TopicsFor(typeof(ProbeTicketCalled)).Select(t => t.Name).Should().Equal("probe.tickets");
        registry.TopicsFor(typeof(string)).Should().BeEmpty();
        registry.Find("nope").Should().BeNull();
    }

    [Fact]
    public void Registry_EventInTwoTopics_ListsBoth()
    {
        var registry = new TopicRegistry([new Declares("Two", t =>
        {
            t.Add("two.a", "read_a", typeof(ProbeTicketIssued));
            t.Add("two.b", "read_b", typeof(ProbeTicketIssued));
        })]);

        registry.TopicsFor(typeof(ProbeTicketIssued)).Select(t => t.Name).Should().BeEquivalentTo("two.a", "two.b");
    }

    [Fact]
    public void Registry_DuplicateNameAcrossModules_Throws()
    {
        var act = () => new TopicRegistry([
            new Declares("One", t => t.Add("same", "read_a", typeof(ProbeTicketIssued))),
            new Declares("Two", t => t.Add("same", "read_b", typeof(ProbeTicketCalled))),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'same'*One*Two*");
    }

    [Fact]
    public void Registry_ForeignEventType_Throws()
    {
        var act = () => new TopicRegistry([new Declares("Thief", t => t.Add("thief.users", "read_x", typeof(User)))]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Thief*barakoCMS.Models.User*own assemblies*");
    }

    [Fact]
    public void Registry_TopicEventTypeNeverRegistered_FailsEnsureRegistered()
    {
        var registry = new TopicRegistry([new SampleProbeModule()]);

        var act = () => registry.EnsureRegistered([typeof(ProbeTicketIssued), typeof(ProbeTicketCalled)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'SampleProbe'*'probe.secrets'*ProbeSecretNoted*never registered*AddEventType*");
    }

    [Fact]
    public void Registry_AllEventTypesRegistered_PassesEnsureRegistered()
    {
        var registry = new TopicRegistry([new SampleProbeModule()]);

        registry.EnsureRegistered([typeof(ProbeTicketIssued), typeof(ProbeTicketCalled), typeof(ProbeSecretNoted)]);
    }

    [Theory]
    [InlineData("Upper.Case")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData(".leading")]
    public void Registry_BadName_Throws(string name)
    {
        var act = () => new TopicRegistry([new Declares("Bad", t => t.Add(name, "read_x", typeof(ProbeTicketIssued)))]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Registry_NoEventTypes_Throws()
    {
        var act = () => new TopicRegistry([new Declares("Empty", t => t.Add("empty.none", "read_x"))]);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one event type*");
    }

    [Fact]
    public void Registry_BlankCapability_Throws()
    {
        var act = () => new TopicRegistry([new Declares("Open", t => t.Add("open.all", " ", typeof(ProbeTicketIssued)))]);

        act.Should().Throw<ArgumentException>().WithMessage("*capability*");
    }
}

[Collection("Integration3")]
public class TopicVocabularyTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    [Fact]
    public void Vocabulary_TopicCapability_IsKnownWithModuleAsSource()
    {
        var vocabulary = host.Services.GetRequiredService<CapabilityVocabulary>();

        vocabulary.IsKnown("read_probe_tickets").Should().BeTrue();
        vocabulary.Entries.Single(e => e.Name == "read_probe_tickets").Source.Should().Be("SampleProbe");
    }
}
