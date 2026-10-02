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
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// What the scoped share link tests and the preview route tests both need: a tenant, a content type
/// with a draft in it, callers with a given permission, and the three anonymous routes.
/// </summary>
/// <remarks>
/// Each client carries its own IP so the redeem rate limit only fires in the test about it.
/// </remarks>
internal sealed class ShareLinkTestHost(IntegrationTestFixture fixture)
{
    public const string SecretValue = "top-secret";

    private static int _ipCounter;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string NextIp()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        return $"203.0.{n / 250 % 250}.{n % 250 + 1}";
    }

    public static string Sha256Hex(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static int WebKeyBytes(string key) =>
        Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + new string('=', (4 - key.Length % 4) % 4)).Length;

    public static string BodyOf(string slug) => $"body of {slug}";

    public async Task<string> TenantAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"scp-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"0 {slug}", IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A type with a slug, a Public body, a Sensitive field and a Public reference field.</summary>
    public async Task<string> TypeAsync(string tenant, bool deliverable = true)
    {
        var type = $"scp{Guid.NewGuid():n}"[..12];
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = deliverable,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "Related", DisplayName = "Related", Type = "reference", ReferenceType = type },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    public async Task<Guid> EntryAsync(
        string tenant,
        string type,
        string slug,
        ContentStatus status = ContentStatus.Draft,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        Guid? related = null)
    {
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object>
        {
            ["Title"] = $"T-{slug}",
            ["Slug"] = slug,
            ["Body"] = BodyOf(slug),
            ["Secret"] = SecretValue,
        };
        if (related is { } target)
        {
            data["Related"] = target.ToString();
        }

        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new Content { Id = id, ContentType = type, Status = status, Sensitivity = sensitivity, Data = data });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    public async Task<List<SiteShareLink>> StoredLinksAsync(string tenant)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        return (await session.Query<SiteShareLink>().ToListAsync(Ct)).ToList();
    }

    /// <summary>Stores a link directly and returns its key.</summary>
    public async Task<string> StoreLinkAsync(string tenant, Action<SiteShareLink> shape)
    {
        var key = WebEncode(RandomNumberGenerator.GetBytes(32));
        var link = new SiteShareLink
        {
            Id = Guid.NewGuid(),
            Label = "Stored",
            KeyHash = Sha256Hex(key),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        };
        shape(link);

        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(link);
        await session.SaveChangesAsync(Ct);
        return key;
    }

    private static string WebEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A caller signed in to <paramref name="tenant"/> holding one role, with the membership the token issuer insists on.</summary>
    public async Task<HttpClient> SignedInAsync(string tenant, Guid roleId, string roleName)
    {
        var userId = Guid.NewGuid();
        var username = $"scp-{Guid.NewGuid():n}"[..14];
        using (var scope = fixture.Services.CreateScope())
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
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [roleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    public Task<HttpClient> SuperAdminInAsync(string tenant) => SignedInAsync(tenant, SystemRoles.SuperAdminRoleId, "SuperAdmin");

    /// <summary>A caller whose one role names <paramref name="type"/> and nothing else.</summary>
    public async Task<HttpClient> EditorInAsync(string tenant, string type, bool mayRead, bool mayUpdate)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"scp-{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = mayRead },
                    Update = new PermissionRule { Enabled = mayUpdate },
                },
            ],
        };
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return await SignedInAsync(tenant, role.Id, role.Name);
    }

    public HttpClient AnonymousIn(string tenant, string? ip = null)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? NextIp());
        return client;
    }

    public static string EntryLinks(Guid entryId) => $"/api/contents/{entryId}/share-links";

    public static async Task<JsonElement> CreateEntryLinkAsync(HttpClient client, Guid entryId, object body)
    {
        var response = await client.PostAsJsonAsync(EntryLinks(entryId), body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public static async Task<JsonElement> CreateSiteLinkAsync(HttpClient client, string label)
    {
        var response = await client.PostAsJsonAsync("/api/site/share-links", new { label }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public Task<HttpResponseMessage> OpenAsync(string tenant, string key, string? ip = null) =>
        AnonymousIn(tenant, ip).PostAsJsonAsync("/api/public/site/share-links/open", new { key }, Ct);

    public Task<HttpResponseMessage> RedeemAsync(string tenant, string key, string? ip = null) =>
        AnonymousIn(tenant, ip).PostAsJsonAsync("/api/public/site/share-links/redeem", new { key }, Ct);

    public Task<HttpResponseMessage> SlugReadAsync(string tenant, string type, string slug, string? preview = null) =>
        AnonymousIn(tenant).GetAsync(
            $"/api/public/{type}/{slug}" + (preview is null ? string.Empty : $"?preview={Uri.EscapeDataString(preview)}"), Ct);
}
