using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The event stream is supposed to carry the whole document. Nothing else in the suite tests that,
/// because every other test reads a document the writer has just written, so the document and the
/// assertion come from the same code path and agree with each other whatever the events contain.
///
/// This deletes the document and rebuilds it from the stream alone.
/// </summary>
[Collection("Sequential")]
public class ContentStreamRebuildTests
{
    private readonly IntegrationTestFixture _fixture;

    public ContentStreamRebuildTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_field_survives_a_rebuild_from_the_stream_alone()
    {
        var id = Guid.NewGuid();
        var author = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var publishAt = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        var unpublishAt = new DateTime(2026, 12, 31, 9, 0, 0, DateTimeKind.Utc);

        // Exercise every Apply overload. An event type with no overload throws in the writer, so a
        // future event that nobody projected cannot reach the stream silently.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();

            var content = await writer.CreateAsync(new ContentCreated(
                id,
                "article",
                new Dictionary<string, object> { ["Title"] = "first" },
                // Deliberately not Draft/Public. Those are the initialisers on a fresh Content, so
                // arranging them here would let a dropped assignment in Apply(ContentCreated) pass
                // unnoticed: the assertion would be satisfied by the default, not by the event.
                ContentStatus.Archived,
                author,
                "first",
                SensitivityLevel.Hidden), default);

            await writer.AppendAsync(content, new ContentUpdated(
                id, new Dictionary<string, object> { ["Title"] = "second" }, editor, "second"), default);
            await writer.AppendAsync(content, new ContentStatusChanged(id, ContentStatus.Published, editor), default);
            await writer.AppendAsync(content, new ContentScheduled(id, publishAt, unpublishAt, editor), default);
            await writer.AppendAsync(content, new ContentSensitivityChanged(id, SensitivityLevel.Sensitive, editor), default);

            await session.SaveChangesAsync();
        }

        Content stored;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            stored = (await session.LoadAsync<Content>(id))!;
            stored.Should().NotBeNull();
        }

        // Delete the document, leaving only the stream.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<Content>(id);
            await session.SaveChangesAsync();
        }

        // Without this the test proves nothing: if the document were still there, the "rebuild"
        // below could be reading it rather than replaying anything.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            (await session.LoadAsync<Content>(id)).Should().BeNull("the rebuild must have nothing to read");
        }

        Content rebuilt;
        var replayed = new List<object>();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stream = await session.Events.FetchStreamAsync(id);
            stream.Should().NotBeEmpty("the stream is the only surviving record");

            rebuilt = new Content();
            foreach (var e in stream)
            {
                replayed.Add(e.Data);
                var at = e.Timestamp.UtcDateTime;
                switch (e.Data)
                {
                    case ContentCreated x: rebuilt.Apply(x, at); break;
                    case ContentUpdated x: rebuilt.Apply(x, at); break;
                    case ContentStatusChanged x: rebuilt.Apply(x, at); break;
                    case ContentScheduled x: rebuilt.Apply(x, at); break;
                    case ContentSensitivityChanged x: rebuilt.Apply(x, at); break;
                    default:
                        throw new InvalidOperationException(
                            $"{e.Data.GetType().Name} is on the stream with no Apply overload, so a rebuild "
                            + "would drop whatever it carries.");
                }
            }
        }

        // The final state alone does not test Apply(ContentCreated): Sensitivity and Status are both
        // overwritten by later events. Replaying only the first event pins those, and it only works
        // because the arranged values are not the defaults of a fresh Content. Asserting the default
        // would be satisfied by the initialiser whether or not the event carried anything.
        var afterCreate = new Content();
        var first = replayed[0];
        first.Should().BeOfType<ContentCreated>("the stream must open with creation");
        afterCreate.Apply((ContentCreated)first, DateTime.UtcNow);

        afterCreate.Id.Should().Be(id);
        afterCreate.ContentType.Should().Be("article");
        afterCreate.Status.Should().Be(ContentStatus.Archived);
        afterCreate.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        afterCreate.SearchText.Should().Be("first");
        afterCreate.LastModifiedBy.Should().Be(author);
        afterCreate.Data.Should().ContainKey("Title").WhoseValue.ToString().Should().Be("first");

        // Asserted against the values written above, NOT against `stored`.
        //
        // Comparing the rebuild to the stored document is the obvious shape and it is worthless:
        // the stored document is produced by these same Apply overloads, through the writer. Delete
        // a field from Apply and both sides lose it, so they still agree and the test still passes.
        // That was the first version of this test, and it passed with Sensitivity deleted.
        rebuilt.Id.Should().Be(id);
        rebuilt.ContentType.Should().Be("article");
        rebuilt.Data.Should().ContainKey("Title").WhoseValue.ToString().Should().Be("second");
        rebuilt.Status.Should().Be(ContentStatus.Published);
        rebuilt.Sensitivity.Should().Be(SensitivityLevel.Sensitive, "sensitivity drives redaction, so "
            + "losing it produces a readable record rather than a broken one");
        rebuilt.SearchText.Should().Be("second");
        rebuilt.LastModifiedBy.Should().Be(editor);
        rebuilt.ScheduledPublishAt.Should().Be(publishAt);
        rebuilt.ScheduledUnpublishAt.Should().Be(unpublishAt);

        // Secondary: the rebuild and the live write path must also agree with each other. This one
        // cannot stand alone, for the reason above.
        rebuilt.Status.Should().Be(stored.Status);
        rebuilt.Sensitivity.Should().Be(stored.Sensitivity);
        rebuilt.SearchText.Should().Be(stored.SearchText);

        // Not exact. The writer stamps DateTime.UtcNow as it applies, while the stream carries the
        // timestamp the database assigned, so the two are close but never equal. A rebuild therefore
        // shifts CreatedAt and UpdatedAt by the write latency. Tolerated here and called out because
        // it is a real limitation of rebuilding, not a flaw in this test.
        rebuilt.CreatedAt.Should().BeCloseTo(stored.CreatedAt, TimeSpan.FromSeconds(30));
        rebuilt.UpdatedAt.Should().BeCloseTo(stored.UpdatedAt, TimeSpan.FromSeconds(30));
    }
}
