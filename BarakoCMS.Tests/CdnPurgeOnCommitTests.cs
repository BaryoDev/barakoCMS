using barakoCMS;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Caching.Purge;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every commit that changes what public delivery serves queues a CDN purge row for the tags it
/// touched, and the sender sends it after the commit (#561, slice 2).
/// </summary>
/// <remarks>
/// A provider stand-in on loopback takes the calls. A test enables purging after it has arranged
/// its entries, makes one write, and reads what the stand-in received once the sender has run with
/// a clock past the debounce window. Routes 1 to 12 are the writers in the shaping notes. The
/// listener is on the store, so a writer reached by HTTP, by a service or by a session meets the same
/// code. Where a route needs more set up than its writer is worth (collections push, config apply,
/// erase, workflow actions) the test makes the same document write through a session and says so.
/// </remarks>
[Collection("Integration1")]
public class CdnPurgeOnCommitTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => CdnPurgeTestHost.Ct;

    private CdnPurgeTestHost Host => CdnPurgeTestHost.For(fixture, nameof(CdnPurgeOnCommitTests));

    private sealed record Arranged(string Tenant, string Type, string Zone);

    /// <summary>A tenant with a deliverable type and a zone of its own. Purging is still off.</summary>
    private async Task<Arranged> ArrangeAsync()
    {
        var tenant = await Host.TenantAsync();
        var type = await Host.DefineAsync(tenant);
        return new Arranged(tenant, type, CdnPurgeTestHost.NewZone());
    }

    private async Task<Arranged> ArrangeEnabledAsync()
    {
        var a = await ArrangeAsync();
        await Host.EnableAsync(a.Tenant, a.Zone);
        return a;
    }

    /// <summary>Checks nothing was sent by the commit, runs the sender, and returns the calls.</summary>
    private async Task<IReadOnlyList<PurgeCall>> SendAsync(Arranged a)
    {
        Host.StandIn.CallsFor(a.Zone).Should().BeEmpty("the commit itself never calls the provider");
        (await Host.PendingAsync(a.Tenant)).Should().NotBeEmpty("the commit leaves a row for the sender");
        (await Host.SweepAsync(a.Tenant)).Should().BeGreaterThan(0);
        return Host.StandIn.CallsFor(a.Zone);
    }

    private static void AssertTags(Arranged a, IReadOnlyList<PurgeCall> calls, params Guid[] entries)
    {
        calls.Should().HaveCount(1, "one flush is one call");
        var tags = calls[0].Tags;
        foreach (var entry in entries)
        {
            tags.Should().Contain(CdnPurgeTestHost.EntryTag(a.Tenant, entry));
        }

        tags.Should().Contain(CdnPurgeTestHost.TypeTag(a.Tenant, a.Type));
        tags.Should().Contain(CdnPurgeTestHost.SitemapTag(a.Tenant));
        tags.Should().OnlyContain(t => t.StartsWith(DeliveryCache.TenantTag(a.Tenant)), "every tag carries the tenant prefix");
    }

    private async Task<Guid> CreateOverHttpAsync(HttpClient client, string type, ContentStatus status, string? title = null)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status = (int)status,
            data = new Dictionary<string, object> { ["Title"] = title ?? $"t{Guid.NewGuid():N}" },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task UpdateOverHttpAsync(HttpClient client, Guid id, string title)
    {
        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = title },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    // Route 1: single create with Status Published.
    [Fact]
    public async Task Creating_a_published_entry_purges_its_entry_type_and_sitemap_tags_once()
    {
        var a = await ArrangeEnabledAsync();
        var client = await Host.AdminClientAsync(a.Tenant);

        var id = await CreateOverHttpAsync(client, a.Type, ContentStatus.Published);

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 2: single update. An edit with no status change still leaves a stale page at the edge.
    [Fact]
    public async Task Editing_a_published_entry_purges_its_tags_once()
    {
        var a = await ArrangeAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var id = await CreateOverHttpAsync(client, a.Type, ContentStatus.Published);
        await Host.EnableAsync(a.Tenant, a.Zone);

        await UpdateOverHttpAsync(client, id, $"edited {Guid.NewGuid():N}");

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 3: status change (publish, unpublish, archive share one endpoint, so one test covers them).
    [Fact]
    public async Task Changing_an_entry_status_purges_its_tags_once()
    {
        var a = await ArrangeAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var id = await CreateOverHttpAsync(client, a.Type, ContentStatus.Draft);
        await Host.EnableAsync(a.Tenant, a.Zone);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = (int)ContentStatus.Published }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 4: batch. The bulk import and the portability import call the same IContentBatchRunner,
    // so this one test covers all three.
    [Fact]
    public async Task A_batch_of_creates_purges_once_after_the_batch_commits()
    {
        var a = await ArrangeEnabledAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        var res = await client.PostAsJsonAsync("/api/contents/batch",
            new { steps = ids.Select(id => BatchCreate(a.Type, id)).ToArray() }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        AssertTags(a, await SendAsync(a), ids);
    }

    // Route 5: scheduled publish. The sweep opens its own session, and the store listeners still apply.
    [Fact]
    public async Task A_scheduled_publish_purges_the_entry_tags_once()
    {
        var a = await ArrangeAsync();
        var id = Guid.NewGuid();
        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            session.Store(new Content
            {
                Id = id, ContentType = a.Type, Status = ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Title"] = "scheduled" },
                ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-5),
            });
            await session.SaveChangesAsync(Ct);
        }

        await Host.EnableAsync(a.Tenant, a.Zone);
        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            (await ScheduledContentService.SweepTenantAsync(session, DateTime.UtcNow, Ct)).Should().Be(1);
        }

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 6: a lifecycle transition that writes a ContentUpdated event and no document. The listener
    // reaches the entry through the stream id, a different branch from the document branch.
    [Fact]
    public async Task An_event_only_write_to_a_published_entry_purges_through_the_stream_id()
    {
        var a = await ArrangeAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EnableAsync(a.Tenant, a.Zone);

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            session.Events.Append(id, new ContentUpdated(
                id, new Dictionary<string, object> { ["Title"] = "moved" }, Guid.NewGuid(), null, DateTime.UtcNow));
            await session.SaveChangesAsync(Ct);
        }

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 7: a workflow action. UpdateFieldAction stores the document with no stream, so the document
    // branch is the only one that can see it.
    [Fact]
    public async Task A_workflow_field_update_that_stores_the_document_purges_its_tags()
    {
        var a = await ArrangeAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EnableAsync(a.Tenant, a.Zone);

        await Host.EntryAsync(a.Tenant, a.Type, ContentStatus.Published, id);

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 8: collections push and sync. One session publishes one row and archives another, the way
    // the sync runner saves a pass.
    [Fact]
    public async Task A_collection_pass_that_publishes_and_archives_purges_both_entries_in_one_call()
    {
        var a = await ArrangeAsync();
        var archived = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EnableAsync(a.Tenant, a.Zone);
        var published = Guid.NewGuid();

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            var old = await session.LoadAsync<Content>(archived, Ct);
            old!.Status = ContentStatus.Archived;
            session.Store(old);
            session.Store(new Content
            {
                Id = published, ContentType = a.Type, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Title"] = "pushed" },
            });
            await session.SaveChangesAsync(Ct);
        }

        AssertTags(a, await SendAsync(a), archived, published);
    }

    // Route 9: config apply. It changes a type definition and the entries under it, through the batch
    // runner. A definition change carries the type and sitemap tags.
    [Fact]
    public async Task A_config_apply_that_changes_a_definition_and_an_entry_purges_both()
    {
        var a = await ArrangeEnabledAsync();
        var id = Guid.NewGuid();

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == a.Type, Ct);
            definition.DisplayName = "changed";
            session.Store(definition);
            session.Store(new Content
            {
                Id = id, ContentType = a.Type, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Title"] = "from config" },
            });
            await session.SaveChangesAsync(Ct);
        }

        AssertTags(a, await SendAsync(a), id);
    }

    // Only a definition changes, so only the definition path can name the type and the sitemap.
    [Fact]
    public async Task A_definition_change_alone_purges_the_type_and_sitemap_tags()
    {
        var a = await ArrangeEnabledAsync();

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == a.Type, Ct);
            definition.DisplayName = "changed alone";
            session.Store(definition);
            await session.SaveChangesAsync(Ct);
        }

        var calls = await SendAsync(a);
        AssertTags(a, calls);
        calls[0].Tags.Should().NotContain(t => t.Contains(":entry:"), "no entry changed");
    }

    // Route 10: erase. The eraser deletes the document, so the listener still knows its type.
    [Fact]
    public async Task Erasing_an_entry_purges_the_tags_of_the_deleted_document()
    {
        var a = await ArrangeAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EnableAsync(a.Tenant, a.Zone);

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            // The loaded document, as the eraser deletes it, so the commit knows the type.
            session.Delete((await session.LoadAsync<Content>(id, Ct))!);
            await session.SaveChangesAsync(Ct);
        }

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 11: history rollback. Restoring a version writes ContentUpdated to a published entry.
    [Fact]
    public async Task Rolling_an_entry_back_purges_its_tags_once()
    {
        var a = await ArrangeAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var original = $"v1 {Guid.NewGuid():N}";
        var id = await CreateOverHttpAsync(client, a.Type, ContentStatus.Published, original);
        await UpdateOverHttpAsync(client, id, $"v2 {Guid.NewGuid():N}");
        var version = await VersionOfAsync(client, id, original);
        await Host.EnableAsync(a.Tenant, a.Zone);

        var res = await client.PostAsJsonAsync($"/api/contents/{id}/rollback/{version}", new { }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        AssertTags(a, await SendAsync(a), id);
    }

    // Route 12: other writers. A sensitivity change on an entry (the field changes job and the
    // scheduled sensitivity change write the same way) and a redirect change.
    [Fact]
    public async Task A_sensitivity_change_and_a_redirect_change_purge_their_tags_in_one_call()
    {
        var a = await ArrangeAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EnableAsync(a.Tenant, a.Zone);

        await using (var session = Host.Store.LightweightSession(TenantScopes.TenantIdFor(a.Tenant)))
        {
            var content = await session.LoadAsync<Content>(id, Ct);
            content!.Sensitivity = SensitivityLevel.Members;
            session.Store(content);
            session.Store(new UrlRedirect { Id = Guid.NewGuid(), FromPath = "/old", ToPath = "/new" });
            await session.SaveChangesAsync(Ct);
        }

        var calls = await SendAsync(a);
        AssertTags(a, calls, id);
        calls[0].Tags.Should().Contain(DeliveryCache.Tag(a.Tenant, CacheScope.Named("redirects")));
    }

    [Fact]
    public async Task A_purge_for_one_tenant_never_carries_a_tag_of_another()
    {
        var a = await ArrangeEnabledAsync();
        var b = await ArrangeEnabledAsync();

        var entryA = await Host.EntryAsync(a.Tenant, a.Type);
        var entryB = await Host.EntryAsync(b.Tenant, b.Type);
        await SendAsync(a);
        await SendAsync(b);

        var forA = Host.StandIn.CallsFor(a.Zone).SelectMany(c => c.Tags).ToList();
        var forB = Host.StandIn.CallsFor(b.Zone).SelectMany(c => c.Tags).ToList();
        forA.Should().Contain(CdnPurgeTestHost.EntryTag(a.Tenant, entryA));
        forB.Should().Contain(CdnPurgeTestHost.EntryTag(b.Tenant, entryB));
        forA.Should().NotContain(t => t.Contains(b.Tenant));
        forB.Should().NotContain(t => t.Contains(a.Tenant));
    }

    [Fact]
    public async Task A_batch_of_fifty_entries_sends_one_flush_after_the_commit()
    {
        var a = await ArrangeEnabledAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        var res = await client.PostAsJsonAsync("/api/contents/batch",
            new { steps = ids.Select(id => BatchCreate(a.Type, id)).ToArray() }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        var rows = await Host.PendingAsync(a.Tenant);
        rows.Should().HaveCount(1, "the batch waits for its commit and writes once, not once per entry");
        AssertTags(a, await SendAsync(a), ids);
    }

    [Fact]
    public async Task A_rolled_back_batch_sends_nothing()
    {
        var a = await ArrangeEnabledAsync();
        var client = await Host.AdminClientAsync(a.Tenant);

        var res = await client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[] { BatchCreate(a.Type, Guid.NewGuid()), new { op = "transition", id = Guid.CreateVersion7(), transition = "Issue" } },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "the second step fails, so the first is rolled back");

        (await Host.PendingAsync(a.Tenant)).Should().BeEmpty();
        await Host.SweepAsync(a.Tenant);
        Host.StandIn.CallsFor(a.Zone).Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_500_leaves_a_failed_delivery_row_and_the_publish_still_returns_200()
    {
        var a = await ArrangeEnabledAsync();
        Host.StandIn.AnswerWith(a.Zone, 500, echoAuthorization: true);
        var client = await Host.AdminClientAsync(a.Tenant);

        var id = await CreateOverHttpAsync(client, a.Type, ContentStatus.Published);
        await Host.SweepAsync(a.Tenant);

        Host.StandIn.CallsFor(a.Zone).Should().HaveCount(1);
        var deliveries = await Host.DeliveriesAsync(a.Tenant);
        deliveries.Should().HaveCount(1);
        deliveries[0].ResponseStatus.Should().Be(500);
        deliveries[0].Error.Should().NotBeNullOrEmpty();
        deliveries[0].Event.Should().Be("CdnPurge");
        deliveries[0].WorkflowId.Should().Be(Guid.Empty);
        deliveries[0].RequestHeaders[CdnPurgeSender.TagCountHeader].Should().NotBeNullOrEmpty();
        var retry = await Host.PendingAsync(a.Tenant);
        retry.Should().HaveCount(1, "a failed call is kept for another attempt");
        retry[0].Attempts.Should().Be(1);
        id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_hung_provider_does_not_hold_the_request_and_the_sender_gives_up_at_its_timeout()
    {
        var a = await ArrangeEnabledAsync();
        Host.StandIn.Hang(a.Zone);
        var client = await Host.AdminClientAsync(a.Tenant);

        var watch = Stopwatch.StartNew();
        await CreateOverHttpAsync(client, a.Type, ContentStatus.Published);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the request does not wait for the provider");

        await WithOptionAsync("Delivery:CdnPurge:TimeoutSeconds", "1", async () =>
        {
            watch.Restart();
            await Host.SweepAsync(a.Tenant);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the call is cut off at the timeout");
        });

        var deliveries = await Host.DeliveriesAsync(a.Tenant);
        deliveries.Should().HaveCount(1);
        deliveries[0].Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_token_appears_in_no_delivery_row_and_no_log_line()
    {
        var a = await ArrangeEnabledAsync();
        Host.StandIn.AnswerWith(a.Zone, 403, echoAuthorization: true);
        await Host.EntryAsync(a.Tenant, a.Type);

        await Host.SweepAsync(a.Tenant);

        Host.StandIn.CallsFor(a.Zone).Should().HaveCount(1);
        Host.StandIn.CallsFor(a.Zone)[0].Authorization.Should().Contain(CdnPurgeTestHost.Token, "the provider does get the token");
        var deliveries = await Host.DeliveriesAsync(a.Tenant);
        deliveries.Should().NotBeEmpty();
        JsonSerializer.Serialize(deliveries).Should().NotContain(CdnPurgeTestHost.Token);
        Host.Logs.Should().NotBeEmpty();
        string.Join('\n', Host.Logs).Should().NotContain(CdnPurgeTestHost.Token);
    }

    [Fact]
    public async Task A_delivery_row_carries_a_tag_count_and_never_a_tag()
    {
        var a = await ArrangeEnabledAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);

        await Host.SweepAsync(a.Tenant);

        var deliveries = await Host.DeliveriesAsync(a.Tenant);
        deliveries.Should().HaveCount(1);
        var json = JsonSerializer.Serialize(deliveries);
        json.Should().NotContain(id.ToString("D"));
        json.Should().NotContain(DeliveryCache.TenantTag(a.Tenant));
        deliveries[0].RequestHeaders[CdnPurgeSender.TagCountHeader]
            .Should().Be(Host.StandIn.CallsFor(a.Zone)[0].Tags.Count.ToString());
        deliveries[0].Url.Should().NotContain("/client/v4", "the row keeps the host and drops the path");
    }

    [Fact]
    public async Task A_pending_row_written_by_one_sender_is_sent_by_a_fresh_one()
    {
        var a = await ArrangeEnabledAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        var first = Host.NewSender();

        (await Host.SweepAsync(a.Tenant, first, TimeSpan.Zero)).Should().Be(0, "the debounce window has not passed");
        Host.StandIn.CallsFor(a.Zone).Should().BeEmpty();
        (await Host.PendingAsync(a.Tenant)).Should().HaveCount(1, "the row outlives the sender that saw it");

        await Host.SweepAsync(a.Tenant, Host.NewSender());

        AssertTags(a, Host.StandIn.CallsFor(a.Zone), id);
        (await Host.PendingAsync(a.Tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_entry_published_twice_inside_the_window_is_sent_once()
    {
        var a = await ArrangeEnabledAsync();
        var id = await Host.EntryAsync(a.Tenant, a.Type);
        await Host.EntryAsync(a.Tenant, a.Type, ContentStatus.Published, id);
        (await Host.PendingAsync(a.Tenant)).Should().HaveCount(2);

        await Host.SweepAsync(a.Tenant, ahead: TimeSpan.Zero);
        Host.StandIn.CallsFor(a.Zone).Should().BeEmpty("both commits are inside the window");
        await Host.SweepAsync(a.Tenant, ahead: TimeSpan.FromMinutes(11));

        var calls = Host.StandIn.CallsFor(a.Zone);
        AssertTags(a, calls, id);
        calls[0].Tags.Count(t => t == CdnPurgeTestHost.EntryTag(a.Tenant, id)).Should().Be(1);
        calls[0].Tags.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_commit_during_an_in_flight_flush_lands_in_the_next_flush()
    {
        var a = await ArrangeEnabledAsync();
        var first = await Host.EntryAsync(a.Tenant, a.Type);
        var (arrived, release) = Host.StandIn.HoldNextCall(a.Zone);

        var flushing = Host.SweepAsync(a.Tenant);
        await arrived.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var second = await Host.EntryAsync(a.Tenant, a.Type);
        release();
        await flushing;

        var calls = Host.StandIn.CallsFor(a.Zone);
        calls.Should().HaveCount(1);
        calls[0].Tags.Should().Contain(CdnPurgeTestHost.EntryTag(a.Tenant, first));
        calls[0].Tags.Should().NotContain(CdnPurgeTestHost.EntryTag(a.Tenant, second));

        await Host.SweepAsync(a.Tenant, ahead: TimeSpan.FromHours(2));
        var all = Host.StandIn.CallsFor(a.Zone);
        all.Should().HaveCount(2);
        all[1].Tags.Should().Contain(CdnPurgeTestHost.EntryTag(a.Tenant, second));
    }

    [Fact]
    public async Task A_failing_flush_is_dropped_at_the_attempt_cap()
    {
        var a = await ArrangeEnabledAsync();
        Host.StandIn.AnswerWith(a.Zone, 500);
        await Host.EntryAsync(a.Tenant, a.Type);

        await WithOptionAsync("Delivery:CdnPurge:MaxAttempts", "1", async () => await Host.SweepAsync(a.Tenant));

        Host.StandIn.CallsFor(a.Zone).Should().HaveCount(1);
        (await Host.DeliveriesAsync(a.Tenant)).Should().HaveCount(1);
        (await Host.PendingAsync(a.Tenant)).Should().BeEmpty("the rows are dropped once the cap is reached");
    }

    [Fact]
    public async Task Two_senders_sweeping_together_send_the_rows_once()
    {
        var a = await ArrangeEnabledAsync();
        await Host.EntryAsync(a.Tenant, a.Type);
        var one = Host.NewSender();
        var two = Host.NewSender();

        await Task.WhenAll(Host.SweepAsync(a.Tenant, one), Host.SweepAsync(a.Tenant, two));

        Host.StandIn.CallsFor(a.Zone).Should().HaveCount(1, "the second node loses the claim and leaves the rows");
        (await Host.PendingAsync(a.Tenant)).Should().BeEmpty();
    }

    // The mapping turns on optimistic concurrency for the pending rows, which is what refuses a second claim.
    [Fact]
    public async Task A_pending_row_claimed_twice_from_the_same_read_is_refused_the_second_time()
    {
        var a = await ArrangeEnabledAsync();
        await Host.EntryAsync(a.Tenant, a.Type);
        var tenantId = TenantScopes.TenantIdFor(a.Tenant);
        await using var first = Host.Store.LightweightSession(tenantId);
        await using var second = Host.Store.LightweightSession(tenantId);
        var one = (await first.Query<PendingCdnPurge>().ToListAsync(Ct)).Single();
        var two = (await second.Query<PendingCdnPurge>().ToListAsync(Ct)).Single();
        one.ClaimedAt = DateTimeOffset.UtcNow;
        first.Store(one);
        await first.SaveChangesAsync(Ct);
        two.ClaimedAt = DateTimeOffset.UtcNow;
        second.Store(two);

        var second_claim = async () => await second.SaveChangesAsync(Ct);

        var thrown = await second_claim.Should().ThrowAsync<Exception>();
        ConcurrencyConflict.Is(thrown.Which).Should().BeTrue("the stale claim is the one the version refuses");
    }

    [Fact]
    public async Task A_tenant_with_purge_off_writes_no_row()
    {
        var never = await ArrangeAsync();
        var off = await ArrangeAsync();
        await Host.EnableAsync(off.Tenant, off.Zone, enabled: false);

        await Host.EntryAsync(never.Tenant, never.Type);
        await Host.EntryAsync(off.Tenant, off.Type);

        (await Host.PendingAsync(never.Tenant)).Should().BeEmpty("a tenant with no settings row");
        (await Host.PendingAsync(off.Tenant)).Should().BeEmpty("a tenant whose settings say off");
    }

    [Fact]
    public async Task Turning_purge_on_takes_effect_without_a_restart()
    {
        var a = await ArrangeAsync();
        await Host.EntryAsync(a.Tenant, a.Type);
        (await Host.PendingAsync(a.Tenant)).Should().BeEmpty();

        await Host.EnableAsync(a.Tenant, a.Zone);
        await Host.EntryAsync(a.Tenant, a.Type);

        (await Host.PendingAsync(a.Tenant)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Past_the_tag_cap_a_flush_collapses_to_the_type_tags()
    {
        var a = await ArrangeEnabledAsync();
        var client = await Host.AdminClientAsync(a.Tenant);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        await client.PostAsJsonAsync("/api/contents/batch", new { steps = ids.Select(id => BatchCreate(a.Type, id)).ToArray() }, Ct);

        await WithOptionAsync("Delivery:CdnPurge:MaxTagsPerFlush", "10", async () => await Host.SweepAsync(a.Tenant));

        var calls = Host.StandIn.CallsFor(a.Zone);
        calls.Should().HaveCount(1);
        calls[0].Tags.Should().Contain(CdnPurgeTestHost.TypeTag(a.Tenant, a.Type));
        calls[0].Tags.Should().NotContain(t => t.Contains(":entry:"), "entry tags are dropped, the type tag covers them");
    }

    private async Task WithOptionAsync(string key, string value, Func<Task> run)
    {
        var before = Host.Config[key];
        Host.Config[key] = value;
        try
        {
            await run();
        }
        finally
        {
            Host.Config[key] = before;
        }
    }

    private static object BatchCreate(string type, Guid id) => new
    {
        op = "create", contentType = type, id, status = "Published",
        data = new Dictionary<string, object> { ["Title"] = $"t{id:N}" },
    };

    private static async Task<Guid> VersionOfAsync(HttpClient client, Guid id, string marker)
    {
        var res = await client.GetAsync($"/api/contents/{id}/history", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var match = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(v => v.GetProperty("data").GetProperty("Title").GetString() == marker)
            .Select(v => v.GetProperty("versionId").GetGuid())
            .ToList();
        match.Should().HaveCount(1);
        return match[0];
    }
}
