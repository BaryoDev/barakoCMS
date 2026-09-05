using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Workflow execution as a queue: what the projection records, what the runner claims, and what an
/// operator can do about a failure.
/// </summary>
/// <remarks>
/// The projection used to execute actions inline, inside Marten's async daemon, which processes a
/// shard sequentially. Three third-party calls held that shard for their whole duration, so one slow
/// provider stalled workflow processing for every tenant.
///
/// The two that carry the most weight here are the lease, which is what stops two nodes sending the
/// same email, and the treatment of a timeout as Unknown rather than Failed. Both are written to
/// take the claim explicitly rather than racing two workers and hoping they overlap, which is the
/// lesson MultiInstanceSchedulingTests already records.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunTests
{
    private readonly IntegrationTestFixture _factory;

    public WorkflowRunTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// Two claims of the same attempt: one wins, one is refused.
    /// </summary>
    /// <remarks>
    /// Taken explicitly rather than by racing two runners. A race that happens not to overlap passes
    /// while proving nothing, and this is the property that decides whether a customer gets one
    /// email or two.
    /// </remarks>
    [Fact]
    public async Task Two_nodes_cannot_claim_the_same_attempt()
    {
        var runId = await SeedRunAsync(actions: 1);
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using var first = store.LightweightSession();
        await using var second = store.LightweightSession();

        var a = await first.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);
        var b = await second.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);

        a!.Actions[0].Status = AttemptStatus.Running;
        a.Actions[0].LeasedBy = "node-a";
        a.Actions[0].LeaseExpiresAt = ParkedUntil;
        a.Recompute();
        first.Update(a);
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        b!.Actions[0].Status = AttemptStatus.Running;
        b.Actions[0].LeasedBy = "node-b";
        b.Actions[0].LeaseExpiresAt = ParkedUntil;
        b.Recompute();
        second.Update(b);

        var claimTwice = async () => await second.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await claimTwice.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Contain("Concurrency",
                "the second claim has to be refused, or both nodes send the same message");

        // The hosted runner is a third node. It polls every five seconds and treats a Running
        // attempt whose lease is not in the future as a dead node's work, so a claim written without
        // LeaseExpiresAt was taken from under this test whenever a poll landed between the first
        // save and the read below. Driving the poll here makes that deterministic: a claim the
        // runner would steal fails every time rather than on a slow CI box.
        await DrainRunnerAsync();

        await using var check = store.QuerySession();
        var latest = await check.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);
        latest!.Actions[0].LeasedBy.Should().Be("node-a", "the first claim stands");
    }

    /// <summary>
    /// The control: one node claiming an unclaimed attempt succeeds.
    /// </summary>
    /// <remarks>
    /// Without it, a document that refused every write would satisfy the test above and no action
    /// would ever run.
    /// </remarks>
    [Fact]
    public async Task One_node_claiming_an_unclaimed_attempt_succeeds()
    {
        var runId = await SeedRunAsync(actions: 1);
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession();
        var run = await session.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);

        run!.Actions[0].Status = AttemptStatus.Running;
        run.Actions[0].LeasedBy = "node-a";
        run.Recompute();
        session.Update(run);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        run.Status.Should().Be(RunStatus.Running);
    }

    /// <summary>
    /// A run reports PartiallyFailed when some actions worked and some did not.
    /// </summary>
    /// <remarks>
    /// Not rounded to Failed. "Post to Facebook, then email, then tweet" is three independent things,
    /// and reporting the whole run as failed because the mail server was down hides that two of them
    /// went out, which is exactly what somebody deciding whether to retry needs to know.
    /// </remarks>
    [Theory]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Succeeded, RunStatus.Succeeded)]
    [InlineData(AttemptStatus.Failed, AttemptStatus.Failed, RunStatus.Failed)]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Failed, RunStatus.PartiallyFailed)]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Unknown, RunStatus.PartiallyFailed)]
    [InlineData(AttemptStatus.Skipped, AttemptStatus.Succeeded, RunStatus.Succeeded)]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Pending, RunStatus.Running)]
    public void A_run_reports_what_actually_happened(AttemptStatus first, AttemptStatus second, RunStatus expected)
    {
        var run = new WorkflowRun
        {
            Actions =
            [
                new WorkflowActionAttempt { Ordinal = 0, Status = first },
                new WorkflowActionAttempt { Ordinal = 1, Status = second },
            ],
        };

        run.Recompute();

        run.Status.Should().Be(expected);
    }

    /// <summary>
    /// Retrying an action that already succeeded is refused.
    /// </summary>
    /// <remarks>
    /// The whole reason a run records each action separately is so retrying a failed third does not
    /// re-send the first two. A retry button that ignores this is how a customer gets two invoices.
    /// </remarks>
    [Fact]
    public async Task Retrying_an_action_that_succeeded_is_refused()
    {
        var runId = await SeedRunAsync(actions: 1, status: AttemptStatus.Succeeded);
        var client = await AdminClient();

        var res = await client.PostAsync($"/api/workflow-runs/{runId}/actions/0/retry", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("second time");
    }

    /// <summary>The control: a failed action can be retried, and comes back Pending.</summary>
    [Fact]
    public async Task Retrying_a_failed_action_queues_it_again()
    {
        var runId = await SeedRunAsync(actions: 1, status: AttemptStatus.Failed);
        var client = await AdminClient();

        var res = await client.PostAsync($"/api/workflow-runs/{runId}/actions/0/retry", null,
            TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("actions")[0].GetProperty("status").GetString()
            .Should().Be(nameof(AttemptStatus.Pending), "queued, not executed inside the request");
    }

    /// <summary>
    /// A retry does not reset the attempt count.
    /// </summary>
    /// <remarks>
    /// An action that keeps failing should still stop. Resetting the count on every manual retry
    /// turns the cap into a suggestion, and the cap is what stops this instance hammering a third
    /// party until they ban the account.
    ///
    /// The retry endpoint clears NextAttemptAt, so the attempt is due the moment this request
    /// commits, and the hosted runner polls every five seconds and could claim, run and record it
    /// before a second query got there: that race is what turned this into Attempts == 4 on a slow
    /// CI box. The response body is built from the same save the endpoint just made, before anything
    /// else could touch it, so reading Attempts from it cannot lose that race.
    ///
    /// The second assertion below has the same hosted runner to contend with, and DrainRunnerAsync's
    /// own runner does not settle it: it is one more node racing the hosted one for the same claim,
    /// and if the hosted runner wins, DrainRunnerAsync finds nothing left for it to claim and returns
    /// at once, with no guarantee the hosted runner's own pass has written the outcome yet. So this
    /// waits for the attempt to leave Running rather than trusting one read straight after the drain
    /// to land after whichever node actually did the work.
    /// </remarks>
    [Fact]
    public async Task A_retry_does_not_reset_the_attempt_count()
    {
        var runId = await SeedRunAsync(actions: 1, status: AttemptStatus.Failed, attempts: 3);
        var client = await AdminClient();

        var res = await client.PostAsync($"/api/workflow-runs/{runId}/actions/0/retry", null,
            TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("actions")[0].GetProperty("attempts").GetInt32()
            .Should().Be(3, "the count carries across a manual retry");

        // Drive the race deterministically rather than leaving it to a real 5-second timer: force
        // the runner to claim and finish the attempt this request just queued (no handler is
        // registered for "Webhook", so it terminates as Failed on one pass), and confirm what
        // actually happens to Attempts once it does. This also stops the now-due attempt from
        // lingering into later tests.
        await DrainRunnerAsync();

        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        WorkflowRun? run = null;

        // The hosted runner may have claimed the attempt instead of DrainRunnerAsync's own runner;
        // it still runs to completion, just on its own schedule. Poll for the attempt to leave
        // Running rather than reading once, bounded so a genuinely stuck attempt still fails loudly.
        for (var i = 0; i < 100; i++)
        {
            await using var session = store.QuerySession();
            run = await session.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);

            if (run!.Actions[0].Status != AttemptStatus.Running) break;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        run!.Actions[0].Attempts.Should().Be(4, "one real run afterward counts once, same as any other attempt");
    }

    /// <summary>
    /// The backoff grows and is bounded.
    /// </summary>
    /// <remarks>
    /// A run that retries forever is a self-inflicted denial of service against a third party, who
    /// answers by rate-limiting or banning the account, which takes down every other integration
    /// pointed at them.
    /// </remarks>
    [Fact]
    public void The_backoff_grows_and_is_capped()
    {
        var random = new Random(1);

        var first = barakoCMS.Features.Workflows.WorkflowRetryPolicy.Backoff(1, random);
        var later = barakoCMS.Features.Workflows.WorkflowRetryPolicy.Backoff(4, random);
        var absurd = barakoCMS.Features.Workflows.WorkflowRetryPolicy.Backoff(50, random);

        later.Should().BeGreaterThan(first, "an immediate retry of a provider that just failed is not a retry");
        absurd.Should().BeLessThan(TimeSpan.FromMinutes(15), "and it stops growing");
    }

    /// <summary>
    /// A list filter that is not a status is refused rather than ignored.
    /// </summary>
    /// <remarks>
    /// A silently dropped filter returns more rows than the caller asked for, and they cannot tell
    /// that from no matches.
    /// </remarks>
    [Fact]
    public async Task An_unknown_status_filter_is_refused()
    {
        var client = await AdminClient();

        var res = await client.GetAsync("/api/workflow-runs?status=Broken", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The control: a real status filters.</summary>
    [Fact]
    public async Task A_real_status_filters_the_list()
    {
        await SeedRunAsync(actions: 1, status: AttemptStatus.Failed);
        var client = await AdminClient();

        var res = await client.GetAsync("/api/workflow-runs?status=Failed", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0,
            "the run seeded above has a failed action, so the filter has something to find");
    }

    /// <summary>
    /// An attempt's stored state carries no response body and no parameters.
    /// </summary>
    /// <remarks>
    /// A 401 from an OAuth provider frequently contains the credential that was sent, and this is
    /// stored, served over the API and shown in the admin. The response shape has nowhere to put
    /// one, which is stronger than remembering to redact.
    /// </remarks>
    [Fact]
    public async Task The_api_returns_no_response_body_and_no_parameters()
    {
        var runId = await SeedRunAsync(actions: 1, status: AttemptStatus.Failed);
        var client = await AdminClient();

        var body = await (await client.GetAsync($"/api/workflow-runs/{runId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(body);
        var action = doc.RootElement.GetProperty("actions")[0];

        action.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["ordinal", "actionType", "status", "attempts", "nextAttemptAt", "responseStatus", "error", "completedAt", "durationMs"],
            "anything else here is a place a credential could arrive in");
    }

    /// <summary>
    /// A thrown action records its type, never the exception message that may contain a secret.
    /// </summary>
    /// <remarks>
    /// This uses the real runner and store rather than calling the catch block directly. A provider
    /// exception is the path that previously persisted its message into the run record, where the
    /// API and admin UI serve it back to operators.
    /// </remarks>
    [Fact]
    public async Task A_thrown_action_records_only_the_exception_type()
    {
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, ThrowingRunnerAction>()));
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Redaction regression",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = "ThrowingRunner",
                    Attempts = barakoCMS.Features.Workflows.WorkflowRetryPolicy.MaxAttempts - 1,
                    IdempotencyKey = $"{Guid.NewGuid():N}",
                },
            ],
        };

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = contentId,
                ContentType = "article",
                Status = ContentStatus.Published,
            });
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Other workflow runs may be older than this one in the shared database, and the hosted
        // runner can claim the run before this test's runner. Drain with the same host so the
        // registered throwing action is available to whichever pass reaches this run.
        await DrainRunnerAsync(host.Services);

        WorkflowRun? recorded = null;
        for (var i = 0; i < 100; i++)
        {
            await using var check = store.QuerySession();
            recorded = await check.LoadAsync<WorkflowRun>(run.Id, TestContext.Current.CancellationToken);

            if (recorded!.Actions[0].Status != AttemptStatus.Running) break;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        var attempt = recorded!.Actions.Should().ContainSingle().Subject;

        attempt.Status.Should().Be(AttemptStatus.Failed);
        attempt.Error.Should().Be(nameof(InvalidOperationException));
        attempt.Error.Should().NotContain("sk_live_1234567890");
    }

    /// <summary>Runs the hosted runner's poll until it finds nothing to claim.</summary>
    /// <remarks>
    /// Drained rather than polled once. RunOnceAsync returns as soon as it claims anything, and the
    /// database holds runs from other classes, so a single poll could return before it ever reached
    /// the run under test and the assertion after it would be vacuous. Draining to "nothing left to
    /// claim" means every candidate was examined.
    /// </remarks>
    private async Task DrainRunnerAsync(IServiceProvider? services = null)
    {
        services ??= _factory.Services;
        var runner = new barakoCMS.Features.Workflows.WorkflowRunner(
            services,
            services.GetRequiredService<ILogger<barakoCMS.Features.Workflows.WorkflowRunner>>(),
            services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(TestContext.Current.CancellationToken))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
    }

    private async Task<HttpClient> AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    /// <summary>
    /// The parking below is load-bearing, so it gets a gate rather than a comment.
    /// </summary>
    /// <remarks>
    /// Every seeded run in this class is parked out of the hosted runner's reach, because the runner
    /// polls every five seconds and claims any Pending or Running run it finds. Without that, the
    /// tests here pass alone and fail in a full suite, which is the shape that took two rounds of CI
    /// to find. This drives one poll directly and asserts the runner leaves a seeded run alone, so
    /// if the parking stops working it fails here and deterministically rather than somewhere else
    /// and sometimes.
    /// </remarks>
    [Fact]
    public async Task A_seeded_run_is_parked_where_the_hosted_runner_will_not_claim_it()
    {
        var pending = await SeedRunAsync(actions: 1);
        var running = await SeedRunAsync(actions: 1, status: AttemptStatus.Running);

        await DrainRunnerAsync();

        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var check = store.QuerySession();

        var afterPending = await check.LoadAsync<WorkflowRun>(pending, TestContext.Current.CancellationToken);
        afterPending!.Actions[0].LeasedBy.Should().BeNull("a pending attempt parked in the future is not due");
        afterPending.Actions[0].Status.Should().Be(AttemptStatus.Pending);

        var afterRunning = await check.LoadAsync<WorkflowRun>(running, TestContext.Current.CancellationToken);
        afterRunning!.Actions[0].LeasedBy.Should().Be("a-node-that-is-not-this-one",
            "a running attempt with a live lease belongs to somebody else");
    }

    /// <summary>
    /// Far enough out that the hosted runner never treats a seeded attempt as due, and far enough
    /// out that a slow suite does not walk into it.
    /// </summary>
    private static DateTimeOffset ParkedUntil => DateTimeOffset.UtcNow.AddHours(1);

    private sealed class ThrowingRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
    {
        public string Type => "ThrowingRunner";

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            throw new InvalidOperationException("provider rejected sk_live_1234567890");

        public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
            Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            throw new InvalidOperationException("provider rejected sk_live_1234567890");
    }

    private async Task<Guid> SeedRunAsync(
        int actions, AttemptStatus status = AttemptStatus.Pending, int attempts = 0)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Notify the team",
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
        };

        for (var i = 0; i < actions; i++)
        {
            run.Actions.Add(new WorkflowActionAttempt
            {
                Ordinal = i,
                ActionType = "Webhook",
                Status = status,
                Attempts = attempts,
                IdempotencyKey = $"{run.Id:N}-{i}",
                Error = status == AttemptStatus.Failed ? "the provider answered 500" : null,

                // Parked out of the hosted runner's reach. It polls every five seconds and claims
                // any Pending or Running run it finds, which includes one seeded here and about to
                // be asserted on: Two_nodes_cannot_claim_the_same_attempt failed in CI with
                // LeasedBy holding the runner's node name instead of the one the test wrote.
                //
                // A future NextAttemptAt makes NextDue skip a Pending attempt and a live lease makes
                // it skip a Running one, and with nothing due the runner returns without writing.
                // The alternative, taking the runner out of the fixture, is what broke every
                // WorkflowFiringTests case: those poll for the hosted runner to do the work.
                NextAttemptAt = status == AttemptStatus.Pending ? ParkedUntil : null,
                LeaseExpiresAt = status == AttemptStatus.Running ? ParkedUntil : null,
                LeasedBy = status == AttemptStatus.Running ? "a-node-that-is-not-this-one" : null,
            });
        }

        run.Recompute();
        session.Store(run);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return run.Id;
    }
}
