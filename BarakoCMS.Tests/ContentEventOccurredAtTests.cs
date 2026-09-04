using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #228: an event says when the change happened, so a rebuild reproduces the timestamps.
/// </summary>
/// <remarks>
/// Two clocks answered that question and they were not the same. The writer stamped
/// <c>DateTime.UtcNow</c> as it applied the event to the document; Marten stamped the transaction
/// time when the event committed, and a replay could only see the second, so a rebuilt document
/// differed from the original by the write latency.
///
/// The half that needs testing hardest is not the new behaviour, it is the old data: an event
/// written before 4.0 has no <c>OccurredAt</c> and deserialises to <c>default</c>. Treating that as
/// an answer would rebuild those documents at year one.
/// </remarks>
[Collection("Sequential")]
public class ContentEventOccurredAtTests
{
    private readonly IntegrationTestFixture _fixture;

    public ContentEventOccurredAtTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>An event that states its time is replayed at that time, not at the commit time.</summary>
    [Fact]
    public async Task A_stamped_event_is_replayed_at_the_time_it_states()
    {
        var occurred = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var id = await StreamAsync(new ContentCreated(
            Guid.NewGuid(), "article", new Dictionary<string, object> { ["Title"] = "stamped" },
            barakoCMS.Models.ContentStatus.Published, Guid.NewGuid(), "stamped",
            barakoCMS.Models.SensitivityLevel.Public, occurred));

        var rebuilt = await FoldAsync(id);

        rebuilt.CreatedAt.Should().Be(occurred,
            "the event carries the time the change happened, and a rebuild reads it");
        rebuilt.UpdatedAt.Should().Be(occurred);
    }

    /// <summary>
    /// An event written before 4.0 falls back to the commit time rather than to year one.
    /// </summary>
    /// <remarks>
    /// The stamp is zeroed with <c>with</c>, which is exactly what deserialising pre-4.0 JSON
    /// produces: the field is absent, so the property takes its default. The obsolete constructor is
    /// not the right stand-in and was the first thing tried here, because it fills the stamp with
    /// the current clock and so never exercises the fallback at all. That version of this test
    /// passed with the fallback removed.
    ///
    /// The assertion is deliberately not "equals the Marten timestamp exactly" but "is a plausible
    /// recent time", because the point is that it is neither <c>default</c> nor invented.
    /// </remarks>
    [Fact]
    public async Task An_event_with_no_stamp_falls_back_to_the_commit_time()
    {
        var id = await StreamAsync(Unstamped() with { OccurredAt = default });

        var rebuilt = await FoldAsync(id);

        rebuilt.CreatedAt.Should().NotBe(default,
            "an unstamped event must not rebuild the document at year one");
        rebuilt.CreatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        rebuilt.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5),
            "the fallback is the commit time, which for an event just written is about now");
    }

    /// <summary>
    /// The obsolete constructors still produce a usable event, so a caller that has not moved across
    /// is not silently broken.
    /// </summary>
    [Fact]
    public void The_old_constructors_stamp_the_current_time()
    {
        var before = DateTime.UtcNow;
        var @event = Unstamped();
        var after = DateTime.UtcNow;

        @event.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "an old caller gets the behaviour it had, which was the clock at construction");
    }

    /// <summary>Every content event answers the question, so the projection never has to special-case one.</summary>
    [Fact]
    public void Every_content_event_carries_the_time_it_happened()
    {
        var events = typeof(ContentCreated).Assembly.GetTypes()
            .Where(t => t.Namespace == "barakoCMS.Events"
                     && t.IsClass && !t.IsAbstract
                     && t.Name.StartsWith("Content", StringComparison.Ordinal))
            .ToList();

        events.Should().NotBeEmpty("the events live in that namespace, or this stopped looking");
        events.Should().OnlyContain(t => typeof(IContentEvent).IsAssignableFrom(t),
            "a content event that does not say when it happened cannot be replayed at that time, and "
          + "the projection would fall back to the commit time without anything looking wrong");
    }

#pragma warning disable CS0618 // deliberately exercising the pre-4.0 shape
    private static ContentCreated Unstamped() => new(
        Guid.NewGuid(), "article", new Dictionary<string, object> { ["Title"] = "old" },
        barakoCMS.Models.ContentStatus.Published, Guid.NewGuid(), "old",
        barakoCMS.Models.SensitivityLevel.Public);
#pragma warning restore CS0618

    private async Task<Guid> StreamAsync(ContentCreated created)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<barakoCMS.Models.Content>(created.Id, created);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return created.Id;
    }

    private async Task<barakoCMS.Models.Content> FoldAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        var rebuilt = ContentProjection.Fold(stream);
        rebuilt.Should().NotBeNull("the stream was just written");
        return rebuilt!;
    }
}
