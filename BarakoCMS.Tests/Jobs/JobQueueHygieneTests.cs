using System.Text;
using barakoCMS.Features.Jobs.List;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Security;
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
/// Dead letters are purged after <c>Jobs:DeadLetterRetentionDays</c>, a stored error does not keep a
/// URL's path or query, and the queue publishes its attempts and backlog (#527).
/// </summary>
/// <remarks>
/// Every job here is on a queue of the test's own, which no worker serves, so the provider built
/// here is the only thing that touches it. The gauges count the whole table, which other tests also
/// write to, so they are asserted as at least what the test stored.
/// </remarks>
[Collection("Sequential")]
public class JobQueueHygieneTests
{
    private readonly IntegrationTestFixture _fixture;

    public JobQueueHygieneTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private static string NewQueue() => $"hygiene-{Guid.NewGuid():N}";

    private MartenJobStorageProvider Provider(JobMetrics metrics)
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
            Metrics = metrics,
        };
    }

    private static JobMetrics OwnMetrics() => new(Prometheus.Metrics.NewCustomRegistry());

    private async Task<JobRecord> StoreAsync(string queue, Action<JobRecord>? shape = null)
    {
        var now = DateTime.UtcNow;
        var record = new JobRecord
        {
            TrackingID = Guid.NewGuid(),
            QueueID = queue,
            CommandType = queue,
            CommandJson = "{}",
            CreatedAt = now,
            ExecuteAfter = now.AddMinutes(-1),
            DequeueAfter = now.AddMinutes(-1),
            ExpireOn = now.AddHours(4),
            MaxAttempts = 2,
            State = JobState.Pending,
        };
        shape?.Invoke(record);

        await using var session = Store.LightweightSession();
        session.Store(record);
        await session.SaveChangesAsync(Ct);

        return record;
    }

    private async Task<JobRecord?> LoadAsync(Guid id)
    {
        await using var session = Store.QuerySession();
        return await session.LoadAsync<JobRecord>(id, Ct);
    }

    private static T SearchParams<T>(params (string Name, object Value)[] values) where T : struct
    {
        object boxed = default(T);
        foreach (var (name, value) in values)
        {
            typeof(T).GetProperty(name)!.GetSetMethod(nonPublic: true)!.Invoke(boxed, [value]);
        }

        return (T)boxed;
    }

    [Fact]
    public async Task Each_attempt_is_counted_by_its_outcome()
    {
        var metrics = OwnMetrics();
        var provider = Provider(metrics);
        var queue = NewQueue();
        var succeeds = await StoreAsync(queue);
        var fails = await StoreAsync(queue);

        var claimed = await provider.GetNextBatchAsync(SearchParams<PendingJobSearchParams<JobRecord>>(
            ("QueueID", queue),
            ("Match", (System.Linq.Expressions.Expression<Func<JobRecord, bool>>)(r => r.QueueID == queue)),
            ("Limit", 10),
            ("ExecutionTimeLimit", TimeSpan.FromMinutes(1)),
            ("CancellationToken", Ct)));
        claimed.Should().HaveCount(2, "both jobs are due on a queue nothing else serves");

        await provider.MarkJobAsCompleteAsync(succeeds, Ct);
        await provider.OnHandlerExecutionFailureAsync(fails, new InvalidOperationException("first"), Ct);
        await provider.OnHandlerExecutionFailureAsync(fails, new InvalidOperationException("second"), Ct);

        metrics.Attempts.WithLabels(JobMetrics.Succeeded).Value.Should().Be(1);
        metrics.Attempts.WithLabels(JobMetrics.Retried).Value.Should().Be(1);
        metrics.Attempts.WithLabels(JobMetrics.DeadLettered).Value.Should().Be(1, "the second failure was the last allowed");

        var dead = await LoadAsync(fails.TrackingID);
        dead.Should().NotBeNull();
        dead!.State.Should().Be(JobState.DeadLettered);
        dead.CompletedAt.Should().NotBeNull("the retention window counts from when the job gave up");
    }

    [Fact]
    public async Task The_gauges_count_the_due_jobs_the_oldest_wait_and_the_dead_letters()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var metrics = new JobMetrics(registry);
        var provider = Provider(metrics);
        var queue = NewQueue();

        (await provider.MeasureAsync(Ct)).Should().BeTrue();
        var deadBefore = metrics.DeadLetteredJobs.Value;

        await StoreAsync(queue, r =>
        {
            r.ExecuteAfter = DateTime.UtcNow.AddHours(-1);
            r.DequeueAfter = r.ExecuteAfter;
        });
        await StoreAsync(queue, r => r.ExecuteAfter = r.DequeueAfter = DateTime.UtcNow.AddHours(1));
        await StoreAsync(queue, r =>
        {
            r.State = JobState.DeadLettered;
            r.CompletedAt = DateTime.UtcNow;
        });

        (await provider.MeasureAsync(Ct)).Should().BeTrue();

        metrics.Due.Value.Should().BeGreaterThanOrEqualTo(1, "the job due an hour ago is claimable");
        metrics.OldestDueAge.Value.Should().BeGreaterThanOrEqualTo(3500, "it has waited about an hour past its time");
        metrics.DeadLetteredJobs.Value.Should().BeGreaterThanOrEqualTo(deadBefore + 1);

        metrics.Recorded(JobMetrics.Succeeded);
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, Ct);
        var published = Encoding.UTF8.GetString(stream.ToArray());

        published.Should().Contain("barakocms_jobs_due ")
            .And.Contain("barakocms_jobs_oldest_due_age_seconds ")
            .And.Contain("barakocms_jobs_dead_lettered ")
            .And.Contain("barakocms_jobs_attempts_total{outcome=\"succeeded\"}");
    }

    [Fact]
    public async Task A_stored_error_keeps_a_urls_scheme_and_host_and_nothing_after()
    {
        var provider = Provider(OwnMetrics());
        var job = await StoreAsync(NewQueue(), r => r.MaxAttempts = 5);

        await provider.OnHandlerExecutionFailureAsync(job, new HttpRequestException(
            "POST https://user:pw@hooks.example.com/t/tok_9f8e7d?sig=abc123 answered 500"), Ct);

        var stored = await LoadAsync(job.TrackingID);
        stored.Should().NotBeNull();
        stored!.LastError.Should().Contain("HttpRequestException")
            .And.Contain("https://hooks.example.com")
            .And.Contain("answered 500");
        stored.LastError.Should().NotContain("tok_9f8e7d")
            .And.NotContain("sig=abc123")
            .And.NotContain("pw@");
    }

    [Fact]
    public void A_job_listed_by_the_api_has_its_stored_url_redacted_even_from_before_the_change()
    {
        var response = JobResponse.From(new JobRecord
        {
            LastError = "HttpRequestException: POST https://hooks.example.com/t/tok_9f8e7d failed",
        });

        response.LastError.Should().Be("HttpRequestException: POST https://hooks.example.com failed");
    }

    [Fact]
    public async Task The_sweep_removes_dead_letters_given_up_on_before_the_window_and_keeps_the_rest()
    {
        var queue = NewQueue();
        var now = DateTime.UtcNow;
        var old = now.AddDays(-JobOptions.DefaultDeadLetterRetentionDays - 10);

        var expired = await StoreAsync(queue, r => { r.State = JobState.DeadLettered; r.CreatedAt = old; r.CompletedAt = old; });
        var legacy = await StoreAsync(queue, r => { r.State = JobState.DeadLettered; r.CreatedAt = old; r.CompletedAt = null; });
        var recent = await StoreAsync(queue, r => { r.State = JobState.DeadLettered; r.CreatedAt = old; r.CompletedAt = now.AddDays(-1); });
        var pending = await StoreAsync(queue, r => r.CreatedAt = old);

        await using (var session = Store.LightweightSession())
        {
            var removed = await JobDeadLetterRetentionService.SweepTenantAsync(
                session, now, JobOptions.DefaultDeadLetterRetentionDays, Ct);
            removed.Should().BeGreaterThanOrEqualTo(2);
        }

        (await LoadAsync(expired.TrackingID)).Should().BeNull("it gave up before the window");
        (await LoadAsync(legacy.TrackingID)).Should().BeNull("a dead letter with no give-up time is aged by when it was queued");
        (await LoadAsync(recent.TrackingID)).Should().NotBeNull("it gave up a day ago, whenever it was queued");
        (await LoadAsync(pending.TrackingID)).Should().NotBeNull("only dead letters are swept");
    }

    [Fact]
    public async Task A_retention_of_zero_keeps_every_dead_letter()
    {
        var old = DateTime.UtcNow.AddYears(-2);
        var expired = await StoreAsync(NewQueue(), r => { r.State = JobState.DeadLettered; r.CreatedAt = old; r.CompletedAt = old; });

        await using (var session = Store.LightweightSession())
        {
            (await JobDeadLetterRetentionService.SweepTenantAsync(session, DateTime.UtcNow, 0, Ct)).Should().Be(0);
        }

        (await LoadAsync(expired.TrackingID)).Should().NotBeNull();
    }
}

public class UrlRedactionTests
{
    [Theory]
    [InlineData("no url here", "no url here")]
    [InlineData("to https://a.example/x?token=1 and http://b.example:8080/y", "to https://a.example and http://b.example:8080")]
    [InlineData("(https://user:secret@a.example/p)", "(https://a.example)")]
    [InlineData("at http://[2001:db8::1]:8443/hook/tok", "at http://[2001:db8::1]:8443")]
    public void Every_url_in_the_text_keeps_its_scheme_and_host_only(string text, string expected)
    {
        UrlRedaction.InText(text).Should().Be(expected);
    }
}
