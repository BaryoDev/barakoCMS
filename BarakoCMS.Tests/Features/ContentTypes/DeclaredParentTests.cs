using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A reference field declared the parent on its definition guards a tree with no code registered
/// for it, and Pages reads its type and fields per tenant (#929).
/// </summary>
[Collection("Sequential")]
public class DeclaredParentTests
{
    private readonly IntegrationTestFixture _fixture;

    public DeclaredParentTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private async Task<HttpClient> AdminAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
    }

    private static async Task<string> CategoryTypeAsync(HttpClient admin, int? maxDepth = 3, string? name = null)
    {
        name ??= NewName("category");
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, isPubliclyDeliverable = true,
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Slug", type = "slug" },
                new { name = "ShowInNavigation", type = "bool" },
                maxDepth is null
                    ? new { name = "Parent", type = "reference", referenceType = name }
                    : (object)new { name = "Parent", type = "reference", referenceType = name, parent = new { maxDepth } },
            },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return name;
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, string type, string slug, Guid? parent = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = slug, ["Slug"] = slug, ["ShowInNavigation"] = true };
        if (parent is { } p)
        {
            data["Parent"] = p.ToString();
        }

        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data, status = "Published" }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient admin, Guid id, string slug, Guid parent, int version) =>
        admin.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object>
            {
                ["Title"] = slug, ["Slug"] = slug, ["ShowInNavigation"] = true, ["Parent"] = parent.ToString(),
            },
            version,
        }, Ct);

    // ---- the core declaration -------------------------------------------------------------

    [Fact]
    public async Task A_declared_parent_refuses_a_loop_with_no_code_registered_for_the_type()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin);
        var root = await CreateAsync(admin, type, NewName("r"));
        var child = await CreateAsync(admin, type, NewName("c"), root);

        var loop = await MoveAsync(admin, root, "root", child, 1);

        var body = await loop.Content.ReadAsStringAsync(Ct);
        loop.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("cycle");
    }

    [Fact]
    public async Task A_declared_parent_refuses_an_entry_as_its_own_parent()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin);
        var root = await CreateAsync(admin, type, NewName("r"));

        var self = await MoveAsync(admin, root, "root", root, 1);

        self.StatusCode.Should().Be(HttpStatusCode.BadRequest, await self.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_create_deeper_than_the_declared_max_depth_is_refused()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin, maxDepth: 2);
        var a = await CreateAsync(admin, type, NewName("a"));
        var b = await CreateAsync(admin, type, NewName("b"), a);
        var c = await CreateAsync(admin, type, NewName("c"), b);

        var data = new Dictionary<string, object> { ["Title"] = "d", ["Slug"] = NewName("d"), ["Parent"] = c.ToString() };
        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("2 levels deep");
    }

    [Fact]
    public async Task The_same_reference_without_the_declaration_still_takes_a_loop()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin, maxDepth: null);
        var root = await CreateAsync(admin, type, NewName("r"));
        var child = await CreateAsync(admin, type, NewName("c"), root);

        var loop = await MoveAsync(admin, root, "root", child, 1);

        loop.StatusCode.Should().Be(HttpStatusCode.OK, "a reference to the own type is not a parent by itself");
    }

    [Theory]
    [InlineData("string", null, false, 3, "not a single reference")]
    [InlineData("reference", "other", false, 3, "has to point at")]
    [InlineData("reference", null, true, 3, "not a single reference")]
    [InlineData("reference", null, false, 0, "may be 1 to 64")]
    [InlineData("reference", null, false, 65, "may be 1 to 64")]
    public async Task A_parent_declaration_that_cannot_be_walked_is_refused(
        string type, string? referenceType, bool multiple, int maxDepth, string expected)
    {
        var admin = await AdminAsync();
        var name = NewName("badparent");
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Parent", type, referenceType = type == "reference" ? referenceType ?? name : null, multiple, parent = new { maxDepth } },
            },
        }, Ct);

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expected);
    }

    [Fact]
    public async Task Only_one_field_of_a_type_may_be_its_parent()
    {
        var admin = await AdminAsync();
        var name = NewName("twoparents");
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[]
            {
                new { name = "Up", type = "reference", referenceType = name, parent = new { maxDepth = 3 } },
                new { name = "Over", type = "reference", referenceType = name, parent = new { maxDepth = 3 } },
            },
        }, Ct);

        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Only one field");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Pages, per tenant ----------------------------------------------------------------

    private static async Task<HttpResponseMessage> SetPagesAsync(HttpClient admin, object settings) =>
        await admin.PutAsJsonAsync("/api/pages/settings", settings, Ct);

    private async Task<List<string>> NavigationSlugsAsync(string tenant)
    {
        var res = await TenantProfileSeed.Anonymous(_fixture, tenant).GetAsync("/api/public/pages/navigation", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToList();
    }

    [Fact]
    public async Task Two_tenants_on_one_deployment_read_navigation_and_resolve_from_their_own_page_types()
    {
        var sectionsTenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var sectionsAdmin = await TenantProfileSeed.AdminInAsync(_fixture, sectionsTenant, Ct);
        var shelvesTenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var shelvesAdmin = await TenantProfileSeed.AdminInAsync(_fixture, shelvesTenant, Ct);

        var sections = await CategoryTypeAsync(sectionsAdmin);
        var shelves = await CategoryTypeAsync(shelvesAdmin);
        (await SetPagesAsync(sectionsAdmin, new { contentType = sections, parentField = "Parent" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetPagesAsync(shelvesAdmin, new { contentType = shelves, parentField = "Parent" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var guides = await CreateAsync(sectionsAdmin, sections, "guides");
        await CreateAsync(sectionsAdmin, sections, "install", guides);
        await CreateAsync(shelvesAdmin, shelves, "bread");

        (await NavigationSlugsAsync(sectionsTenant)).Should().Equal("guides");
        (await NavigationSlugsAsync(shelvesTenant)).Should().Equal("bread");

        var resolved = await TenantProfileSeed.Anonymous(_fixture, sectionsTenant)
            .GetAsync("/api/public/pages/resolve?path=/guides/install", Ct);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync(Ct));
        var crumbs = (await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("breadcrumbs");
        crumbs.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).Should().Equal("guides", "install");

        (await TenantProfileSeed.Anonymous(_fixture, shelvesTenant).GetAsync("/api/public/pages/resolve?path=/guides/install", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the other tenant's tree is not this one's");
    }

    [Fact]
    public async Task A_tenant_page_type_gets_the_reserved_slug_and_loop_rules_with_no_declaration_on_the_type()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin, maxDepth: null);
        (await SetPagesAsync(admin, new { contentType = type, parentField = "Parent", reservedSlugs = new[] { "api" }, maxDepth = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var reserved = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, data = new Dictionary<string, object> { ["Title"] = "x", ["Slug"] = "api" },
        }, Ct);
        reserved.StatusCode.Should().Be(HttpStatusCode.BadRequest, await reserved.Content.ReadAsStringAsync(Ct));

        var root = await CreateAsync(admin, type, NewName("r"));
        var child = await CreateAsync(admin, type, NewName("c"), root);
        (await MoveAsync(admin, root, "root", child, 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_read_back_what_was_set_over_the_deployment_values()
    {
        var admin = await AdminAsync();

        var before = await (await admin.GetAsync("/api/pages/settings", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        before.GetProperty("tenant").GetProperty("contentType").ValueKind.Should().Be(JsonValueKind.Null);
        var deploymentType = before.GetProperty("effective").GetProperty("contentType").GetString();
        deploymentType.Should().NotBeNullOrEmpty();

        var section = await CategoryTypeAsync(admin);
        var set = await SetPagesAsync(admin, new { contentType = section, maxDepth = 5 });
        var body = await set.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("effective").GetProperty("contentType").GetString().Should().Be(section);
        body.GetProperty("effective").GetProperty("maxDepth").GetInt32().Should().Be(5);
        body.GetProperty("effective").GetProperty("parentField").GetString().Should().NotBeNullOrEmpty();

        await SetPagesAsync(admin, new { });
        var after = await (await admin.GetAsync("/api/pages/settings", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        after.GetProperty("effective").GetProperty("contentType").GetString().Should().Be(deploymentType);
    }

    [Fact]
    public async Task Settings_naming_a_type_or_field_that_does_not_exist_are_refused()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin);

        var typo = await SetPagesAsync(admin, new { contentType = type + "x" });
        typo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await typo.Content.ReadAsStringAsync(Ct)).Should().Contain("There is no content type");

        var field = await SetPagesAsync(admin, new { contentType = type, parentField = "Parnet" });
        field.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await field.Content.ReadAsStringAsync(Ct)).Should().Contain("no field 'Parnet'");

        (await SetPagesAsync(admin, new { contentType = type, parentField = "Parent" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_parent_declared_deeper_than_the_tree_allows_does_not_lift_the_trees_max_depth()
    {
        var admin = await AdminAsync();
        var type = await CategoryTypeAsync(admin, maxDepth: 64);
        (await SetPagesAsync(admin, new { contentType = type, parentField = "Parent", maxDepth = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var a = await CreateAsync(admin, type, NewName("a"));
        var b = await CreateAsync(admin, type, NewName("b"), a);

        var data = new Dictionary<string, object> { ["Title"] = "c", ["Slug"] = NewName("c"), ["Parent"] = b.ToString() };
        var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("""{ "maxDepth": 0 }""")]
    [InlineData("""{ "maxDepth": 65 }""")]
    [InlineData("""{ "contentType": " " }""")]
    [InlineData("""{ "reservedSlugs": [ " " ] }""")]
    public async Task Settings_out_of_range_are_refused(string json)
    {
        var admin = await AdminAsync();

        var res = await admin.PutAsync("/api/pages/settings",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Settings_need_manage_content_types()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        (await admin.GetAsync("/api/pages/settings", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var anonymous = TenantProfileSeed.Anonymous(_fixture, slug);
        (await anonymous.GetAsync("/api/pages/settings", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync("/api/pages/settings", new { contentType = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        (await user.GetAsync("/api/pages/settings", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync("/api/pages/settings", new { contentType = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
