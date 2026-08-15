using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Preview;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

/// <summary>
/// Draft preview: GET /api/public/{type}/{slug}?preview=&lt;token&gt; reveals a DRAFT only with a valid
/// token bound to that exact tenant + type + slug + entry id, still stripping non-Public fields and
/// refusing a document-Sensitive entry. Adversarial: no token, wrong slug, wrong tenant, and garbage all
/// fall back to published-only (404 for a draft). Plus the authenticated mint endpoint.
/// </summary>
[Collection("Sequential")]
public class PreviewTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public PreviewTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private IConfiguration Config => _factory.Services.GetRequiredService<IConfiguration>();
    private static string Tenant => barakoCMS.Models.Tenant.DefaultSlug;

    /// <summary>Seed a type + entries; returns slug -> entry id so tests can bind tokens to the right entry.</summary>
    private async Task<Dictionary<string, Guid>> SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });
        var ids = new Dictionary<string, Guid>();
        Guid Doc(string slug, ContentStatus st, SensitivityLevel sev)
        {
            var id = Guid.NewGuid();
            s.Store(new Content { Id = id, ContentType = type, Status = st, Sensitivity = sev,
                Data = new() { ["Title"] = $"T-{slug}", ["Slug"] = slug, ["Body"] = $"body of {slug}", ["Secret"] = "top-secret" } });
            ids[slug] = id;
            return id;
        }
        Doc("live", ContentStatus.Published, SensitivityLevel.Public);
        Doc("wip", ContentStatus.Draft, SensitivityLevel.Public);
        Doc("hidden-wip", ContentStatus.Draft, SensitivityLevel.Sensitive);
        await s.SaveChangesAsync();
        return ids;
    }

    private string Token(string type, string slug, Guid entryId, string? tenant = null) =>
        PreviewToken.Create(Config, tenant ?? Tenant, type, slug, entryId).Token;

    // ---- delivery ----

    [Fact]
    public async Task Draft_WithoutToken_Is404()
    {
        var type = "prev_a"; await SeedAsync(type);
        (await _client.GetAsync($"/api/public/{type}/wip")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/public/{type}/live")).StatusCode.Should().Be(HttpStatusCode.OK, "published is unaffected");
    }

    [Fact]
    public async Task Draft_WithValidToken_IsReturned_PublicFieldsOnly_NoStore()
    {
        var type = "prev_b"; var ids = await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/wip?preview={Token(type, "wip", ids["wip"])}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("body of wip");
        body.Should().NotContain("top-secret", "the Sensitive field is never emitted, even in preview");
        res.Headers.CacheControl!.NoStore.Should().BeTrue("a draft must not be cached");
    }

    [Fact]
    public async Task Draft_WithTokenForDifferentSlug_Is404()
    {
        var type = "prev_c"; var ids = await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/wip?preview={Token(type, "live", ids["live"])}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a token is bound to one slug+entry");
    }

    [Fact]
    public async Task Draft_WithTokenForDifferentTenant_Is404()
    {
        var type = "prev_d"; var ids = await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/wip?preview={Token(type, "wip", ids["wip"], tenant: "some-other-tenant")}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a token is bound to its tenant");
    }

    [Fact]
    public async Task Draft_WithGarbageToken_Is404()
    {
        var type = "prev_e"; await SeedAsync(type);
        (await _client.GetAsync($"/api/public/{type}/wip?preview=not-a-real-token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SensitiveDraft_EvenWithValidToken_Is404()
    {
        var type = "prev_f"; var ids = await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/hidden-wip?preview={Token(type, "hidden-wip", ids["hidden-wip"])}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "preview lifts only the Published gate, never doc-Sensitivity");
    }

    // ---- mint endpoint ----

    [Fact]
    public async Task Mint_Unauthenticated_Is401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.PostAsJsonAsync("/api/preview", new { Type = "prev_g", Slug = "wip" });
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mint_AsSuperAdmin_ReturnsTokenThatWorks()
    {
        var type = "prev_h"; await SeedAsync(type);

        // A user whose roles include SuperAdmin (bypasses the read-permission check).
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            userId = Guid.NewGuid();
            s.Store(new User { Id = userId, Username = $"admin-{userId}", RoleIds = new() { barakoCMS.Data.DataSeeder.SuperAdminRoleId } });
            await s.SaveChangesAsync();
        }
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));

        var mint = await _client.PostAsJsonAsync("/api/preview", new { Type = type, Slug = "wip" });
        mint.StatusCode.Should().Be(HttpStatusCode.OK, because: await mint.Content.ReadAsStringAsync());
        var token = (await mint.Content.ReadFromJsonAsync<MintResult>())!.Token;
        token.Should().NotBeNullOrEmpty();

        _client.DefaultRequestHeaders.Authorization = null; // preview is anonymous
        var preview = await _client.GetAsync($"/api/public/{type}/wip?preview={token}");
        preview.StatusCode.Should().Be(HttpStatusCode.OK, "the minted token reveals its draft");
    }

    [Fact]
    public async Task PreviewToken_IsRejected_ByTheMainBearerScheme()
    {
        // A preview token must NOT be usable as an API access token (distinct audience).
        var type = "prev_i"; var ids = await SeedAsync(type);
        var previewJwt = Token(type, "wip", ids["wip"]);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", previewJwt);
        var res = await _client.GetAsync("/api/me/tenants"); // any authenticated endpoint
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    private sealed record MintResult(string Token, DateTime ExpiresAt, string QueryParam);
}
