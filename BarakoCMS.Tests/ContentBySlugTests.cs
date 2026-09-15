using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>GET /api/contents/by-slug/{type}/{slug}</c> answers what <c>GET /api/contents/{id}</c> answers
/// for the entry the slug names.
/// </summary>
/// <remarks>
/// Every type here is left not publicly deliverable and every entry a Draft, because that is the case
/// this route exists for: a page the anonymous delivery route will never serve, read by a signed-in
/// viewer.
/// </remarks>
[Collection("Sequential")]
public class ContentBySlugTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentBySlugTests(IntegrationTestFixture factory) => _factory = factory;

    private static ContentTypeDefinition TypeDefinition(string type) => new()
    {
        Id = Guid.NewGuid(),
        Name = type,
        DisplayName = type,
        IsPubliclyDeliverable = false,
        Fields =
        [
            new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
            new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
            new FieldDefinition { Name = "BirthDay", DisplayName = "BirthDay", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            new FieldDefinition { Name = "SSN", DisplayName = "SSN", Type = "string", Sensitivity = SensitivityLevel.Hidden },
        ],
    };

    private static Content Entry(string type, string slug, string title) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = ContentStatus.Draft,
        Sensitivity = SensitivityLevel.Public,
        Data = new Dictionary<string, object>
        {
            ["Title"] = title,
            ["Slug"] = slug,
            ["BirthDay"] = "1990-05-15",
            ["SSN"] = "123-45-6789",
        },
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A type and two entries in the default tenant. The decoy is the older of the two, so a lookup
    /// that ignored the slug and took the oldest row would return it.
    /// </summary>
    private async Task<(string Type, Content Target)> SeedAsync()
    {
        var type = $"gated_{Guid.NewGuid():N}"[..20];
        var decoy = Entry(type, "decoy", "not this one");
        decoy.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        var target = Entry(type, "members-only", "the gated page");

        using var scope = _factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(TypeDefinition(type));
        session.Store(decoy);
        session.Store(target);
        await session.SaveChangesAsync();
        return (type, target);
    }

    private async Task<HttpClient> ViewerAsync(string type, bool canRead, string ip, Dictionary<string, object>? readConditions = null)
    {
        using var scope = _factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"viewer_{Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = canRead, Conditions = readConditions },
                    Create = new PermissionRule { Enabled = false },
                    Update = new PermissionRule { Enabled = false },
                    Delete = new PermissionRule { Enabled = false },
                },
            ],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"viewer_{Guid.NewGuid():N}"[..20],
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync();

        var client = Client(ip);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken([$"Viewer_{Guid.NewGuid():N}"], user.Id.ToString()));
        return client;
    }

    /// <summary>Its own rate-limit bucket, so a 429 cannot stand in for the status under test.</summary>
    private HttpClient Client(string ip)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task A_viewer_with_read_permission_gets_the_entry_by_slug()
    {
        var (type, target) = await SeedAsync();
        var viewer = await ViewerAsync(type, canRead: true, "203.0.113.171");

        var resp = await viewer.GetAsync($"/api/contents/by-slug/{type}/members-only");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(resp);
        body.GetProperty("id").GetGuid().Should().Be(target.Id);
        body.GetProperty("data").GetProperty("Title").GetString().Should().Be("the gated page");
    }

    [Fact]
    public async Task A_viewer_without_read_permission_cannot_tell_an_unreadable_slug_from_a_missing_one()
    {
        var (type, target) = await SeedAsync();
        var viewer = await ViewerAsync(type, canRead: false, "203.0.113.172");

        var byId = await viewer.GetAsync($"/api/contents/{target.Id}");
        var unreadable = await viewer.GetAsync($"/api/contents/by-slug/{type}/members-only");
        var missing = await viewer.GetAsync($"/api/contents/by-slug/{type}/no-such-page");

        byId.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the entry exists and the viewer is refused it");
        unreadable.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unreadable.Content.ReadAsStringAsync()).Should().NotContain("the gated page");
    }

    [Fact]
    public async Task A_readable_newer_duplicate_is_returned_when_the_older_one_is_not_readable()
    {
        var type = $"dup_{Guid.NewGuid():N}"[..20];
        var older = Entry(type, "shared-slug", "older");
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        var newer = Entry(type, "shared-slug", "newer");

        using (var scope = _factory.Services.CreateScope())
        {
            await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
            session.Store(TypeDefinition(type));
            session.Store(older);
            session.Store(newer);
            await session.SaveChangesAsync();
        }

        var viewer = await ViewerAsync(type, canRead: true, "203.0.113.177", new Dictionary<string, object>
        {
            ["Title"] = new Dictionary<string, object> { ["_eq"] = "newer" },
        });

        var olderById = await viewer.GetAsync($"/api/contents/{older.Id}");
        var bySlug = await viewer.GetAsync($"/api/contents/by-slug/{type}/shared-slug");

        olderById.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the row condition must deny the older duplicate");
        bySlug.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(bySlug)).GetProperty("id").GetGuid().Should().Be(newer.Id);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        var (type, _) = await SeedAsync();

        var resp = await Client("203.0.113.173").GetAsync($"/api/contents/by-slug/{type}/members-only");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_slug_is_not_found()
    {
        var (type, _) = await SeedAsync();
        var viewer = await ViewerAsync(type, canRead: true, "203.0.113.174");

        var known = await viewer.GetAsync($"/api/contents/by-slug/{type}/members-only");
        var unknown = await viewer.GetAsync($"/api/contents/by-slug/{type}/no-such-page");

        known.StatusCode.Should().Be(HttpStatusCode.OK, "a route that answers 404 for everything would pass the next line");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sensitive_fields_are_masked_as_they_are_by_id()
    {
        var (type, target) = await SeedAsync();
        var viewer = await ViewerAsync(type, canRead: true, "203.0.113.175");

        var byIdResp = await viewer.GetAsync($"/api/contents/{target.Id}");
        byIdResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var byId = await JsonAsync(byIdResp);

        var bySlugResp = await viewer.GetAsync($"/api/contents/by-slug/{type}/members-only");
        bySlugResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await JsonAsync(bySlugResp)).GetProperty("data");

        data.GetProperty("Title").GetString().Should().Be("the gated page");
        data.TryGetProperty("SSN", out _).Should().BeFalse("a Hidden field is removed for a plain viewer");
        data.GetProperty("BirthDay").GetString().Should().Be("***", "a Sensitive field is redacted for a plain viewer");
        data.GetRawText().Should().Be(byId.GetProperty("data").GetRawText());
    }

    [Fact]
    public async Task Another_tenants_entry_is_not_found()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();
        var type = $"xt_{Guid.NewGuid():N}"[..14];

        await StoreInTenantAsync(globex, type, Entry(type, "globex-only", "globex secret"));
        await StoreInTenantAsync(acme, type, Entry(type, "acme-page", "acme's own"));

        var acmeClient = await MemberAsync(acme, "203.0.113.176");

        var own = await acmeClient.GetAsync($"/api/contents/by-slug/{type}/acme-page");
        var foreign = await acmeClient.GetAsync($"/api/contents/by-slug/{type}/globex-only");

        own.StatusCode.Should().Be(HttpStatusCode.OK, "isolation that also blocks your own tenant is an outage");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).Should().NotContain("globex secret");
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14];
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    private async Task StoreInTenantAsync(string tenant, string type, Content entry)
    {
        using var scope = _factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        session.Store(TypeDefinition(type));
        session.Store(entry);
        await session.SaveChangesAsync();
    }

    private async Task<HttpClient> MemberAsync(string tenant, string ip)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"xt-{Guid.NewGuid():N}"[..14],
                Email = $"xt-{Guid.NewGuid():N}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync();
        }

        var client = Client(ip);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }
}
