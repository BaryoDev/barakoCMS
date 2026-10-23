using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class CreateEntryActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Content Trigger() => new() { Id = Guid.NewGuid(), ContentType = "order" };

    private static (CreateEntryAction Action, IContentCreator Creator, IDocumentSession Session) Build(IReadOnlyList<string> errors)
    {
        var creator = Substitute.For<IContentCreator>();
        creator.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(errors);
        creator.StageAsync(default!, default, default, default)
            .ReturnsForAnyArgs(_ => new Content { Id = Guid.NewGuid(), ContentType = "task" });
        var session = Substitute.For<IDocumentSession>();
        return (new CreateEntryAction(session, creator, NullLogger<CreateEntryAction>.Instance), creator, session);
    }

    [Fact]
    public async Task A_valid_entry_is_created_with_the_resolved_fields_and_its_id_is_returned()
    {
        var (action, creator, session) = Build([]);

        var result = await action.RunAsync(
            new Dictionary<string, string>
            {
                ["ContentType"] = "task",
                ["Status"] = "Published",
                ["Data.Title"] = "Review order 7",
            },
            Trigger(),
            Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Outputs.Should().ContainKey("Id");
        var staged = creator.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IContentCreator.StageAsync))
            .Select(c => (ContentCreateRequest)c.GetArguments()[0]!)
            .ToList();
        staged.Should().HaveCount(1);
        staged[0].ContentType.Should().Be("task");
        staged[0].Status.Should().Be(ContentStatus.Published);
        staged[0].Data["Title"].Should().Be("Review order 7");
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_data_fails_the_step_and_nothing_is_staged_or_saved()
    {
        var (action, creator, session) = Build(["Title is required."]);

        var result = await action.RunAsync(
            new Dictionary<string, string> { ["ContentType"] = "task" }, Trigger(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Title is required.");
        await creator.DidNotReceiveWithAnyArgs().StageAsync(default!, default, default, default);
        await session.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task The_entry_is_checked_as_the_workflow_and_not_as_a_user()
    {
        var (action, creator, _) = Build([]);

        await action.RunAsync(new Dictionary<string, string> { ["ContentType"] = "task" }, Trigger(), Ct);

        var check = creator.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IContentCreator.CheckAsync)).ToList();
        check.Should().HaveCount(1);
        check[0].GetArguments()[1].Should().Be(Guid.Empty);
        ((HttpContext)check[0].GetArguments()[2]!).User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_content_type_or_an_unknown_status_fails_the_step()
    {
        var (action, creator, _) = Build([]);

        var noType = await action.RunAsync(new Dictionary<string, string>(), Trigger(), Ct);
        var badStatus = await action.RunAsync(
            new Dictionary<string, string> { ["ContentType"] = "task", ["Status"] = "Nope" }, Trigger(), Ct);

        noType.Succeeded.Should().BeFalse();
        badStatus.Succeeded.Should().BeFalse();
        await creator.DidNotReceiveWithAnyArgs().CheckAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task A_rerun_with_the_same_idempotency_key_returns_the_entry_it_already_made()
    {
        var (action, creator, session) = Build([]);
        var parameters = new Dictionary<string, string>
        {
            ["ContentType"] = "task",
            ["IdempotencyKey"] = "run-1:2",
        };

        await action.RunAsync(parameters, Trigger(), Ct);
        var staged = creator.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IContentCreator.StageAsync))
            .Select(c => (ContentCreateRequest)c.GetArguments()[0]!)
            .ToList();
        staged.Should().HaveCount(1);
        var id = staged[0].Id;
        id.Should().NotBeNull();

        session.LoadAsync<Content>(id!.Value, Arg.Any<CancellationToken>())
            .Returns(new Content { Id = id.Value, ContentType = "task" });
        var rerun = await action.RunAsync(parameters, Trigger(), Ct);

        rerun.Succeeded.Should().BeTrue(rerun.Error);
        rerun.Outputs["Id"].Should().Be(id.Value.ToString());
        creator.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IContentCreator.StageAsync))
            .Should().Be(1);
    }
}
