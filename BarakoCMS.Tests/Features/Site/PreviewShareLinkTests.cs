using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// <c>POST /api/preview</c> as a wrapper over an entry share link (#857): the same body and status
/// codes as before, and a token that is now stored, listed, audited and revocable.
/// </summary>
[Collection("Sequential")]
public class PreviewShareLinkTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ShareLinkTestHost _host;

    public PreviewShareLinkTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = new ShareLinkTestHost(fixture);
    }

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private static Task<HttpResponseMessage> MintAsync(HttpClient client, string type, string slug) =>
        client.PostAsJsonAsync("/api/preview", new { Type = type, Slug = slug }, Ct);

    private static async Task<string> TokenAsync(HttpClient client, string type, string slug)
    {
        var response = await MintAsync(client, type, slug);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task The_preview_route_answers_in_the_shape_it_always_had_with_a_deprecation_header()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);

        var minted = await MintAsync(admin, type, "wip");

        minted.StatusCode.Should().Be(HttpStatusCode.OK, await minted.Content.ReadAsStringAsync(Ct));
        minted.Headers.CacheControl!.NoStore.Should().BeTrue();
        minted.Headers.TryGetValues("Deprecation", out var deprecation).Should().BeTrue();
        deprecation!.Should().Equal("@1790899200");
        var body = await minted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["token", "expiresAt", "queryParam"]);
        body.GetProperty("queryParam").GetString().Should().Be("preview");
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(2));
        var token = body.GetProperty("token").GetString()!;
        token.Should().NotContain(".", "the token is a share link key now, not a JWT");
        ShareLinkTestHost.WebKeyBytes(token).Should().Be(32);

        var missing = await MintAsync(admin, type, "no-such-slug");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Headers.Contains("Deprecation").Should().BeTrue();
        (await MintAsync(_host.AnonymousIn(tenant), type, "wip")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _host.SlugReadAsync(tenant, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.OK, "the token works where it always went");
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(1, "only the one answered 200 issued a link");
    }

    [Fact]
    public async Task A_preview_token_is_a_stored_entry_link_that_is_listed_and_audited()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);

        var token = await TokenAsync(admin, type, "wip");

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(1);
        stored[0].KeyHash.Should().Be(ShareLinkTestHost.Sha256Hex(token));
        stored[0].EntryId.Should().Be(wip);
        stored[0].Preview.Should().BeTrue();
        stored[0].Path.Should().BeNull();
        stored[0].CreatedBy.Should().StartWith("scp-");
        (stored[0].ExpiresAt - stored[0].CreatedAt).Should().Be(PreviewToken.DefaultLifetime);

        var listed = await admin.GetAsync(ShareLinkTestHost.EntryLinks(wip), Ct);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        var listText = await listed.Content.ReadAsStringAsync(Ct);
        using var list = JsonDocument.Parse(listText);
        list.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        list.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(stored[0].Id);
        list.RootElement.GetProperty("items")[0].GetProperty("preview").GetBoolean().Should().BeTrue();
        list.RootElement.GetProperty("items")[0].GetProperty("scope").GetString().Should().Be("entry");
        listText.Should().NotContain(token).And.NotContain(stored[0].KeyHash);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var audits = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant && e.Action.StartsWith("site.share_link."))
            .ToListAsync(Ct);
        audits.Should().HaveCount(1);
        audits[0].Action.Should().Be("site.share_link.created");
        audits[0].TargetId.Should().Be(stored[0].Id.ToString());
        var auditText = JsonSerializer.Serialize(audits);
        auditText.Should().Contain(wip.ToString());
        auditText.Should().NotContain(token).And.NotContain(stored[0].KeyHash);
    }

    [Fact]
    public async Task A_preview_token_opens_its_draft_in_the_query_and_the_use_is_recorded()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var other = await _host.EntryAsync(tenant, type, "other");
        await _host.EntryAsync(tenant, type, "wip", related: other);
        await _host.EntryAsync(tenant, type, "hidden", sensitivity: SensitivityLevel.Sensitive);
        var admin = await _host.SuperAdminInAsync(tenant);
        var token = await TokenAsync(admin, type, "wip");
        var hiddenToken = await TokenAsync(admin, type, "hidden");

        (await _host.SlugReadAsync(tenant, type, "wip")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the draft is closed without the token");
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(2).And.OnlyContain(l => l.LastUsedAt == null);

        var preview = await _host.SlugReadAsync(tenant, type, "wip", preview: token);

        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync(Ct));
        preview.Headers.CacheControl!.NoStore.Should().BeTrue("a draft must not be cached");
        var text = await preview.Content.ReadAsStringAsync(Ct);
        text.Should().Contain(ShareLinkTestHost.BodyOf("wip"));
        text.Should().NotContain(ShareLinkTestHost.SecretValue).And.NotContain(ShareLinkTestHost.BodyOf("other"));

        (await _host.SlugReadAsync(tenant, type, "other", preview: token)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the token opens the entry it was issued for, whatever slug is in the URL");
        await _host.EntryAsync(tenant, type, "live", ContentStatus.Published);
        var published = await _host.SlugReadAsync(tenant, type, "live", preview: token);
        published.StatusCode.Should().Be(HttpStatusCode.OK, "at another slug the token is no token, and a published entry reads as it always does");
        (published.Headers.CacheControl?.NoStore ?? false).Should().BeFalse();
        (await published.Content.ReadAsStringAsync(Ct)).Should().Contain(ShareLinkTestHost.BodyOf("live")).And.NotContain(ShareLinkTestHost.BodyOf("wip"));
        (await _host.SlugReadAsync(tenant, type, "hidden", preview: hiddenToken)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a token lifts the Published gate, never the document's sensitivity");

        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(2);
        stored.Single(l => l.KeyHash == ShareLinkTestHost.Sha256Hex(token)).LastUsedAt
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        stored.Single(l => l.KeyHash == ShareLinkTestHost.Sha256Hex(hiddenToken)).LastUsedAt
            .Should().BeNull("a token that opened nothing was not used");
    }

    [Fact]
    public async Task Revoking_a_preview_token_ends_the_preview_at_once()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var token = await TokenAsync(admin, type, "wip");
        (await _host.SlugReadAsync(tenant, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        var linkId = (await _host.StoredLinksAsync(tenant)).Single().Id;

        var revoked = await admin.DeleteAsync($"{ShareLinkTestHost.EntryLinks(wip)}/{linkId}", Ct);

        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _host.SlugReadAsync(tenant, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.OpenAsync(tenant, token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_expired_preview_token_opens_nothing_and_is_removed_at_the_next_mint()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var expired = await _host.StoreLinkAsync(tenant, l =>
        {
            l.EntryId = wip;
            l.Preview = true;
            l.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-31);
            l.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });
        var expiredEditorLink = await _host.StoreLinkAsync(tenant, l =>
        {
            l.EntryId = wip;
            l.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
            l.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        (await _host.SlugReadAsync(tenant, type, "wip", preview: expired)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(2);

        var token = await TokenAsync(admin, type, "wip");

        (await _host.SlugReadAsync(tenant, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        var stored = await _host.StoredLinksAsync(tenant);
        stored.Should().HaveCount(2, "the expired preview token is gone and the new one is in");
        stored.Select(l => l.KeyHash).Should().BeEquivalentTo(
            new[] { ShareLinkTestHost.Sha256Hex(token), ShareLinkTestHost.Sha256Hex(expiredEditorLink) },
            "an expired link an editor made stays in the entry's list");
    }

    [Fact]
    public async Task A_preview_token_opens_nothing_on_another_tenant_and_is_not_a_link_to_the_site()
    {
        var ours = await _host.TenantAsync();
        var theirs = await _host.TenantAsync();
        var type = await _host.TypeAsync(ours);
        await _host.EntryAsync(ours, type, "wip");
        var token = await TokenAsync(await _host.SuperAdminInAsync(ours), type, "wip");

        // The same type name and slug on the other tenant, so only the tenant differs.
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(theirs))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                IsPubliclyDeliverable = true,
                Fields =
                [
                    new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                    new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
                ],
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Draft,
                Data = new() { ["Slug"] = "wip", ["Body"] = "their draft" },
            });
            await session.SaveChangesAsync(Ct);
        }

        (await _host.SlugReadAsync(ours, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var crossed = await _host.SlugReadAsync(theirs, type, "wip", preview: token);
        crossed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await crossed.Content.ReadAsStringAsync(Ct)).Should().NotContain("their draft").And.NotContain(ShareLinkTestHost.BodyOf("wip"));
        (await _host.OpenAsync(theirs, token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.RedeemAsync(ours, token)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a preview token must not open the whole held site");
    }

    [Fact]
    public async Task A_caller_with_read_alone_still_mints_and_minting_is_not_capped()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var reader = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: false);
        var stranger = await _host.EditorInAsync(tenant, type, mayRead: false, mayUpdate: false);
        for (var i = 0; i < 25; i++)
        {
            await _host.StoreLinkAsync(tenant, l =>
            {
                l.EntryId = wip;
                l.Preview = true;
                l.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            });
        }

        var token = await TokenAsync(reader, type, "wip");

        (await _host.SlugReadAsync(tenant, type, "wip", preview: token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(26);
        (await MintAsync(stranger, type, "wip")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no read on the entry, as before");
        (await reader.GetAsync(ShareLinkTestHost.EntryLinks(wip), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "minting a 30 minute token takes read, managing the entry's links takes update");
        (await _host.StoredLinksAsync(tenant)).Should().HaveCount(26);
    }

    [Fact]
    public async Task A_JWT_issued_before_the_route_changed_still_previews_until_it_expires()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var config = _fixture.Services.GetRequiredService<IConfiguration>();
        var jwt = PreviewToken.Create(config, tenant, type, "wip", wip).Token;
        var expiredJwt = PreviewToken.Create(config, tenant, type, "wip", wip, TimeSpan.FromMinutes(-5)).Token;

        var preview = await _host.SlugReadAsync(tenant, type, "wip", preview: jwt);

        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync(Ct));
        preview.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await _host.SlugReadAsync(tenant, type, "wip", preview: expiredJwt)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.StoredLinksAsync(tenant)).Should().BeEmpty("a JWT is verified by its signature and stores nothing");
    }

    [Fact]
    public async Task A_preview_token_is_not_an_API_credential()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await _host.EntryAsync(tenant, type, "wip");
        var token = await TokenAsync(await _host.SuperAdminInAsync(tenant), type, "wip");
        var client = _host.AnonymousIn(tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/me/tenants", Ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
