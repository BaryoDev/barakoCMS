using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Tests.Clinic;
using FluentAssertions;
using Marten;
using NSubstitute;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>What the registry accepts from a module, and what a workflow may be saved on because of it.</summary>
public class WorkflowTriggerRegistryTests
{
    private sealed class Declares(string name, Action<IModuleWorkflowTriggers> declare) : IBarakoModule
    {
        public string Name => name;

        public void ConfigureWorkflowTriggers(IModuleWorkflowTriggers triggers) => declare(triggers);
    }

    [Fact]
    public void A_module_trigger_is_registered_with_the_module_that_named_it()
    {
        var registry = new WorkflowTriggerRegistry([new ClinicProbeModule()]);

        registry.ModuleTriggers.Should().ContainSingle()
            .Which.Should().Be(new ModuleWorkflowTrigger(
                ClinicProbeModule.TicketCalledTrigger, "ClinicProbe", "A ticket was called to a counter."));
        registry.IsRegistered(ClinicProbeModule.TicketCalledTrigger).Should().BeTrue();
        registry.IsModuleTrigger(ClinicProbeModule.TicketCalledTrigger).Should().BeTrue();
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Updated")]
    [InlineData("Deleted")]
    [InlineData("Published")]
    [InlineData("Unpublished")]
    [InlineData("transition:Approve")]
    public void Core_triggers_are_registered_and_are_not_a_module_s_to_fire(string trigger)
    {
        var registry = new WorkflowTriggerRegistry([new ClinicProbeModule()]);

        registry.IsRegistered(trigger).Should().BeTrue();
        registry.IsModuleTrigger(trigger).Should().BeFalse();
    }

    [Theory]
    [InlineData("probe.nothing")]
    [InlineData("published")]
    [InlineData("")]
    public void A_name_nothing_registered_is_not_registered(string trigger)
    {
        new WorkflowTriggerRegistry([new ClinicProbeModule()]).IsRegistered(trigger).Should().BeFalse();
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("Forms.Submitted")]
    [InlineData("forms submitted")]
    [InlineData("transition:forms.x")]
    [InlineData("")]
    public void A_name_without_a_dot_or_outside_the_pattern_stops_the_host(string name)
    {
        var act = () => new WorkflowTriggerRegistry([new Declares("Forms", t => t.Add(name, "Something happened."))]);

        act.Should().Throw<ArgumentException>().WithMessage("*Forms*");
    }

    [Fact]
    public void Two_modules_registering_one_name_stops_the_host()
    {
        var act = () => new WorkflowTriggerRegistry([
            new Declares("One", t => t.Add("shared.thing", "One.")),
            new Declares("Two", t => t.Add("shared.thing", "Two.")),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*One*Two*");
    }

    [Fact]
    public void A_trigger_with_no_description_stops_the_host()
    {
        var act = () => new WorkflowTriggerRegistry([new Declares("Forms", t => t.Add("forms.submitted", " "))]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_validator_accepts_a_registered_module_trigger_and_refuses_one_nobody_registered()
    {
        var plugins = Substitute.For<IWorkflowPluginRegistry>();
        plugins.IsActionRegistered("Email").Returns(true);
        plugins.GetAllActions().Returns([]);

        var validator = new WorkflowSchemaValidator(plugins, Substitute.For<IQuerySession>(), null,
            new WorkflowTriggerRegistry([new ClinicProbeModule()]));

        validator.Validate(Workflow(ClinicProbeModule.TicketCalledTrigger), TestContext.Current.CancellationToken).IsValid.Should().BeTrue();

        var refused = validator.Validate(Workflow("probe.nothing"), TestContext.Current.CancellationToken);
        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().ContainSingle(e => e.Field == "triggerEvent")
            .Which.Message.Should().Contain(ClinicProbeModule.TicketCalledTrigger, "the error lists what may be chosen");

        var listed = validator.Validate(Workflow("Created", ClinicProbeModule.TicketCalledTrigger), TestContext.Current.CancellationToken);
        listed.IsValid.Should().BeTrue("a list entry is checked against the same registry");
    }

    private static WorkflowDefinition Workflow(string trigger, params string[] more) => new()
    {
        Name = "probe",
        TriggerContentType = "ticket",
        TriggerEvent = trigger,
        TriggerEvents = more.ToList(),
        Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" } }],
    };
}
