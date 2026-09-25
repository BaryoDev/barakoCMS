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
    public async Task Applying_site_creates_a_json_collections_field()
    {
        var client = await AdminInAsync(await TenantAsync());

        (await client.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await types.Content.ReadAsStringAsync(Ct));
        var site = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "site");
        var fields = site.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().ContainKey("Collections");
        fields["Collections"].GetProperty("type").GetString().Should().Be("json");
        fields["Collections"].GetProperty("isRequired").GetBoolean().Should().BeFalse("a tenant with no collections behaves exactly as it does today");
    }

    [Fact]
    public async Task A_published_sites_collections_setting_round_trips_in_the_shape_barakoPress_reads()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (Dictionary<string, object>)SiteData("Events club");
        data["Collections"] = new Dictionary<string, object>
        {
            ["events"] = new Dictionary<string, object>
            {
                ["type"] = "event",
                ["route"] = "/events",
                ["fields"] = new Dictionary<string, object>
                {
                    ["title"] = "Title",
                    ["date"] = "StartDate",
                },
                ["sort"] = "-StartDate",
                ["colorBy"] = "EntryType",
            },
        };
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using (var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            await PublishAsync(admin, createdBody.RootElement.GetProperty("id").GetGuid());
        }

        var live = await AnonymousIn(tenant).GetAsync("/api/public/site", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await live.Content.ReadAsStringAsync(Ct));
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var events = items[0].GetProperty("data").GetProperty("Collections").GetProperty("events");
        events.GetProperty("type").GetString().Should().Be("event");
        events.GetProperty("route").GetString().Should().Be("/events");
        events.GetProperty("colorBy").GetString().Should().Be("EntryType");
        events.GetProperty("fields").GetProperty("date").GetString().Should().Be("StartDate");
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

    [Fact]
    public async Task Applying_site_creates_the_holding_fields_and_nothing_secret()
    {
        var client = await AdminInAsync(await TenantAsync());

        (await client.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await types.Content.ReadAsStringAsync(Ct));
        var site = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "site");
        var fields = site.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().NotBeEmpty();
        fields["Mode"].GetProperty("type").GetString().Should().Be("string");
        fields["Mode"].GetProperty("isRequired").GetBoolean().Should().BeFalse("unset means Live");
        fields["HoldingPath"].GetProperty("type").GetString().Should().Be("string");
        fields.Keys.Should().NotContain(["ComingSoon", "ComingSoonBlocks", "ComingSoonPath", "PreviewKeyHash"]);
        fields.Keys.Should().NotContain(k => k.Contains("Key") || k.Contains("Hash"));
    }

    [Fact]
    public async Task A_published_site_delivers_the_holding_fields_and_no_share_link_secret()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var shared = await admin.PostAsJsonAsync("/api/site/share-links", new { label = "Board preview" }, Ct);
        shared.StatusCode.Should().Be(HttpStatusCode.Created, await shared.Content.ReadAsStringAsync(Ct));
        var key = (await shared.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var data = (Dictionary<string, object>)SiteData("Holding club");
        data["Mode"] = "Holding";
        data["HoldingPath"] = "/holding";
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using (var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            await PublishAsync(admin, createdBody.RootElement.GetProperty("id").GetGuid());
        }

        var live = await AnonymousIn(tenant).GetAsync("/api/public/site", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await live.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var delivered = items[0].GetProperty("data");
        delivered.GetProperty("Mode").GetString().Should().Be("Holding");
        delivered.GetProperty("HoldingPath").GetString().Should().Be("/holding");
        text.Should().NotContain(hash).And.NotContain(key).And.NotContainEquivalentOf("KeyHash");
    }

    /// <summary>
    /// The four region settings barakoPress reads (site.ts, <c>regions</c>). A tone is one of the
    /// renderer's tones, so the field offers exactly those and nothing a renderer would drop.
    /// </summary>
    [Fact]
    public async Task Applying_site_creates_optional_header_and_footer_region_fields()
    {
        var client = await AdminInAsync(await TenantAsync());

        (await client.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await types.Content.ReadAsStringAsync(Ct));
        var site = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "site");
        var fields = site.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().ContainKeys("HeaderPath", "FooterPath", "HeaderTone", "FooterTone");
        foreach (var path in new[] { "HeaderPath", "FooterPath" })
        {
            fields[path].GetProperty("type").GetString().Should().Be("string");
            fields[path].GetProperty("isRequired").GetBoolean().Should().BeFalse("unset keeps the built-in header and footer");
        }

        foreach (var tone in new[] { "HeaderTone", "FooterTone" })
        {
            fields[tone].GetProperty("type").GetString().Should().Be("choice");
            fields[tone].GetProperty("isRequired").GetBoolean().Should().BeFalse();
            var options = fields[tone].GetProperty("options").EnumerateArray()
                .Select(o => o.GetProperty("value").GetString()).ToList();
            options.Should().HaveCount(6);
            options.Should().Equal("page", "surface", "accent", "inverse", "gradient", "wash");
        }
    }

    [Fact]
    public async Task A_published_sites_region_settings_are_delivered_and_an_unknown_tone_is_refused()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var wrong = (Dictionary<string, object>)SiteData("Regions club");
        wrong["HeaderTone"] = "neon";
        (await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data = wrong }, Ct))
            .IsSuccessStatusCode.Should().BeFalse("a tone the renderer does not know would be dropped without a word");

        var data = (Dictionary<string, object>)SiteData("Regions club");
        data["HeaderPath"] = "/regions/header";
        data["HeaderTone"] = "inverse";
        data["FooterPath"] = "/regions/footer";
        data["FooterTone"] = "wash";
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using (var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            await PublishAsync(admin, createdBody.RootElement.GetProperty("id").GetGuid());
        }

        var live = await AnonymousIn(tenant).GetAsync("/api/public/site", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await live.Content.ReadAsStringAsync(Ct));
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var delivered = items[0].GetProperty("data");
        delivered.GetProperty("HeaderPath").GetString().Should().Be("/regions/header");
        delivered.GetProperty("HeaderTone").GetString().Should().Be("inverse");
        delivered.GetProperty("FooterPath").GetString().Should().Be("/regions/footer");
        delivered.GetProperty("FooterTone").GetString().Should().Be("wash");
    }

    /// <summary>
    /// The settings barakoPress 0.8.0 reads beyond the regions: tokens and tones (#125), the phone
    /// menu and header actions (#127), style recipes (#143) and enabled plugins (#144). barakoBrew
    /// edits only what the blueprint declares, so undeclared they could only be set through the API.
    /// </summary>
    [Fact]
    public async Task Applying_site_creates_optional_json_fields_for_the_settings_barakoPress_0_8_reads()
    {
        var client = await AdminInAsync(await TenantAsync());

        (await client.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        using var doc = JsonDocument.Parse(await types.Content.ReadAsStringAsync(Ct));
        var site = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "site");
        var fields = site.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        var added = new[] { "Tokens", "Tones", "StyleRecipes", "MenuLinks", "HeaderActions", "Plugins" };
        fields.Should().ContainKeys(added);
        foreach (var name in added)
        {
            fields[name].GetProperty("type").GetString().Should().Be("json", name);
            fields[name].GetProperty("isRequired").GetBoolean().Should().BeFalse("unset renders the site as it did before ({0})", name);
        }
    }

    [Fact]
    public async Task A_published_sites_tokens_tones_menus_actions_recipes_and_plugins_are_delivered()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (Dictionary<string, object>)SiteData("Tokens club");
        data["Tokens"] = new Dictionary<string, object> { ["cms-ink"] = "#1D3A8A", ["gutter"] = "24px" };
        data["Tones"] = new Dictionary<string, object>
        {
            ["cms"] = new Dictionary<string, object> { ["ink"] = "cms-ink", ["bg"] = "#E8EEFD", ["edge"] = "#B9C8F5" },
        };
        data["StyleRecipes"] = new Dictionary<string, object>
        {
            ["card"] = new Dictionary<string, object>
            {
                ["class"] = "lift",
                ["style"] = new Dictionary<string, object> { ["padding"] = "22px 24px", ["background"] = "{colors.surface}" },
            },
        };
        data["MenuLinks"] = new object[] { new Dictionary<string, object> { ["label"] = "Docs", ["href"] = "/docs" } };
        data["HeaderActions"] = new object[]
        {
            new Dictionary<string, object> { ["label"] = "Get started", ["href"] = "/start", ["variant"] = "secondary" },
        };
        data["Plugins"] = new object[] { "tally" };
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using (var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            await PublishAsync(admin, createdBody.RootElement.GetProperty("id").GetGuid());
        }

        var live = await AnonymousIn(tenant).GetAsync("/api/public/site", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await live.Content.ReadAsStringAsync(Ct));
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var delivered = items[0].GetProperty("data");
        delivered.TryGetProperty("Tokens", out var tokens).Should().BeTrue("public delivery sends only declared fields");
        tokens.GetProperty("cms-ink").GetString().Should().Be("#1D3A8A");
        delivered.GetProperty("Tones").GetProperty("cms").GetProperty("ink").GetString().Should().Be("cms-ink");
        var card = delivered.GetProperty("StyleRecipes").GetProperty("card");
        card.GetProperty("class").GetString().Should().Be("lift");
        card.GetProperty("style").GetProperty("background").GetString().Should().Be("{colors.surface}");
        delivered.GetProperty("MenuLinks").GetArrayLength().Should().Be(1);
        delivered.GetProperty("MenuLinks")[0].GetProperty("href").GetString().Should().Be("/docs");
        delivered.GetProperty("HeaderActions").GetArrayLength().Should().Be(1);
        delivered.GetProperty("HeaderActions")[0].GetProperty("variant").GetString().Should().Be("secondary");
        var plugins = delivered.GetProperty("Plugins").EnumerateArray().Select(p => p.GetString()).ToList();
        plugins.Should().Equal("tally");
    }

    /// <summary>
    /// <c>HeaderLinks</c> and <c>Collections</c> are json, so the API stores whatever keys barakoPress
    /// adds to them. This pins that: the header's <c>activeOn</c> and <c>children</c> (#127), a
    /// collection's <c>index</c> object and <c>indexPage</c> (#126), and a docs tree's
    /// <c>variant</c>, <c>searchIndex</c> and a product's <c>note</c> (#145) all come back as sent.
    /// </summary>
    [Fact]
    public async Task Header_links_and_collections_keep_the_keys_barakoPress_0_8_adds()
    {
        var tenant = await TenantAsync();
        var admin = await AdminInAsync(tenant);
        (await admin.PostAsync("/api/content-types/blueprints/site", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (Dictionary<string, object>)SiteData("Docs club");
        data["HeaderLinks"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["label"] = "Docs",
                ["href"] = "/docs",
                ["activeOn"] = "/docs /guides",
                ["children"] = new object[] { new Dictionary<string, object> { ["label"] = "API", ["href"] = "/docs/api" } },
            },
        };
        data["Collections"] = new Dictionary<string, object>
        {
            ["docs"] = new Dictionary<string, object>
            {
                ["type"] = "doc",
                ["route"] = "/docs",
                ["fields"] = new Dictionary<string, object> { ["title"] = "Title" },
                ["index"] = new Dictionary<string, object> { ["heading"] = "The manual", ["empty"] = "Nothing yet" },
                ["indexPage"] = "/site/docs",
                ["tree"] = new Dictionary<string, object>
                {
                    ["section"] = "Section",
                    ["variant"] = new Dictionary<string, object> { ["switcher"] = "list", ["sidebar"] = "boxed", ["rail"] = true },
                    ["searchIndex"] = true,
                    ["products"] = new object[]
                    {
                        new Dictionary<string, object> { ["key"] = "cms", ["label"] = "barakoCMS", ["note"] = "on GitHub" },
                    },
                },
            },
        };
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        using (var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            await PublishAsync(admin, createdBody.RootElement.GetProperty("id").GetGuid());
        }

        var live = await AnonymousIn(tenant).GetAsync("/api/public/site", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await live.Content.ReadAsStringAsync(Ct));
        var items = body.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var delivered = items[0].GetProperty("data");
        var links = delivered.GetProperty("HeaderLinks");
        links.GetArrayLength().Should().Be(1);
        links[0].GetProperty("activeOn").GetString().Should().Be("/docs /guides");
        links[0].GetProperty("children").GetArrayLength().Should().Be(1);
        links[0].GetProperty("children")[0].GetProperty("href").GetString().Should().Be("/docs/api");
        var docs = delivered.GetProperty("Collections").GetProperty("docs");
        docs.GetProperty("index").GetProperty("heading").GetString().Should().Be("The manual");
        docs.GetProperty("indexPage").GetString().Should().Be("/site/docs");
        var tree = docs.GetProperty("tree");
        tree.GetProperty("variant").GetProperty("sidebar").GetString().Should().Be("boxed");
        tree.GetProperty("variant").GetProperty("rail").GetBoolean().Should().BeTrue();
        tree.GetProperty("searchIndex").GetBoolean().Should().BeTrue();
        tree.GetProperty("products").GetArrayLength().Should().Be(1);
        tree.GetProperty("products")[0].GetProperty("note").GetString().Should().Be("on GitHub");
    }
}
