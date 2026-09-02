using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Opt-in event sourcing, per content type: the decision (#230) and the write path and projection
/// that honour it (#331).
/// </summary>
/// <remarks>
/// Every assertion here is paired. A refusal test on its own passes against a server that refuses
/// everything, and "no events were appended" passes against a write path that appends nothing at
/// all, so each refusal is written next to the request that must still succeed and each rebuild next
/// to the type whose documents must not come back.
/// </remarks>
[Collection("Sequential")]
public class ContentSourcingTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentSourcingTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// An admin client whose token names a user that actually exists.
    /// </summary>
    /// <remarks>
    /// A minted token is enough for the content-type endpoints, which only look at roles, and is not
    /// enough for the content endpoints: those load the user to resolve permissions and answer 401
    /// when there is none.
    /// </remarks>
    private async Task<HttpClient> ClientAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string NewTypeName() => "es" + Guid.NewGuid().ToString("N")[..10];

    private static Task<HttpResponseMessage> CreateTypeAsync(
        HttpClient client, string name, bool eventSourced, object[]? fields = null) =>
        client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Sourcing probe",
            eventSourced,
            fields = fields ?? [new { name = "Title", type = "string", sensitivity = "Public" }],
        });

    private async Task<Guid> CreateContentAsync(HttpClient client, string type, string title)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = title },
            status = "Published",
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Deletes the definition, which is what "delete the type and make it again" leaves behind.</summary>
    /// <remarks>
    /// Done through Marten rather than an endpoint because there is no delete endpoint for a content
    /// type. That is the point of the test: the policy has to survive the definition going away by
    /// any route, including one nobody has written yet.
    /// </remarks>
    private async Task DeleteDefinitionAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name);
        def.Should().NotBeNull("the type has to exist before deleting it proves anything");
        session.Delete(def!);
        await session.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------
    // #230: the decision
    // ---------------------------------------------------------------------------------------

    /// <summary>The default is what every deployment already has, and it is not event sourcing.</summary>
    [Fact]
    public async Task A_content_type_is_not_event_sourced_unless_it_asks_to_be()
    {
        var client = await ClientAsync();
        var quiet = NewTypeName();
        var loud = NewTypeName();

        var quietResponse = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = quiet,
            displayName = "No opinion offered",
            fields = new[] { new { name = "Title", type = "string" } },
        });
        quietResponse.StatusCode.Should().Be(HttpStatusCode.OK, await quietResponse.Content.ReadAsStringAsync());

        var loudResponse = await CreateTypeAsync(client, loud, eventSourced: true);
        loudResponse.StatusCode.Should().Be(HttpStatusCode.OK, await loudResponse.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>();

        (await policy.IsEventSourcedAsync(quiet, default)).Should().BeFalse(
            "a request that says nothing about sourcing gets the behaviour every existing type has");

        // The pair. Without it the assertion above is satisfied by a server that records nothing.
        (await policy.IsEventSourcedAsync(loud, default)).Should().BeTrue(
            "and a request that asks for event sourcing gets it");

        var recorded = await policy.GetAsync(quiet, default);
        recorded.Should().NotBeNull("the decision is recorded for every name, not only for the ones that say yes");
        recorded!.DecidedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    /// <summary>The decision belongs to the name, so deleting the type cannot re-open it.</summary>
    [Fact]
    public async Task Recreating_a_type_name_inherits_the_sourcing_decision_it_was_created_with()
    {
        var client = await ClientAsync();
        var name = NewTypeName();

        (await CreateTypeAsync(client, name, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        await DeleteDefinitionAsync(name);

        var flipped = await CreateTypeAsync(client, name, eventSourced: false);
        flipped.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the obvious way around an immutable flag is to delete the type and make it again");

        var message = await flipped.Content.ReadAsStringAsync();
        message.Should().Contain("eventSourced set to true", "the refusal has to say what the standing answer is");

        // The pair. Without it a server that refused every recreation would pass the assertion above.
        var honoured = await CreateTypeAsync(client, name, eventSourced: true);
        honoured.StatusCode.Should().Be(HttpStatusCode.OK, await honoured.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>()
            .IsEventSourcedAsync(name, default)).Should().BeTrue("the recreated type inherits, it does not re-decide");
    }

    /// <summary>Event sourcing cannot be turned on for a name that already has entries.</summary>
    [Fact]
    public async Task A_name_that_already_has_entries_cannot_become_event_sourced()
    {
        var client = await ClientAsync();
        var used = NewTypeName();

        (await CreateTypeAsync(client, used, eventSourced: false)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateContentAsync(client, used, "written under document sourcing");

        // The policy goes too, which is what a database upgraded from 3.x looks like: types and
        // entries that predate the decision existing. Without this the standing policy refuses the
        // request first and the guard being tested never runs.
        await DeleteDefinitionAsync(used);
        await DeletePolicyAsync(used);

        var refused = await CreateTypeAsync(client, used, eventSourced: true);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "there is no history the stream can claim to be the source of truth for");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("before the first entry");

        // The pair: a name with nothing behind it takes the same request.
        var fresh = NewTypeName();
        (await CreateTypeAsync(client, fresh, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the refusal above is about the entries, not about event sourcing");
    }

    /// <summary>An event-sourced type may not hold non-Public fields.</summary>
    [Fact]
    public async Task An_event_sourced_type_is_refused_a_non_public_field()
    {
        var client = await ClientAsync();

        object[] withSecret =
        [
            new { name = "Title", type = "string", sensitivity = "Public" },
            new { name = "Secret", type = "string", sensitivity = "Sensitive" },
        ];

        var refused = await CreateTypeAsync(client, NewTypeName(), eventSourced: true, fields: withSecret);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "erasing a value out of an append-only stream is not something this server can do");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Secret", "the refusal names the field");

        // Two pairs, because there are two ways this could pass for the wrong reason: a server that
        // refuses every event-sourced type, and a server that refuses every non-Public field.
        var publicOnly = await CreateTypeAsync(client, NewTypeName(), eventSourced: true);
        publicOnly.StatusCode.Should().Be(HttpStatusCode.OK, await publicOnly.Content.ReadAsStringAsync());

        var documentMode = await CreateTypeAsync(client, NewTypeName(), eventSourced: false, fields: withSecret);
        documentMode.StatusCode.Should().Be(HttpStatusCode.OK, await documentMode.Content.ReadAsStringAsync());
    }

    /// <summary>And it cannot acquire one afterwards either.</summary>
    [Fact]
    public async Task A_field_on_an_event_sourced_type_cannot_be_raised_above_public()
    {
        var client = await ClientAsync();
        var sourced = NewTypeName();
        var plain = NewTypeName();

        (await CreateTypeAsync(client, sourced, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateTypeAsync(client, plain, eventSourced: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        var refused = await client.PutAsJsonAsync(
            $"/api/content-types/{sourced}/fields/Title/sensitivity", new { sensitivity = "Sensitive" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a type that could not be created with a non-Public field must not be able to grow one");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("event sourced");

        // The pair. The endpoint still does its job for every other type.
        var allowed = await client.PutAsJsonAsync(
            $"/api/content-types/{plain}/fields/Title/sensitivity", new { sensitivity = "Sensitive" });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>Removes the policy, leaving a name with entries and no recorded decision.</summary>
    private async Task DeletePolicyAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Delete<ContentTypeSourcingPolicy>(name);
        await session.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------
    // #331: the adoption
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The one test that decides whether the feature is real: delete every document and get them
    /// back from the streams alone.
    /// </summary>
    [Fact]
    public async Task The_read_model_of_an_event_sourced_type_is_rebuilt_from_its_stream_alone()
    {
        var client = await ClientAsync();
        var type = NewTypeName();
        (await CreateTypeAsync(client, type, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var id = await CreateContentAsync(client, type, "first");

        var edited = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "second" },
            version = 1,
        });
        edited.StatusCode.Should().Be(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());

        var archived = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { newStatus = "Archived" });
        archived.StatusCode.Should().Be(HttpStatusCode.OK, await archived.Content.ReadAsStringAsync());

        var before = await LoadAsync(id);
        before.Should().NotBeNull();

        await DeleteEveryDocumentAsync(type);
        (await LoadAsync(id)).Should().BeNull("the rebuild must have nothing to read");

        var rebuild = await client.PostAsJsonAsync($"/api/content-types/{type}/rebuild", new { });
        rebuild.StatusCode.Should().Be(HttpStatusCode.OK, await rebuild.Content.ReadAsStringAsync());

        using (var body = JsonDocument.Parse(await rebuild.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("rebuilt").GetInt32().Should().Be(1);
        }

        var after = await LoadAsync(id);
        after.Should().NotBeNull("the stream is the source of truth, so the document comes back");

        // Asserted against the values written above rather than only against `before`. Comparing the
        // rebuild to what the write path stored is the obvious shape and it is worthless on its own:
        // both sides come from the same Apply overloads, so a field dropped from Apply disappears
        // from both and they still agree.
        after!.Id.Should().Be(id);
        after.ContentType.Should().Be(type);
        after.Data.Should().ContainKey("Title").WhoseValue.ToString().Should().Be("second");
        after.Status.Should().Be(ContentStatus.Archived);
        after.Sensitivity.Should().Be(SensitivityLevel.Public);
        after.SearchText.Should().Be("second");
        after.CreatedBy.Should().Be(before!.CreatedBy).And.NotBe(Guid.Empty);
        after.LastModifiedBy.Should().Be(before.LastModifiedBy);
        after.LifecycleState.Should().Be(before.LifecycleState);
        after.ScheduledPublishAt.Should().Be(before.ScheduledPublishAt);
        after.ScheduledUnpublishAt.Should().Be(before.ScheduledUnpublishAt);

        // Not exact. The write path stamps UtcNow as it applies and the stream carries the timestamp
        // the database assigned, so a rebuild shifts both by the write latency. A real limitation of
        // rebuilding, called out rather than hidden behind an equality that would be flaky.
        after.CreatedAt.Should().BeCloseTo(before.CreatedAt, TimeSpan.FromMinutes(5));
        after.UpdatedAt.Should().BeCloseTo(before.UpdatedAt, TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// The control. A document-mode type is refused a rebuild, and its documents stay gone.
    /// </summary>
    /// <remarks>
    /// Without this the test above proves nothing about the flag: a rebuild that restored both modes
    /// would mean something else was writing the documents.
    /// </remarks>
    [Fact]
    public async Task A_document_type_is_refused_a_rebuild_and_its_documents_do_not_come_back()
    {
        var client = await ClientAsync();
        var type = NewTypeName();
        (await CreateTypeAsync(client, type, eventSourced: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        var id = await CreateContentAsync(client, type, "written by the handler");
        (await LoadAsync(id)).Should().NotBeNull();

        await DeleteEveryDocumentAsync(type);

        var refused = await client.PostAsJsonAsync($"/api/content-types/{type}/rebuild", new { });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "its document is the source of truth and its stream is an audit trail");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("not event sourced");

        (await LoadAsync(id)).Should().BeNull(
            "if this came back, the documents are produced from the stream whatever the flag says");
    }

    /// <summary>
    /// A write against a stale read of an event-sourced type is refused, and the same write against
    /// a document type still wins.
    /// </summary>
    [Fact]
    public async Task A_stale_write_is_refused_on_an_event_sourced_type_and_accepted_on_a_document_type()
    {
        var client = await ClientAsync();
        var sourced = NewTypeName();
        var plain = NewTypeName();

        (await CreateTypeAsync(client, sourced, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateTypeAsync(client, plain, eventSourced: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        var sourcedId = await CreateContentAsync(client, sourced, "first");
        var plainId = await CreateContentAsync(client, plain, "first");

        // Version 0 is "I did not check", which is exactly the write a stale read produces.
        var refused = await client.PutAsJsonAsync($"/api/contents/{sourcedId}", new
        {
            id = sourcedId,
            data = new Dictionary<string, object> { ["Title"] = "written blind" },
            version = 0,
        });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the stream is the record, so a write that cannot say where the stream was is refused");
        (await LoadAsync(sourcedId))!.Data["Title"].ToString().Should().Be("first",
            "and the refusal has to mean the write did not land");

        // First pair: the same request on a document type keeps last-write-wins.
        var accepted = await client.PutAsJsonAsync($"/api/contents/{plainId}", new
        {
            id = plainId,
            data = new Dictionary<string, object> { ["Title"] = "written blind" },
            version = 0,
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await LoadAsync(plainId))!.Data["Title"].ToString().Should().Be("written blind");

        // Second pair: an event-sourced type is still writable by a caller that did check, or the
        // 409 above would just be "event-sourced types are read only".
        var honest = await client.PutAsJsonAsync($"/api/contents/{sourcedId}", new
        {
            id = sourcedId,
            data = new Dictionary<string, object> { ["Title"] = "written from a fresh read" },
            version = 1,
        });
        honest.StatusCode.Should().Be(HttpStatusCode.OK, await honest.Content.ReadAsStringAsync());
        (await LoadAsync(sourcedId))!.Data["Title"].ToString().Should().Be("written from a fresh read");

        // And a version that is wrong rather than missing is refused too, with the same status.
        var wrong = await client.PutAsJsonAsync($"/api/contents/{sourcedId}", new
        {
            id = sourcedId,
            data = new Dictionary<string, object> { ["Title"] = "written from a stale read" },
            version = 1,
        });
        wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// The write path itself, not the rebuild: on an event-sourced type the document is produced by
    /// folding the stream, so a value that reached it by another route does not survive the next
    /// write. On a document type the same value does survive, because the document is the record.
    /// </summary>
    [Fact]
    public async Task An_event_sourced_document_is_produced_from_its_stream_and_a_document_type_keeps_what_it_holds()
    {
        var client = await ClientAsync();
        var sourced = NewTypeName();
        var plain = NewTypeName();

        (await CreateTypeAsync(client, sourced, eventSourced: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateTypeAsync(client, plain, eventSourced: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        var sourcedId = await CreateContentAsync(client, sourced, "first");
        var plainId = await CreateContentAsync(client, plain, "first");

        // Drift, planted straight onto the documents with no event behind it. This is what a write
        // path that appends nothing leaves, and what a partial restore leaves.
        await TamperAsync(sourcedId, "planted, never appended");
        await TamperAsync(plainId, "planted, never appended");

        (await LoadAsync(sourcedId))!.Data["Drift"].ToString().Should().Be("planted, never appended",
            "the tamper has to be there, or the assertions below pass without anything happening");

        // The event-sourced one has to say where the stream was. The plain one still does not, which
        // is the pairing: if the version became mandatory everywhere, that would be the breaking
        // change this whole design is arranged to avoid.
        await ScheduleOkAsync(client, sourcedId, await StreamVersionAsync(sourcedId));
        await ScheduleOkAsync(client, plainId);

        var sourcedAfter = await LoadAsync(sourcedId);
        sourcedAfter!.Data.Should().NotContainKey("Drift",
            "the stream is the source of truth, so a value no event carries is gone after the next write");
        sourcedAfter.ScheduledPublishAt.Should().NotBeNull("and the write itself still landed");

        var plainAfter = await LoadAsync(plainId);
        plainAfter!.Data.Should().ContainKey("Drift").WhoseValue.ToString().Should().Be("planted, never appended",
            "a document type's document is the record, so nothing discards it");
        plainAfter.ScheduledPublishAt.Should().NotBeNull();
    }

    /// <summary>
    /// A rebuild runs under the tenant its events carry and does not reach across.
    /// </summary>
    /// <remarks>
    /// A regression test for #287 in a new place rather than a new idea. A rebuild that crossed
    /// tenants would write one tenant's content into another's documents, which is a breach and not
    /// a bug, so it is asserted with two tenants rather than assumed of Marten.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_stays_inside_the_tenant_the_events_carry()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var type = NewTypeName();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        foreach (var (tenant, id, title) in new[] { ("tenant-a", idA, "belongs to a"), ("tenant-b", idB, "belongs to b") })
        {
            await using var session = store.LightweightSession(tenant);
            var policy = new ContentSourcingPolicyService(session);
            await policy.DecideAsync(type, eventSourced: true, default);

            var writer = new ContentWriter(session, policy);
            await writer.CreateAsync(new ContentCreated(
                id, type, new Dictionary<string, object> { ["Title"] = title },
                ContentStatus.Published, Guid.NewGuid(), title, SensitivityLevel.Public), default);

            await session.SaveChangesAsync();
        }

        // Both documents gone, so anything that comes back came from a stream.
        foreach (var (tenant, id) in new[] { ("tenant-a", idA), ("tenant-b", idB) })
        {
            await using var session = store.LightweightSession(tenant);
            session.Delete<Content>(id);
            await session.SaveChangesAsync();
        }

        int rebuilt;
        await using (var session = store.LightweightSession("tenant-a"))
        {
            var result = await new ContentRebuilder(session, new ContentSourcingPolicyService(session))
                .RebuildAsync(type, default);
            result.EventSourced.Should().BeTrue();
            rebuilt = result.Rebuilt;
        }

        rebuilt.Should().Be(1, "tenant-a has exactly one stream of this type, and tenant-b's is not its business");

        await using (var session = store.QuerySession("tenant-a"))
        {
            var back = await session.LoadAsync<Content>(idA);
            back.Should().NotBeNull("tenant-a's own document is rebuilt");
            back!.Data["Title"].ToString().Should().Be("belongs to a");
        }

        await using (var session = store.QuerySession("tenant-b"))
        {
            (await session.LoadAsync<Content>(idB)).Should().BeNull(
                "a rebuild run by tenant-a must not write into tenant-b");
        }

        // The pair. Without it "tenant-b is still empty" is satisfied by a rebuild that does nothing
        // at all, in either tenant.
        await using (var session = store.LightweightSession("tenant-b"))
        {
            var result = await new ContentRebuilder(session, new ContentSourcingPolicyService(session))
                .RebuildAsync(type, default);
            result.Rebuilt.Should().Be(1);
        }

        await using (var session = store.QuerySession("tenant-b"))
        {
            var back = await session.LoadAsync<Content>(idB);
            back.Should().NotBeNull();
            back!.Data["Title"].ToString().Should().Be("belongs to b");
        }
    }

    // ---------------------------------------------------------------------------------------

    private async Task<Content?> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id);
    }

    private async Task DeleteEveryDocumentAsync(string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var docs = await session.Query<Content>().Where(c => c.ContentType == contentType).ToListAsync();
        docs.Should().NotBeEmpty("deleting nothing would make the rebuild assertions meaningless");

        foreach (var doc in docs)
        {
            session.Delete(doc);
        }

        await session.SaveChangesAsync();
    }

    /// <summary>Writes a value onto the document with no event behind it.</summary>
    private async Task TamperAsync(Guid id, string value)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var doc = await session.LoadAsync<Content>(id);
        doc.Should().NotBeNull();
        doc!.Data["Drift"] = value;
        session.Store(doc);
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// A name that already exists in another case cannot be created as event sourced.
    /// </summary>
    /// <remarks>
    /// Names are normalised on the way in from 4.0 and were not before, so a 3.x import stored the
    /// file's own spelling and "Article" can be sitting in a deployed database. Postgres compares
    /// exactly, while every reader in the codebase matches names with OrdinalIgnoreCase. So the
    /// duplicate check found nothing, the entry count found nothing, and "article" could be created
    /// as event sourced beside entries written under document rules, whose sensitivity a rebuild
    /// would then invent.
    ///
    /// The row is written straight to the database rather than through the endpoint, deliberately:
    /// the endpoint normalises, so it can no longer produce the state this is about.
    ///
    /// Paired with a name nobody has, because a guard that refused every event-sourced creation
    /// would pass the first half of this on its own.
    /// </remarks>
    [Fact]
    public async Task A_name_that_exists_in_another_case_cannot_be_created_as_event_sourced()
    {
        var client = await ClientAsync();
        var name = NewTypeName();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name.ToUpperInvariant(),
                DisplayName = "A pre-4.0 import",
                Fields = new List<FieldDefinition>(),
            });
            await session.SaveChangesAsync();
        }

        var refused = await CreateTypeAsync(client, name, eventSourced: true);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the same name in another case is the same type to every reader: {0}",
            await refused.Content.ReadAsStringAsync());

        var fresh = await CreateTypeAsync(client, NewTypeName(), eventSourced: true);
        fresh.IsSuccessStatusCode.Should().BeTrue(
            "a name nobody has must still be creatable, or the refusal above proves only that "
          + "everything is refused: {0}",
            await fresh.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Scheduling is a write, so an event-sourced type asks it where the stream was like any other.
    /// </summary>
    /// <remarks>
    /// It did not. The schedule endpoint called the single-event append, which never consults a
    /// version, so a type documented as answering 409 to a stale write answered 200 here and armed a
    /// publish time against a copy that had since been edited or archived. The scheduler then acted
    /// on that days later.
    ///
    /// Paired with the document type deliberately: making the version mandatory for everyone would
    /// break every existing client of this endpoint, and that is the change this must not be.
    /// </remarks>
    [Fact]
    public async Task A_schedule_with_no_version_is_refused_on_an_event_sourced_type_and_accepted_on_a_document_type()
    {
        var client = await ClientAsync();

        var sourcedType = NewTypeName();
        var plainType = NewTypeName();
        (await CreateTypeAsync(client, sourcedType, eventSourced: true)).IsSuccessStatusCode.Should().BeTrue();
        (await CreateTypeAsync(client, plainType, eventSourced: false)).IsSuccessStatusCode.Should().BeTrue();

        var sourcedId = await CreateContentAsync(client, sourcedType, "sourced");
        var plainId = await CreateContentAsync(client, plainType, "plain");

        var refused = await ScheduleAsync(client, sourcedId);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "no version means the caller cannot say what it was scheduling against");

        var accepted = await ScheduleAsync(client, plainId);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK,
            "every client of this endpoint sends no version today, and a document type must keep working");

        // And the refusal has to have refused something, not merely returned 409 with the schedule
        // armed anyway.
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(sourcedId);
        stored!.ScheduledPublishAt.Should().BeNull("the refused write must not have landed");

        // Sending the version it actually is at goes through.
        await ScheduleOkAsync(client, sourcedId, await StreamVersionAsync(sourcedId));
    }

    /// <param name="version">
    /// The stream version, or 0 for the documented bypass. An event-sourced type answers 409 to 0,
    /// which is the point of the endpoint taking a version at all.
    /// </param>
    private static async Task<HttpResponseMessage> ScheduleAsync(HttpClient client, Guid id, long version = 0)
        => await client.PutAsJsonAsync($"/api/contents/{id}/schedule", new
        {
            id,
            scheduledPublishAt = DateTime.UtcNow.AddDays(1),
            version,
        });

    private static async Task ScheduleOkAsync(HttpClient client, Guid id, long version = 0)
    {
        var response = await ScheduleAsync(client, id, version);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> StreamVersionAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var state = await session.Events.FetchStreamStateAsync(id);
        return state?.Version ?? 0;
    }
}
