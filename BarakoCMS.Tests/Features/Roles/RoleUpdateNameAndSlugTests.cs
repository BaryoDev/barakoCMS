using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// What <c>POST /api/roles</c> and <c>PUT /api/roles/{id}</c> refuse before they store a role: a
/// blank name on a create or an update, and two rules that name the same content type. A repeat the
/// stored role already holds is allowed back, a new one is not.
/// </summary>
[Collection("Integration1")]
public class RoleUpdateNameAndSlugTests
{
    private readonly IntegrationTestFixture _fixture;

    public RoleUpdateNameAndSlugTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static object Rule(string slug) => new
    {
        contentTypeSlug = slug,
        create = new { enabled = false },
        read = new { enabled = true },
        update = new { enabled = false },
        delete = new { enabled = false },
    };

    private static async Task<(Guid Id, string Name)> ExistingRoleAsync(HttpClient admin)
    {
        var name = $"Named_{Guid.NewGuid():n}";
        var res = await admin.PostAsJsonAsync("/api/roles", new
        {
            name,
            description = "saved before the change under test",
            permissions = Array.Empty<object>(),
        }, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("id").GetGuid(), name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_update_with_a_blank_name_is_refused_and_keeps_the_stored_name(string blank)
    {
        var admin = await AdminAsync();
        var (id, name) = await ExistingRoleAsync(admin);

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name = blank,
            description = "renamed to nothing",
            permissions = Array.Empty<object>(),
            systemCapabilities = Array.Empty<string>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
        using var scope = _fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
        stored!.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_create_with_a_blank_name_is_refused(string blank)
    {
        var admin = await AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/roles", new
        {
            name = blank,
            description = "no name",
            permissions = Array.Empty<object>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_role_already_holding_two_spellings_of_one_type_can_be_saved_as_it_is()
    {
        var admin = await AdminAsync();
        var id = Guid.NewGuid();
        var name = $"Legacy_{Guid.NewGuid():n}";
        await new Config.ConfigCallers(_fixture).StoreInTenantAsync(null, new Role
        {
            Id = id,
            Name = name,
            IsPlatform = true,
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = "blog", Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = "BLOG ", Read = new PermissionRule { Enabled = true } },
            ],
        });

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "sent back unchanged",
            permissions = new[] { Rule("blog"), Rule("BLOG ") },
            systemCapabilities = Array.Empty<string>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_role_holding_one_repeat_cannot_be_saved_with_a_second()
    {
        var admin = await AdminAsync();
        var id = Guid.NewGuid();
        var name = $"Legacy_{Guid.NewGuid():n}";
        await new Config.ConfigCallers(_fixture).StoreInTenantAsync(null, new Role
        {
            Id = id,
            Name = name,
            IsPlatform = true,
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = "blog", Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = "BLOG ", Read = new PermissionRule { Enabled = true } },
            ],
        });

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "adds another repeat",
            permissions = new[] { Rule("blog"), Rule("BLOG "), Rule("news"), Rule("NEWS ") },
            systemCapabilities = Array.Empty<string>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_create_naming_one_content_type_twice_is_refused()
    {
        var admin = await AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/roles", new
        {
            name = $"Twice_{Guid.NewGuid():n}",
            description = "two rules for one type",
            permissions = new[] { Rule("Blog"), Rule("blog ") },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_update_naming_one_content_type_twice_is_refused()
    {
        var admin = await AdminAsync();
        var (id, name) = await ExistingRoleAsync(admin);

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "two rules for one type",
            permissions = new[] { Rule("Blog"), Rule("blog ") },
            systemCapabilities = Array.Empty<string>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_update_naming_two_different_content_types_is_accepted()
    {
        var admin = await AdminAsync();
        var (id, name) = await ExistingRoleAsync(admin);

        var res = await admin.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "two rules for two types",
            permissions = new[] { Rule("blog"), Rule("news") },
            systemCapabilities = Array.Empty<string>(),
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }
}
