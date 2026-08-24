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
    /// A sweep declines while another holds the lock.
    /// </summary>
    /// <remarks>
    /// The lock is taken here rather than by racing two sweeps against each other. Two concurrent
    /// calls only contend if they happen to overlap, and a sweep with nothing to do finishes in
    /// milliseconds, so they can serialise and both legitimately succeed. That test would pass or
    /// fail on timing, which for a lock is the one thing it must not do.
    ///
    /// Holding the lock explicitly means the assertion is about the lock and nothing else.
    /// </remarks>
    [Fact]
    public async Task A_sweep_declines_while_another_instance_holds_the_lock()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        await using var holder = store.Storage.Database.CreateConnection();
        await holder.OpenAsync();

        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "select pg_advisory_lock(8242026001)";
            await take.ExecuteScalarAsync();
        }

        try
        {
            var swept = await new ScheduledContentService(store, logger)
                .TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None);

            swept.Should().BeFalse(
                "another connection holds the sweep lock, so this instance must decline rather than "
                + "transition the same content a second time");
        }
        finally
        {
            await using var release = holder.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(8242026001)";
            await release.ExecuteScalarAsync();
        }
    }

    /// <summary>The lock is available again once the holder lets go.</summary>
    [Fact]
    public async Task A_sweep_proceeds_once_the_lock_is_free()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var swept = await new ScheduledContentService(store, logger)
            .TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None);

        swept.Should().BeTrue("nothing holds the lock, so the sweep must run");
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

        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue(
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
