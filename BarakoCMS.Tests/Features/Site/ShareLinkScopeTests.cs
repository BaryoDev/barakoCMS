using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// Entry and page share links (#857): what each opens, and what it does not. A link is a bearer
/// credential for unpublished content in the hands of someone with no account, so every refusal
/// here sits beside a control that passes.
/// </summary>
[Collection("Sequential")]
public class ShareLinkScopeTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ShareLinkTestHost _host;

    public ShareLinkScopeTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = new ShareLinkTestHost(fixture);
    }

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private static string KeyOf(JsonElement created) => created.GetProperty("key").GetString()!;

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task An_entry_link_opens_that_draft_with_public_fields_only_and_leaves_a_referenced_draft_closed()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var other = await _host.EntryAsync(tenant, type, "other");
        var wip = await _host.EntryAsync(tenant, type, "wip", related: other);
        var admin = await _host.SuperAdminInAsync(tenant);

        (await _host.SlugReadAsync(tenant, type, "wip")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the draft is closed without a link");

        var created = await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "Client review" });

        created.GetProperty("scope").GetString().Should().Be("entry");
        var key = KeyOf(created);
        ShareLinkTestHost.WebKeyBytes(key).Should().Be(32);
        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(1);
        stored[0].EntryId.Should().Be(wip);
        stored[0].Path.Should().BeNull();
        stored[0].Preview.Should().BeFalse();
        stored[0].KeyHash.Should().Be(ShareLinkTestHost.EntryHashHex(key));

        var opened = await _host.OpenAsync(tenant, key);

        opened.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await opened.Content.ReadAsStringAsync(Ct);
        var body = await OkJsonAsync(opened);
        body.GetProperty("scope").GetString().Should().Be("entry");
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(created.GetProperty("expiresAt").GetDateTimeOffset());
        var entry = body.GetProperty("entry");
        entry.GetProperty("id").GetGuid().Should().Be(wip);
        entry.GetProperty("slug").GetString().Should().Be("wip");
        entry.GetProperty("data").GetProperty("Body").GetString().Should().Be(ShareLinkTestHost.BodyOf("wip"));
        entry.GetProperty("data").GetProperty("Body").GetString().Should().NotBeNullOrEmpty("the Public fields are delivered");
        entry.GetProperty("data").TryGetProperty("Related", out _).Should().BeFalse(
            "the referenced entry is a draft, and a reference to one is left out as on the delivery routes");
        text.Should().NotContain(other.ToString());
        text.Should().NotContain(ShareLinkTestHost.SecretValue, "a link is not a signed-in caller and gets Public fields only");
        text.Should().NotContain(ShareLinkTestHost.BodyOf("other"), "the referenced draft is not opened by a link to this one");
        text.Should().NotContain(key).And.NotContain(ShareLinkTestHost.EntryHashHex(key));
        (await _host.StoredLinksAsync(tenant)).Single().LastUsedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        (await _host.SlugReadAsync(tenant, type, "other")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await OkJsonAsync(await _host.AnonymousIn(tenant).GetAsync($"/api/public/{type}", Ct));
        list.GetProperty("totalItems").GetInt32().Should().Be(0, "a link opens one entry, not the type's drafts");
    }

    [Fact]
    public async Task An_entry_key_does_not_redeem_as_a_link_to_the_whole_site()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var entryKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "One entry" }));
        var pageKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "One page", path = "/wip" }));
        var siteKey = KeyOf(await ShareLinkTestHost.CreateSiteLinkAsync(admin, "Whole site"));

        (await _host.RedeemAsync(tenant, siteKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        (await _host.OpenAsync(tenant, entryKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the entry key is live");
        (await _host.OpenAsync(tenant, pageKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the page key is live");

        var wrong = await _host.RedeemAsync(tenant, "wrong");
        var entryRedeem = await _host.RedeemAsync(tenant, entryKey);
        var pageRedeem = await _host.RedeemAsync(tenant, pageKey);

        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        entryRedeem.StatusCode.Should().Be(HttpStatusCode.NotFound, "a frontend reads a 200 here as leave to show the whole held site");
        pageRedeem.StatusCode.Should().Be(HttpStatusCode.NotFound);
        entryRedeem.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await entryRedeem.Content.ReadAsStringAsync(Ct)).Should().Be(await wrong.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_row_that_names_an_entry_is_not_a_site_link_even_under_the_sites_hash()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var siteKey = await _host.StoreLinkAsync(tenant, _ => { });
        var misfiledKey = await _host.StoreLinkAsync(tenant, l => l.EntryId = wip, plainHash: true);
        var misfiled = (await _host.StoredLinksAsync(tenant)).Single(l => l.EntryId == wip);
        misfiled.KeyHash.Should().Be(ShareLinkTestHost.Sha256Hex(misfiledKey), "otherwise redeem never finds the row and the refusal below is the lookup's, not the check's");

        (await _host.RedeemAsync(tenant, siteKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        (await _host.RedeemAsync(tenant, misfiledKey)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var opened = await _host.OpenAsync(tenant, misfiledKey);
        opened.StatusCode.Should().Be(HttpStatusCode.NotFound, "open serves an entry only from a row under the entry hash");
        (await opened.Content.ReadAsStringAsync(Ct)).Should().NotContain(ShareLinkTestHost.BodyOf("wip"));
        (await _host.StoredLinksAsync(tenant)).Single(l => l.EntryId == wip).LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_entry_page_or_preview_key_is_stored_under_a_hash_a_site_lookup_cannot_find()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var entryKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "One entry" }));
        var pageKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "One page", path = "/wip" }));
        var minted = await admin.PostAsJsonAsync("/api/preview", new { Type = type, Slug = "wip" }, Ct);
        var previewKey = (await OkJsonAsync(minted)).GetProperty("token").GetString()!;
        var siteKey = KeyOf(await ShareLinkTestHost.CreateSiteLinkAsync(admin, "Whole site"));

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(4);
        var hashes = stored.Select(l => l.KeyHash).ToList();
        hashes.Should().Contain(ShareLinkTestHost.Sha256Hex(siteKey), "a site link keeps the hash it always had, which is the control");

        // The lookup a build from before entry links runs at redeem: the plain SHA-256 of what was sent.
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        foreach (var key in new[] { entryKey, pageKey, previewKey })
        {
            hashes.Should().NotContain(ShareLinkTestHost.Sha256Hex(key));
            hashes.Should().Contain(ShareLinkTestHost.EntryHashHex(key));
            (await ShareLinkKeys.FindActiveAsync(session, key, DateTimeOffset.UtcNow, Ct)).Should().BeNull();
            (await ShareLinkKeys.FindActiveEntryAsync(session, key, DateTimeOffset.UtcNow, Ct)).Should().NotBeNull();
        }

        (await ShareLinkKeys.FindActiveAsync(session, siteKey, DateTimeOffset.UtcNow, Ct)).Should().NotBeNull();
        (await ShareLinkKeys.FindActiveEntryAsync(session, siteKey, DateTimeOffset.UtcNow, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_key_to_the_whole_site_opens_as_site_scope_and_carries_no_entry()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "wip");
        var siteKey = KeyOf(await ShareLinkTestHost.CreateSiteLinkAsync(await _host.SuperAdminInAsync(tenant), "Whole site"));

        var opened = await _host.OpenAsync(tenant, siteKey);

        opened.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await opened.Content.ReadAsStringAsync(Ct);
        var body = await OkJsonAsync(opened);
        body.GetProperty("scope").GetString().Should().Be("site");
        (body.TryGetProperty("entry", out var entry) && entry.ValueKind != JsonValueKind.Null).Should().BeFalse();
        text.Should().NotContain(ShareLinkTestHost.BodyOf("wip"), "a site link opens nothing the API holds back");
        (await _host.SlugReadAsync(tenant, type, "wip")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_entry_key_in_the_preview_query_opens_nothing()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var key = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(await _host.SuperAdminInAsync(tenant), wip, new { label = "Not for a query" }));

        (await _host.OpenAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK, "the key is live, posted in a body");

        (await _host.SlugReadAsync(tenant, type, "wip", preview: key)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a key that can last 90 days is not taken from a query string, which is logged");
    }

    [Fact]
    public async Task An_entry_key_opens_nothing_on_another_tenant()
    {
        var ours = await _host.TenantAsync();
        var theirs = await _host.TenantAsync();
        var type = await _host.TypeAsync(ours);
        var wip = await _host.EntryAsync(ours, type, "wip");
        var theirType = await _host.TypeAsync(theirs);
        var theirWip = await _host.EntryAsync(theirs, theirType, "wip");
        var ourKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(await _host.SuperAdminInAsync(ours), wip, new { label = "Ours" }));
        var theirKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(await _host.SuperAdminInAsync(theirs), theirWip, new { label = "Theirs" }));

        (await _host.OpenAsync(ours, ourKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        (await _host.OpenAsync(theirs, theirKey)).StatusCode.Should().Be(HttpStatusCode.OK, "their key is live on their tenant");

        var crossed = await _host.OpenAsync(theirs, ourKey);
        crossed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await crossed.Content.ReadAsStringAsync(Ct)).Should().NotContain(ShareLinkTestHost.BodyOf("wip"));
        (await _host.OpenAsync(ours, theirKey)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A signed-in editor of one tenant cannot make a link to another tenant's entry either.
        var crossedCreate = await (await _host.SuperAdminInAsync(theirs))
            .PostAsJsonAsync(ShareLinkTestHost.EntryLinks(wip), new { label = "Across" }, Ct);
        crossedCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.StoredLinksAsync(theirs)).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_admin_of_another_tenant_cannot_list_or_revoke_an_entrys_links()
    {
        var ours = await _host.TenantAsync();
        var theirs = await _host.TenantAsync();
        var type = await _host.TypeAsync(ours);
        var wip = await _host.EntryAsync(ours, type, "wip");
        var theirType = await _host.TypeAsync(theirs);
        var theirWip = await _host.EntryAsync(theirs, theirType, "wip");
        var ourAdmin = await _host.SuperAdminInAsync(ours);
        var theirAdmin = await _host.SuperAdminInAsync(theirs);
        var created = await ShareLinkTestHost.CreateEntryLinkAsync(ourAdmin, wip, new { label = "Ours" });
        var linkId = created.GetProperty("id").GetGuid();

        var ourList = await OkJsonAsync(await ourAdmin.GetAsync(ShareLinkTestHost.EntryLinks(wip), Ct));
        ourList.GetProperty("items").GetArrayLength().Should().Be(1, "the positive control");

        (await theirAdmin.GetAsync(ShareLinkTestHost.EntryLinks(wip), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirAdmin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{linkId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirAdmin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(theirWip)}/{linkId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "their own entry does not reach a link in our partition");
        var theirList = await OkJsonAsync(await theirAdmin.GetAsync(ShareLinkTestHost.EntryLinks(theirWip), Ct));
        theirList.GetProperty("totalItems").GetInt32().Should().Be(0);

        var stored = await _host.StoredLinksAsync(ours);
        stored.Should().HaveCount(1);
        stored[0].RevokedAt.Should().BeNull();
        (await _host.OpenAsync(ours, KeyOf(created))).StatusCode.Should().Be(HttpStatusCode.OK, "the link is still live");
    }

    [Fact]
    public async Task A_caller_whose_update_is_conditional_manages_links_only_where_the_condition_holds()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var draft = await _host.EntryAsync(tenant, type, "draft");
        var published = await _host.EntryAsync(tenant, type, "published", ContentStatus.Published);
        var whileDraft = new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                ["$status"] = new Dictionary<string, object> { ["_eq"] = "Draft" },
            },
        };
        var editor = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: true, update: whileDraft);
        var publishedLinkId = (await ShareLinkTestHost.CreateEntryLinkAsync(
            await _host.SuperAdminInAsync(tenant), published, new { label = "Admin's link" })).GetProperty("id").GetGuid();

        var draftLinkId = (await ShareLinkTestHost.CreateEntryLinkAsync(editor, draft, new { label = "On a draft" })).GetProperty("id").GetGuid();
        (await editor.GetAsync(ShareLinkTestHost.EntryLinks(draft), Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the condition holds for a draft");

        (await editor.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(published), new { label = "Nope" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the rule grants update only while the entry is a draft");
        (await editor.GetAsync(ShareLinkTestHost.EntryLinks(published), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.DeleteAsync($"{ShareLinkTestHost.EntryLinks(published)}/{publishedLinkId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(l => l.RevokedAt == null);
        (await editor.DeleteAsync($"{ShareLinkTestHost.EntryLinks(draft)}/{draftLinkId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_wrong_expired_or_revoked_entry_link_answers_one_404_and_revoking_ends_it_at_once()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var created = await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "Short lived" });
        var key = KeyOf(created);
        var expiredKey = await _host.StoreLinkAsync(tenant, l =>
        {
            l.EntryId = wip;
            l.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
            l.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        (await _host.OpenAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var wrong = await _host.OpenAsync(tenant, key[..^1] + (key[^1] == 'A' ? 'B' : 'A'));
        var expired = await _host.OpenAsync(tenant, expiredKey);
        var revoke = await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{created.GetProperty("id").GetGuid()}", Ct);
        var revoked = await _host.OpenAsync(tenant, key);
        var empty = await _host.OpenAsync(tenant, string.Empty);

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var wrongBody = await wrong.Content.ReadAsStringAsync(Ct);
        foreach (var refused in new[] { wrong, expired, revoked, empty })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            refused.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Be(wrongBody);
        }

        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{created.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "revoking twice is still 204");
        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_link_is_refused_for_an_entry_that_cannot_be_delivered_and_a_stored_one_opens_nothing()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var closedType = await _host.TypeAsync(tenant, deliverable: false);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var gone = await _host.EntryAsync(tenant, type, "gone");
        var sensitive = await _host.EntryAsync(tenant, type, "hidden", sensitivity: SensitivityLevel.Sensitive);
        var closed = await _host.EntryAsync(tenant, closedType, "closed");
        var admin = await _host.SuperAdminInAsync(tenant);

        var wipKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "Deliverable" }));
        var goneKey = KeyOf(await ShareLinkTestHost.CreateEntryLinkAsync(admin, gone, new { label = "Soon gone" }));
        (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(sensitive), new { label = "Sensitive" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(closed), new { label = "Not deliverable" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(2);

        // Links that exist anyway, as they would if the entry or its type changed after the link was made.
        var sensitiveKey = await _host.StoreLinkAsync(tenant, l => l.EntryId = sensitive);
        var closedKey = await _host.StoreLinkAsync(tenant, l => l.EntryId = closed);
        (await _host.OpenAsync(tenant, goneKey)).StatusCode.Should().Be(HttpStatusCode.OK, "before the entry is deleted");
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Delete<Content>(gone);
            await session.SaveChangesAsync(Ct);
        }

        (await _host.OpenAsync(tenant, wipKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        foreach (var key in new[] { sensitiveKey, closedKey, goneKey })
        {
            var refused = await _host.OpenAsync(tenant, key);
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var text = await refused.Content.ReadAsStringAsync(Ct);
            text.Should().NotContain(ShareLinkTestHost.BodyOf("hidden")).And.NotContain(ShareLinkTestHost.BodyOf("closed"));
        }

        var unopened = (await _host.StoredLinksAsync(tenant)).Where(l => l.EntryId == sensitive || l.EntryId == closed).ToList();
        unopened.Should().HaveCount(2);
        unopened.Should().OnlyContain(l => l.LastUsedAt == null, "a link that opened nothing was not used");
    }

    [Fact]
    public async Task A_page_link_carries_its_path_and_a_path_that_leaves_the_site_is_refused()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var page = await _host.EntryAsync(tenant, type, "team");
        await _host.EntryAsync(tenant, type, "child");
        var admin = await _host.SuperAdminInAsync(tenant);

        var created = await ShareLinkTestHost.CreateEntryLinkAsync(admin, page, new { label = "Team page", path = "/about/team" });

        created.GetProperty("scope").GetString().Should().Be("page");
        created.GetProperty("path").GetString().Should().Be("/about/team");
        var opened = await _host.OpenAsync(tenant, KeyOf(created));
        var text = await opened.Content.ReadAsStringAsync(Ct);
        var body = await OkJsonAsync(opened);
        body.GetProperty("scope").GetString().Should().Be("page");
        body.GetProperty("path").GetString().Should().Be("/about/team");
        body.GetProperty("entry").GetProperty("id").GetGuid().Should().Be(page);
        text.Should().NotContain(ShareLinkTestHost.BodyOf("child"), "a page link opens the page and no other draft");
        text.Should().NotContain(ShareLinkTestHost.SecretValue);

        var refusedPaths = new[]
        {
            "//evil.example", "/\\evil.example", "about/team", "/about team", "/about?x=1", "/about#x", "",
            "/" + new string('a', 2048),
            "/.//evil.example", "/..//evil.example", "/a/..//evil.example", "/a//b",
            "/a/./b", "/a/../b", "/a/..", "/.", "/a\\b",
            "/a%2f%2fevil.example", "/%2e%2e/b",
            "/a\u202Eb", "/a\u200Bb", "/a\u2066b", "/a\u0000b", "/a\tb",
        };
        refusedPaths.Should().HaveCount(24);
        foreach (var path in refusedPaths)
        {
            (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(page), new { label = "Bad path", path }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is not a path on the site", Uri.EscapeDataString(path));
        }

        // The other side of each rule: a dot inside a segment, a trailing slash and the root are paths.
        var acceptedPaths = new[] { "/", "/about/team/", "/v1.2/notes", "/a..b" };
        foreach (var path in acceptedPaths)
        {
            (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(page), new { label = "Good path", path }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Created, "'{0}' is a path on the site", path);
        }

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(1 + acceptedPaths.Length);
        stored.Select(l => l.Path).Should().BeEquivalentTo(new[] { "/about/team" }.Concat(acceptedPaths));
        stored.Should().OnlyContain(l => l.EntryId == page);
    }

    [Fact]
    public async Task Only_a_caller_who_may_update_the_entry_can_create_list_or_revoke_its_links()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var editor = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: true);
        var reader = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: false);
        var links = ShareLinkTestHost.EntryLinks(wip);

        var id = (await ShareLinkTestHost.CreateEntryLinkAsync(editor, wip, new { label = "Editor's link" })).GetProperty("id").GetGuid();
        (await editor.GetAsync(links, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await reader.PostAsJsonAsync(links, new { label = "Nope" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.GetAsync(links, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"{links}/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var anonymous = _host.AnonymousIn(tenant);
        (await anonymous.PostAsJsonAsync(links, new { label = "Nope" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(links, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"{links}/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await editor.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(Guid.NewGuid()), new { label = "No entry" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(1);
        stored[0].RevokedAt.Should().BeNull();

        (await editor.DeleteAsync($"{links}/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _host.StoredLinksAsync(tenant)).Single().RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Site_links_and_entry_links_are_listed_and_revoked_apart()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var other = await _host.EntryAsync(tenant, type, "other");
        var admin = await _host.SuperAdminInAsync(tenant);
        var siteId = (await ShareLinkTestHost.CreateSiteLinkAsync(admin, "Whole site")).GetProperty("id").GetGuid();
        var entryId = (await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "One entry" })).GetProperty("id").GetGuid();
        var otherId = (await ShareLinkTestHost.CreateEntryLinkAsync(admin, other, new { label = "Another entry" })).GetProperty("id").GetGuid();

        var siteList = await OkJsonAsync(await admin.GetAsync("/api/site/share-links", Ct));
        siteList.GetProperty("totalItems").GetInt32().Should().Be(1, "the site panel lists links to the whole site only");
        siteList.GetProperty("items").GetArrayLength().Should().Be(1);
        siteList.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(siteId);
        siteList.GetProperty("items")[0].GetProperty("scope").GetString().Should().Be("site");

        var entryList = await OkJsonAsync(await admin.GetAsync(ShareLinkTestHost.EntryLinks(wip), Ct));
        entryList.GetProperty("totalItems").GetInt32().Should().Be(1);
        entryList.GetProperty("items").GetArrayLength().Should().Be(1);
        entryList.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(entryId);
        entryList.GetProperty("items")[0].GetProperty("scope").GetString().Should().Be("entry");
        entryList.GetProperty("maxExpiryDays").GetInt32().Should().Be(ShareLinkKeys.MaxExpiryDays);
        var listText = entryList.GetRawText();
        listText.Should().NotContainEquivalentOf("hash").And.NotContainEquivalentOf("\"key\"");

        (await admin.DeleteAsync($"/api/site/share-links/{entryId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "update on the site type is not what revokes an entry's link");
        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{siteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{otherId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a link is revoked through the entry it opens");

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(3);
        stored.Should().OnlyContain(l => l.RevokedAt == null);

        (await admin.DeleteAsync($"/api/site/share-links/{siteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent, "the positive control");
        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{entryId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Entry_links_do_not_use_up_the_sites_100_and_one_entry_holds_20()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var full = await _host.EntryAsync(tenant, type, "full");
        var free = await _host.EntryAsync(tenant, type, "free");
        var previewed = await _host.EntryAsync(tenant, type, "previewed");
        var admin = await _host.SuperAdminInAsync(tenant);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 100; i++)
            {
                session.Store(new SiteShareLink
                {
                    Id = Guid.NewGuid(),
                    Label = $"Entry link {i}",
                    KeyHash = ShareLinkTestHost.Sha256Hex($"full-{i}-{Guid.NewGuid():n}"),
                    CreatedAt = now,
                    ExpiresAt = now.AddDays(1),
                    EntryId = full,
                });
            }

            for (var i = 0; i < ShareLinkKeys.MaxActivePerEntry; i++)
            {
                session.Store(new SiteShareLink
                {
                    Id = Guid.NewGuid(),
                    Label = $"Preview {i}",
                    KeyHash = ShareLinkTestHost.Sha256Hex($"previewed-{i}-{Guid.NewGuid():n}"),
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(30),
                    EntryId = previewed,
                    Preview = true,
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(100 + ShareLinkKeys.MaxActivePerEntry);

        (await admin.PostAsJsonAsync("/api/site/share-links", new { label = "Whole site" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created,
            "a hundred links to one entry are not a hundred links to the site");
        (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(full), new { label = "One too many" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(free), new { label = "Another entry" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created, "the limit is per entry");
        (await admin.PostAsJsonAsync(ShareLinkTestHost.EntryLinks(previewed), new { label = "Beside previews" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created, "links the preview route issued are not counted");
    }

    [Fact]
    public async Task A_link_stored_before_scopes_existed_is_still_a_link_to_the_whole_site()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.SuperAdminInAsync(tenant);
        var created = await ShareLinkTestHost.CreateSiteLinkAsync(admin, "From 4.2");
        var id = created.GetProperty("id").GetGuid();
        var key = KeyOf(created);

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var strip = new NpgsqlCommand(
                "update public.mt_doc_site_share_links set data = data - 'EntryId' - 'Path' - 'Preview' where id = @id", connection);
            strip.Parameters.AddWithValue("id", id);
            (await strip.ExecuteNonQueryAsync(Ct)).Should().Be(1);

            await using var read = new NpgsqlCommand(
                "select (data ? 'EntryId') or (data ? 'Path') or (data ? 'Preview') from public.mt_doc_site_share_links where id = @id", connection);
            read.Parameters.AddWithValue("id", id);
            ((bool)(await read.ExecuteScalarAsync(Ct))!).Should().BeFalse("otherwise this row is not the shape 4.2 stored");
        }

        (await _host.RedeemAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK);
        var opened = await OkJsonAsync(await _host.OpenAsync(tenant, key));
        opened.GetProperty("scope").GetString().Should().Be("site");
        var list = await OkJsonAsync(await admin.GetAsync("/api/site/share-links", Ct));
        list.GetProperty("totalItems").GetInt32().Should().Be(1);
        list.GetProperty("items").GetArrayLength().Should().Be(1);
        list.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(id);
        list.GetProperty("items")[0].GetProperty("scope").GetString().Should().Be("site");
        (await admin.DeleteAsync($"/api/site/share-links/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _host.RedeemAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Opening_shares_the_redeem_limit_and_a_throttled_answer_is_not_stored()
    {
        var tenant = await _host.TenantAsync();
        var ip = ShareLinkTestHost.NextIp();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            statuses.Add((await _host.OpenAsync(tenant, "wrong", ip)).StatusCode);
        }

        var throttled = await _host.OpenAsync(tenant, "wrong", ip);

        statuses.Should().HaveCount(10);
        statuses.Should().OnlyContain(s => s == HttpStatusCode.NotFound);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await _host.RedeemAsync(tenant, "wrong", ip)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "one bucket for both routes");
        (await _host.OpenAsync(tenant, "wrong")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the limit is per client IP");
    }

    [Fact]
    public async Task Audit_rows_for_an_entry_link_keep_the_action_names_and_never_hold_the_key_or_hash()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var created = await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "Audited entry link" });
        var key = KeyOf(created);
        var hash = ShareLinkTestHost.EntryHashHex(key);
        (await _host.StoredLinksAsync(tenant)).Single().KeyHash.Should().Be(hash, "otherwise the absence below proves nothing");
        (await _host.OpenAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{created.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var audits = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant && e.Action.StartsWith("site.share_link."))
            .ToListAsync(Ct);
        audits.Select(a => a.Action).Should().BeEquivalentTo(["site.share_link.created", "site.share_link.revoked"]);
        var auditText = JsonSerializer.Serialize(audits);
        auditText.Should().Contain("Audited entry link").And.Contain(wip.ToString());
        auditText.Should().NotContain(hash).And.NotContain(key).And.NotContain(ShareLinkTestHost.Sha256Hex(key));
    }
}
