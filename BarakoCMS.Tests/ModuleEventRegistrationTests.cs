using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Xunit;

namespace BarakoCMS.Tests;

public class ModuleEventRegistrationTests
{
    private sealed class Probe : IBarakoModule
    {
        public string Name => "Probe";
    }

    public sealed record ProbeHappened(Guid Id);

    public sealed class ProbeDoc
    {
        public Guid Id { get; set; }
    }

    public sealed class ProbeDocProjection : SingleStreamProjection<ProbeDoc, Guid>
    {
        public ProbeDoc Create(ProbeHappened e) => new() { Id = e.Id };
    }

    // Owned projection, but it writes core's User document.
    public sealed class UserStealingProjection : SingleStreamProjection<User, Guid>
    {
        public User Create(ProbeHappened e) => new() { Id = e.Id };
    }

    private sealed class BareSchema : IModuleSchema
    {
        public MartenRegistry.DocumentMappingExpression<T> For<T>() => throw new NotImplementedException();
    }

    [Fact]
    public void AddEventType_OwnedType_MapsModuleAlias()
    {
        var options = new StoreOptions();

        new ModuleSchema(options, new Probe()).AddEventType<ProbeHappened>();

        ((EventGraph)options.Events).EventMappingFor(typeof(ProbeHappened)).EventTypeName
            .Should().Be("probe.probe_happened");
    }

    [Fact]
    public void AddEventType_CoreType_Throws()
    {
        var schema = new ModuleSchema(new StoreOptions(), new Probe());

        var act = () => schema.AddEventType<User>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Probe*")
            .WithMessage("*own assemblies*")
            .Where(e => e.Message.Contains("barakoCMS.Models.User"));
    }

    [Fact]
    public void AddProjection_OwnedProjectionAndDocument_Registers()
    {
        var schema = new ModuleSchema(new StoreOptions(), new Probe());

        var act = () => schema.AddProjection<ProbeDocProjection>(ProjectionLifecycle.Inline);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddProjection_ProjectsIntoCoreDocument_Throws()
    {
        var schema = new ModuleSchema(new StoreOptions(), new Probe());

        var act = () => schema.AddProjection<UserStealingProjection>(ProjectionLifecycle.Inline);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("barakoCMS.Models.User") && e.Message.Contains("Probe"));
    }

    [Fact]
    public void For_CoreType_KeepsExistingMessage()
    {
        var schema = new ModuleSchema(new StoreOptions(), new Probe());

        var act = () => schema.For<User>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*configure the schema for*")
            .WithMessage("*may only configure document types from its own assemblies*");
    }

    [Theory]
    [InlineData("ClinicProbe", typeof(ProbeHappened), "clinic-probe.probe_happened")]
    [InlineData("Queue", typeof(ProbeDoc), "queue.probe_doc")]
    [InlineData("HTTPTools", typeof(ProbeDocProjection), "http-tools.probe_doc_projection")]
    [InlineData("my module", typeof(ProbeHappened), "my-module.probe_happened")]
    public void EventAlias_Names_AreStableAndLowercase(string module, Type type, string expected)
    {
        ModuleSchema.EventAlias(module, type).Should().Be(expected);
    }

    [Fact]
    public void ProjectedDocument_SingleStream_ReturnsDocumentType()
    {
        ModuleSchema.ProjectedDocument(typeof(ProbeDocProjection)).Should().Be(typeof(ProbeDoc));
    }

    [Fact]
    public void DefaultInterfaceMembers_OnOlderImplementation_ThrowNotSupported()
    {
        IModuleSchema schema = new BareSchema();

        var act = () => schema.AddEventType<ProbeHappened>();

        act.Should().Throw<NotSupportedException>().WithMessage("*4.7*");
    }
}
