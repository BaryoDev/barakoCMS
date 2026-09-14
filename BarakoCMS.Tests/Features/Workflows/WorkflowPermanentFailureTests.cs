using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #599: a run says whether a failure was permanent, and a retry of one is recorded as such.
/// </summary>
[Collection("Sequential")]
public class WorkflowPermanentFailureTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowPermanentFailureTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<Guid> SeedAsync(string actionType, AttemptStatus status, int attempts, bool? retryable)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var contentId = Guid.NewGuid();
        session.Store(new Content { Id = contentId, ContentType = "article", Status = ContentStatus.Published });

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Permanent failure",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = actionType,
                    Status = status,
                    Attempts = attempts,
                    Retryable = retryable,
                    Error = status == AttemptStatus.Failed ? "the URL is not allowed" : null,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                },
            ],
        };

        run.Recompute();
        session.Store(run);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run.Id;
    }

    private async Task<WorkflowRun> RunToCompletionAsync(Guid runId)
    {
        var runner = new WorkflowRunner(
            _fixture.Services,
            _fixture.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            _fixture.Services.GetRequiredService<IConfiguration>());
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        for (var i = 0; i < 200; i++)
        {
            await using (var check = store.QuerySession())
            {
                var run = await check.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);
                if (run!.Actions[0].Status is AttemptStatus.Failed or AttemptStatus.Succeeded) return run;
            }

            if (!await runner.RunOnceAsync(TestContext.Current.CancellationToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            }
        }

        throw new TimeoutException("the attempt never finished");
    }

    private async Task<JsonElement> AttemptFromApiAsync(Guid runId)
    {
        var client = await AdminClientAsync();
        var body = await client.GetStringAsync($"/api/workflow-runs/{runId}", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("actions")[0].Clone();
    }

    [Fact]
    public async Task A_permanent_failure_and_a_transient_one_are_told_apart_on_the_run()
    {
        // No handler is registered for this type, which the runner reports as permanent.
        var permanentId = await SeedAsync("NoHandlerForThisType", AttemptStatus.Pending, attempts: 0, retryable: null);
        // Throws on its last allowed attempt: a transient failure that ran out of retries.
        var transientId = await SeedAsync("ThrowingRunner", AttemptStatus.Pending,
            attempts: WorkflowRetryPolicy.MaxAttempts - 1, retryable: null);

        var permanent = await RunToCompletionAsync(permanentId);
        var transient = await RunToCompletionAsync(transientId);

        permanent.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        transient.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        permanent.Actions[0].Retryable.Should().BeFalse();
        transient.Actions[0].Retryable.Should().BeTrue();

        (await AttemptFromApiAsync(permanentId)).GetProperty("retryable").ValueKind.Should().Be(JsonValueKind.False);
        (await AttemptFromApiAsync(transientId)).GetProperty("retryable").ValueKind.Should().Be(JsonValueKind.True);
    }

    [Fact]
    public async Task Retrying_a_permanent_failure_is_allowed_and_audited_as_permanent()
    {
        var runId = await SeedAsync("NoHandlerForThisType", AttemptStatus.Failed, attempts: 1, retryable: false);
        var client = await AdminClientAsync();

        var res = await client.PostAsync($"/api/workflow-runs/{runId}/actions/0/retry", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using (var doc = JsonDocument.Parse(body))
        {
            var attempt = doc.RootElement.GetProperty("actions")[0];
            attempt.GetProperty("status").GetString().Should().Be(nameof(AttemptStatus.Pending));
            attempt.GetProperty("retryable").ValueKind.Should().Be(JsonValueKind.Null,
                "a queued attempt has not failed, so it is neither kind yet");
        }

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var entries = await session.Query<AuditEvent>()
            .Where(a => a.Action == "workflow.action.retried" && a.TargetId == runId.ToString())
            .ToListAsync(TestContext.Current.CancellationToken);

        entries.Should().HaveCount(1);
        entries[0].Metadata.Should().NotBeNull().And.ContainKey("wasPermanent");
        entries[0].Metadata!["wasPermanent"].ToString().Should().BeEquivalentTo("true");
    }

    [Fact]
    public async Task Retrying_a_transient_failure_is_not_audited_as_permanent()
    {
        var runId = await SeedAsync("NoHandlerForThisType", AttemptStatus.Failed, attempts: 1, retryable: true);
        var client = await AdminClientAsync();

        var res = await client.PostAsync($"/api/workflow-runs/{runId}/actions/0/retry", null, TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue();

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var entries = await session.Query<AuditEvent>()
            .Where(a => a.Action == "workflow.action.retried" && a.TargetId == runId.ToString())
            .ToListAsync(TestContext.Current.CancellationToken);

        entries.Should().HaveCount(1);
        entries[0].Metadata.Should().NotBeNull().And.ContainKey("wasPermanent");
        entries[0].Metadata!["wasPermanent"].ToString().Should().BeEquivalentTo("false");
    }
}
