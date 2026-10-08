using System.Diagnostics;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Jobs;

/// <summary>
/// Issue #691: the job queue's spans. A claim, the run it starts, and the write that records how
/// the run went, carrying the tenant and never the command.
/// </summary>
/// <remarks>
/// The job is on a queue of the test's own, which no worker serves, and in a tenant of its own, so
/// the provider built here is the only thing that touches it and the spans are picked out by its id.
/// </remarks>
[Collection("Sequential")]
public class JobRunSpanTests
{
    private const string Payload = "the-payload-a-request-queued-4f1c";

    private readonly IntegrationTestFixture _fixture;

    public JobRunSpanTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task A_job_run_emits_its_claim_run_and_finish_spans_with_the_tenant_and_no_payload()
    {
        using var capture = new SpanCapture(BarakoTracing.SourceName);
        var provider = Provider();
        var queue = $"spans-{Guid.NewGuid():N}";
        var tenant = $"spans-{Guid.NewGuid():N}"[..20];
        var job = await StoreAsync(queue, tenant);

        var claimed = await provider.GetNextBatchAsync(Claim(queue));
        claimed.Should().ContainSingle("the one job is due on a queue nothing else serves");
        provider.RunSpans.Count.Should().BeGreaterThanOrEqualTo(1, "the claim started a run span and nothing has finished it");

        await provider.MarkJobAsCompleteAsync(claimed.Single(), Ct);

        var id = job.TrackingID.ToString();
        var spans = capture.Exported;
        var claim = spans.Where(s => s.OperationName == BarakoTracing.JobClaimSpan
            && (string?)s.GetTagItem(BarakoTracing.JobQueueTag) == queue).ToList();
        var run = spans.Where(s => s.OperationName == BarakoTracing.JobRunSpan
            && (string?)s.GetTagItem(BarakoTracing.JobIdTag) == id).ToList();
        var finish = spans.Where(s => s.OperationName == BarakoTracing.JobFinishSpan
            && (string?)s.GetTagItem(BarakoTracing.JobIdTag) == id).ToList();

        claim.Should().HaveCount(1);
        run.Should().HaveCount(1);
        finish.Should().HaveCount(1);

        claim[0].GetTagItem(BarakoTracing.JobClaimedTag).Should().Be(1);

        run[0].ParentSpanId.Should().Be(claim[0].SpanId, "the run starts at its claim");
        run[0].TraceId.Should().Be(claim[0].TraceId);
        finish[0].ParentSpanId.Should().Be(run[0].SpanId, "the finishing write sits under the run");

        var runTags = run[0].TagObjects.ToDictionary(t => t.Key, t => t.Value);
        runTags.Should().HaveCount(5);
        runTags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            [BarakoTracing.TenantTag] = tenant,
            [BarakoTracing.JobIdTag] = id,
            [BarakoTracing.JobCommandTag] = queue,
            [BarakoTracing.JobAttemptTag] = 1,
            [BarakoTracing.JobOutcomeTag] = JobMetrics.Succeeded,
        });
        finish[0].GetTagItem(BarakoTracing.TenantTag).Should().Be(tenant);

        var values = claim.Concat(run).Concat(finish)
            .SelectMany(s => s.TagObjects.Select(t => t.Value?.ToString() ?? ""))
            .ToList();
        values.Should().NotBeEmpty();
        values.Should().NotContain(v => v.Contains(Payload), "the command is what a request queued, and it is never on a span");

        provider.RunSpans.Take(job.TrackingID).Should().BeNull("finishing the job took its run span");
    }

    [Fact]
    public async Task A_failed_attempt_ends_its_run_as_retried_without_the_error()
    {
        using var capture = new SpanCapture(BarakoTracing.SourceName);
        var provider = Provider();
        var queue = $"spans-{Guid.NewGuid():N}";
        var job = await StoreAsync(queue, tenant: null);

        var claimed = await provider.GetNextBatchAsync(Claim(queue));
        claimed.Should().ContainSingle();
        await provider.OnHandlerExecutionFailureAsync(
            claimed.Single(), new InvalidOperationException("provider said: " + Payload), Ct);

        var run = capture.Exported.Where(s => s.OperationName == BarakoTracing.JobRunSpan
            && (string?)s.GetTagItem(BarakoTracing.JobIdTag) == job.TrackingID.ToString()).ToList();

        run.Should().HaveCount(1);
        run[0].GetTagItem(BarakoTracing.JobOutcomeTag).Should().Be(JobMetrics.Retried);
        run[0].GetTagItem(BarakoTracing.TenantTag).Should().Be(Tenant.DefaultSlug);
        run[0].Status.Should().Be(ActivityStatusCode.Error);
        run[0].StatusDescription.Should().BeNull();
        run[0].TagObjects.Select(t => t.Value?.ToString() ?? "").Should().NotContain(v => v.Contains(Payload));
    }

    private MartenJobStorageProvider Provider()
    {
        var gate = new JobStorageGate();
        gate.Open();

        return new MartenJobStorageProvider(
            Store,
            new HttpContextAccessor(),
            new JobOptions(),
            NullLogger<MartenJobStorageProvider>.Instance,
            gate,
            _fixture.Services.GetRequiredService<IConfiguration>())
        {
            Metrics = new JobMetrics(Prometheus.Metrics.NewCustomRegistry()),
        };
    }

    private async Task<JobRecord> StoreAsync(string queue, string? tenant)
    {
        var now = DateTime.UtcNow;
        var record = new JobRecord
        {
            TrackingID = Guid.NewGuid(),
            QueueID = queue,
            CommandType = queue,
            CommandJson = $$"""{"Body":"{{Payload}}"}""",
            CreatedAt = now,
            ExecuteAfter = now.AddMinutes(-1),
            DequeueAfter = now.AddMinutes(-1),
            ExpireOn = now.AddHours(4),
            MaxAttempts = 3,
            State = JobState.Pending,
        };

        await using var session = tenant is null ? Store.LightweightSession() : Store.LightweightSession(tenant);
        session.Store(record);
        await session.SaveChangesAsync(Ct);

        return record;
    }

    private static PendingJobSearchParams<JobRecord> Claim(string queue) =>
        SearchParams<PendingJobSearchParams<JobRecord>>(
            ("QueueID", queue),
            ("Match", (System.Linq.Expressions.Expression<Func<JobRecord, bool>>)(r => r.QueueID == queue)),
            ("Limit", 10),
            ("ExecutionTimeLimit", TimeSpan.FromMinutes(1)),
            ("CancellationToken", Ct));

    private static T SearchParams<T>(params (string Name, object Value)[] values) where T : struct
    {
        object boxed = default(T);
        foreach (var (name, value) in values)
        {
            typeof(T).GetProperty(name)!.GetSetMethod(nonPublic: true)!.Invoke(boxed, [value]);
        }

        return (T)boxed;
    }
}
