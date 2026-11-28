using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using BarakoCMS.Tests.Features.Config;
using BarakoCMS.Tests.Jobs;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Accounts;

/// <summary>
/// A content type owned by an account is used by every tenant in it (#945). The tenants in the
/// account see it and validate their own entries against it, a tenant outside the account does not,
/// the tenant's admin cannot change it, and an account manager can.
/// </summary>
[Collection("Integration1")]
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

    private async Task<HttpClient> ManagerAsync(Guid accountId, string? tenantSlug = null)
    {
        var userId = Guid.NewGuid();
        await StoreAsync(
            new User { Id = userId, Username = $"am-{userId:N}"[..14], Email = $"am-{userId:N}@example.com", RoleIds = [] },
            new TenantAccountManager { Id = Guid.NewGuid(), AccountId = accountId, UserId = userId });
        var client = _fixture.CreateClient();
        if (tenantSlug is null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                _fixture.CreateToken(roles: [$"Account Manager {userId:N}"], userId: userId.ToString()));
            return client;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: [$"Account Manager {userId:N}"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    /// <summary>An account manager in a tenant who also holds ManageApiKeys there, through an Admin membership.</summary>
    private async Task<HttpClient> KeyReadingManagerAsync(Guid accountId, string tenantSlug)
    {
        var userId = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{Unique("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenantSlug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            },
            new TenantAccountManager { Id = Guid.NewGuid(), AccountId = accountId, UserId = userId });
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: ["Admin", $"Account Manager {userId:N}"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
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

    private async Task<Guid> StoreKeyAsync(string[] contentTypes, string tenant = Tenant.DefaultSlug, bool revoked = false, DateTime? expiresAt = null)
    {
        var id = Guid.NewGuid();
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await StoreAsync(new ApiKey
        {
            Id = id, Name = "warn-test", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
            UserId = Guid.NewGuid(), TenantSlug = tenant, Scopes = ["content:write"],
            ContentTypes = [.. contentTypes], Revoked = revoked, ExpiresAt = expiresAt,
        });
        return id;
    }

    private async Task<JsonElement> DryRunAsync(HttpClient manager, TenantAccount account, string name)
    {
        var response = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}?dryRun=true", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<bool> TypeExistsAsync(HttpClient manager, TenantAccount account, string name)
    {
        var response = await manager.GetAsync($"/api/accounts/{account.Slug}/content-types", Ct);
        return (await response.Content.ReadAsStringAsync(Ct)).Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_dry_run_delete_names_the_live_key_that_uses_the_type_and_deletes_nothing()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await KeyReadingManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var key = await StoreKeyAsync([name.ToUpperInvariant()], inA);
        await StoreKeyAsync(["unrelated"], inA);

        var body = await DryRunAsync(manager, account, name);

        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("apiKeysAffected").GetInt32().Should().Be(1);
        body.GetProperty("apiKeyWarnings").EnumerateArray().Select(w => w.GetProperty("keyId").GetGuid())
            .Should().ContainSingle().Which.Should().Be(key);
        (await TypeExistsAsync(manager, account, name)).Should().BeTrue("a dry run deletes nothing");
    }

    [Fact]
    public async Task A_real_delete_of_a_type_a_key_names_answers_204_with_no_body_and_the_type_is_gone()
    {
        var account = await AccountAsync();
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreKeyAsync([name]);

        var response = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
        (await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_without_manage_api_keys_gets_the_count_and_no_key_names()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreKeyAsync([name], inA);

        var body = await DryRunAsync(manager, account, name);

        body.GetProperty("apiKeysAffected").GetInt32().Should().Be(1);
        body.GetProperty("apiKeyWarnings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_dry_run_warning_has_exactly_the_key_id_name_prefix_and_types_and_never_the_hash()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = Unique("room");
        await StoreAsync(new AccountContentType
        {
            Id = Guid.NewGuid(), AccountId = account.Id, Name = name,
            Definition = new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name, DisplayName = "Room", Fields = [] },
        });
        await StoreKeyAsync([name], inA);
        var client = await KeyReadingManagerAsync(account.Id, inA);

        var body = await DryRunAsync(client, account, name);

        var warning = body.GetProperty("apiKeyWarnings")[0];
        warning.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("keyId", "name", "prefix", "contentTypes");
        warning.GetProperty("name").GetString().Should().Be("warn-test");
        warning.GetProperty("prefix").GetString().Should().StartWith("bcms_");
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("dryRun", "apiKeysAffected", "apiKeyWarnings");
    }

    [Fact]
    public async Task A_key_of_a_tenant_outside_the_account_is_not_counted_on_a_type_dry_run()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreKeyAsync([name], await TenantAsync(null));

        (await DryRunAsync(manager, account, name)).GetProperty("apiKeysAffected").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_key_of_a_sibling_tenant_of_the_account_is_counted_but_not_named_to_a_caller_in_the_first_tenant()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var inB = await TenantAsync(account.Id);
        var manager = await KeyReadingManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(manager, account);
        var own = await StoreKeyAsync([name], inA);
        await StoreKeyAsync([name], inB);

        var body = await DryRunAsync(manager, account, name);

        body.GetProperty("apiKeysAffected").GetInt32().Should().Be(2, "the delete removes the type for every tenant of the account");
        body.GetProperty("apiKeyWarnings").EnumerateArray().Select(w => w.GetProperty("keyId").GetGuid())
            .Should().ContainSingle().Which.Should().Be(own, "names are limited to the caller's tenant");
    }

    [Fact]
    public async Task A_revoked_or_expired_key_is_not_counted_and_one_not_yet_expired_is_on_a_type_dry_run()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreKeyAsync([name], inA, revoked: true);
        await StoreKeyAsync([name], inA, expiresAt: DateTime.UtcNow.AddDays(-1));
        await StoreKeyAsync([name], inA, expiresAt: DateTime.UtcNow.AddDays(1));

        (await DryRunAsync(manager, account, name)).GetProperty("apiKeysAffected").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_dry_run_of_a_type_that_has_entries_answers_409_like_the_real_delete()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(manager, account);
        await StoreInTenantAsync(inA, new Content { Id = Guid.NewGuid(), ContentType = name, Data = new() { ["Title"] = "Suite" } });

        var dry = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}?dryRun=true", Ct);
        var real = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct);

        real.StatusCode.Should().Be(HttpStatusCode.Conflict);
        dry.StatusCode.Should().Be(real.StatusCode);
    }

    [Fact]
    public async Task A_dry_run_from_a_caller_who_does_not_manage_the_account_answers_403_like_the_real_delete()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var tenantAdmin = await TenantAdminAsync(inA);

        var dry = await tenantAdmin.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}?dryRun=true", Ct);
        var real = await tenantAdmin.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct);

        real.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        dry.StatusCode.Should().Be(real.StatusCode);
    }

    [Fact]
    public async Task A_dry_run_of_an_unknown_type_answers_what_the_real_delete_answers()
    {
        var account = await AccountAsync();
        var manager = await ManagerAsync(account.Id);
        var name = Unique("nope");

        var real = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct);
        var dry = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}?dryRun=true", Ct);

        real.StatusCode.Should().Be(HttpStatusCode.NotFound);
        dry.StatusCode.Should().Be(real.StatusCode);
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

    private Task<HttpClient> SuperAdminAsync() =>
        _fixture.CreateClientAsync("SuperAdmin");

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
    public async Task An_account_type_is_found_by_its_name_in_any_case()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = Unique("case");
        var created = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "Case",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var shouted = name.ToUpperInvariant();
        shouted.Should().NotBe(name);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(inA);
        var found = await AccountContentTypes.FindAsync(session, shouted, Ct);

        found.Should().NotBeNull("a name differing only in case is the same type");
        found!.Name.Should().BeEquivalentTo(name);
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
    public async Task An_apply_cannot_create_a_tenant_type_with_the_name_of_the_account_type()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var tenantAdmin = await TenantAdminAsync(inA);

        var (status, body) = await BundleCalls.ApplyAsync(
            tenantAdmin,
            [new { name, displayName = "Mine", fields = new[] { new { name = "Title", displayName = "Title", type = "string" } } }],
            []);

        status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);
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

    [Fact]
    public async Task An_entry_stored_in_another_case_still_blocks_the_account_type_delete()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = await CreateSharedTypeAsync(manager, account);
        var spelled = name.ToUpperInvariant();
        spelled.Should().NotBe(name);
        await StoreInTenantAsync(inA, new Content { Id = Guid.NewGuid(), ContentType = spelled, Data = new() { ["Title"] = "Suite" } });

        (await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{name}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_account_cannot_take_a_name_a_tenant_type_holds_in_another_case()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var stored = "Mixed" + Unique("t")[..8];
        await StoreInTenantAsync(inA, LegacyTenantType(stored, SensitivityLevel.Public));

        var clash = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name = stored.ToLowerInvariant(),
            displayName = "Room",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict, await clash.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_formula_field_added_to_an_account_type_is_worked_out_for_the_entries_a_tenant_holds()
    {
        var account = await AccountAsync();
        var held = await TenantAsync(account.Id);
        var empty = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = Unique("sale");
        (await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "Sale",
            fields = new[]
            {
                new { name = "Qty", displayName = "Qty", type = "decimal", isRequired = false },
                new { name = "Cost", displayName = "Cost", type = "decimal", isRequired = false },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = Guid.NewGuid();
        await StoreInTenantAsync(held, new Content { Id = entry, ContentType = name, Data = new() { ["Qty"] = 4m, ["Cost"] = 2.5m } });

        var added = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", new
        {
            fieldName = "Total",
            displayName = "Total",
            type = "decimal",
            computed = new { expression = "Qty * Cost" },
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        Guid changeId;
        await using (var session = store.QuerySession(held))
        {
            var changes = await session.Query<FieldChange>()
                .Where(c => c.ContentType == name && c.Kind == FieldChangeKind.Recompute).ToListAsync(Ct);
            changes.Should().ContainSingle("the tenant holds an entry of the type");
            changeId = changes[0].Id;
        }

        await using (var session = store.QuerySession(empty))
        {
            (await session.Query<FieldChange>().AnyAsync(c => c.ContentType == name, Ct))
                .Should().BeFalse("a tenant with no entry of the type has nothing to work out");
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if ((await FieldChanges.RunAsync(store, configuration, held, changeId, maxBatches: null, Ct))?.State == FieldChangeState.Completed)
                {
                    break;
                }
            }
            catch (Exception e) when (TestHelpers.LostConcurrencyCheck(e))
            {
                // The queued job committed the same batch first; the next run reads its progress.
            }
        }

        await using var read = store.QuerySession(held);
        var stored = await read.LoadAsync<Content>(entry, Ct);
        decimal.Parse(stored!.Data["Total"].ToString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(10m);
    }

    [Fact]
    public async Task The_recompute_job_of_a_formula_field_is_stored_in_the_tenant_whose_entries_it_works_out()
    {
        var account = await AccountAsync();
        var held = await TenantAsync(account.Id);
        var manager = await ManagerAsync(account.Id);
        var name = Unique("sale");
        (await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name,
            displayName = "Sale",
            fields = new[]
            {
                new { name = "Qty", displayName = "Qty", type = "decimal", isRequired = false },
                new { name = "Cost", displayName = "Cost", type = "decimal", isRequired = false },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await StoreInTenantAsync(held, new Content { Id = Guid.NewGuid(), ContentType = name, Data = new() { ["Qty"] = 4m, ["Cost"] = 2.5m } });

        var added = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types/{name}/fields", new
        {
            fieldName = "Total",
            displayName = "Total",
            type = "decimal",
            computed = new { expression = "Qty * Cost" },
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var read = store.QuerySession(held);
        var change = await read.Query<FieldChange>().SingleAsync(c => c.ContentType == name && c.Kind == FieldChangeKind.Recompute, Ct);

        var jobs = await QueuedJobs.ForChangeAsync(store, change.Id, Ct);

        jobs.Should().ContainSingle("the change was saved running, and a change with no job locks its type for good");
        jobs[0].TenantId.Should().Be(held, "the job belongs to the tenant of the change, not the request's");
    }

    [Fact]
    public async Task The_field_changes_of_an_account_type_are_listed_in_a_tenant_of_the_account()
    {
        var account = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var admin = await KeyReadingManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(await ManagerAsync(account.Id), account);
        var change = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = name, Field = "Title", Kind = FieldChangeKind.Recompute,
            State = FieldChangeState.Completed, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await StoreInTenantAsync(inA, change);

        var res = await admin.GetAsync($"/api/content-types/{name}/field-changes", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(change.Id);
    }

    [Fact]
    public async Task The_field_changes_of_another_accounts_type_are_not_found()
    {
        var account = await AccountAsync();
        var other = await AccountAsync();
        var inA = await TenantAsync(account.Id);
        var admin = await KeyReadingManagerAsync(account.Id, inA);
        var name = await CreateSharedTypeAsync(await ManagerAsync(other.Id), other);

        var res = await admin.GetAsync($"/api/content-types/{name}/field-changes", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
