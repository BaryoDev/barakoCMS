using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>Transition moves an entry as the system, treats an entry already there as done, and sorts refusals.</summary>
[Collection("Sequential")]
public class TransitionActionTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Moves_the_target_and_reports_from_and_to()
    {
        var (type, id) = await StoreTicketAsync();

        var result = await RunAsync(Parameters(id, type, "Call"), Subject());

        result.Succeeded.Should().BeTrue(result.Error);
        result.Outputs["from"].Should().Be("Waiting");
        result.Outputs["to"].Should().Be("Done");
        result.Outputs["skipped"].Should().Be("false");
        (await LoadAsync(id)).LifecycleState.Should().Be("Done");
        type.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_subject_is_the_default_target()
    {
        var (type, id) = await StoreTicketAsync();

        var result = await RunAsync(new() { ["Transition"] = "Call", ["ContentType"] = type }, await LoadAsync(id));

        result.Succeeded.Should().BeTrue(result.Error);
        (await LoadAsync(id)).LifecycleState.Should().Be("Done");
    }

    [Fact]
    public async Task Already_in_the_target_state_is_success_and_skipped()
    {
        var (type, id) = await StoreTicketAsync();
        await RunAsync(Parameters(id, type, "Call"), Subject());

        var again = await RunAsync(Parameters(id, type, "Call"), Subject());

        again.Succeeded.Should().BeTrue(again.Error);
        again.Outputs["skipped"].Should().Be("true");
    }

    [Fact]
    public async Task An_unknown_transition_is_permanent()
    {
        var (type, id) = await StoreTicketAsync();

        var result = await RunAsync(Parameters(id, type, "Nope"), Subject());

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Theory]
    [InlineData(ContentTransitionOutcome.RuleFailed, false)]
    [InlineData(ContentTransitionOutcome.Invalid, false)]
    [InlineData(ContentTransitionOutcome.Forbidden, false)]
    [InlineData(ContentTransitionOutcome.Conflict, true)]
    public async Task A_refusal_is_permanent_and_a_conflict_is_retryable(ContentTransitionOutcome outcome, bool retryable)
    {
        var (type, id) = await StoreTicketAsync();
        var stub = new StubTransitioner(outcome switch
        {
            ContentTransitionOutcome.RuleFailed => ContentTransitionResult.RuleFailed("A rule said no."),
            ContentTransitionOutcome.Invalid => ContentTransitionResult.Invalid(["Bad value."]),
            ContentTransitionOutcome.Forbidden => ContentTransitionResult.Forbidden("Not allowed."),
            _ => ContentTransitionResult.Conflict("Changed by another writer."),
        });

        using var scope = fixture.Services.CreateScope();
        var action = new TransitionAction(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), stub);
        var result = await action.RunAsync(Parameters(id, type, "Call"), Subject(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().Be(retryable);
        result.Error.Should().NotBeNullOrEmpty();
        stub.Options!.SkipPermissionChecks.Should().BeTrue();
        stub.Actor!.IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task A_target_of_another_type_is_refused_and_not_moved()
    {
        var (type, id) = await StoreTicketAsync();

        var result = await RunAsync(Parameters(id, "some-other-type", "Call"), Subject());

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("some-other-type");
        (await LoadAsync(id)).LifecycleState.Should().Be("Waiting");
        type.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_target_from_another_branch_is_refused_and_not_moved()
    {
        var (type, id) = await StoreTicketAsync();
        var subject = Subject();
        subject.BranchId = Guid.CreateVersion7();

        var result = await RunAsync(Parameters(id, type, "Call"), subject);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("another branch");
        (await LoadAsync(id)).LifecycleState.Should().Be("Waiting");
    }

    [Fact]
    public async Task A_missing_content_type_is_a_permanent_failure()
    {
        var (_, id) = await StoreTicketAsync();

        var result = await RunAsync(new() { ["Target"] = id.ToString(), ["Transition"] = "Call" }, Subject());

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        (await LoadAsync(id)).LifecycleState.Should().Be("Waiting");
    }

    private static Content Subject() => new() { Id = Guid.NewGuid(), ContentType = "Order" };

    private static Dictionary<string, string> Parameters(Guid target, string type, string transition) =>
        new() { ["Target"] = target.ToString(), ["ContentType"] = type, ["Transition"] = transition };

    private async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content subject)
    {
        using var scope = fixture.Services.CreateScope();
        var action = ActivatorUtilities.CreateInstance<TransitionAction>(scope.ServiceProvider);
        return await action.RunAsync(parameters, subject, Ct);
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, Ct))!;
    }

    /// <summary>A type with Call from Waiting to Done, and one entry made through the create path so it has a stream.</summary>
    private async Task<(string Type, Guid Id)> StoreTicketAsync()
    {
        var type = "ticket-" + Guid.NewGuid().ToString("n")[..10];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" }],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Waiting", "Done"],
                InitialState = "Waiting",
                Transitions = [new StateTransition { Name = "Call", From = "Waiting", To = "Done" }],
            },
        });
        await session.SaveChangesAsync(Ct);

        var created = await scope.ServiceProvider.GetRequiredService<IContentCreator>().StageAsync(
            new ContentCreateRequest { ContentType = type, Data = new() { ["Name"] = "A ticket" } },
            Guid.NewGuid(), batch: null, Ct);
        await session.SaveChangesAsync(Ct);
        return (type, created.Id);
    }

    /// <summary>Answers every move with one result and keeps what it was asked.</summary>
    private sealed class StubTransitioner(ContentTransitionResult result) : IContentTransitioner
    {
        public ContentTransitionActor? Actor { get; private set; }
        public ContentTransitionOptions? Options { get; private set; }

        public Task<ContentTransitionResult> TransitionAsync(
            Content content, string transition, ContentTransitionActor actor,
            ContentTransitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Actor = actor;
            Options = options;
            return Task.FromResult(result);
        }
    }
}
