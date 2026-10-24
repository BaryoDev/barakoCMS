using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Deleting a role or a group is recoverable until it is erased (#625).
/// </summary>
[Collection("Integration1")]
public class RoleSoftDeleteTests
{
    private readonly IntegrationTestFixture _factory;

    public RoleSoftDeleteTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_deleted_role_leaves_every_read_and_comes_back_on_restore_with_the_same_permissions()
    {
        var client = await ClientFor("SuperAdmin");
        var (id, _) = await CreateRoleAsync(client);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/roles/{id}", Ct);
        before.GetProperty("permissions").GetArrayLength().Should().BeGreaterThan(0);

        (await client.DeleteAsync($"/api/roles/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"/api/roles/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await IdsAsync(client, "/api/roles")).Should().NotContain(id);
        (await IdsAsync(client, "/api/roles/deleted")).Should().Contain(id);

        (await client.PostAsync($"/api/roles/{id}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/roles/{id}", Ct);
        after.GetProperty("permissions").GetRawText().Should().Be(before.GetProperty("permissions").GetRawText());
        after.GetProperty("systemCapabilities").GetRawText().Should().Be(before.GetProperty("systemCapabilities").GetRawText());
        (await IdsAsync(client, "/api/roles")).Should().Contain(id);
        (await IdsAsync(client, "/api/roles/deleted")).Should().NotContain(id);
    }

    [Fact]
    public async Task A_deleted_role_cannot_be_edited_or_assigned_until_it_is_restored()
    {
        var client = await ClientFor("SuperAdmin");
        var (id, name) = await CreateRoleAsync(client, isPlatform: true);
        var userId = await StoreUserAsync();

        (await AssignAsync(client, userId, id)).StatusCode.Should().Be(HttpStatusCode.OK, "a live role is assignable, so the refusals below are about deletion");
        await client.DeleteAsync($"/api/users/{userId}/roles/{id}", Ct);
        await client.DeleteAsync($"/api/roles/{id}", Ct);

        (await AssignAsync(client, userId, id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var edit = await client.PutAsJsonAsync($"/api/roles/{id}", new { name = name + "x", description = "", permissions = Array.Empty<object>(), systemCapabilities = Array.Empty<string>() }, Ct);
        edit.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await client.PostAsync($"/api/roles/{id}/restore", null, Ct);
        (await AssignAsync(client, userId, id)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deleted_role_stops_granting_at_once_and_a_restored_one_grants_what_it_had()
    {
        var slug = "softdel" + Guid.NewGuid().ToString("n")[..8];
        var roleId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "holder" + Guid.NewGuid().ToString("n")[..10],
            Email = $"{Guid.NewGuid():n}@example.com",
            PasswordHash = "x",
            RoleIds = [roleId],
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Role
            {
                Id = roleId,
                Name = "reader" + slug,
                TenantSlug = null,
                Permissions = [new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } }],
            });
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        (await CanReadAsync(user, slug)).Should().BeTrue("the role grants read, so the denial below is about deletion");

        await SetDeletedAsync(roleId, DateTime.UtcNow);
        (await CanReadAsync(user, slug)).Should().BeFalse("a deleted role grants nothing, even to a holder");

        await SetDeletedAsync(roleId, null);
        (await CanReadAsync(user, slug)).Should().BeTrue("a restored role grants what it had");
    }

    [Fact]
    public async Task Erasing_a_deleted_role_is_final_and_a_live_role_cannot_be_erased()
    {
        var client = await ClientFor("SuperAdmin");
        var (live, _) = await CreateRoleAsync(client);
        (await client.DeleteAsync($"/api/roles/{live}/erase", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/roles/{live}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await client.DeleteAsync($"/api/roles/{live}", Ct);
        (await client.DeleteAsync($"/api/roles/{live}/erase", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync($"/api/roles/{live}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.LoadAsync<Role>(live, Ct)).Should().BeNull("erase removes the row, it does not tombstone it");
    }

    [Fact]
    public async Task A_new_role_can_take_the_name_of_a_deleted_one_and_that_one_is_gone_for_good()
    {
        var client = await ClientFor("SuperAdmin");
        var (old, name) = await CreateRoleAsync(client);
        await client.DeleteAsync($"/api/roles/{old}", Ct);

        var (fresh, _) = await CreateRoleAsync(client, name);

        fresh.Should().NotBe(old);
        (await client.PostAsync($"/api/roles/{old}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await IdsAsync(client, "/api/roles/deleted")).Should().NotContain(old);
    }

    [Theory]
    [InlineData("GET", "deleted")]
    [InlineData("POST", "restore")]
    [InlineData("DELETE", "erase")]
    public async Task Listing_restoring_and_erasing_need_the_permission_delete_needs(string method, string action)
    {
        var admin = await ClientFor("SuperAdmin");
        var (id, _) = await CreateRoleAsync(admin);
        await admin.DeleteAsync($"/api/roles/{id}", Ct);
        var url = action == "deleted" ? "/api/roles/deleted" : $"/api/roles/{id}/{action}";

        var editor = await ClientFor("Editor");
        (await editor.SendAsync(new HttpRequestMessage(new HttpMethod(method), url), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await IdsAsync(admin, "/api/roles/deleted")).Should().Contain(id, "the refused calls changed nothing");
    }

    [Fact]
    public async Task A_deleted_group_leaves_every_read_cannot_be_joined_and_comes_back_on_restore()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var res = await client.PostAsJsonAsync("/api/user-groups", new { name = "g" + Guid.NewGuid().ToString("n")[..8], description = "d" }, Ct);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var userId = await StoreUserAsync();

        (await client.DeleteAsync($"/api/user-groups/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"/api/user-groups/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await IdsAsync(client, "/api/user-groups")).Should().NotContain(id);
        (await IdsAsync(client, "/api/user-groups/deleted")).Should().Contain(id);
        (await client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = id }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/user-groups/{id}/users", new { userId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.PostAsync($"/api/user-groups/{id}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/user-groups/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = id }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Erasing_a_deleted_group_is_final_and_a_live_group_cannot_be_erased()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var res = await client.PostAsJsonAsync("/api/user-groups", new { name = "g" + Guid.NewGuid().ToString("n")[..8], description = "d" }, Ct);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        (await client.DeleteAsync($"/api/user-groups/{id}/erase", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/user-groups/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await client.DeleteAsync($"/api/user-groups/{id}", Ct);
        (await client.DeleteAsync($"/api/user-groups/{id}/erase", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/user-groups/{id}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<bool> CanReadAsync(User user, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        resolver.InvalidateAllPermissions();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct);
        return await resolver.CanPerformActionAsync(stored!, slug, "read", cancellationToken: Ct);
    }

    private async Task SetDeletedAsync(Guid roleId, DateTime? at)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.LoadAsync<Role>(roleId, Ct);
        role!.DeletedAt = at;
        session.Store(role);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Guid> StoreUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "member" + Guid.NewGuid().ToString("n")[..10],
            Email = $"{Guid.NewGuid():n}@example.com",
            PasswordHash = "x",
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user.Id;
    }

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid userId, Guid roleId) =>
        client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId }, Ct);

    /// <param name="isPlatform">The global assign route takes only a platform role.</param>
    private static async Task<(Guid Id, string Name)> CreateRoleAsync(
        HttpClient client, string? name = null, bool isPlatform = false)
    {
        name ??= "role" + Guid.NewGuid().ToString("n")[..10];
        var res = await client.PostAsJsonAsync("/api/roles", new
        {
            name,
            isPlatform,
            description = "d",
            permissions = new[] { new { contentTypeSlug = "softdelarticle", read = new { enabled = true } } },
            systemCapabilities = Array.Empty<string>(),
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), name);
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient client, string url)
    {
        var ids = new List<Guid>();
        for (var number = 1; ; number++)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"{url}?pageSize=100&page={number}", Ct);
            var items = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
            ids.AddRange(items);
            if (items.Count < 100)
            {
                return ids;
            }
        }
    }

    private async Task<HttpClient> ClientFor(params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync(roles));
        return client;
    }
}
