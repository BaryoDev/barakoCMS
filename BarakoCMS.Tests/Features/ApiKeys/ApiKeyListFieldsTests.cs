using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.ApiKeys;

/// <summary>
/// The key list says which keys a rotation linked and what kind each key is, so a console can tell
/// an overlap from an ordinary expiry without guessing.
/// </summary>
[Collection("Integration1")]
public class ApiKeyListFieldsTests
{
    private readonly IntegrationTestFixture _fixture;

    public ApiKeyListFieldsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid UserId, HttpClient Admin)> AdminAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", Ct)
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"kl-{userId:N}"[..16], Email = $"kl-{userId:N}@example.com", RoleIds = [role.Id] });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(["SuperAdmin"], userId.ToString()));
        return (userId, client);
    }

    private async Task<(Guid Id, string Secret)> StoreKeyAsync(
        Guid owner, Guid? principal = null, bool readOnly = false)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "list fields",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = owner,
            ServicePrincipalId = principal,
            ReadOnly = readOnly,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = ["content:read"],
        };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(key);
        await session.SaveChangesAsync(Ct);
        return (key.Id, secret);
    }

    private static async Task<(Dictionary<Guid, JsonElement> Items, string Raw)> ListAsync(HttpClient admin)
    {
        var res = await admin.GetAsync("/api/api-keys?pageSize=100", Ct);
        var raw = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.Clone());
        return (items, raw);
    }

    private static async Task<Guid> RotateAsync(HttpClient admin, Guid id)
    {
        var res = await admin.PostAsJsonAsync($"/api/api-keys/{id}/rotate", new { overlapMinutes = 60 }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_rotated_key_lists_the_key_that_replaced_it_and_the_new_key_lists_the_one_it_replaces()
    {
        var (owner, admin) = await AdminAsync();
        var (oldId, _) = await StoreKeyAsync(owner);
        var untouched = (await StoreKeyAsync(owner)).Id;

        var newId = await RotateAsync(admin, oldId);
        var (items, _) = await ListAsync(admin);

        items[oldId].GetProperty("replacedBy").GetGuid().Should().Be(newId);
        items[oldId].GetProperty("replaces").ValueKind.Should().Be(JsonValueKind.Null);
        items[newId].GetProperty("replaces").GetGuid().Should().Be(oldId);
        items[newId].GetProperty("replacedBy").ValueKind.Should().Be(JsonValueKind.Null);
        items[untouched].GetProperty("replacedBy").ValueKind.Should().Be(JsonValueKind.Null);
        items[untouched].GetProperty("replaces").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Rotating_the_same_key_twice_keeps_the_first_link_and_links_the_second_new_key_back()
    {
        var (owner, admin) = await AdminAsync();
        var (oldId, _) = await StoreKeyAsync(owner);

        var first = await RotateAsync(admin, oldId);
        var second = await RotateAsync(admin, oldId);
        var (items, _) = await ListAsync(admin);

        first.Should().NotBe(second);
        items[oldId].GetProperty("replacedBy").GetGuid().Should().Be(first, "the first successor is not overwritten");
        items[first].GetProperty("replaces").GetGuid().Should().Be(oldId);
        items[second].GetProperty("replaces").GetGuid().Should().Be(oldId);
    }

    [Fact]
    public async Task A_key_stored_without_the_rotation_fields_loads_and_lists_them_as_null()
    {
        var (owner, admin) = await AdminAsync();
        var (id, _) = await StoreKeyAsync(owner);
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "update public.mt_doc_api_keys set data = data - 'ReplacedByKeyId' - 'ReplacesKeyId' where id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1, "the control: the row is as an earlier release left it");
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(id, Ct);
            loaded.Should().NotBeNull();
            loaded!.ReplacedByKeyId.Should().BeNull();
            loaded.ReplacesKeyId.Should().BeNull();
        }

        var (items, _) = await ListAsync(admin);
        items[id].GetProperty("replacedBy").ValueKind.Should().Be(JsonValueKind.Null);
        items[id].GetProperty("replaces").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_list_gives_each_key_its_kind()
    {
        var (owner, admin) = await AdminAsync();
        var user = (await StoreKeyAsync(owner)).Id;
        var principal = (await StoreKeyAsync(owner, principal: Guid.NewGuid())).Id;
        var readOnly = (await StoreKeyAsync(owner, readOnly: true)).Id;

        var (items, _) = await ListAsync(admin);

        items[user].GetProperty("kind").GetString().Should().Be("user");
        items[principal].GetProperty("kind").GetString().Should().Be("servicePrincipal");
        items[readOnly].GetProperty("kind").GetString().Should().Be("readOnly");
    }

    [Fact]
    public async Task The_list_never_carries_a_secret_or_a_hash()
    {
        var (owner, admin) = await AdminAsync();
        var (id, secret) = await StoreKeyAsync(owner);
        await RotateAsync(admin, id);

        var (items, raw) = await ListAsync(admin);

        items.Should().ContainKey(id);
        raw.Should().NotContain(secret).And.NotContain(ApiKeyService.Hash(secret));
        raw.ToLowerInvariant().Should().NotContain("keyhash");
    }
}
