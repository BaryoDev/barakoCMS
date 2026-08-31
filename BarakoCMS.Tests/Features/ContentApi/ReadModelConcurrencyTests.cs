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
    /// The real race, both sides running at once: a scheduler sweep and an edit of the same item.
    /// Whichever wins, the document must still say what the stream says.
    /// </summary>
    [Fact]
    public async Task A_sweep_racing_an_edit_leaves_the_document_agreeing_with_the_stream()
    {
        var id = await DraftAsync("v1", publishAt: DateTime.UtcNow.AddMinutes(-5));

        async Task SweepAsync()
        {
            using var scope = Scope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await ScheduledContentService.SweepTenantAsync(session, DateTime.UtcNow, default);
        }

        async Task EditAsync()
        {
            using var scope = Scope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
            var content = (await session.LoadAsync<Content>(id))!;

            try
            {
                await writer.AppendOptimisticAsync(
                    content,
                    new object[] { new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "v2" }, Guid.NewGuid(), "v2") },
                    default);
                await session.SaveChangesAsync();
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("Concurrency")
                || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
            {
                // A rejected edit is a correct outcome here. A silently reverted one is not, and that
                // is what the assertions below are about.
            }
        }

        await Task.WhenAll(SweepAsync(), EditAsync());

        var stored = await StoredAsync(id);
        var replayed = await ReplayAsync(id);

        stored.Status.Should().Be(replayed.Status, "the document is a projection of the stream");
        stored.Data["Title"].ToString().Should().Be(replayed.Data["Title"].ToString());
        stored.SearchText.Should().Be(replayed.SearchText);
    }
}
