using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The Pages module (#718): the write rules on the page type, and the three tree endpoints.
/// </summary>
/// <remarks>
/// The fixture configures the module for <c>pagetreeprobe</c> with MaxDepth 3 and reserved slugs
/// <c>api</c> and <c>Blog</c>. Every test writes pages under slugs unique to that test, because the
/// collection shares one database and core refuses a slug the type already holds.
/// </remarks>
[Collection("Sequential")]
public class PagesModuleTests
{
    private const string TypeName = "pagetreeprobe";

    private readonly IntegrationTestFixture _factory;

    public PagesModuleTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typeDeclared;

    private async Task DeclareTypeAsync()
    {
        await TypeGate.WaitAsync(Ct);
        try
        {
            if (_typeDeclared)
            {
                return;
            }

            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == TypeName, Ct))
            {
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = TypeName,
                    DisplayName = "Page tree probe",
                    IsPubliclyDeliverable = true,
                    Fields =
                    [
                        new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                        new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                        new FieldDefinition { Name = "ParentPage", DisplayName = "Parent page", Type = "reference", ReferenceType = TypeName },
                        new FieldDefinition { Name = "ShowInNavigation", DisplayName = "Show in navigation", Type = "bool" },
                        new FieldDefinition { Name = "NavigationOrder", DisplayName = "Navigation order", Type = "int" },
                        new FieldDefinition
                        {
                            Name = "Secret",
                            DisplayName = "Secret",
                            Type = "string",
                            Sensitivity = SensitivityLevel.Sensitive,
                        },
                    ],
                });
                await session.SaveChangesAsync(Ct);
            }

            _typeDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private async Task<HttpClient> AdminAsync()
    {
        await DeclareTypeAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleNames = new[] { "SuperAdmin", "Admin" };
        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
            roleIds.Add(role!.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"pages-{userId:n}",
            Email = $"pages-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync(Ct);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():n}"[..(name.Length + 9)];

    private static Dictionary<string, object> PageData(string title, string slug, Guid? parent, bool nav = false, int? order = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = title, ["Slug"] = slug };
        if (parent is { } p)
        {
            data["ParentPage"] = p.ToString();
        }

        if (nav)
        {
            data["ShowInNavigation"] = true;
        }

        if (order is { } o)
        {
            data["NavigationOrder"] = o;
        }

        return data;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string title, string slug, Guid? parent = null) =>
        client.PostAsJsonAsync("/api/contents", new { contentType = TypeName, data = PageData(title, slug, parent) }, Ct);

    private static async Task<Guid> CreateAsync(HttpClient client, string title, string slug, Guid? parent = null)
    {
        var res = await PostAsync(client, title, slug, parent);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, string title, string slug, Guid? parent) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new { id, data = PageData(title, slug, parent) }, Ct);

    /// <summary>Stores pages straight into the database, with the status and sensitivity a test needs.</summary>
    private async Task<Guid> StoreAsync(
        string title,
        string slug,
        Guid? parent = null,
        bool nav = false,
        int? order = null,
        ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        string? secret = null)
    {
        await DeclareTypeAsync();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var data = PageData(title, slug, parent, nav, order);
        if (secret is not null)
        {
            data["Secret"] = secret;
        }

        session.Store(new Content { Id = id, ContentType = TypeName, Status = status, Sensitivity = sensitivity, Data = data });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage res, string expected)
    {
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expected);
    }

    // Rules on write

    [Fact]
    public async Task A_page_that_is_its_own_parent_is_refused()
    {
        var client = await AdminAsync();
        var slug = Unique("self");
        var a = await CreateAsync(client, "Self", slug);

        await ShouldBeRefusedAsync(await UpdateAsync(client, a, "Self", slug, a), "itself");
    }

    /// <summary>
    /// The probe type has no ParentReferenceHook of its own. Only the module registers one, so without
    /// PageTreeHook this update is a 200 and the loop is stored.
    /// </summary>
    [Fact]
    public async Task A_parent_cycle_is_refused()
    {
        var client = await AdminAsync();
        var aSlug = Unique("cyc-a");
        var bSlug = Unique("cyc-b");
        var a = await CreateAsync(client, "A", aSlug);
        var b = await CreateAsync(client, "B", bSlug, parent: a);

        await ShouldBeRefusedAsync(await UpdateAsync(client, a, "A", aSlug, b), "cycle");
    }

    [Fact]
    public async Task A_create_deeper_than_MaxDepth_is_refused()
    {
        var client = await AdminAsync();
        var root = await CreateAsync(client, "D0", Unique("d0"));
        var one = await CreateAsync(client, "D1", Unique("d1"), root);
        var two = await CreateAsync(client, "D2", Unique("d2"), one);
        var three = await CreateAsync(client, "D3", Unique("d3"), two);

        three.Should().NotBeEmpty("three ancestors is exactly the configured limit");
        await ShouldBeRefusedAsync(await PostAsync(client, "D4", Unique("d4"), three), "more than 3 levels deep");
    }

    [Fact]
    public async Task A_top_level_page_may_not_take_a_reserved_slug()
    {
        var client = await AdminAsync();

        await ShouldBeRefusedAsync(await PostAsync(client, "Api", "api"), "reserved");
        await ShouldBeRefusedAsync(await PostAsync(client, "Blog", "blog"), "reserved");
    }

    [Fact]
    public async Task A_reserved_slug_is_allowed_below_the_top_level()
    {
        var client = await AdminAsync();
        var parent = await CreateAsync(client, "Docs", Unique("docs"));

        var res = await PostAsync(client, "Api docs", "api", parent);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_sibling_with_the_same_slug_is_refused()
    {
        var client = await AdminAsync();
        var parent = await CreateAsync(client, "Parent", Unique("sib-parent"));
        var slug = Unique("sib");
        await CreateAsync(client, "First", slug, parent);

        var res = await PostAsync(client, "Second", slug, parent);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    // Public navigation

    private async Task<JsonElement> NavigationAsync()
    {
        var res = await _factory.CreateClient().GetAsync("/api/public/pages/navigation", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement items) =>
        items.EnumerateArray().SelectMany(i => new[] { i }.Concat(Flatten(i.GetProperty("children"))));

    [Fact]
    public async Task Navigation_nests_and_orders_published_flagged_pages_with_paths()
    {
        await DeclareTypeAsync();
        var aboutSlug = Unique("about");
        var about = await StoreAsync("About", aboutSlug, nav: true, order: 2);
        var teamSlug = Unique("team");
        await StoreAsync("Team", teamSlug, about, nav: true, order: 2);
        await StoreAsync("History", Unique("history"), about, nav: true, order: 1);
        await StoreAsync("Unflagged", Unique("unflag"), about);
        var hidden = await StoreAsync("Hidden middle", Unique("middle"), about);
        await StoreAsync("Under hidden", Unique("under"), hidden, nav: true, order: 3);

        var root = await NavigationAsync();

        root.GetProperty("contract").GetInt32().Should().Be(1);
        var aboutItem = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("slug").GetString() == aboutSlug);
        aboutItem.GetProperty("path").GetString().Should().Be($"/{aboutSlug}");

        var children = aboutItem.GetProperty("children").EnumerateArray().ToList();
        children.Should().HaveCount(3, "two flagged children plus a flagged grandchild under an unflagged page");
        children.Select(c => c.GetProperty("title").GetString())
            .Should().Equal("History", "Team", "Under hidden");
        children[1].GetProperty("path").GetString().Should().Be($"/{aboutSlug}/{teamSlug}");
        children[2].GetProperty("path").GetString().Should().Contain("/middle-", "it keeps its real path");
    }

    [Fact]
    public async Task Navigation_never_includes_a_draft_or_sensitive_page_or_anything_under_one()
    {
        await DeclareTypeAsync();
        var published = Unique("pubnav");
        await StoreAsync("Published", published, nav: true, secret: "nav-secret-value");
        var draft = await StoreAsync("Draft page", Unique("draftnav"), nav: true, status: ContentStatus.Draft);
        await StoreAsync("Under draft", Unique("underdraft"), draft, nav: true);
        var sensitive = await StoreAsync("Sensitive page", Unique("sensnav"), nav: true, sensitivity: SensitivityLevel.Sensitive);
        await StoreAsync("Under sensitive", Unique("undersens"), sensitive, nav: true);

        var root = await NavigationAsync();
        var titles = Flatten(root.GetProperty("items")).Select(i => i.GetProperty("title").GetString()).ToList();

        titles.Should().Contain("Published", "the control: the same request does serve a published page");
        titles.Should().NotContain(["Draft page", "Under draft", "Sensitive page", "Under sensitive"]);
        root.GetRawText().Should().NotContain("nav-secret-value");
    }

    // Public resolve

    private Task<HttpResponseMessage> ResolveAsync(string path) =>
        _factory.CreateClient().GetAsync($"/api/public/pages/resolve?path={Uri.EscapeDataString(path)}", Ct);

    [Fact]
    public async Task Resolve_returns_the_entry_path_and_breadcrumbs()
    {
        var sectionA = Unique("sect-a");
        var sectionB = Unique("sect-b");
        var leaf = Unique("leaf");
        var a = await StoreAsync("Section A", sectionA);
        var b = await StoreAsync("Section B", sectionB);
        await StoreAsync("Leaf", leaf, a, secret: "resolve-secret-value");

        var res = await ResolveAsync($"/{sectionA.ToUpperInvariant()}/{leaf}/");
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("contract").GetInt32().Should().Be(1);
        root.GetProperty("path").GetString().Should().Be($"/{sectionA}/{leaf}");
        root.GetProperty("entry").GetProperty("slug").GetString().Should().Be(leaf);
        root.GetProperty("entry").GetProperty("data").GetProperty("Title").GetString().Should().Be("Leaf");
        body.Should().NotContain("resolve-secret-value", "the entry is the projected one");

        var crumbs = root.GetProperty("breadcrumbs").EnumerateArray().ToList();
        crumbs.Should().HaveCount(2);
        crumbs.Select(c => c.GetProperty("path").GetString()).Should().Equal($"/{sectionA}", $"/{sectionA}/{leaf}");

        (await ResolveAsync($"/{sectionB}/{leaf}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the leaf exists, but not under section B");
        b.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Resolve_answers_404_for_a_miss_a_draft_a_sensitive_page_and_a_page_under_a_draft()
    {
        var published = Unique("pubres");
        await StoreAsync("Published", published);
        var draft = Unique("draftres");
        var draftId = await StoreAsync("Draft", draft, status: ContentStatus.Draft);
        var under = Unique("underres");
        await StoreAsync("Under draft", under, draftId);
        var sensitive = Unique("sensres");
        await StoreAsync("Sensitive", sensitive, sensitivity: SensitivityLevel.Sensitive);

        (await ResolveAsync($"/{published}")).StatusCode.Should().Be(HttpStatusCode.OK, "the control");
        (await ResolveAsync($"/{Unique("nothing")}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ResolveAsync($"/{draft}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ResolveAsync($"/{draft}/{under}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ResolveAsync($"/{sensitive}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Authenticated tree

    [Fact]
    public async Task The_tree_requires_a_signed_in_caller()
    {
        var res = await _factory.CreateClient().GetAsync("/api/pages/tree", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_tree_includes_drafts_with_status_and_path()
    {
        var client = await AdminAsync();
        var parentSlug = Unique("tree-parent");
        var parent = await StoreAsync("Tree parent", parentSlug);
        var childSlug = Unique("tree-child");
        await StoreAsync("Tree child", childSlug, parent, status: ContentStatus.Draft);

        var res = await client.GetAsync("/api/pages/tree", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("contract").GetInt32().Should().Be(1);
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var parentItem = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("slug").GetString() == parentSlug);
        var children = parentItem.GetProperty("children").EnumerateArray().ToList();
        children.Should().HaveCount(1);
        children[0].GetProperty("status").GetString().Should().Be("Draft");
        children[0].GetProperty("path").GetString().Should().Be($"/{parentSlug}/{childSlug}");
    }

    private const string LandingType = "landingprobe";
    private static readonly Lock LandingGate = new();
    private static WebApplicationFactory<Program>? _landingHost;

    /// <summary>
    /// A host whose Pages options name none of the defaults. One for the class and never disposed, per
    /// the note on IntegrationTestFixture.WithSetting.
    /// </summary>
    private WebApplicationFactory<Program> LandingHost()
    {
        lock (LandingGate)
        {
            return _landingHost ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Configure<BarakoCMS.Pages.PagesOptions>(o =>
                {
                    o.ContentType = LandingType;
                    o.ParentField = "Parent";
                    o.ShowInNavigationField = "InMenu";
                    o.OrderField = "MenuWeight";
                    o.TitleField = "Heading";
                    o.MaxDepth = 5;
                    o.ReservedSlugs = ["shop"];
                    o.HomeSlug = "start";
                })));
        }
    }

    [Fact]
    public async Task The_tree_reports_the_configured_type_and_field_names()
    {
        var admin = await AdminAsync();
        var slug = Unique("landing");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == LandingType, Ct))
            {
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = LandingType,
                    DisplayName = "Landing probe",
                    Fields =
                    [
                        new FieldDefinition { Name = "Heading", DisplayName = "Heading", Type = "string" },
                        new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                        new FieldDefinition { Name = "Parent", DisplayName = "Parent", Type = "reference", ReferenceType = LandingType },
                        new FieldDefinition { Name = "InMenu", DisplayName = "In menu", Type = "bool" },
                        new FieldDefinition { Name = "MenuWeight", DisplayName = "Menu weight", Type = "int" },
                    ],
                });
            }

            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = LandingType,
                Status = ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Heading"] = "Landing", ["Slug"] = slug, ["InMenu"] = true, ["MenuWeight"] = 7 },
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = LandingHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        var res = await client.GetAsync("/api/pages/tree", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        var options = root.GetProperty("options");
        options.GetProperty("contentType").GetString().Should().Be(LandingType);
        options.GetProperty("parentField").GetString().Should().Be("Parent");
        options.GetProperty("showInNavigationField").GetString().Should().Be("InMenu");
        options.GetProperty("orderField").GetString().Should().Be("MenuWeight");
        options.GetProperty("titleField").GetString().Should().Be("Heading");
        options.GetProperty("maxDepth").GetInt32().Should().Be(5);
        var reserved = options.GetProperty("reservedSlugs").EnumerateArray().Select(e => e.GetString()).ToList();
        reserved.Should().HaveCount(1);
        reserved.Should().Equal("shop");
        options.GetProperty("homeSlug").GetString().Should().Be("start");

        var item = root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug);
        item.GetProperty("title").GetString().Should().Be("Landing", "the tree is read with the same names it reports");
        item.GetProperty("order").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task The_public_navigation_does_not_carry_the_options()
    {
        var slug = Unique("navopts");
        await StoreAsync("Nav options", slug, nav: true, order: 1);

        var res = await _factory.CreateClient().GetAsync("/api/public/pages/navigation", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.TryGetProperty("options", out _).Should().BeFalse();
    }
}
