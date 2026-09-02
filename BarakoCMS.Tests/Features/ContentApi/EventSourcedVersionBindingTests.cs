using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// The expected version an event-sourced write carries has to reach the database, not just a
/// comparison in C#.
/// </summary>
/// <remarks>
/// Comparing the caller's version against a freshly fetched stream state and then appending is two
/// round trips with a gap in the middle. Another writer committing inside that gap leaves the
/// comparison passing on a stream that has already moved, and the write lands on a record the caller
/// never saw. These tests force the interleaving by hand: the append is staged, a second session
/// commits, and only then does the first session commit.
///
/// The second test is the one that matters. Marten 9 defaults to the Quick append mode, where the
/// server assigns versions, and the expected-version overload behaves differently there than under
/// Rich. A binding that the store quietly ignores is the same as no binding at all, so it is
/// asserted rather than assumed.
/// </remarks>
[Collection("Sequential")]
public class EventSourcedVersionBindingTests
{
    private readonly IntegrationTestFixture _fixture;

    public EventSourcedVersionBindingTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static string NewTypeName() => "vb" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>An event-sourced type holding one draft, and the stream version it sits at.</summary>
    private async Task<(string Type, Guid Id, long Version)> SeedAsync()
    {
        var type = NewTypeName();
        var id = Guid.NewGuid();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var policy = scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();

        await policy.DecideAsync(type, true, default);

        await writer.CreateAsync(new ContentCreated(
            id, type,
            new Dictionary<string, object> { ["Title"] = "first" },
            ContentStatus.Draft, Guid.NewGuid(), "first", SensitivityLevel.Public), default);

        await session.SaveChangesAsync();

        var state = await session.Events.FetchStreamStateAsync(id);
        state.Should().NotBeNull("the seed has to leave a stream behind or nothing below is testing a stream");

        return (type, id, state!.Version);
    }

    private static ContentUpdated Update(Guid id, string title) => new(
        id,
        new Dictionary<string, object> { ["Title"] = title },
        Guid.NewGuid(),
        title);

    [Fact]
    public async Task A_write_whose_version_was_overtaken_after_the_check_is_refused_at_commit()
    {
        var (_, id, version) = await SeedAsync();

        using var slow = _fixture.Services.CreateScope();
        var slowSession = slow.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slowWriter = slow.ServiceProvider.GetRequiredService<IContentWriter>();

        var content = await slowSession.LoadAsync<Content>(id);
        content.Should().NotBeNull();

        // Staged, not committed. The version check inside has already run and passed: at this moment
        // the stream really is where the caller thinks it is.
        await slowWriter.AppendAsync(content!, new object[] { Update(id, "slow") }, version, default);

        // The gap. A second writer commits while the first is still holding its unit of work.
        using (var fast = _fixture.Services.CreateScope())
        {
            var fastSession = fast.ServiceProvider.GetRequiredService<IDocumentSession>();
            var fastWriter = fast.ServiceProvider.GetRequiredService<IContentWriter>();

            var theirs = await fastSession.LoadAsync<Content>(id);
            await fastWriter.AppendAsync(theirs!, new object[] { Update(id, "fast") }, version, default);
            await fastSession.SaveChangesAsync();
        }

        var commit = async () => await slowSession.SaveChangesAsync();

        await commit.Should().ThrowAsync<Exception>(
            "the first writer's version was true when it was checked and false by the time it committed");
    }

    [Fact]
    public async Task The_write_that_was_not_overtaken_still_commits()
    {
        // Paired with the test above deliberately. "It threw" is not evidence of a concurrency guard
        // unless the uncontended write of the same shape goes through.
        var (_, id, version) = await SeedAsync();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();

        var content = await session.LoadAsync<Content>(id);
        await writer.AppendAsync(content!, new object[] { Update(id, "only") }, version, default);
        await session.SaveChangesAsync();

        var state = await session.Events.FetchStreamStateAsync(id);
        state!.Version.Should().Be(version + 1);

        var stored = await session.LoadAsync<Content>(id);
        stored!.Data["Title"].ToString().Should().Be("only");
    }

    /// <summary>
    /// A rebuild has to find entries whose stream carries the name in whatever case it was written.
    /// </summary>
    /// <remarks>
    /// Type names are matched case-insensitively everywhere a caller supplies one, so a client that
    /// posted <c>contentType: "Article"</c> passed validation and put "Article" in the event. The
    /// rebuild queried the stored form, matched none of them, and answered <c>rebuilt: 0</c>, which
    /// reads as "nothing needed doing" rather than as a failure.
    ///
    /// The event is written straight to the stream here rather than through the create endpoint,
    /// deliberately: the endpoint now normalises, so going through it could no longer produce the
    /// row this is about. What is being tested is the data already sitting in deployed databases.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_finds_entries_whose_stream_spells_the_type_in_a_different_case()
    {
        var type = NewTypeName();
        var id = Guid.NewGuid();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var policy = scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>();

        await policy.DecideAsync(type, true, default);

        // Upper case, the way an older deployment stored it.
        session.Events.StartStream<Content>(id, new ContentCreated(
            id, type.ToUpperInvariant(),
            new Dictionary<string, object> { ["Title"] = "shouted" },
            ContentStatus.Draft, Guid.NewGuid(), "shouted", SensitivityLevel.Public));

        await session.SaveChangesAsync();

        // No document exists yet: the events were written without one, which is the state a rebuild
        // is for. If the query misses the stream, nothing is stored and the count stays 0.
        var rebuilder = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IContentRebuilder>();
        var result = await rebuilder.RebuildAsync(type, default);
        await session.SaveChangesAsync();

        result.Rebuilt.Should().Be(1, "the stream is there and its name differs only in case");

        var stored = await session.LoadAsync<Content>(id);
        stored.Should().NotBeNull("a rebuild that counted an item has to have stored it");
        stored!.Data["Title"].ToString().Should().Be("shouted");
    }
}
