using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What makes a workflow fire, how often, and what the run record says about it.
/// </summary>
/// <remarks>
/// Every assertion about something NOT happening is paired with one that proves it still happens
/// when it should. A projection that fires nothing passes every duplicate-suppression test on its
/// own, and that is the failure this area keeps producing.
///
/// The daemon is asynchronous, so the counts are polled to a timeout rather than read once after a
/// fixed sleep. The invariant in each case is "exactly N, eventually", so waiting for the first and
/// then asserting the total is the invariant stated properly: a workflow that never fires still
/// fails, on the timeout, with the reason attached.
/// </remarks>
[Collection("Sequential")]
public class WorkflowFiringTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public WorkflowFiringTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Publishing_an_item_that_is_already_published_appends_no_second_event()
    {
        await AuthenticateAsync();
        var id = await CreateContentAsync(NewTypeName());

        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();

        (await CountStatusChangesAsync(id)).Should().Be(1,
            "a repeat of the current status changed nothing, and an event that changed nothing is a "
          + "publish in the content history that never happened");
    }

    [Fact]
    public async Task Publishing_an_item_that_is_already_published_does_not_run_the_actions_again()
    {
        await AuthenticateAsync();
        var contentType = NewTypeName();
        var probeType = NewTypeName();
        await CreateWorkflowAsync(contentType, probeType);

        var id = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();

        var runs = await WaitForProbesAsync(probeType, atLeast: 1);

        runs.Should().Be(1,
            "the second request published nothing, so re-running every Published workflow sends the "
          + "confirmation email twice and calls the webhook twice");
    }

    [Fact]
    public async Task Publishing_again_after_a_return_to_draft_does_run_the_actions_again()
    {
        await AuthenticateAsync();
        var contentType = NewTypeName();
        var probeType = NewTypeName();
        await CreateWorkflowAsync(contentType, probeType);

        var id = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();
        (await ChangeStatusAsync(id, ContentStatus.Draft)).EnsureSuccessStatusCode();
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();

        var runs = await WaitForProbesAsync(probeType, atLeast: 2);

        runs.Should().Be(2,
            "this item was published twice, with a real transition each time. Suppressing the second "
          + "one would be a worse bug than the duplicate it was meant to prevent");

        (await CountStatusChangesAsync(id)).Should().Be(3, "each of the three transitions changed the status");
    }

    [Fact]
    public async Task An_action_that_reports_failure_is_recorded_on_the_run_with_its_reason()
    {
        await AuthenticateAsync();

        // A URL the outbound guard refuses. It fails before any socket is opened, so the test needs
        // no network and no listener, and the refusal is one of the outcomes that used to be a log
        // line and nothing else.
        var contentType = NewTypeName();
        var workflowId = await CreateWorkflowAsync(contentType, probeContentType: null, webhookUrl: "not-a-url");

        var id = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();

        var run = await WaitForRunAsync(workflowId);

        run.Success.Should().BeFalse("an action failed, so the run failed");
        run.Actions.Should().ContainSingle();
        run.Actions[0].ActionType.Should().Be("Webhook");
        run.Actions[0].Success.Should().BeFalse();
        run.Actions[0].ErrorMessage.Should().Contain("not allowed",
            "the reason is the point: without it a failed action and an action that did nothing look "
          + "identical once the log line has scrolled away");
    }

    [Fact]
    public async Task An_action_that_succeeds_is_recorded_as_successful()
    {
        await AuthenticateAsync();
        var contentType = NewTypeName();
        var probeType = NewTypeName();
        var workflowId = await CreateWorkflowAsync(contentType, probeType);

        var id = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(id, ContentStatus.Published)).EnsureSuccessStatusCode();

        var run = await WaitForRunAsync(workflowId);

        run.Success.Should().BeTrue();
        run.Actions.Should().ContainSingle();
        run.Actions[0].Success.Should().BeTrue(
            "a run record that only ever says 'failed' is as useless as one that never appears");
        run.Actions[0].ErrorMessage.Should().BeNull();
    }

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // Every test gets its own content type. The daemon is asynchronous, so one test's events can
    // still be waiting when the next test registers a workflow, and a shared type made that workflow
    // fire for the previous test's content.
    private static string NewTypeName() => $"wf-{Guid.NewGuid():N}";

    private async Task<Guid> CreateContentAsync(string contentType)
    {
        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = contentType,
            Data = new Dictionary<string, object> { { "Title", "workflow subject" } },
        });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> ChangeStatusAsync(Guid id, ContentStatus status) =>
        _client.PutAsJsonAsync(
            $"/api/contents/{id}/status",
            new barakoCMS.Features.Content.ChangeStatus.Request { Id = id, NewStatus = status });

    /// <summary>
    /// A workflow on Published for one content type, which either creates a probe item or calls a
    /// webhook.
    /// </summary>
    private async Task<Guid> CreateWorkflowAsync(string triggerContentType, string? probeContentType, string? webhookUrl = null)
    {
        var action = webhookUrl is null
            ? new WorkflowAction
            {
                Type = "CreateTask",
                Parameters = new Dictionary<string, string>
                {
                    ["ContentType"] = probeContentType!,
                    ["Title"] = "probe",
                },
            }
            : new WorkflowAction
            {
                Type = "Webhook",
                Parameters = new Dictionary<string, string> { ["Url"] = webhookUrl },
            };

        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            TriggerContentType = triggerContentType,
            TriggerEvent = "Published",
            Actions = [action],
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        await session.SaveChangesAsync();

        return workflow.Id;
    }

    private async Task<int> CountStatusChangesAsync(Guid contentId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(contentId);

        return stream.Count(e => e.Data is barakoCMS.Events.ContentStatusChanged);
    }

    /// <summary>
    /// Waits for the action to have run at least <paramref name="atLeast"/> times, then returns the
    /// count so the caller can assert it is not more than that.
    /// </summary>
    private async Task<int> WaitForProbesAsync(string probeContentType, int atLeast)
    {
        var count = 0;

        await PollAsync(
            async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
                count = await session.Query<barakoCMS.Models.Content>()
                    .CountAsync(c => c.ContentType == probeContentType);

                return count >= atLeast;
            },
            $"the workflow never created {atLeast} '{probeContentType}' item(s). A workflow that "
          + "fires nothing satisfies every duplicate-suppression assertion in this class");

        // The daemon could still be processing a second event, which is the bug under test, so give
        // it room to produce the duplicate rather than racing it.
        await Task.Delay(TimeSpan.FromSeconds(2));

        using var finalScope = _factory.Services.CreateScope();
        var finalSession = finalScope.ServiceProvider.GetRequiredService<IQuerySession>();

        return await finalSession.Query<barakoCMS.Models.Content>()
            .CountAsync(c => c.ContentType == probeContentType);
    }

    private async Task<WorkflowExecutionLog> WaitForRunAsync(Guid workflowId)
    {
        WorkflowExecutionLog? run = null;

        await PollAsync(
            async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
                run = await session.Query<WorkflowExecutionLog>()
                    .Where(l => l.WorkflowId == workflowId)
                    .FirstOrDefaultAsync();

                return run is not null;
            },
            $"workflow {workflowId} recorded no run. Either it never fired, or the engine still has "
          + "no outcome to record, which is the whole point of the action result");

        return run!;
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {PollTimeout.TotalSeconds:0}s: {because}");
    }
}
