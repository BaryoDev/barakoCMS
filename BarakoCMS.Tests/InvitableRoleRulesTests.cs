using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class InvitableRoleRulesTests
{
    private readonly IntegrationTestFixture _fixture;

    public InvitableRoleRulesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static string Name() => $"Inv rule {Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient client, string[] capabilities, bool invitable, bool isPlatform = false) =>
        await client.PostAsJsonAsync("/api/roles", new
        {
            name = Name(),
            description = "invitable rules",
            permissions = Array.Empty<object>(),
            systemCapabilities = capabilities,
            invitable,
            isPlatform,
        }, Ct);

    private static async Task<Guid> CreatedAsync(HttpClient client, string[] capabilities, bool invitable)
    {
        var response = await CreateAsync(client, capabilities, invitable);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>(Ct))!.Id;
    }

    private async Task<HttpResponseMessage> UpdateAsync(
        HttpClient client, Guid id, string[] capabilities, bool? invitable)
    {
        var stored = await StoredAsync(id);
        return await client.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name = stored!.Name,
            description = "changed",
            permissions = Array.Empty<object>(),
            systemCapabilities = capabilities,
            invitable,
        }, Ct);
    }

    private async Task<Role?> StoredAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
    }

    [Theory]
    [InlineData("manage_tenant_members")]
    [InlineData("manage_platform_roles")]
    public async Task Create_refuses_invitable_on_a_role_holding_a_capability(string capability)
    {
        var response = await CreateAsync(await SuperAdminAsync(), [capability], invitable: true);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_refuses_invitable_on_a_platform_role()
    {
        var response = await CreateAsync(await SuperAdminAsync(), [], invitable: true, isPlatform: true);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_accepts_invitable_on_a_plain_role()
    {
        (await CreateAsync(await SuperAdminAsync(), [], invitable: true)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("manage_tenant_members")]
    [InlineData("manage_platform_roles")]
    public async Task Update_refuses_invitable_on_a_role_with_a_capability(string capability)
    {
        var client = await SuperAdminAsync();
        var id = await CreatedAsync(client, [capability], invitable: false);
        (await UpdateAsync(client, id, [capability], invitable: true)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(id))!.Invitable.Should().BeFalse();
    }

    [Fact]
    public async Task Update_refuses_invitable_on_a_platform_role()
    {
        var client = await SuperAdminAsync();
        var response = await CreateAsync(client, [], invitable: false, isPlatform: true);
        var id = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>(Ct))!.Id;
        (await UpdateAsync(client, id, [], invitable: true)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_refuses_a_capability_on_a_role_that_stays_invitable()
    {
        var client = await SuperAdminAsync();
        var id = await CreatedAsync(client, [], invitable: true);
        // invitable left out keeps the stored true
        (await UpdateAsync(client, id, ["manage_tenant_members"], invitable: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(id))!.SystemCapabilities.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_lets_a_capability_in_when_the_role_stops_being_invitable()
    {
        var client = await SuperAdminAsync();
        var id = await CreatedAsync(client, [], invitable: true);
        (await UpdateAsync(client, id, ["view_audit_log"], invitable: false)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
