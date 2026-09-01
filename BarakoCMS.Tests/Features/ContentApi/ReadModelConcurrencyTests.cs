using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// The read-model document and the stream it is a projection of must not be able to disagree.
/// </summary>
/// <remarks>
/// They could. The writer stored the document the caller loaded at the start of its request, with
/// the events applied on top, and the expected-version check covered only the stream from the append
/// onwards. So the scheduler published a due draft and committed, an editor's update that had loaded
/// the earlier copy appended cleanly afterwards, and the document it stored said Draft. Nothing
/// recorded the reversal: replaying the stream gives Published, the document says Draft, and
/// delivery stops serving an item that was published. See issue #299.
/// </remarks>
[Collection("Sequential")]
public class ReadModelConcurrencyTests
{
    private readonly IntegrationTestFixture _fixture;

    public ReadModelConcurrencyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    private async Task<Guid> DraftAsync(string title, DateTime? publishAt = null)
    {
        var id = Guid.NewGuid();

        using var scope = Scope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();

        var content = writer.Create(new ContentCreated(
            id,
            "article",
            new Dictionary<string, object> { ["Title"] = title },
            ContentStatus.Draft,
            Guid.NewGuid(),
            title,
            SensitivityLevel.Public));

        if (publishAt is not null)
        {
            writer.Append(content, new ContentScheduled(id, publishAt, null, Guid.NewGuid()));
        }

        await session.SaveChangesAsync();
        return id;
    }

    /// <summary>Replays the stream, which is the record the document is supposed to agree with.</summary>
    private async Task<Content> ReplayAsync(Guid id)
    {
        using var scope = Scope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(id);

        var replayed = new Content();
        foreach (var e in stream)
        {
            var at = e.Timestamp.UtcDateTime;
            switch (e.Data)
            {
                case ContentCreated x: replayed.Apply(x, at); break;
                case ContentUpdated x: replayed.Apply(x, at); break;
                case ContentStatusChanged x: replayed.Apply(x, at); break;
                case ContentScheduled x: replayed.Apply(x, at); break;
                case ContentSensitivityChanged x: replayed.Apply(x, at); break;
                default: throw new InvalidOperationException($"{e.Data.GetType().Name} has no Apply overload");
            }
        }

        return replayed;
    }

    private async Task<Content> StoredAsync(Guid id)
    {
        using var scope = Scope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id))!;
    }

    /// <summary>
    /// The interleaving from the issue: the scheduler publishes while an edit is in flight, and the
    /// edit carries no status event of its own because the copy it read still said Draft.
    /// </summary>
    [Fact]
    public async Task A_publish_that_lands_mid_edit_survives_the_edit()
    {
        var id = await DraftAsync("v1");

        // The editor's request starts and loads the content: Draft.
        using var editorScope = Scope();
        var editorSession = editorScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var editorWriter = editorScope.ServiceProvider.GetRequiredService<IContentWriter>();
        var loadedByEditor = (await editorSession.LoadAsync<Content>(id))!;
        loadedByEditor.Status.Should().Be(ContentStatus.Draft);

        // The scheduler publishes it and commits while that request is still running.
        using (var schedulerScope = Scope())
        {
            var session = schedulerScope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = schedulerScope.ServiceProvider.GetRequiredService<IContentWriter>();
            var content = (await session.LoadAsync<Content>(id))!;
            await writer.AppendOptimisticAsync(
                content,
                new object[] { new ContentStatusChanged(id, ContentStatus.Published, Guid.Empty) },
                default);
            await session.SaveChangesAsync();
        }

        // The editor's request finishes. Its status is unchanged as far as it knows, so it emits no
        // status event at all.
        await editorWriter.AppendOptimisticAsync(
            loadedByEditor,
            new object[] { new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "v2" }, Guid.NewGuid(), "v2") },
            default);
        await editorSession.SaveChangesAsync();

        var stored = await StoredAsync(id);
        var replayed = await ReplayAsync(id);

        stored.Status.Should().Be(ContentStatus.Published,
            "the publish was committed and no event un-published it");
        stored.Data["Title"].ToString().Should().Be("v2", "the edit still has to land");
        stored.Status.Should().Be(replayed.Status, "the document is a projection of the stream");
        stored.Data["Title"].ToString().Should().Be(replayed.Data["Title"].ToString());
    }

    /// <summary>
    /// The mirror: an edit commits inside the window between the sweep reading an item and saving its
    /// transition. The sweep must not put the old data back.
    /// </summary>
    [Fact]
    public async Task An_edit_that_lands_mid_publish_survives_the_publish()
    {
        var id = await DraftAsync("v1");

        // The sweep reads the due item.
        using var sweepScope = Scope();
        var sweepSession = sweepScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var sweepWriter = sweepScope.ServiceProvider.GetRequiredService<IContentWriter>();
        var loadedBySweep = (await sweepSession.LoadAsync<Content>(id))!;

        // An editor commits before the sweep saves.
        using (var editorScope = Scope())
        {
            var session = editorScope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = editorScope.ServiceProvider.GetRequiredService<IContentWriter>();
            var content = (await session.LoadAsync<Content>(id))!;
            await writer.AppendOptimisticAsync(
                content,
                new object[] { new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "v2" }, Guid.NewGuid(), "v2") },
                default);
            await session.SaveChangesAsync();
        }

        await sweepWriter.AppendOptimisticAsync(
            loadedBySweep,
            new object[] { new ContentStatusChanged(id, ContentStatus.Published, Guid.Empty) },
            default);
        await sweepSession.SaveChangesAsync();

        var stored = await StoredAsync(id);
        var replayed = await ReplayAsync(id);

        stored.Data["Title"].ToString().Should().Be("v2", "the edit was committed and no event undid it");
        stored.Status.Should().Be(ContentStatus.Published);
        stored.Data["Title"].ToString().Should().Be(replayed.Data["Title"].ToString());
    }

    /// <summary>
    /// The positive control. Refreshing the document from the store before applying events must not
    /// throw away what the caller actually asked for, or every uncontended save would silently do
    /// nothing and both tests above would still pass.
    /// </summary>
    [Fact]
    public async Task An_uncontended_write_applies_exactly_what_the_caller_asked_for()
    {
        var id = await DraftAsync("v1");

        using var scope = Scope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
        var content = (await session.LoadAsync<Content>(id))!;

        await writer.AppendOptimisticAsync(
            content,
            new object[]
            {
                new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "only" }, Guid.NewGuid(), "only"),
                new ContentStatusChanged(id, ContentStatus.Archived, Guid.NewGuid()),
            },
            default);
        await session.SaveChangesAsync();

        var stored = await StoredAsync(id);
        stored.Data["Title"].ToString().Should().Be("only");
        stored.Status.Should().Be(ContentStatus.Archived);
        stored.SearchText.Should().Be("only");
    }

    /// <summary>
    /// The sweep loses a race it is actually in, and leaves the edit alone.
    /// </summary>
    /// <remarks>
    /// This replaces a version that started a sweep and an edit with Task.WhenAll and asserted the
    /// document agreed with the stream. Nothing made the two collide, and the assertion holds
    /// trivially when they do not, so deleting the expected-version append from the sweep left it
    /// green. A guard whose test passes without it is not guarded. See #393.
    ///
    /// The edit commits from the hook, which fires after the sweep has loaded the item and before it
    /// saves. That is the exact interleaving, every run, rather than one the scheduler might produce.
    ///
    /// Note what is asserted. The sweep losing is the correct outcome, not a failure: the schedule
    /// is still armed and the next tick picks it up against fresh state. What must never happen is
    /// the sweep writing its stale copy over the editor's data.
    /// </remarks>
    [Fact]
    public async Task A_sweep_that_loses_to_an_editor_does_not_overwrite_the_edit()
    {
        var id = await DraftAsync("v1", publishAt: DateTime.UtcNow.AddMinutes(-5));

        var attempted = false;
        var edited = false;
        Exception? editorFailure = null;

        try
        {

            async Task EditOnceAsync(barakoCMS.Models.Content item, CancellationToken ct)
            {
                // Only this test's item. The sweep processes whatever else the suite has left due, and
                // editing one of those would be a different test with a worse name.
                if (item.Id != id || attempted) return;
                attempted = true;

                // Two flags, not one, and the exception is kept rather than allowed to escape.
                //
                // This used to set a single flag on entry and let anything thrown propagate. Both
                // were wrong in the same direction. The flag proved the hook was entered, not that
                // the edit committed, and the sweep's catch filter matches any exception whose type
                // name contains "Concurrency", so an editor append that lost its own race was
                // swallowed by the sweep and read as the sweep winning. The test then failed on the
                // title with a message about the sweep overwriting an edit that never landed.
                //
                // That is the shape of the intermittent CI failure in #424, and whether or not it is
                // the only cause, a test cannot report the difference while the two look identical.
                try
                {
                    using var editorScope = Scope();
                    var session = editorScope.ServiceProvider.GetRequiredService<IDocumentSession>();
                    var writer = editorScope.ServiceProvider.GetRequiredService<IContentWriter>();
                    var content = (await session.LoadAsync<barakoCMS.Models.Content>(id, ct))!;
                    await writer.AppendOptimisticAsync(
                        content,
                        new object[] { new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "v2" }, Guid.NewGuid(), "v2") },
                        ct);
                    await session.SaveChangesAsync(ct);
                    edited = true;
                }
                catch (Exception ex)
                {
                    editorFailure = ex;
                }
            }

            using (var scope = Scope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                await ScheduledContentService.SweepTenantAsync(
                    session,
                    DateTime.UtcNow,
                    logger: null,
                    ScheduledContentService.DefaultBatchSize,
                    ScheduledContentService.DefaultMaxBatchesPerSweep,
                    beforeSave: EditOnceAsync,
                    default);
            }

            attempted.Should().BeTrue("the hook has to have run against this item, or this test proves nothing");

            // Named before the outcome assertions, so a failing editor reports itself instead of
            // surfacing as "the sweep overwrote the edit" three lines further down.
            editorFailure.Should().BeNull(
                "the editor's own append has to commit for there to be a race at all. It threw {0}: {1}",
                editorFailure?.GetType().Name, editorFailure?.Message);
            edited.Should().BeTrue("the edit has to have committed, not merely been attempted");

            // Deliberately not asserting on the flip count. Other tests leave due content behind, so
            // that number belongs to the whole suite and not to this test. What this item did is the
            // claim worth making.
            var stored = await StoredAsync(id);
            var replayed = await ReplayAsync(id);

            stored.Data["Title"].ToString().Should().Be("v2",
                "the sweep held a copy loaded before the edit, and saving it would have reverted the "
                + "editor's data with no event recording it");
            stored.Status.Should().Be(ContentStatus.Draft,
                "the sweep lost, so the transition did not happen and the schedule is still armed");
            stored.Data["Title"].ToString().Should().Be(replayed.Data["Title"].ToString(),
                "the document is a projection of the stream");
            stored.Status.Should().Be(replayed.Status);
        }
        finally
        {
            // In finally, not after the assertions. The sweep, the reads or any assertion can throw,
            // and this item is deliberately left Draft with a publish time in the past, which is to
            // say still due. Leaving it behind makes this test's state every other sweep test's
            // problem, and a failure here would then cause failures elsewhere that look unrelated.
            using var cleanup = Scope();
            var session = cleanup.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<barakoCMS.Models.Content>(id);
            await session.SaveChangesAsync();
        }
    }

}
