using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// API keys end to end against the real API over a real Postgres. Beyond the happy path this is
/// deliberately adversarial: a forged, revoked, or expired key is refused, a read-only key can't
/// write, and a key can't reach the platform-management surface at all. Auth changes get abuse cases,
/// not just edge cases (see AI_DEVELOPMENT_LIFECYCLE.md).
/// </summary>
[Collection("Sequential")]
public class ApiKeyIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ApiKeyIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // A real SuperAdmin user + JWT. SuperAdmin so the content permission resolver bypasses (leaving the
    // API-key scope check as the thing under test), and a real user so a key can act as it.
    private async Task<(Guid userId, string token)> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"admin-{userId}", Email = $"admin-{userId}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync();
        return (userId, _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
    }

    // Insert a key with a known secret so a test can present it. tenant defaults to "default" so the
    // membership check is skipped — cross-tenant behaviour is its own concern.
    private async Task<string> StoreKeyAsync(Guid ownerId, string[] scopes, bool revoked = false, DateTime? expiresAt = null)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "test",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = scopes.ToList(),
            Revoked = revoked,
            ExpiresAt = expiresAt,
        });
        await session.SaveChangesAsync();
        return secret;
    }

    private HttpRequestMessage Get(string path, string bearer) => WithKey(HttpMethod.Get, path, bearer);
    private HttpRequestMessage WithKey(HttpMethod method, string path, string bearer)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    // ---- happy path ----------------------------------------------------------

    [Fact]
    public async Task ValidKey_WithContentRead_CanListContent()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var res = await _client.SendAsync(Get("/api/contents", secret));

        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());
    }

    // ---- abuse cases: bad credentials ---------------------------------------

    [Fact]
    public async Task ForgedKey_IsRefused()
    {
        var res = await _client.SendAsync(Get("/api/contents", "bcms_" + Guid.NewGuid().ToString("N")));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokedKey_IsRefused()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" }, revoked: true);

        var res = await _client.SendAsync(Get("/api/contents", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredKey_IsRefused()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" }, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var res = await _client.SendAsync(Get("/api/contents", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- abuse cases: scope confinement -------------------------------------

    [Fact]
    public async Task ReadOnlyKey_CannotWriteContent()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var req = WithKey(HttpMethod.Post, "/api/contents", secret);
        req.Content = JsonContent.Create(new { contentType = "anything", status = 1, sensitivity = 0, data = new Dictionary<string, object>() });
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a content:read key must not be allowed to write");
    }

    [Fact]
    public async Task Key_CannotReachManagementApi()
    {
        var (userId, _) = await SuperAdminAsync();
        // Even a wildcard key is confined to the content surface — it cannot manage users, tenants, or keys.
        var secret = await StoreKeyAsync(userId, new[] { "*" });

        var res = await _client.SendAsync(Get("/api/users", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "API keys are limited to the content API");
    }

    [Fact]
    public async Task A_key_cannot_read_the_describe_document()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "*" });

        var res = await _client.SendAsync(Get("/api/meta/describe", secret));

        // Exactly 403. The owner is a SuperAdmin who would be served with a session, so this is the
        // key being refused and not the caller, and a 404 would mean the route is not there.
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "API keys are limited to the content API");
    }

    // ---- destructive content operations (#653) -------------------------------

    private async Task<Guid> SeedContentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        var id = Guid.NewGuid();

        await writer.CreateAsync(new barakoCMS.Events.ContentCreated(
            id, "apikey-probe", new Dictionary<string, object> { ["Title"] = "keep me" },
            ContentStatus.Draft, Guid.NewGuid(), $"apikey-probe-{id:n}",
            SensitivityLevel.Public), default);

        await session.SaveChangesAsync();
        return id;
    }

    private async Task<bool> ContentExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<barakoCMS.Models.Content>(id) is not null;
    }

    [Theory]
    [InlineData("/api/contents/{0}/erase")]
    [InlineData("/API/Contents/{0}/Erase/")]
    public async Task WriteKey_CannotEraseContent(string route)
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read", "content:write" });
        var id = await SeedContentAsync();

        var res = await _client.SendAsync(WithKey(HttpMethod.Delete, string.Format(route, id), secret));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "erasure needs content:destructive, not content:write");
        (await ContentExistsAsync(id)).Should().BeTrue("a refused erase must leave the entry in place");
    }

    [Theory]
    [InlineData("/api/contents/{0}/rollback/{1}")]
    [InlineData("/API/Contents/{0}/Rollback/{1}/")]
    public async Task WriteKey_CannotRollBackContent(string route)
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read", "content:write" });
        var id = await SeedContentAsync();

        var res = await _client.SendAsync(
            WithKey(HttpMethod.Post, string.Format(route, id, Guid.NewGuid()), secret));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "rollback needs content:destructive, not content:write");
    }

    [Theory]
    [InlineData("/api/contents/by-slug/erase/missing")]
    [InlineData("/api/contents/by-slug/rollback/missing")]
    [InlineData("/API/Contents/By-Slug/Rollback/missing/")]
    public async Task ReadWriteKey_ReadingBySlugFromATypeNamedEraseOrRollback_IsNotTreatedAsDestructive(string route)
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read", "content:write" });

        var res = await _client.SendAsync(Get(route, secret));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a by-slug read needs content:read, and a missing slug is 404 whatever the type is called");
    }

    [Theory]
    [InlineData("content:destructive")]
    [InlineData("*")]
    public async Task DestructiveOrWildcardKey_CanEraseContent(string scope)
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { scope });
        var id = await SeedContentAsync();

        var res = await _client.SendAsync(WithKey(HttpMethod.Delete, $"/api/contents/{id}/erase", secret));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await ContentExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task DestructiveKey_CannotMakeOrdinaryWrites()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:destructive" });

        var req = WithKey(HttpMethod.Post, "/api/contents", secret);
        req.Content = JsonContent.Create(new { contentType = "anything", status = 1, sensitivity = 0, data = new Dictionary<string, object>() });
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "content:destructive is not content:write");
    }

    // ---- management endpoints (human admin, JWT) ----------------------------

    [Fact]
    public async Task Admin_CanCreateListAndRevoke_AndSecretIsShownOnce()
    {
        var (_, token) = await SuperAdminAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // create
        var createRes = await _client.PostAsJsonAsync("/api/api-keys",
            new { name = "CI key", scopes = new[] { "content:read" } });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK, because: await createRes.Content.ReadAsStringAsync());
        var created = await createRes.Content.ReadFromJsonAsync<CreatedKey>();
        created!.Key.Should().StartWith("bcms_", "the plaintext secret is returned once, on create");
        created.Id.Should().NotBe(Guid.Empty);

        // list — never leaks the secret or hash
        var listRes = await _client.GetAsync("/api/api-keys");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await listRes.Content.ReadAsStringAsync();
        body.Should().Contain(created.Prefix);
        body.Should().NotContain(created.Key, "the full secret must never appear in a listing");
        body.ToLowerInvariant().Should().NotContain("keyhash");

        // revoke → the key stops working
        var secret = created.Key;
        var revokeRes = await _client.DeleteAsync($"/api/api-keys/{created.Id}");
        revokeRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _client.DefaultRequestHeaders.Authorization = null;
        var useRes = await _client.SendAsync(Get("/api/contents", secret));
        useRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a revoked key is refused immediately");
    }

    [Fact]
    public async Task Create_RejectsUnknownScope()
    {
        var (_, token) = await SuperAdminAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.PostAsJsonAsync("/api/api-keys",
            new { name = "bad", scopes = new[] { "users:delete" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TouchingLastUsed_DoesNotResurrectARevokedKey()
    {
        // Guards the security-review finding: last-used must be a targeted patch, so a concurrent
        // revoke is never clobbered by a stale full-document write that resets Revoked to false.
        var keyId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ApiKey
            {
                Id = keyId,
                Name = "race",
                KeyHash = ApiKeyService.Hash("bcms_" + Guid.NewGuid().ToString("N")),
                Prefix = "bcms_race",
                UserId = Guid.NewGuid(),
                TenantSlug = Tenant.DefaultSlug,
                Scopes = new() { "content:read" },
            });
            await s.SaveChangesAsync();
        }

        // An admin revokes it...
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var k = await s.LoadAsync<ApiKey>(keyId);
            k!.Revoked = true;
            s.Update(k);
            await s.SaveChangesAsync();
        }

        // ...then the same targeted patch the handler runs for last-used lands afterwards.
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Patch<ApiKey>(keyId).Set(x => x.LastUsedAt, DateTime.UtcNow);
            await s.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var k = await s.LoadAsync<ApiKey>(keyId);
            k!.Revoked.Should().BeTrue("a last-used patch must never un-revoke a key");
            k.LastUsedAt.Should().NotBeNull("the patch still records last-used");
        }
    }

    // ---- entry share links (#857) ---------------------------------------------

    /// <summary>A draft of a deliverable type in the default tenant, with one link stored on it.</summary>
    private async Task<(Guid EntryId, Guid LinkId)> SeedLinkedEntryAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = $"keylink{Guid.NewGuid():n}"[..14];
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields = new() { new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" } },
        });
        var entryId = Guid.NewGuid();
        session.Store(new Content
        {
            Id = entryId,
            ContentType = type,
            Status = ContentStatus.Draft,
            Data = new() { ["Title"] = "draft" },
        });
        var linkId = Guid.NewGuid();
        session.Store(new SiteShareLink
        {
            Id = linkId,
            Label = "Stored",
            KeyHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            EntryId = entryId,
        });
        await session.SaveChangesAsync();
        return (entryId, linkId);
    }

    private async Task<List<SiteShareLink>> LinksOfAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<SiteShareLink>().Where(l => l.EntryId == entryId).ToListAsync()).ToList();
    }

    /// <summary>
    /// A share link is a credential for someone with no account, and one a key made would outlive
    /// the key's revocation. The entry link routes sit under <c>/api/contents</c>, the one path a
    /// key reaches, so they are closed by shape, like the site's share link routes.
    /// </summary>
    [Theory]
    [InlineData("*", "/api/contents/{0}/share-links")]
    [InlineData("content:write", "/api/contents/{0}/share-links")]
    [InlineData("*", "/API/Contents/{0}/Share-Links/")]
    public async Task Key_CannotCreateListOrRevokeAnEntrysShareLinks(string scope, string route)
    {
        var (userId, jwt) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read", scope });
        var (entryId, linkId) = await SeedLinkedEntryAsync();
        var links = string.Format(route, entryId);

        var create = WithKey(HttpMethod.Post, links, secret);
        create.Content = JsonContent.Create(new { label = "From a key" });
        var refusals = new[]
        {
            await _client.SendAsync(create),
            await _client.SendAsync(Get(links, secret)),
            await _client.SendAsync(WithKey(HttpMethod.Delete, $"{links.TrimEnd('/')}/{linkId}", secret)),
        };

        refusals.Should().HaveCount(3);
        foreach (var refusal in refusals)
        {
            refusal.StatusCode.Should().Be(HttpStatusCode.Forbidden, "API keys do not reach share links");
            (await refusal.Content.ReadAsStringAsync()).Should().Be("API keys are limited to the content API.");
        }

        var untouched = await LinksOfAsync(entryId);
        untouched.Should().HaveCount(1, "the key made no link");
        untouched[0].RevokedAt.Should().BeNull("the key revoked no link");

        // The control: the same user, signed in, does all three.
        var plain = $"/api/contents/{entryId}/share-links";
        var signedInCreate = WithKey(HttpMethod.Post, plain, jwt);
        signedInCreate.Content = JsonContent.Create(new { label = "Signed in" });
        var created = await _client.SendAsync(signedInCreate);
        created.StatusCode.Should().Be(HttpStatusCode.Created, because: await created.Content.ReadAsStringAsync());
        (await _client.SendAsync(Get(plain, jwt))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.SendAsync(WithKey(HttpMethod.Delete, $"{plain}/{linkId}", jwt))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await LinksOfAsync(entryId);
        after.Should().HaveCount(2);
        after.Single(l => l.Id == linkId).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadKey_ReadingBySlugFromATypeNamedShareLinks_IsNotTreatedAsAShareLinkRoute()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var res = await _client.SendAsync(Get("/api/contents/by-slug/share-links/missing", secret));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a by-slug read needs content:read, and a missing slug is 404 whatever the type is called");
    }

    // ---- files scopes and rotation (#906) -------------------------------------

    private async Task<HttpResponseMessage> UploadWithKeyAsync(string secret)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FileSamples.Png(1, 2, 3));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        var req = WithKey(HttpMethod.Post, "/api/files", secret);
        req.Content = form;
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task A_key_with_files_write_can_upload_a_file()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "files:write" });

        var res = await UploadWithKeyAsync(secret);

        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_without_files_write_cannot_upload_a_file()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:write", "files:read" });

        var res = await UploadWithKeyAsync(secret);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_files_read_key_lists_files_and_a_content_key_does_not()
    {
        var (userId, _) = await SuperAdminAsync();
        var reader = await StoreKeyAsync(userId, new[] { "files:read" });
        var content = await StoreKeyAsync(userId, new[] { "content:read" });

        (await _client.SendAsync(Get("/api/files", reader))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.SendAsync(Get("/api/files", content))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<(Guid Id, string Secret)> RotateAsync(string jwt, Guid keyId, int overlapMinutes)
    {
        var req = WithKey(HttpMethod.Post, $"/api/api-keys/{keyId}/rotate", jwt);
        req.Content = JsonContent.Create(new { overlapMinutes });
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<CreatedKey>();
        return (body!.Id, body.Key);
    }

    private async Task<Guid> KeyIdOfAsync(string secret)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var hash = ApiKeyService.Hash(secret);
        return (await session.Query<ApiKey>().FirstAsync(k => k.KeyHash == hash)).Id;
    }

    [Fact]
    public async Task Rotating_a_key_keeps_both_valid_during_the_overlap()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var oldSecret = await StoreKeyAsync(userId, new[] { "content:read" });
        var oldId = await KeyIdOfAsync(oldSecret);

        var (newId, newSecret) = await RotateAsync(jwt, oldId, 60);

        newSecret.Should().NotBe(oldSecret);
        newId.Should().NotBe(oldId);
        (await _client.SendAsync(Get("/api/contents", oldSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.SendAsync(Get("/api/contents", newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rotating_with_no_overlap_leaves_only_the_new_key()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var oldSecret = await StoreKeyAsync(userId, new[] { "content:read" });

        var (_, newSecret) = await RotateAsync(jwt, await KeyIdOfAsync(oldSecret), 0);
        await Task.Delay(50);

        (await _client.SendAsync(Get("/api/contents", oldSecret))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Get("/api/contents", newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_old_key_stops_working_once_the_overlap_has_passed()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var oldSecret = await StoreKeyAsync(userId, new[] { "content:read" });
        var oldId = await KeyIdOfAsync(oldSecret);

        var (_, newSecret) = await RotateAsync(jwt, oldId, 60);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var old = await session.LoadAsync<ApiKey>(oldId);
            old!.ExpiresAt.Should().NotBeNull();
            old.ExpiresAt!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(1));
            old.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            session.Store(old);
            await session.SaveChangesAsync();
        }

        (await _client.SendAsync(Get("/api/contents", oldSecret))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.SendAsync(Get("/api/contents", newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_revoked_key_cannot_be_rotated_and_a_key_cannot_rotate_itself()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var revoked = await StoreKeyAsync(userId, new[] { "content:read" }, revoked: true);
        var live = await StoreKeyAsync(userId, new[] { "*" });

        var req = WithKey(HttpMethod.Post, $"/api/api-keys/{await KeyIdOfAsync(revoked)}/rotate", jwt);
        req.Content = JsonContent.Create(new { overlapMinutes = 5 });
        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var self = WithKey(HttpMethod.Post, $"/api/api-keys/{await KeyIdOfAsync(live)}/rotate", live);
        self.Content = JsonContent.Create(new { overlapMinutes = 5 });
        (await _client.SendAsync(self)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rotating_with_an_overlap_over_the_limit_is_rejected()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var req = WithKey(HttpMethod.Post, $"/api/api-keys/{await KeyIdOfAsync(secret)}/rotate", jwt);
        req.Content = JsonContent.Create(new { overlapMinutes = 10081 });
        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<(Guid userId, string token)> KeyAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Key Steward {Guid.NewGuid():N}",
            Permissions = new(),
            SystemCapabilities = [SystemCapabilities.ManageApiKeys],
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"steward-{userId}", Email = $"steward-{userId}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync();
        return (userId, _factory.CreateToken(new[] { role.Name }, userId.ToString()));
    }

    private async Task<int> KeyCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<ApiKey>().CountAsync();
    }

    private async Task<HttpResponseMessage> RotateRawAsync(string jwt, Guid keyId)
    {
        var req = WithKey(HttpMethod.Post, $"/api/api-keys/{keyId}/rotate", jwt);
        req.Content = JsonContent.Create(new { overlapMinutes = 60 });
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task An_admin_cannot_rotate_a_super_admins_key()
    {
        var (superId, _) = await SuperAdminAsync();
        var (_, adminJwt) = await KeyAdminAsync();
        var secret = await StoreKeyAsync(superId, new[] { "*" });
        var keyId = await KeyIdOfAsync(secret);
        var before = await KeyCountAsync();

        var res = await RotateRawAsync(adminJwt, keyId);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await KeyCountAsync()).Should().Be(before, "no replacement key may be minted");
        using var scope = _factory.Services.CreateScope();
        var old = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(keyId);
        old!.ExpiresAt.Should().BeNull("the refused rotation must not touch the old key");
    }

    [Fact]
    public async Task An_owner_can_rotate_their_own_key()
    {
        var (adminId, adminJwt) = await KeyAdminAsync();
        var secret = await StoreKeyAsync(adminId, new[] { "content:read" });

        var (newId, newSecret) = await RotateAsync(adminJwt, await KeyIdOfAsync(secret), 60);

        newId.Should().NotBe(Guid.Empty);
        (await _client.SendAsync(Get("/api/contents", secret))).StatusCode.Should().Be(HttpStatusCode.OK, "the old secret works inside the overlap");
        (await _client.SendAsync(Get("/api/contents", newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_super_admin_can_rotate_another_users_key()
    {
        var (ownerId, _) = await KeyAdminAsync();
        var (_, superJwt) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(ownerId, new[] { "content:read" });

        var (newId, _) = await RotateAsync(superJwt, await KeyIdOfAsync(secret), 60);

        newId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_revoke_between_the_rotation_read_and_write_stays_revoked()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var keyId = await KeyIdOfAsync(await StoreKeyAsync(userId, new[] { "content:read" }));

        using var scope = _factory.Services.CreateScope();
        var rotation = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var loaded = await rotation.LoadAsync<ApiKey>(keyId);
        loaded!.Revoked.Should().BeFalse();

        var revoke = await _client.SendAsync(WithKey(HttpMethod.Delete, $"/api/api-keys/{keyId}", jwt));
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        loaded.ExpiresAt = DateTime.UtcNow.AddMinutes(60);
        rotation.Store(loaded);
        var save = async () => await rotation.SaveChangesAsync();
        await save.Should().ThrowAsync<JasperFx.ConcurrencyException>();

        using var check = _factory.Services.CreateScope();
        var after = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(keyId);
        after!.Revoked.Should().BeTrue();
    }

    [Fact]
    public async Task A_wildcard_key_is_refused_on_files_and_a_files_key_is_allowed()
    {
        var (userId, _) = await SuperAdminAsync();
        var wildcard = await StoreKeyAsync(userId, new[] { "*" });
        var explicitKey = await StoreKeyAsync(userId, new[] { "files:read", "files:write" });

        (await _client.SendAsync(Get("/api/files", wildcard))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await UploadWithKeyAsync(wildcard)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Get("/api/files", explicitKey))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadWithKeyAsync(explicitKey)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_denied_rotation_writes_an_audit_row_naming_the_actor_and_key()
    {
        var (superId, _) = await SuperAdminAsync();
        var (adminId, adminJwt) = await KeyAdminAsync();
        var keyId = await KeyIdOfAsync(await StoreKeyAsync(superId, new[] { "*" }));

        (await RotateRawAsync(adminJwt, keyId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "apikey.rotate.denied" && e.TargetId == keyId.ToString()).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(adminId);
    }

    [Fact]
    public async Task Rotating_a_key_whose_owner_no_longer_exists_is_rejected()
    {
        var (_, superJwt) = await SuperAdminAsync();
        var keyId = await KeyIdOfAsync(await StoreKeyAsync(Guid.NewGuid(), new[] { "content:read" }));
        var before = await KeyCountAsync();

        (await RotateRawAsync(superJwt, keyId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await KeyCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task A_rotation_that_loses_a_race_with_a_revoke_answers_409_and_the_key_stays_revoked()
    {
        var (userId, jwt) = await SuperAdminAsync();
        var keyId = await KeyIdOfAsync(await StoreKeyAsync(userId, new[] { "content:read" }));
        var before = await KeyCountAsync();

        RotateApiKeyEndpoint.AfterLoadForTests = async (_, _) =>
        {
            var revoke = await _client.SendAsync(WithKey(HttpMethod.Delete, $"/api/api-keys/{keyId}", jwt));
            revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        };
        try
        {
            (await RotateRawAsync(jwt, keyId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            RotateApiKeyEndpoint.AfterLoadForTests = null;
        }

        (await KeyCountAsync()).Should().Be(before, "the replacement must not be stored");
        using var scope = _factory.Services.CreateScope();
        var after = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(keyId);
        after!.Revoked.Should().BeTrue();
    }

    private sealed record CreatedKey(Guid Id, string Key, string Prefix, string Name);
}
