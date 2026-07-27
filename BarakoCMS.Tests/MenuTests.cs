using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Site navigation menus: admin CRUD (JWT) and anonymous public read, against the real API over real
/// Postgres. Also pins the route precedence (public menus route must win over the content route) and
/// the one-level nesting cap.
/// </summary>
[Collection("Sequential")]
public class MenuTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public MenuTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> AdminTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = Guid.NewGuid(), Name = "SuperAdmin", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"admin-{userId}", Email = $"admin-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();
        return _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
    }

    private void AsAdmin(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task Admin_CanCreate_AndPublicCanReadAnonymously()
    {
        AsAdmin(await AdminTokenAsync());
        var create = await _client.PostAsJsonAsync("/api/menus", new
        {
            slug = "main",
            name = "Main nav",
            items = new object[]
            {
                new { label = "Blog", url = "/blog", openInNewTab = false, children = new object[0] },
                new { label = "GitHub", url = "https://github.com/BaryoDev", openInNewTab = true, children = new object[0] },
            },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await create.Content.ReadAsStringAsync());

        /* Anonymous read: the literal "menus" route must win over /api/public/{type}/{slug}. */
        _client.DefaultRequestHeaders.Authorization = null;
        var pub = await _client.GetAsync("/api/public/menus/main");
        pub.StatusCode.Should().Be(HttpStatusCode.OK, "the public menus route resolves, not the content route");
        var body = await pub.Content.ReadAsStringAsync();
        body.Should().Contain("Blog");
        body.Should().Contain("/blog");
        pub.Headers.CacheControl?.Public.Should().BeTrue();
    }

    [Fact]
    public async Task Create_RejectsDuplicateSlug()
    {
        AsAdmin(await AdminTokenAsync());
        await _client.PostAsJsonAsync("/api/menus", new { slug = "footer", name = "Footer", items = new object[0] });
        var dup = await _client.PostAsJsonAsync("/api/menus", new { slug = "footer", name = "Footer 2", items = new object[0] });
        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ReplacesItems_AndDelete_Removes()
    {
        AsAdmin(await AdminTokenAsync());
        await _client.PostAsJsonAsync("/api/menus", new { slug = "edit-me", name = "Edit", items = new[] { new { label = "Old", url = "/old", openInNewTab = false, children = new object[0] } } });

        var upd = await _client.PutAsJsonAsync("/api/menus/edit-me", new { slug = "edit-me", name = "Edit", items = new[] { new { label = "New", url = "/new", openInNewTab = false, children = new object[0] } } });
        upd.StatusCode.Should().Be(HttpStatusCode.OK);
        (await upd.Content.ReadAsStringAsync()).Should().Contain("New").And.NotContain("/old");

        var del = await _client.DeleteAsync("/api/menus/edit-me");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _client.DefaultRequestHeaders.Authorization = null;
        var gone = await _client.GetAsync("/api/public/menus/edit-me");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Nesting_IsCappedAtOneLevel()
    {
        AsAdmin(await AdminTokenAsync());
        var create = await _client.PostAsJsonAsync("/api/menus", new
        {
            slug = "deep",
            name = "Deep",
            items = new object[]
            {
                new
                {
                    label = "Parent", url = "/p", openInNewTab = false,
                    children = new object[]
                    {
                        new { label = "Child", url = "/c", openInNewTab = false,
                              children = new object[] { new { label = "Grandchild", url = "/g", openInNewTab = false, children = new object[0] } } },
                    },
                },
            },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await create.Content.ReadAsStringAsync();
        body.Should().Contain("Child", "one level of nesting is kept");
        body.Should().NotContain("Grandchild", "deeper nesting is dropped on write");
    }

    [Fact]
    public async Task PublicMenu_Missing_Is404()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.GetAsync("/api/public/menus/does-not-exist");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Menus_RequireAdmin()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.PostAsJsonAsync("/api/menus", new { slug = "x", name = "x", items = new object[0] });
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
