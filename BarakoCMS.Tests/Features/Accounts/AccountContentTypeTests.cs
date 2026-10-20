using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Accounts;

/// <summary>
/// A content type owned by an account is used by every tenant in it (#945). The tenants in the
/// account see it and validate their own entries against it, a tenant outside the account does not,
/// the tenant's admin cannot change it, and an account manager can.
/// </summary>
[Collection("Sequential")]
public class AccountContentTypeTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ipCounter;

    public AccountContentTypeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private static string NextIp() => $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<TenantAccount> AccountAsync()
    {
        var slug = Unique("ac");
        var account = new TenantAccount { Id = Guid.NewGuid(), Slug = slug, Name = slug };
        await StoreAsync(account);
        return account;
    }

    private async Task<string> TenantAsync(Guid? accountId)
    {
        var slug = Unique("tn");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, AccountId = accountId });
        return slug;
    }

    private async Task<HttpClient> ManagerAsync(Guid accountId)
    {
        var userId = Guid.NewGuid();
        await StoreAsync(
            new User { Id = userId, Username = $"am-{userId:N}"[..14], Email = $"am-{userId:N}@example.com", RoleIds = [] },
            new TenantAccountManager { Id = Guid.NewGuid(), AccountId = accountId, UserId = userId });
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(roles: [$"Account Manager {userId:N}"], userId: userId.ToString()));
        return client;
    }

    /// <summary>A user who holds Admin in one tenant through a membership and manages no account.</summary>
    private async Task<HttpClient> TenantAdminAsync(string slug)
    {
        var id = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new User { Id = id, Username = username, Email = $"{Unique("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = id, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: ["Admin"],
                userId: id.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task<string> CreateSharedTypeAsync(HttpClient manager, TenantAccount account, string? name = null)
    {
        name ??= Unique("room");
        var response = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "Room",
            fields = new[] { new { name = "title", displayName = "Title", type = "text", isRequired = true } },
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return name;
    }

    private async Task<List<string>> TypeNamesSeenByAsync(HttpClient tenantAdmin)
    {
        var response = await tenantAdmin.GetAsync("/api/content-types?pageSize=100", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();
    }

    /// <summary>Validates an entry the way a write in that tenant does.</summary>
    private async Task<List<string>> ErrorsForEntryAsync(string tenantSlug, string type, Dictionary<string, object> data)
    {
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenantSlug;
        var validator = scope.ServiceProvider.GetRequiredService<IContentValidatorService>();
        var (_, errors) = await validator.ValidateAsync(type, data, null);
        return errors;
    }

    [Fact]
    public async Task Every_tenant_of_the_account_sees_the_type_and_a_tenant_outside_does_not()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var inB = await TenantAsync(account.Id);
        var outside = await TenantAsync(null);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);

        (await TypeNamesSeenByAsync(await TenantAdminAsync(inA))).Should().Contain(name);
        (await TypeNamesSeenByAsync(await TenantAdminAsync(inB))).Should().Contain(name);
        (await TypeNamesSeenByAsync(await TenantAdminAsync(outside))).Should().NotContain(name);
    }

    [Fact]
    public async Task An_entry_is_validated_against_the_account_type_in_each_tenant_of_the_account_only()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var outside = await TenantAsync(null);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);

        var missing = await ErrorsForEntryAsync(inA, name, new Dictionary<string, object>());
        missing.Should().NotBeEmpty("title is required by the account's definition");

        var valid = await ErrorsForEntryAsync(inA, name, new Dictionary<string, object> { ["title"] = "Suite" });
        valid.Should().BeEmpty();

        var unvalidated = await ErrorsForEntryAsync(outside, name, new Dictionary<string, object>());
        unvalidated.Should().BeEmpty("the tenant outside the account has no type of that name");
    }

    [Fact]
    public async Task A_field_added_to_the_account_type_applies_to_both_tenants_in_and_not_the_one_out()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var inB = await TenantAsync(account.Id);
        var outside = await TenantAsync(null);
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);

        var added = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", new
        {
            fieldName = "sku",
            displayName = "SKU",
            type = "text",
            isRequired = true,
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var onlyTitle = new Dictionary<string, object> { ["title"] = "Suite" };
        (await ErrorsForEntryAsync(inA, name, onlyTitle)).Should().NotBeEmpty("sku is now required in tenant A");
        (await ErrorsForEntryAsync(inB, name, onlyTitle)).Should().NotBeEmpty("sku is now required in tenant B");
        (await ErrorsForEntryAsync(outside, name, onlyTitle)).Should().BeEmpty("the outside tenant is unaffected");

        var both = new Dictionary<string, object> { ["title"] = "Suite", ["sku"] = "S-1" };
        (await ErrorsForEntryAsync(inA, name, both)).Should().BeEmpty();
        (await ErrorsForEntryAsync(inB, name, both)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_admin_cannot_change_or_delete_the_account_type_and_an_account_manager_can()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);
        var tenantAdmin = await TenantAdminAsync(inA);
        var field = new { fieldName = "sku", displayName = "SKU", type = "text" };

        (await tenantAdmin.PostAsJsonAsync($"/api/content-types/{name}/fields", field, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the tenant route refuses a type the account owns");
        (await tenantAdmin.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", field, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tenantAdmin.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", field, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TypeNamesSeenByAsync(tenantAdmin)).Should().NotContain(name);
    }

    [Fact]
    public async Task A_manager_of_another_account_does_not_reach_the_account_type()
    {
        var mine = await AccountAsync();
        var theirs = await AccountAsync();
        var name = await CreateSharedTypeAsync(await ManagerAsync(mine.Id), mine);
        var other = await ManagerAsync(theirs.Id);

        (await other.GetAsync($"/api/accounts/{mine.Slug}/content-types", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/accounts/{mine.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_tenant_cannot_create_its_own_type_with_the_name_of_the_account_type()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var tenantAdmin = await TenantAdminAsync(inA);

        var response = await tenantAdmin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Mine",
            fields = new[] { new { name = "title", displayName = "Title", type = "text" } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_account_cannot_take_a_name_a_tenant_in_it_already_uses()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var outside = await TenantAsync(null);
        var manager = await ManagerAsync(account.Id);
        var taken = Unique("taken");

        async Task<HttpResponseMessage> OwnTypeAsync(string tenant) =>
            await (await TenantAdminAsync(tenant)).PostAsJsonAsync("/api/content-types", new
            {
                name = taken,
                displayName = "Own",
                fields = new[] { new { name = "title", displayName = "Title", type = "text" } },
            }, Ct);

        (await OwnTypeAsync(inA)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OwnTypeAsync(outside)).StatusCode.Should().Be(HttpStatusCode.OK);

        var clash = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name = taken,
            displayName = "Shared",
            fields = new[] { new { name = "title", displayName = "Title", type = "text" } },
        }, Ct);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict, await clash.Content.ReadAsStringAsync(Ct));
    }
}
