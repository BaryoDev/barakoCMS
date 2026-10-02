using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The policy on the way in: saved through the API, refused when it names nothing, copied onto a
/// run when the workflow fires, and honoured by the engine that runs actions in line.
/// </summary>
[Collection("Sequential")]
public class WorkflowHaltDefinitionTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowHaltDefinitionTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static Dictionary<string, string> EmailParameters => new()
    {
        ["To"] = "a@example.com",
        ["Subject"] = "s",
        ["Body"] = "b",
    };

    /// <summary>
    /// One action names the policy and one leaves it out, in the same request, so "Halt" coming
    /// back is not the answer for every action.
    /// </summary>
    [Fact]
    public async Task A_workflow_saved_with_a_halting_action_keeps_the_policy_and_defaults_the_rest_to_continue()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("576-wf"),
            triggerContentType = WorkflowStopHarness.NewName("chain"),
            triggerEvent = WorkflowEvents.Published,
            actions = new object[]
            {
                new { type = "Email", parameters = EmailParameters, onFailure = "Halt" },
                new { type = "Email", parameters = EmailParameters },
            },
        }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        Guid id;
        using (var doc = JsonDocument.Parse(body))
        {
            id = doc.RootElement.GetProperty("id").GetGuid();

            var actions = doc.RootElement.GetProperty("actions");
            actions.GetArrayLength().Should().Be(2);
            actions.EnumerateArray().Select(a => a.GetProperty("onFailure").ValueKind)
                .Should().Equal(JsonValueKind.String, JsonValueKind.String);
            actions.EnumerateArray().Select(a => a.GetProperty("onFailure").GetString())
                .Should().Equal("Halt", "Continue");
        }

        var stored = await _harness.LoadWorkflowAsync(id);
        stored.Should().NotBeNull();
        stored!.Actions.Should().HaveCount(2);
        stored.Actions.Select(a => a.OnFailure).Should().Equal(WorkflowFailurePolicy.Halt, WorkflowFailurePolicy.Continue);
    }

    /// <summary>
    /// A number is read wherever a name is, so 7 arrives as a policy that names nothing.
    /// </summary>
    [Fact]
    public async Task A_policy_that_names_nothing_is_refused_and_nothing_is_stored()
    {
        var admin = await _harness.AdminAsync();
        var name = WorkflowStopHarness.NewName("576-bad");

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name,
            triggerContentType = WorkflowStopHarness.NewName("chain"),
            triggerEvent = WorkflowEvents.Published,
            actions = new object[]
            {
                new { type = "Email", parameters = EmailParameters, onFailure = 7 },
            },
        }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0}", body);
        body.Should().Contain("onFailure must be one of: Continue, Halt");

        await using var session = _harness.Store.QuerySession();
        (await session.Query<WorkflowDefinition>().Where(w => w.Name == name).CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>
    /// A run carries a copy of its actions, so the policy has to be copied with them or the runner
    /// never sees it.
    /// </summary>
    /// <remarks>
    /// Read straight after queueing and only for the policy. The hosted runner may take the run in
    /// between, and nothing it writes changes that field.
    /// </remarks>
    [Fact]
    public async Task A_queued_run_copies_each_actions_policy_from_the_workflow()
    {
        var contentType = WorkflowStopHarness.NewName("chain");
        var workflowId = await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Actions =
            [
                new WorkflowAction { Type = WorkflowStopHarness.Counting, OnFailure = WorkflowFailurePolicy.Halt },
                new WorkflowAction { Type = WorkflowStopHarness.Counting },
                new WorkflowAction { Type = WorkflowStopHarness.Counting, OnFailure = null },
            ];
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

            (await queue.EnqueueAsync(
                NewEntry(contentType), WorkflowEvents.Published, Random.Shared.NextInt64(1, long.MaxValue), Ct))
                .Should().Be(1);
        }

        var runs = await _harness.RunsOfAsync(workflowId);
        runs.Should().HaveCount(1);
        runs[0].Actions.Should().HaveCount(3);
        runs[0].Actions.OrderBy(a => a.Ordinal).Select(a => a.OnFailure)
            .Should().Equal(WorkflowFailurePolicy.Halt, WorkflowFailurePolicy.Continue, WorkflowFailurePolicy.Continue);
    }

    /// <summary>
    /// A name is refused where the request is read, before the validator. The answer is a 400 and
    /// not a 500, and nothing is stored.
    /// </summary>
    [Fact]
    public async Task A_policy_sent_as_a_name_that_is_not_one_is_refused_with_a_400_and_nothing_is_stored()
    {
        var admin = await _harness.AdminAsync();
        var name = WorkflowStopHarness.NewName("576-stop");

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name,
            triggerContentType = WorkflowStopHarness.NewName("chain"),
            triggerEvent = WorkflowEvents.Published,
            actions = new object[]
            {
                new { type = "Email", parameters = EmailParameters, onFailure = "Stop" },
            },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0}", await res.Content.ReadAsStringAsync(Ct));

        await using var session = _harness.Store.QuerySession();
        (await session.Query<WorkflowDefinition>().Where(w => w.Name == name).CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>
    /// A client that sends its optional fields as null is not refused. Null reads as Continue.
    /// </summary>
    [Fact]
    public async Task A_policy_sent_as_null_is_accepted_and_reads_as_continue()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("576-null"),
            triggerContentType = WorkflowStopHarness.NewName("chain"),
            triggerEvent = WorkflowEvents.Published,
            actions = new object[]
            {
                new { type = "Email", parameters = EmailParameters, onFailure = (string?)null },
            },
        }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var actions = doc.RootElement.GetProperty("actions");
        actions.GetArrayLength().Should().Be(1);
        actions[0].GetProperty("onFailure").GetString().Should().Be("Continue");
    }

    /// <summary>
    /// A child of a Conditional has no policy. One that names it is refused, and the same workflow
    /// without the key is accepted, so the refusal is the key and nothing else about the request.
    /// </summary>
    [Fact]
    public async Task A_policy_on_a_child_of_a_conditional_is_refused_and_the_same_child_without_it_is_accepted()
    {
        var admin = await _harness.AdminAsync();
        var refused = WorkflowStopHarness.NewName("576-child");

        var withPolicy = JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = EmailParameters, OnFailure = "Halt" },
        });
        var without = JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = EmailParameters },
        });

        var res = await admin.PostAsJsonAsync("/api/workflows", Conditional(refused, withPolicy), Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0}", body);
        body.Should().Contain("An action inside a Conditional has no onFailure");

        await using (var session = _harness.Store.QuerySession())
        {
            (await session.Query<WorkflowDefinition>().Where(w => w.Name == refused).CountAsync(Ct)).Should().Be(0);
        }

        var accepted = await admin.PostAsJsonAsync(
            "/api/workflows", Conditional(WorkflowStopHarness.NewName("576-child-ok"), without), Ct);
        accepted.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    private static object Conditional(string name, string thenActions) => new
    {
        name,
        triggerContentType = WorkflowStopHarness.NewName("chain"),
        triggerEvent = WorkflowEvents.Published,
        actions = new object[]
        {
            new
            {
                type = "Conditional",
                parameters = new Dictionary<string, string>
                {
                    ["Condition"] = "{{status}} == Published",
                    ["ThenActions"] = thenActions,
                },
            },
        },
    };

    /// <summary>
    /// The engine that runs actions in line, which a host can still call. Two workflows on one
    /// event, each a failing action followed by a counting one, differing only in the policy.
    /// </summary>
    /// <remarks>
    /// One case for each way an action fails there: it throws, it returns a failure, no handler is
    /// registered for its type, and it holds a credential the key cannot decrypt.
    ///
    /// The twin that continues shows the engine reached both workflows, so a count of zero for the
    /// halting one is the policy and not an event that fired nothing.
    /// </remarks>
    [Theory]
    [InlineData("throws")]
    [InlineData("returns a failure")]
    [InlineData("has no handler")]
    [InlineData("holds a credential that cannot be decrypted")]
    public async Task The_engine_stops_at_a_halting_action_that_fails_and_records_the_rest_as_not_run(string failure)
    {
        var contentType = WorkflowStopHarness.NewName("chain");
        var afterHalt = Guid.NewGuid().ToString("N");
        var afterContinue = Guid.NewGuid().ToString("N");

        var halting = await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Actions = [Failing(failure, WorkflowFailurePolicy.Halt), Keyed(afterHalt)];
        });
        await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Actions = [Failing(failure, WorkflowFailurePolicy.Continue), Keyed(afterContinue)];
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

            await engine.ProcessEventAsync(contentType, WorkflowEvents.Published, NewEntry(contentType), Ct);
        }

        CountingRunnerAction.RunsByKey.GetValueOrDefault(afterContinue).Should().Be(1,
            "the action before it failed, or this twin proves nothing: {0}", failure);
        CountingRunnerAction.RunsByKey.GetValueOrDefault(afterHalt).Should().Be(0);

        await using var session = _harness.Store.QuerySession();
        var logs = await session.Query<WorkflowExecutionLog>().Where(l => l.WorkflowId == halting).ToListAsync(Ct);

        logs.Should().HaveCount(1);
        logs[0].Success.Should().BeFalse();
        logs[0].Actions.Should().HaveCount(2, "an action that was not run is still recorded");
        logs[0].Actions[0].Success.Should().BeFalse();
        logs[0].Actions[1].ActionType.Should().Be(WorkflowStopHarness.Counting);
        logs[0].Actions[1].Success.Should().BeFalse();
        logs[0].Actions[1].ErrorMessage.Should().Be(WorkflowRun.SkippedAfterHalt);
    }

    private static WorkflowAction Failing(string failure, WorkflowFailurePolicy policy) => failure switch
    {
        "throws" => new WorkflowAction { Type = "ThrowingRunner", OnFailure = policy },
        "returns a failure" => new WorkflowAction { Type = "HookedRunner", OnFailure = policy },
        "has no handler" => new WorkflowAction { Type = "NoSuchAction", OnFailure = policy },
        "holds a credential that cannot be decrypted" => new WorkflowAction
        {
            Type = WorkflowStopHarness.Counting,
            OnFailure = policy,
            // An envelope written under a key this host does not have, as after a changed Secrets:Key.
            Parameters = new Dictionary<string, string>
            {
                ["ApiKey"] = AesGcmEnvelope.VersionPrefix
                    + AesGcmEnvelope.Protect(AesGcmEnvelope.DeriveKey("a key this host does not have"), "sk_test"),
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "not a failure this test knows"),
    };

    private static WorkflowAction Keyed(string key) => new()
    {
        Type = WorkflowStopHarness.Counting,
        Parameters = new Dictionary<string, string> { ["IdempotencyKey"] = key },
    };

    private static Content NewEntry(string contentType) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = contentType,
        Data = new Dictionary<string, object>(),
    };
}
