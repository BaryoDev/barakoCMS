using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The claim in docs/background-jobs.md: two instances polling the same table cannot both run one
/// job, because the claim is a load, a lease and a save under optimistic concurrency. This pins the
/// primitive that rests on: two sessions that loaded the same record cannot both save it.
/// </summary>
[Collection("Sequential")]
public class JobClaimTests
{
    private readonly IntegrationTestFixture _factory;

    public JobClaimTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task Two_sessions_that_loaded_the_same_job_cannot_both_claim_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var id = Guid.NewGuid();
        await using (var seed = store.LightweightSession())
        {
            seed.Store(new JobRecord
            {
                TrackingID = id,
                QueueID = "claim-test",
                CommandType = "claim-test",
                CommandJson = "{}",
                CreatedAt = DateTime.UtcNow,
                ExecuteAfter = DateTime.UtcNow,
                ExpireOn = DateTime.UtcNow.AddHours(4),
                DequeueAfter = DateTime.UtcNow,
                MaxAttempts = 5,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var nodeA = store.LightweightSession();
        await using var nodeB = store.LightweightSession();
        var seenByA = await nodeA.LoadAsync<JobRecord>(id, ct);
        var seenByB = await nodeB.LoadAsync<JobRecord>(id, ct);
        seenByA.Should().NotBeNull();
        seenByB.Should().NotBeNull();

        seenByA!.State = JobState.Running;
        seenByA.DequeueAfter = DateTime.UtcNow.AddMinutes(10);
        nodeA.Store(seenByA);
        await nodeA.SaveChangesAsync(ct);

        seenByB!.State = JobState.Running;
        seenByB.DequeueAfter = DateTime.UtcNow.AddMinutes(10);
        nodeB.Store(seenByB);
        var second = async () => await nodeB.SaveChangesAsync(ct);

        await second.Should().ThrowAsync<JasperFx.ConcurrencyException>(
            "the second node loaded the record before the first node's lease was written, so its save must be refused");
    }
}
