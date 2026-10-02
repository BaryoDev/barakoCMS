using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarakoCMS.Tests.Jobs;

/// <summary>
/// The job store schedules a retry with the jittered wait, not the exact one.
/// </summary>
/// <remarks>
/// JobBackoffTests covers the arithmetic. This covers the one place that calls it, with a base above
/// zero: the shared fixture sets the base to 0, where a jittered wait and an exact one are both
/// nothing and no test on it can tell them apart.
/// </remarks>
[Collection("Sequential")]
public class JobRetryJitterTests
{
    private const int BaseSeconds = 40;

    // The second failure waits twice the base.
    private static readonly TimeSpan Full = TimeSpan.FromSeconds(BaseSeconds * 2);

    private readonly IntegrationTestFixture _factory;

    public JobRetryJitterTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task Jobs_that_fail_at_the_same_attempt_are_not_all_retried_after_the_same_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        var provider = new MartenJobStorageProvider(
            store,
            new HttpContextAccessor(),
            new JobOptions { MaxAttempts = 5, BackoffBaseSeconds = BaseSeconds, BackoffMaxSeconds = 3600 },
            NullLogger<MartenJobStorageProvider>.Instance,
            new JobStorageGate(),
            _factory.Services.GetRequiredService<IConfiguration>());

        var jobs = new List<JobRecord>();
        await using (var seed = store.LightweightSession())
        {
            for (var i = 0; i < 12; i++)
            {
                var job = new JobRecord
                {
                    TrackingID = Guid.NewGuid(),
                    QueueID = "retry-jitter-test",
                    CommandType = "retry-jitter-test",
                    CommandJson = "{}",
                    CreatedAt = DateTime.UtcNow,
                    ExecuteAfter = DateTime.UtcNow,
                    ExpireOn = DateTime.UtcNow.AddHours(4),
                    DequeueAfter = DateTime.UtcNow.AddMinutes(10),
                    State = JobState.Running,
                    AttemptCount = 1,
                    MaxAttempts = 5,
                };

                seed.Store(job);
                jobs.Add(job);
            }

            await seed.SaveChangesAsync(ct);
        }

        // The store reads the clock itself, so the wait it chose is only known to lie between
        // these two: measured from just after the call it is too short, from just before too long.
        var atLeast = new List<TimeSpan>();
        var atMost = new List<TimeSpan>();

        foreach (var job in jobs)
        {
            var before = DateTime.UtcNow;
            await provider.OnHandlerExecutionFailureAsync(job, new InvalidOperationException("the provider is down"), ct);
            var after = DateTime.UtcNow;

            await using var check = store.QuerySession();
            var stored = await check.LoadAsync<JobRecord>(job.TrackingID, ct);

            stored.Should().NotBeNull();
            stored!.AttemptCount.Should().Be(2);
            stored.NextAttemptAt.Should().NotBeNull();

            var next = stored.NextAttemptAt!.Value.ToUniversalTime();
            atLeast.Add(next - after);
            atMost.Add(next - before);
        }

        atLeast.Should().HaveCount(12);
        atMost.Should().HaveCount(12);

        atLeast.Should().OnlyContain(wait => wait <= Full, "jitter never lengthens a wait");
        atMost.Should().OnlyContain(wait => wait > Full * (1 - JobBackoff.JitterFraction),
            "jitter takes off a quarter at most");

        // Without jitter every wait is exactly the full one and these differ only by how long a
        // call took. Twelve jittered waits fall across twenty seconds.
        (atMost.Max() - atMost.Min()).Should().BeGreaterThan(TimeSpan.FromSeconds(2),
            "jobs that failed together must not retry together");
    }
}
