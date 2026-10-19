using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Viewers;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Tests;

/// <summary>
/// The Members audience (#912): an entry or a field at that level is withheld from an anonymous
/// delivery call and served with a viewer token, and a viewer token opens no authoring route.
/// </summary>
[Collection("Sequential")]
public class MembersAudienceTests
{
    private readonly IntegrationTestFixture _factory;

    public MembersAudienceTests(IntegrationTestFixture factory) => _factory = factory;

    private IConfiguration Config => _factory.Services.GetRequiredService<IConfiguration>();
    private static string DefaultTenant => barakoCMS.Models.Tenant.DefaultSlug;

    private string Viewer(string? tenant = null) => ViewerToken.Create(Config, tenant ?? DefaultTenant, "member-1").Token;

    private static HttpRequestMessage Get(string url, string? viewerToken = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (viewerToken is not null)
            req.Headers.Add(ViewerToken.Header, viewerToken);
        return req;
    }

    private async Task SeedAsync(string type, string? tenant = null)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var s = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Lookup = new LookupDefinition { MatchField = "Slug", VerifyFields = ["Title"], ReturnFields = ["Title"] },
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Perk", DisplayName = "Perk", Type = "string", Sensitivity = SensitivityLevel.Members },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });
        void Doc(string slug, SensitivityLevel level, ContentStatus status = ContentStatus.Published) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = status, Sensitivity = level,
                SearchText = $"clubhouse {slug}",
                Data = new() { ["Title"] = $"T-{slug}", ["Slug"] = slug, ["Perk"] = $"perk-{slug}", ["Secret"] = "top-secret" },
            });
        Doc("open", SensitivityLevel.Public);
        Doc("club", SensitivityLevel.Members);
        Doc("club-draft", SensitivityLevel.Members, ContentStatus.Draft);
        Doc("staff", SensitivityLevel.Sensitive);
        await s.SaveChangesAsync();
    }

    private async Task<string> SeedTenantAsync()
    {
        var slug = $"mem-{Guid.NewGuid():N}"[..14];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var s = store.LightweightSession();
        s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, Status = TenantStatus.Active });
        await s.SaveChangesAsync();
        return slug;
    }

    [Fact]
    public async Task A_members_entry_is_refused_anonymously_and_served_with_a_viewer_token()
    {
        var type = "mem_entry"; await SeedAsync(type);
        var client = _factory.CreateClient();

        (await client.GetAsync($"/api/public/{type}/club")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var res = await client.SendAsync(Get($"/api/public/{type}/club", Viewer()));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("T-club");
        res.Headers.CacheControl!.NoStore.Should().BeTrue("a response built for one viewer is never cached");
    }

    [Fact]
    public async Task A_members_field_is_absent_anonymously_and_present_for_a_member()
    {
        var type = "mem_field"; await SeedAsync(type);
        var client = _factory.CreateClient();

        var anon = await (await client.GetAsync($"/api/public/{type}/open")).Content.ReadAsStringAsync();
        anon.Should().Contain("T-open");
        anon.Should().NotContain("perk-open");

        var member = await (await client.SendAsync(Get($"/api/public/{type}/open", Viewer()))).Content.ReadAsStringAsync();
        member.Should().Contain("perk-open");
        member.Should().NotContain("top-secret", "a Sensitive field stays staff-only for a member");
    }

    [Fact]
    public async Task The_list_adds_members_entries_for_a_viewer_and_never_a_draft_or_a_staff_entry()
    {
        var type = "mem_list"; await SeedAsync(type);
        var client = _factory.CreateClient();

        async Task<List<string>> Slugs(string? token)
        {
            var res = await client.SendAsync(Get($"/api/public/{type}", token));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("slug").GetString()!).ToList();
        }

        var anon = await Slugs(null);
        anon.Should().Equal("open");

        var member = await Slugs(Viewer());
        member.Should().HaveCount(2);
        member.Should().BeEquivalentTo(new[] { "open", "club" });
    }

    private static async Task<List<string>> SlugsAsync(HttpClient client, HttpRequestMessage req, string array)
    {
        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var items = array.Length == 0 ? root : root.GetProperty(array);
        return items.EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToList();
    }

    [Fact]
    public async Task No_anonymous_listing_route_serves_a_members_entry()
    {
        var type = "mem_routes"; await SeedAsync(type);
        var client = _factory.CreateClient();

        var page = await SlugsAsync(client, Get($"/api/public/{type}"), "items");
        page.Should().HaveCount(1);
        page.Should().Equal("open");

        var search = await SlugsAsync(client, Get($"/api/public/{type}/search?q=clubhouse"), "results");
        search.Should().HaveCount(1, "the Public entry matches the text, the Members one must not");
        search.Should().Equal("open");

        var feed = await (await client.GetAsync($"/api/public/{type}/feed.xml")).Content.ReadAsStringAsync();
        feed.Should().Contain("T-open");
        feed.Should().NotContain("T-club");

        var sitemap = await (await client.GetAsync("/api/public/sitemap.xml")).Content.ReadAsStringAsync();
        sitemap.Should().NotContain($"/{type}/club");

        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "198.51.100.241");
        (await client.PostAsJsonAsync($"/api/public/{type}/lookup", new { reference = "open", verify = "T-open" })).StatusCode
            .Should().Be(HttpStatusCode.OK, "positive control: the Public entry is found by the same lookup");
        var lookup = await client.PostAsJsonAsync($"/api/public/{type}/lookup", new { reference = "club", verify = "T-club" });
        lookup.StatusCode.Should().Be(HttpStatusCode.NotFound, "a lookup never returns a Members entry");

        var described = await (await client.GetAsync($"/api/public/types/{type}/description")).Content.ReadAsStringAsync();
        described.Should().NotContain("Perk", "a Members field is not named to an anonymous caller");
    }

    [Fact]
    public async Task A_viewer_of_the_same_tenant_gets_the_members_entry_and_one_of_another_tenant_does_not()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var type = "mem_tenant";
        await SeedAsync(type, tenantA);
        await SeedAsync(type, tenantB);

        HttpClient On(string tenant)
        {
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Add("X-Tenant", tenant);
            return c;
        }

        var tokenA = Viewer(tenantA);

        var own = await SlugsAsync(On(tenantA), Get($"/api/public/{type}", tokenA), "items");
        own.Should().HaveCount(2);
        own.Should().BeEquivalentTo(new[] { "open", "club" });

        var other = await On(tenantB).SendAsync(Get($"/api/public/{type}", tokenA));
        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a token for tenant A reads nothing of tenant B");
        (await On(tenantB).SendAsync(Get($"/api/public/{type}/club", tokenA))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await On(tenantB).GetAsync($"/api/public/{type}/club")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "and B's own anonymous caller still sees none of it");
    }

    [Fact]
    public async Task A_response_built_for_a_viewer_is_never_cached_and_never_leaks_to_the_next_anonymous_call()
    {
        var type = "mem_cache"; await SeedAsync(type);
        var client = _factory.CreateClient();

        var anonBefore = await client.GetAsync($"/api/public/{type}");
        anonBefore.Headers.CacheControl!.Public.Should().BeTrue("positive control: anonymous delivery stays cacheable");

        foreach (var url in new[] { $"/api/public/{type}", $"/api/public/{type}/club", $"/api/public/{type}/open" })
        {
            var res = await client.SendAsync(Get(url, Viewer()));
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var cc = res.Headers.CacheControl!;
            cc.Private.Should().BeTrue(url);
            cc.NoStore.Should().BeTrue(url);
            cc.Public.Should().BeFalse(url);
            res.Headers.Vary.Should().Contain("Authorization");
            res.Headers.Vary.Should().Contain(ViewerToken.Header);
            res.Headers.ETag.Should().BeNull(url);
            res.Headers.Contains("Surrogate-Key").Should().BeFalse(url);
            res.Headers.Contains("Cache-Tag").Should().BeFalse(url);
        }

        var anonAfter = await (await client.GetAsync($"/api/public/{type}")).Content.ReadAsStringAsync();
        anonAfter.Should().Contain("T-open");
        anonAfter.Should().NotContain("T-club");
        anonAfter.Should().NotContain("perk-");
    }

    [Fact]
    public async Task Existing_content_stays_public_and_a_request_without_the_header_answers_as_before()
    {
        var type = "mem_default"; await SeedAsync(type);
        var client = _factory.CreateClient();

        var res = await client.GetAsync($"/api/public/{type}/open");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        new Content().Sensitivity.Should().Be(SensitivityLevel.Public);
        ((int)SensitivityLevel.Hidden).Should().Be(2, "the stored numbers of the older levels hold");
        ((int)SensitivityLevel.Members).Should().Be(3);
    }

    [Fact]
    public async Task A_viewer_token_that_does_not_verify_is_401_not_an_anonymous_read()
    {
        var type = "mem_bad"; await SeedAsync(type);
        var client = _factory.CreateClient();

        (await client.SendAsync(Get($"/api/public/{type}/club", "not-a-token"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get($"/api/public/{type}", Viewer("some-other-tenant")))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a token is bound to its tenant");
    }

    [Fact]
    public async Task A_viewer_token_grants_no_authoring_route()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Viewer());

        (await client.GetAsync("/api/me/tenants")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/contents")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/viewer-tokens", new { Subject = "x" })).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Minting_a_viewer_token_needs_the_delivery_capability_and_the_token_works()
    {
        var type = "mem_mint"; await SeedAsync(type);
        var client = _factory.CreateClient();

        (await client.PostAsJsonAsync("/api/viewer-tokens", new { Subject = "m" })).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            userId = Guid.NewGuid();
            s.Store(new User { Id = userId, Username = $"admin-{userId}", RoleIds = new() { barakoCMS.Data.DataSeeder.SuperAdminRoleId } });
            await s.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));

        var empty = await client.PostAsJsonAsync("/api/viewer-tokens", new { Subject = "" });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var mint = await client.PostAsJsonAsync("/api/viewer-tokens", new { Subject = "member-9" });
        mint.StatusCode.Should().Be(HttpStatusCode.OK, await mint.Content.ReadAsStringAsync());
        var token = (await mint.Content.ReadFromJsonAsync<MintResult>())!.Token;
        token.Should().NotBeNullOrEmpty();

        var anon = _factory.CreateClient();
        (await anon.SendAsync(Get($"/api/public/{type}/club", token))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record MintResult(string Token, DateTime ExpiresAt, string Header);
}
