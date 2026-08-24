using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Running more than one instance must not make anything happen twice.
/// </summary>
/// <remarks>
/// Both mechanisms that act on their own schedule had this problem and each needed a different
/// answer. The projection daemon ran <c>Solo</c>, which Marten documents as assuming a single node,
/// so every node processed every event. The scheduled-content sweep is a BackgroundService with no
/// leader election at all.
///
/// The symptom in both cases is a workflow action running once per node. For an email or a webhook
/// action that reaches the outside world, so it is not something the next deploy quietly fixes.
/// </remarks>
[Collection("Sequential")]
public class MultiInstanceSchedulingTests
{
    private readonly IntegrationTestFixture _fixture;

    public MultiInstanceSchedulingTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Two sweeps running at once: one does the work, the other declines.
    /// </summary>
    /// <remarks>
    /// The assertion is on the return value rather than a count of transitions, because a count is
    /// satisfied by the second sweep simply finding nothing left to do. That would pass without any
    /// lock, since the first sweep changes Status and the second one's query no longer matches it.
    /// Distinguishing "declined to run" from "ran and found nothing" is the whole point.
    /// </remarks>
    [Fact]
    public async Task Only_one_instance_sweeps_at_a_time()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var a = new ScheduledContentService(store, logger);
        var b = new ScheduledContentService(store, logger);

        var now = DateTime.UtcNow;
        var results = await Task.WhenAll(
            a.SweepAllTenantsAsync(now, CancellationToken.None),
            b.SweepAllTenantsAsync(now, CancellationToken.None));

        results.Count(swept => swept).Should().Be(1,
            "exactly one instance should hold the advisory lock; two means both would transition the "
            + "same content and fire every Published workflow twice");
    }

    /// <summary>
    /// The lock is released, so the next tick is not blocked by the previous one.
    /// </summary>
    /// <remarks>
    /// A lock that is taken and never released turns a duplicate-work bug into a no-work bug, which
    /// is quieter and worse: scheduled content simply stops publishing and nothing errors.
    /// </remarks>
    [Fact]
    public async Task The_lock_is_released_after_a_sweep()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var service = new ScheduledContentService(store, logger);

        (await service.SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
        (await service.SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue(
            "a second sweep after the first has finished must be able to take the lock again");
    }

    /// <summary>
    /// Scheduled content transitions exactly once even when two instances sweep together.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the first test. Counts events on the stream rather than the
    /// document's final state, because the document looks identical whether it was transitioned once
    /// or twice. The stream is where the duplication is visible, and the stream is what the workflow
    /// projection reads.
    /// </remarks>
    [Fact]
    public async Task Due_content_transitions_once_when_two_instances_sweep_together()
    {
        var id = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();

            var content = writer.Create(new ContentCreated(
                id, "scheduled-article", new Dictionary<string, object> { ["Title"] = "due" },
                ContentStatus.Draft, author, "due", SensitivityLevel.Public));

            writer.Append(content, new ContentScheduled(
                id, DateTime.UtcNow.AddMinutes(-5), null, author));

            await session.SaveChangesAsync();
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var logger = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

            await Task.WhenAll(
                new ScheduledContentService(store, logger).SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None),
                new ScheduledContentService(store, logger).SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None));
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stream = await session.Events.FetchStreamAsync(id);

            var transitions = stream.Count(e => e.Data is ContentStatusChanged);
            transitions.Should().Be(1,
                "two ContentStatusChanged events means the workflow projection fires every Published "
                + "workflow twice, and an email action sends two emails");
        }
    }
}
