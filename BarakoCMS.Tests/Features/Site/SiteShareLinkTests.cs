using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// Share links for previewing a held site (#841): created and revoked by whoever may update the
/// <c>site</c> type, redeemed anonymously, and never readable as a key or a hash after creation.
/// </summary>
/// <remarks>
/// Each client carries its own IP so the redeem rate limit only ever fires in the test about it.
/// </remarks>
[Collection("Sequential")]
public class SiteShareLinkTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ipCounter;

    public SiteShareLinkTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        return $"198.51.{n / 250 % 250}.{n % 250 + 1}";
    }

    private static string Sha256Hex(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"shr-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"0 {slug}", IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<List<SiteShareLink>> StoredLinksAsync(string slug)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(slug);
        return (await session.Query<SiteShareLink>().ToListAsync(Ct)).ToList();
    }

    /// <summary>A caller signed in to <paramref name="slug"/> holding one role, with the membership the token issuer insists on.</summary>
    private async Task<HttpClient> SignedInAsync(string slug, Guid roleId, string roleName)
    {
        var userId = Guid.NewGuid();
        var username = $"shr-{Guid.NewGuid():n}"[..14];
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = username,
                Email = $"{username}@example.com",
                RoleIds = [roleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [roleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private Task<HttpClient> SuperAdminInAsync(string slug) => SignedInAsync(slug, SystemRoles.SuperAdminRoleId, "SuperAdmin");

    private async Task<HttpClient> EditorInAsync(string slug, bool mayUpdateSite)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"site-{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = "site",
                    Read = new PermissionRule { Enabled = true },
                    Update = new PermissionRule { Enabled = mayUpdateSite },
                },
            ],
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return await SignedInAsync(slug, role.Id, role.Name);
    }

    private HttpClient AnonymousIn(string slug, string? ip = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? NextIp());
        return client;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/site/share-links", body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<HttpResponseMessage> RedeemAsync(string slug, string key, string? ip = null) =>
        AnonymousIn(slug, ip).PostAsJsonAsync("/api/public/site/share-links/redeem", new { key }, Ct);

    [Fact]
    public async Task Create_returns_the_key_once_and_the_list_never_shows_the_key_or_hash()
    {
        var slug = await TenantAsync();
        var admin = await SuperAdminInAsync(slug);

        var created = await CreateAsync(admin, new { label = "Board preview" });

        var key = created.GetProperty("key").GetString()!;
        WebKeyBytes(key).Should().Be(32);
        created.GetProperty("label").GetString().Should().Be("Board preview");
        var stored = await StoredLinksAsync(slug);
        stored.Should().HaveCount(1);
        stored[0].KeyHash.Should().Be(Sha256Hex(key));
        stored[0].CreatedBy.Should().StartWith("shr-");

        var list = await admin.GetAsync("/api/site/share-links", Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await list.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("id").GetGuid().Should().Be(created.GetProperty("id").GetGuid());
        items[0].GetProperty("createdBy").GetString().Should().Be(stored[0].CreatedBy);
        items[0].GetProperty("lastUsedAt").ValueKind.Should().Be(JsonValueKind.Null);
        items[0].TryGetProperty("key", out _).Should().BeFalse();
        text.Should().NotContain(key).And.NotContain(Sha256Hex(key)).And.NotContainEquivalentOf("hash");
    }

    private static int WebKeyBytes(string key) =>
        Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + new string('=', (4 - key.Length % 4) % 4)).Length;

    [Fact]
    public async Task Only_a_caller_who_may_update_site_can_create_list_or_revoke()
    {
        var slug = await TenantAsync();
        var editor = await EditorInAsync(slug, mayUpdateSite: true);
        var reader = await EditorInAsync(slug, mayUpdateSite: false);
        var id = (await CreateAsync(editor, new { label = "Editor's link" })).GetProperty("id").GetGuid();

        (await editor.GetAsync("/api/site/share-links", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PostAsJsonAsync("/api/site/share-links", new { label = "Nope" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.GetAsync("/api/site/share-links", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"/api/site/share-links/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AnonymousIn(slug).PostAsJsonAsync("/api/site/share-links", new { label = "Nope" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var stored = await StoredLinksAsync(slug);
        stored.Should().HaveCount(1);
        stored[0].RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Expiry_defaults_to_30_days_and_cannot_pass_90()
    {
        var slug = await TenantAsync();
        var admin = await SuperAdminInAsync(slug);

        var byDefault = await CreateAsync(admin, new { label = "Default" });
        byDefault.GetProperty("expiresAt").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromMinutes(2));

        var at90 = DateTimeOffset.UtcNow.AddDays(90);
        (await CreateAsync(admin, new { label = "Longest", expiresAt = at90 })).GetProperty("expiresAt").GetDateTimeOffset()
            .Should().BeCloseTo(at90, TimeSpan.FromSeconds(1));

        (await admin.PostAsJsonAsync("/api/site/share-links", new { label = "Too long", expiresAt = DateTimeOffset.UtcNow.AddDays(91) }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/site/share-links", new { label = "Past", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/site/share-links", new { label = "" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/site/share-links", new { label = new string('a', 101) }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await StoredLinksAsync(slug)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_valid_key_redeems_with_its_expiry_no_store_and_records_the_use()
    {
        var slug = await TenantAsync();
        var created = await CreateAsync(await SuperAdminInAsync(slug), new { label = "Client" });
        var key = created.GetProperty("key").GetString()!;

        var redeemed = await RedeemAsync(slug, key);

        redeemed.StatusCode.Should().Be(HttpStatusCode.OK);
        redeemed.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await redeemed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("expiresAt").GetDateTimeOffset()
            .Should().Be(created.GetProperty("expiresAt").GetDateTimeOffset());
        var stored = await StoredLinksAsync(slug);
        stored.Should().HaveCount(1);
        stored[0].LastUsedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_wrong_expired_or_other_tenant_key_is_404()
    {
        var ours = await TenantAsync();
        var theirs = await TenantAsync();
        var ourKey = (await CreateAsync(await SuperAdminInAsync(ours), new { label = "Ours" })).GetProperty("key").GetString()!;
        var theirKey = (await CreateAsync(await SuperAdminInAsync(theirs), new { label = "Theirs" })).GetProperty("key").GetString()!;

        const string expiredKey = "expired-key-000000000000000000000000000000";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(ours))
        {
            session.Store(new SiteShareLink
            {
                Id = Guid.NewGuid(),
                Label = "Expired",
                KeyHash = Sha256Hex(expiredKey),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-31),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await session.SaveChangesAsync(Ct);
        }

        (await RedeemAsync(ours, ourKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var wrong = await RedeemAsync(ours, ourKey[..^1] + (ourKey[^1] == 'A' ? 'B' : 'A'));
        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrong.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await RedeemAsync(ours, string.Empty)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RedeemAsync(ours, expiredKey)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RedeemAsync(ours, theirKey)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RedeemAsync(theirs, ourKey)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RedeemAsync(theirs, theirKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the key is valid, only on the wrong tenant");
    }

    [Fact]
    public async Task A_revoked_link_no_longer_redeems_and_an_unknown_id_is_404()
    {
        var slug = await TenantAsync();
        var admin = await SuperAdminInAsync(slug);
        var created = await CreateAsync(admin, new { label = "Short lived" });
        var key = created.GetProperty("key").GetString()!;
        (await RedeemAsync(slug, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoked = await admin.DeleteAsync($"/api/site/share-links/{created.GetProperty("id").GetGuid()}", Ct);

        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredLinksAsync(slug)).Single().RevokedAt.Should().NotBeNull();
        (await RedeemAsync(slug, key)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"/api/site/share-links/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redeems_from_one_client_are_throttled_with_429()
    {
        var slug = await TenantAsync();
        var ip = $"192.0.2.{Random.Shared.Next(1, 250)}";

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await RedeemAsync(slug, "wrong", ip)).StatusCode);
        }

        statuses.Should().HaveCount(12);
        statuses.Take(10).Should().OnlyContain(s => s == HttpStatusCode.NotFound);
        statuses.Skip(10).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
        (await RedeemAsync(slug, "wrong")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the limit is per client IP");
    }

    [Fact]
    public async Task Wrong_keys_on_one_tenant_do_not_throttle_another_tenant_from_the_same_ip()
    {
        var spent = await TenantAsync();
        var other = await TenantAsync();
        var key = (await CreateAsync(await SuperAdminInAsync(other), new { label = "Other site" })).GetProperty("key").GetString()!;
        var ip = NextIp();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await RedeemAsync(spent, "wrong", ip)).StatusCode);
        }

        statuses.Should().HaveCount(11);
        statuses.Take(10).Should().OnlyContain(s => s == HttpStatusCode.NotFound);
        statuses[10].Should().Be(HttpStatusCode.TooManyRequests, "otherwise the bucket was never spent");
        (await RedeemAsync(other, key, ip)).StatusCode.Should().Be(HttpStatusCode.OK,
            "one renderer IP redeems for every tenant it serves, so a tenant's bucket is its own");
    }

    [Fact]
    public async Task A_revoke_between_the_redeem_read_and_write_stays_revoked()
    {
        var slug = await TenantAsync();
        var admin = await SuperAdminInAsync(slug);
        var created = await CreateAsync(admin, new { label = "Raced" });
        var key = created.GetProperty("key").GetString()!;
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using (var redeem = store.LightweightSession(slug))
        {
            var read = await ShareLinkKeys.FindActiveAsync(redeem, key, DateTimeOffset.UtcNow, Ct);
            read.Should().NotBeNull();
            read!.RevokedAt.Should().BeNull();

            (await admin.DeleteAsync($"/api/site/share-links/{created.GetProperty("id").GetGuid()}", Ct))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            ShareLinkKeys.RecordUse(redeem, read, DateTimeOffset.UtcNow);
            await redeem.SaveChangesAsync(Ct);
        }

        var stored = (await StoredLinksAsync(slug)).Single();
        stored.LastUsedAt.Should().NotBeNull("the redeem write landed");
        stored.RevokedAt.Should().NotBeNull("the redeem write must not put back the unrevoked copy it read");
        (await RedeemAsync(slug, key)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_one_of_101_active_links_redeems()
    {
        var slug = await TenantAsync();
        var keys = Enumerable.Range(0, 101).Select(_ => ShareLinkKeys.NewKey()).ToList();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(slug))
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < keys.Count; i++)
            {
                session.Store(new SiteShareLink
                {
                    Id = Guid.NewGuid(),
                    Label = $"Link {i}",
                    KeyHash = Sha256Hex(keys[i]),
                    CreatedAt = now.AddSeconds(i),
                    ExpiresAt = now.AddDays(1),
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        (await StoredLinksAsync(slug)).Should().HaveCount(101);

        var statuses = new List<HttpStatusCode>();
        foreach (var key in keys)
        {
            statuses.Add((await RedeemAsync(slug, key)).StatusCode);
        }

        statuses.Should().HaveCount(101).And.OnlyContain(s => s == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Audit_entries_and_the_portability_export_never_hold_the_key_or_hash()
    {
        var slug = await TenantAsync();
        var admin = await SuperAdminInAsync(slug);
        var created = await CreateAsync(admin, new { label = "Audited" });
        var key = created.GetProperty("key").GetString()!;
        var hash = Sha256Hex(key);
        (await StoredLinksAsync(slug)).Single().KeyHash.Should().Be(hash, "otherwise the absence below proves nothing");
        (await admin.DeleteAsync($"/api/site/share-links/{created.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var audits = await session.Query<AuditEvent>()
                .Where(e => e.TenantSlug == slug && e.Action.StartsWith("site.share_link."))
                .ToListAsync(Ct);
            audits.Select(a => a.Action).Should().BeEquivalentTo(["site.share_link.created", "site.share_link.revoked"]);
            var auditText = JsonSerializer.Serialize(audits);
            auditText.Should().Contain("Audited");
            auditText.Should().NotContain(hash).And.NotContain(key);
        }

        var export = await admin.GetAsync("/api/portability/export", Ct);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportText = await export.Content.ReadAsStringAsync(Ct);
        exportText.Should().NotBeNullOrWhiteSpace();
        exportText.Should().NotContain(hash).And.NotContain(key).And.NotContain("Audited");
    }
}
