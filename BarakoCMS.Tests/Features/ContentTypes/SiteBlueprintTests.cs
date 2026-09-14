using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A site's identity, theme and chrome are one entry of the built-in <c>site</c> type, read by a
/// renderer from public delivery (#793).
/// </summary>
/// <remarks>
/// What the renderer depends on is the round trip, so that is what these assert: the blueprint makes
/// a singleton that is publicly deliverable, a published entry comes back anonymously with its JSON
/// intact, a draft does not, and one tenant's site is not another's.
///
/// Every apply runs in a tenant made for the test, for the reason ContentTypeBlueprintTests gives.
/// </remarks>
[Collection("Sequential")]
public class SiteBlueprintTests
{
    private readonly IntegrationTestFixture _factory;

    public SiteBlueprintTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"site-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminInAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"site-{Guid.NewGuid():n}"[..14],
                Email = $"site-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.8.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private HttpClient AnonymousIn(string tenantSlug)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.7.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private static object SiteData(string name) => new Dictionary<string, object>
    {
        ["Name"] = name,
        ["Tagline"] = "People of action",
        ["Url"] = "https://rckoronadal.example",
        ["Colors"] = new Dictionary<string, object> { ["accent"] = "#17458F", ["gold"] = "#F7A81B" },
        ["Fonts"] = new Dictionary<string, object> { ["heading"] = "Zilla Slab", ["body"] = "Open Sans" },
        ["FooterColumns"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["heading"] = "Club",
                ["links"] = new object[] { new Dictionary<string, object> { ["label"] = "About", ["href"] = "/about" } },
            },
        },
    };

    private static async Task<Guid> CreateEntryAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new { contentType = "site", data = SiteData(name) }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task PublishAsync(HttpClient client, Guid id)
    {
        var published = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = "Published" }, Ct);
        published.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", published.StatusCode, await published.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Applying_site_creates_one_publicly_deliverable_singleton()
    {
        var client = await AdminInAsync(await TenantAsync());

        var applied = await client.PostAsync("/api/content-types/blueprints/site", null, Ct);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        var types = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await types.Content.ReadAsStringAsync(Ct));
        var site = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "site");
        site.GetProperty("isSingleton").GetBoolean().Should().BeTrue();
        site.GetProperty("isPubliclyDeliverable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_second_site_entry_in_the_same_tenant_is_refused()
    {
        var client = await AdminInAsync(await TenantAsync());
        (await client.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateEntryAsync(client, "First");

        var second = await client.PostAsJsonAsync("/api/contents", new { contentType = "site", data = SiteData("Second") }, Ct);

        second.IsSuccessStatusCode.Should().BeFalse("a tenant has one site, and two is the ambiguity a renderer cannot resolve");
    }

    [Fact]
    public async Task A_published_site_is_read_anonymously_with_its_theme_intact_and_a_draft_is_not()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await CreateEntryAsync(admin, "Rotary Club of Koronadal");
        var anonymous = AnonymousIn(tenant);

        var whileDraft = await anonymous.GetAsync("/api/public/site", Ct);
        using (var draftBody = JsonDocument.Parse(await whileDraft.Content.ReadAsStringAsync(Ct)))
        {
            whileDraft.StatusCode.Should().Be(HttpStatusCode.OK);
            draftBody.RootElement.GetProperty("items").GetArrayLength().Should().Be(0, "a theme is live when it is published, not while it is being edited");
        }

        await PublishAsync(admin, id);

        var live = await anonymous.GetAsync("/api/public/site", Ct);
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await live.Content.ReadAsStringAsync(Ct));
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var data = items[0].GetProperty("data");
        data.GetProperty("Name").GetString().Should().Be("Rotary Club of Koronadal");
        data.GetProperty("Colors").GetProperty("gold").GetString().Should().Be("#F7A81B");
        data.GetProperty("Fonts").GetProperty("heading").GetString().Should().Be("Zilla Slab");
        var columns = data.GetProperty("FooterColumns");
        columns.GetArrayLength().Should().Be(1);
        columns[0].GetProperty("links")[0].GetProperty("href").GetString().Should().Be("/about");
    }

    [Fact]
    public async Task One_tenants_site_is_not_delivered_to_another_tenant()
    {
        var ours = await TenantAsync();
        var theirs = await TenantAsync();
        var admin = await AdminInAsync(ours);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await PublishAsync(admin, await CreateEntryAsync(admin, "Ours"));

        (await AnonymousIn(ours).GetAsync("/api/public/site", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var elsewhere = await AnonymousIn(theirs).GetAsync("/api/public/site", Ct);
        var text = await elsewhere.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain("Ours");
        elsewhere.StatusCode.Should().NotBe(HttpStatusCode.OK, "the other tenant never applied the blueprint, so it has no site type");
    }
}
