using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Spikes;

/// <summary>
/// Spike: an entry created in a batch is moved by a later step of the same batch.
/// </summary>
/// <remarks>
/// A batch holds each new stream until the commit, so a transition appended to the entry finds no
/// stream, and the document-mode append (<c>AppendOptimistic</c>) throws on a stream that does not
/// exist. The batch therefore starts its streams inside the open transaction
/// (<see cref="BatchTransaction.EagerStreamsScope"/>) whenever a step moves an entry the batch created.
/// These facts pin that: the stream reads ContentCreated then ContentTransitioned, the state is
/// the target state, and a batch that does not commit leaves nothing.
/// </remarks>
[Collection("Integration3")]
public class BatchCreateThenTransitionSpikeTests(IntegrationTestFixture host)
{
    private async Task<string> TypeAsync()
    {
        var name = $"bct{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Spike",
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Issued"],
                InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Issue", From = "Draft", To = "Issued" }],
            },
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<Guid> RunAsync(string type, bool commit)
    {
        using var hostScope = host.Services.CreateScope();
        var runner = hostScope.ServiceProvider.GetRequiredService<IContentBatchRunner>();
        var id = Guid.CreateVersion7();

        return await runner.RunAsync(async (scope, ct) =>
        {
            using var eager = scope.GetRequiredService<BatchTransaction>().EagerStreamsScope();

            var creator = scope.GetRequiredService<IContentCreator>();
            var session = scope.GetRequiredService<IDocumentSession>();
            var request = new ContentCreateRequest
            {
                ContentType = type,
                Data = new Dictionary<string, object> { ["Title"] = "spike" },
                Id = id,
            };

            await creator.StageAsync(request, Guid.NewGuid(), new ContentCreateBatch(), ct);
            await session.SaveChangesAsync(ct);

            var content = await session.LoadAsync<Content>(id, ct);
            var moved = await scope.GetRequiredService<IContentTransitioner>().TransitionAsync(
                content!, "Issue", ContentTransitionActor.ForSystem("spike"),
                new ContentTransitionOptions { SkipPermissionChecks = true }, ct);
            moved.Succeeded.Should().BeTrue(string.Join("; ", moved.Errors));

            return new ContentBatchOutcome<Guid>(id, commit);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_stream_holds_the_create_then_the_transition_and_the_state_is_the_target()
    {
        var id = await RunAsync(await TypeAsync(), commit: true);

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Select(e => e.EventType).Should().Equal(typeof(ContentCreated), typeof(ContentTransitioned));
        (await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken))!
            .LifecycleState.Should().Be("Issued");
    }

    [Fact]
    public async Task A_batch_that_does_not_commit_leaves_no_stream_and_no_document()
    {
        var id = await RunAsync(await TypeAsync(), commit: false);

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_batch_that_never_opens_the_scope_still_defers_its_streams()
    {
        using var hostScope = host.Services.CreateScope();
        var runner = hostScope.ServiceProvider.GetRequiredService<IContentBatchRunner>();

        var (eager, defers) = await runner.RunAsync((scope, ct) =>
        {
            var batch = scope.GetRequiredService<BatchTransaction>();
            return Task.FromResult(new ContentBatchOutcome<(bool, bool)>((batch.EagerStreams, batch.DefersStreams), false));
        }, TestContext.Current.CancellationToken);

        eager.Should().BeFalse();
        defers.Should().BeTrue();
    }

    [Fact]
    public async Task The_flag_ends_with_its_scope_and_does_not_reach_a_second_batch()
    {
        using var hostScope = host.Services.CreateScope();
        var runner = hostScope.ServiceProvider.GetRequiredService<IContentBatchRunner>();

        var (inside, after) = await runner.RunAsync((scope, ct) =>
        {
            var batch = scope.GetRequiredService<BatchTransaction>();
            bool inScope;
            using (batch.EagerStreamsScope())
                inScope = batch.EagerStreams;
            return Task.FromResult(new ContentBatchOutcome<(bool, bool)>((inScope, batch.EagerStreams), false));
        }, TestContext.Current.CancellationToken);

        inside.Should().BeTrue();
        after.Should().BeFalse();

        var next = await runner.RunAsync((scope, ct) => Task.FromResult(
            new ContentBatchOutcome<bool>(scope.GetRequiredService<BatchTransaction>().EagerStreams, false)),
            TestContext.Current.CancellationToken);
        next.Should().BeFalse();
    }
}
