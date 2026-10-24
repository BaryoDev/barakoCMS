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
[Collection("Integration2")]
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
            fields = new[] { new { name = "Title", displayName = "Title", type = "string", isRequired = true } },
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

        var valid = await ErrorsForEntryAsync(inA, name, new Dictionary<string, object> { ["Title"] = "Suite" });
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
            fieldName = "Sku",
            displayName = "SKU",
            type = "string",
            isRequired = true,
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var onlyTitle = new Dictionary<string, object> { ["Title"] = "Suite" };
        (await ErrorsForEntryAsync(inA, name, onlyTitle)).Should().NotBeEmpty("sku is now required in tenant A");
        (await ErrorsForEntryAsync(inB, name, onlyTitle)).Should().NotBeEmpty("sku is now required in tenant B");
        (await ErrorsForEntryAsync(outside, name, onlyTitle)).Should().BeEmpty("the outside tenant is unaffected");

        var both = new Dictionary<string, object> { ["Title"] = "Suite", ["Sku"] = "S-1" };
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
        var field = new { fieldName = "Sku", displayName = "SKU", type = "string" };

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
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
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
                fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
            }, Ct);

        (await OwnTypeAsync(inA)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OwnTypeAsync(outside)).StatusCode.Should().Be(HttpStatusCode.OK);

        var clash = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name = taken,
            displayName = "Shared",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict, await clash.Content.ReadAsStringAsync(Ct));
    }

    private async Task StoreInTenantAsync(string tenantSlug, params object[] documents)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private static ContentTypeDefinition LegacyTenantType(string name, SensitivityLevel level) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = "Legacy",
        Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = false, Sensitivity = level }],
    };

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    [Fact]
    public async Task The_account_type_wins_over_a_tenant_type_of_the_same_name_that_was_stored_before()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        await StoreInTenantAsync(inA, LegacyTenantType(name, SensitivityLevel.Public));

        var errors = await ErrorsForEntryAsync(inA, name, new Dictionary<string, object>());

        errors.Should().NotBeEmpty("the account's title is required and the tenant's looser copy must not shadow it");
    }

    [Fact]
    public async Task The_resolver_returns_the_account_definition_with_its_sensitivity_over_the_tenants_own()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = Unique("sec");
        var created = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "Sec",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string", sensitivity = "Sensitive" } },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        await StoreInTenantAsync(inA, LegacyTenantType(name, SensitivityLevel.Public));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(inA);
        var resolved = await AccountContentTypes.ResolveAsync(session, name, Ct);
        var many = await AccountContentTypes.ResolveManyAsync(session, [name], Ct);

        resolved.Should().NotBeNull();
        resolved!.Fields.Should().ContainSingle().Which.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        many[name].Fields.Should().ContainSingle().Which.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
    }

    [Fact]
    public async Task The_redactor_names_a_sensitive_field_of_an_account_type()
    {
        var account = await AccountAsync();
        var manager = await ManagerAsync(account.Id);
        var field = "SecretNote";
        var created = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name = Unique("sec"),
            displayName = "Sec",
            fields = new[] { new { name = field, displayName = "F", type = "string", sensitivity = "Sensitive" } },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var owned = await session.Query<AccountContentType>().Where(t => t.AccountId == account.Id).ToListAsync(Ct);
        owned.SelectMany(t => t.Definition.Fields).Should().Contain(f => f.Name == field && f.Sensitivity != SensitivityLevel.Public);

        var names = await BarakoCMS.Diagnostics.Redaction.SensitiveFieldNames.LoadAsync(session, false, Ct);
        names.Should().Contain(field);
    }

    [Fact]
    public async Task A_blueprint_is_refused_in_a_tenant_whose_account_owns_one_of_its_type_names()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var tenantAdmin = await TenantAdminAsync(inA);

        var list = await tenantAdmin.GetFromJsonAsync<JsonElement>("/api/content-types/blueprints", Ct);
        var blueprint = list.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("contentTypes").GetArrayLength() > 0);
        var typeName = blueprint.GetProperty("contentTypes")[0].GetString()!;

        var manager = await ManagerAsync(account.Id);
        await CreateSharedTypeAsync(manager, account, barakoCMS.Core.ContentTypeName.Normalize(typeName));

        var response = await tenantAdmin.PostAsync($"/api/content-types/blueprints/{blueprint.GetProperty("name").GetString()}", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_import_cannot_create_a_tenant_type_with_the_name_of_the_account_type()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var tenantAdmin = await TenantAdminAsync(inA);

        var response = await tenantAdmin.PostAsJsonAsync("/api/portability/import", new
        {
            contentTypes = new[]
            {
                new { name, displayName = "Mine", fields = new[] { new { name = "Title", displayName = "Title", type = "string" } } },
            },
            contents = Array.Empty<object>(),
        }, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);
        body.Should().Contain("account owns");
    }

    [Fact]
    public async Task A_tenant_holding_entries_of_its_accounts_types_cannot_move_out_of_the_account()
    {
        var account = await AccountAsync();
        var other = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        await StoreInTenantAsync(inA, new Content
        {
            Id = Guid.NewGuid(), ContentType = name, Data = new() { ["Title"] = "Suite" },
        });

        var admin = await SuperAdminAsync();
        var move = await admin.PutAsJsonAsync($"/api/tenants/{inA}", new { name = inA, accountId = other.Id }, Ct);

        move.StatusCode.Should().Be(HttpStatusCode.Conflict, await move.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_tenant_with_no_entries_of_the_account_types_can_move_out()
    {
        var account = await AccountAsync();
        var other = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);

        var admin = await SuperAdminAsync();
        var move = await admin.PutAsJsonAsync($"/api/tenants/{inA}", new { name = inA, accountId = other.Id }, Ct);

        move.StatusCode.Should().Be(HttpStatusCode.OK, await move.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_account_type_cannot_name_a_non_public_field_when_a_tenant_decided_the_name_as_event_sourced()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = Unique("es");
        await StoreInTenantAsync(inA, new ContentTypeSourcingPolicy { Name = name, EventSourced = true });
        var manager = await ManagerAsync(account.Id);

        var response = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "ES",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string", sensitivity = "Sensitive" } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_non_public_field_added_to_an_account_type_is_refused_when_a_tenant_decided_the_name_as_event_sourced()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreInTenantAsync(inA, new ContentTypeSourcingPolicy { Name = name, EventSourced = true });

        var response = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", new
        {
            fieldName = "Secret", displayName = "Secret", type = "string", sensitivity = "Sensitive",
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_account_type_takes_platform_roles_in_visibleToRoles_and_refuses_a_tenant_role()
    {
        var account = await AccountAsync();
        var manager = await ManagerAsync(account.Id);
        var platformName = Unique("plat");
        var tenantRoleName = Unique("trole");
        await StoreAsync(
            new Role { Id = Guid.NewGuid(), Name = platformName, IsPlatform = true },
            new Role { Id = Guid.NewGuid(), Name = tenantRoleName, IsPlatform = false, TenantSlug = "some-tenant" });

        async Task<HttpResponseMessage> CreateWithAsync(string role) =>
            await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
            {
                name = Unique("vr"),
                displayName = "VR",
                fields = new[] { new { name = "Title", displayName = "Title", type = "string", sensitivity = "Sensitive", visibleToRoles = new[] { role } } },
            }, Ct);

        var refused = await CreateWithAsync(tenantRoleName);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await CreateWithAsync(platformName)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_writers_of_the_same_account_type_cannot_lose_each_others_field()
    {
        var account = await AccountAsync();
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using var first = store.LightweightSession();
        await using var second = store.LightweightSession();
        var a = await first.Query<AccountContentType>().FirstAsync(t => t.AccountId == account.Id && t.Name == name, Ct);
        var b = await second.Query<AccountContentType>().FirstAsync(t => t.AccountId == account.Id && t.Name == name, Ct);
        a.Definition.Fields.Add(new FieldDefinition { Name = "One", DisplayName = "One", Type = "string" });
        b.Definition.Fields.Add(new FieldDefinition { Name = "Two", DisplayName = "Two", Type = "string" });
        first.Store(a);
        second.Store(b);
        await first.SaveChangesAsync(Ct);

        var act = async () => await second.SaveChangesAsync(Ct);

        await act.Should().ThrowAsync<JasperFx.ConcurrencyException>();
    }

    [Fact]
    public async Task An_entry_in_another_tenant_of_the_account_blocks_the_delete_and_a_required_field_without_default()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreInTenantAsync(inA, new Content { Id = Guid.NewGuid(), ContentType = name, Data = new() { ["Title"] = "Suite" } });

        var required = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", new
        {
            fieldName = "Sku", displayName = "SKU", type = "string", isRequired = true,
        }, Ct);
        required.StatusCode.Should().Be(HttpStatusCode.BadRequest, await required.Content.ReadAsStringAsync(Ct));

        (await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
