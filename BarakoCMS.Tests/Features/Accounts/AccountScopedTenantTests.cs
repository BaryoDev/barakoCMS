using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Accounts;

/// <summary>
/// Accounts sit above tenants (#898). A user made a manager of one account manages that account's
/// tenants and no others, a caller holding manage_tenants still manages them all, and a deployment
/// with no accounts behaves as it did before.
/// </summary>
[Collection("Sequential")]
public class AccountScopedTenantTests
{
    private readonly IntegrationTestFixture _fixture;

    public AccountScopedTenantTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task<TenantAccount> StoreAccountAsync(string? name = null)
    {
        var slug = Unique("ac");
        var account = new TenantAccount { Id = Guid.NewGuid(), Slug = slug, Name = name ?? $"Account {slug}" };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(account);
        await session.SaveChangesAsync(Ct);
        return account;
    }

    private async Task<Tenant> StoreTenantAsync(Guid? accountId, Action<Tenant>? shape = null)
    {
        var slug = Unique("tn");
        var tenant = new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"Name of {slug}", AccountId = accountId };
        shape?.Invoke(tenant);
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(tenant);
        await session.SaveChangesAsync(Ct);
        return tenant;
    }

    private async Task<Tenant> ReadTenantAsync(string slug)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Tenant>().SingleAsync(t => t.Slug == slug, Ct);
    }

    /// <summary>A user with no roles who manages the given accounts, and nothing else.</summary>
    private async Task<(HttpClient Client, Guid UserId)> ManagerOfAsync(params Guid[] accountIds)
    {
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"am-{userId:N}"[..14],
                Email = $"am-{userId:N}@example.com",
                RoleIds = [],
            });
            foreach (var accountId in accountIds)
                session.Store(new TenantAccountManager { Id = Guid.NewGuid(), AccountId = accountId, UserId = userId });
            await session.SaveChangesAsync(Ct);
        }

        return (Bearer(_fixture.CreateToken(roles: [$"Account Manager {userId:N}"], userId: userId.ToString())), userId);
    }

    private async Task<HttpClient> PlatformManagerAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private HttpClient Bearer(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<string>> SlugsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToList();
    }

    private static async Task<List<string>> AllSlugsAsync(HttpClient client, string path)
    {
        var all = new List<string>();
        for (var page = 1; ; page++)
        {
            var response = await client.GetAsync($"{path}?pageSize=100&page={page}", Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            all.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!));
            if (all.Count >= body.GetProperty("totalItems").GetInt32()) return all;
        }
    }

    [Fact]
    public async Task A_scoped_manager_lists_only_the_tenants_of_their_account()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var own = await StoreTenantAsync(mine.Id);
        var other = await StoreTenantAsync(theirs.Id);
        var unassigned = await StoreTenantAsync(null);
        var (client, _) = await ManagerOfAsync(mine.Id);

        var slugs = await SlugsAsync(await client.GetAsync("/api/tenants?pageSize=100", Ct));

        slugs.Should().Contain(own.Slug, "the control: without it a list that returns nothing satisfies the rest");
        slugs.Should().NotContain(other.Slug);
        slugs.Should().NotContain(unassigned.Slug, "a tenant in the default account is not in this one");
    }

    [Fact]
    public async Task A_scoped_manager_cannot_change_another_accounts_tenant()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var other = await StoreTenantAsync(theirs.Id);
        var (client, _) = await ManagerOfAsync(mine.Id);

        var response = await client.PutAsJsonAsync($"/api/tenants/{other.Slug}", new { name = "Taken over" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "another account's tenant is not visible to this caller");
        (await ReadTenantAsync(other.Slug)).Name.Should().Be(other.Name);
    }

    [Fact]
    public async Task A_scoped_manager_changes_a_tenant_of_their_account()
    {
        var mine = await StoreAccountAsync();
        var own = await StoreTenantAsync(mine.Id);
        var (client, _) = await ManagerOfAsync(mine.Id);

        var response = await client.PutAsJsonAsync($"/api/tenants/{own.Slug}", new { name = "Renamed" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadTenantAsync(own.Slug)).Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task A_scoped_manager_cannot_move_a_tenant_to_another_account()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var own = await StoreTenantAsync(mine.Id);
        var (client, _) = await ManagerOfAsync(mine.Id);

        var response = await client.PutAsJsonAsync(
            $"/api/tenants/{own.Slug}", new { name = own.Name, accountId = theirs.Id }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadTenantAsync(own.Slug)).AccountId.Should().Be(mine.Id);
    }

    [Fact]
    public async Task A_scoped_manager_creates_a_tenant_in_their_account_and_not_in_another()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var (client, _) = await ManagerOfAsync(mine.Id);

        var refused = await client.PostAsJsonAsync(
            "/api/tenants", new { handle = Unique("nw"), name = "Not mine", accountId = theirs.Id }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var handle = Unique("nw");
        var created = await client.PostAsJsonAsync("/api/tenants", new { handle, name = "Mine" }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        (await ReadTenantAsync(handle)).AccountId.Should().Be(mine.Id,
            "a caller who manages exactly one account creates into it");
    }

    [Fact]
    public async Task A_caller_with_no_capability_and_no_account_grant_is_refused()
    {
        var client = await ManagerOfAsync();

        (await client.Client.GetAsync("/api/tenants", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.Client.GetAsync("/api/accounts", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_platform_manager_still_reaches_every_tenant_and_a_deployment_with_no_accounts_is_unchanged()
    {
        var theirs = await StoreAccountAsync();
        var other = await StoreTenantAsync(theirs.Id);
        var unassigned = await StoreTenantAsync(null);
        var admin = await PlatformManagerAsync();

        var slugs = await AllSlugsAsync(admin, "/api/tenants");
        slugs.Should().Contain([other.Slug, unassigned.Slug]);

        var update = await admin.PutAsJsonAsync($"/api/tenants/{unassigned.Slug}", new { name = "Still works" }, Ct);
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync(Ct));
        var body = await update.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("accountId").GetGuid().Should().Be(TenantAccount.DefaultId);
        (await ReadTenantAsync(unassigned.Slug)).AccountId.Should().BeNull(
            "a tenant in the default account is stored as it always was");
    }

    [Fact]
    public async Task A_domain_clash_does_not_name_a_tenant_the_scoped_caller_may_not_reach()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var domain = $"{Unique("d")}.example.com";
        var other = await StoreTenantAsync(theirs.Id, t => t.Domains = [domain]);
        var own = await StoreTenantAsync(mine.Id);
        var (client, _) = await ManagerOfAsync(mine.Id);

        var response = await client.PutAsJsonAsync(
            $"/api/tenants/{own.Slug}", new { name = own.Name, domains = new[] { domain } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(other.Slug);
    }

    [Fact]
    public async Task A_scoped_manager_reads_only_their_own_accounts()
    {
        var mine = await StoreAccountAsync();
        var theirs = await StoreAccountAsync();
        var (client, _) = await ManagerOfAsync(mine.Id);

        var slugs = await SlugsAsync(await client.GetAsync("/api/accounts", Ct));

        slugs.Should().Contain(mine.Slug);
        slugs.Should().NotContain(theirs.Slug);
        (await client.GetAsync($"/api/accounts/{theirs.Slug}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/accounts/{mine.Slug}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_scoped_manager_cannot_create_or_edit_an_account_or_grant_management()
    {
        var mine = await StoreAccountAsync();
        var (client, userId) = await ManagerOfAsync(mine.Id);

        (await client.PostAsJsonAsync("/api/accounts", new { slug = Unique("zz"), name = "New" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/accounts/{mine.Slug}", new { name = "Mine now" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/accounts/{mine.Slug}/managers", new { userId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/accounts/{mine.Slug}/managers", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_account_list_leads_with_the_implicit_default_account_for_a_platform_manager()
    {
        var admin = await PlatformManagerAsync();
        await StoreAccountAsync();

        var slugs = await SlugsAsync(await admin.GetAsync("/api/accounts?pageSize=100", Ct));

        slugs.Should().HaveCountGreaterThan(1);
        slugs[0].Should().Be(TenantAccount.DefaultSlug);
    }

    [Fact]
    public async Task A_platform_manager_creates_an_account_sets_its_branding_and_grants_a_manager()
    {
        var admin = await PlatformManagerAsync();
        var slug = Unique("ag");

        var created = await admin.PostAsJsonAsync("/api/accounts", new
        {
            slug,
            name = "Agency",
            logoUrl = "https://example.com/logo.png",
            colours = new Dictionary<string, string> { ["primary"] = "#112233" },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        (await admin.PostAsJsonAsync("/api/accounts", new { slug, name = "Again" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a slug is held once");
        (await admin.PostAsJsonAsync("/api/accounts", new { slug = Unique("bl"), name = "Bad", logoUrl = "javascript:alert(1)" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (_, userId) = await ManagerOfAsync();
        var grant = await admin.PostAsJsonAsync($"/api/accounts/{slug}/managers", new { userId }, Ct);
        grant.StatusCode.Should().Be(HttpStatusCode.OK, await grant.Content.ReadAsStringAsync(Ct));
        (await admin.PostAsJsonAsync($"/api/accounts/{slug}/managers", new { userId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "granting twice changes nothing");

        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{slug}/managers", Ct);
        listed.GetProperty("items").GetArrayLength().Should().Be(1);
        listed.GetProperty("totalItems").GetInt32().Should().Be(1);

        (await admin.DeleteAsync($"/api/accounts/{slug}/managers/{userId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{slug}/managers", Ct);
        after.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_grant_and_a_revoke_each_leave_an_audit_entry_naming_the_account_the_user_and_the_actor()
    {
        var admin = await PlatformManagerAsync();
        var account = await StoreAccountAsync();
        var (_, userId) = await ManagerOfAsync();

        (await admin.PostAsJsonAsync($"/api/accounts/{account.Slug}/managers", new { userId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/accounts/{account.Slug}/managers/{userId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var target = userId.ToString();
        var entries = await session.Query<AuditEvent>().Where(e => e.TargetId == target).ToListAsync(Ct);

        entries.Should().HaveCount(2);
        entries.Select(e => e.Action).Should().BeEquivalentTo(["account.manager_granted", "account.manager_revoked"]);
        foreach (var entry in entries)
        {
            entry.ActorUserId.Should().NotBeNull("the entry names who made the change");
            entry.Metadata!["accountSlug"].ToString().Should().Be(account.Slug);
            entry.Metadata["accountId"].ToString().Should().Be(account.Id.ToString());
        }
    }

    [Fact]
    public async Task Revoking_the_grant_takes_the_tenants_away_without_a_new_token()
    {
        var mine = await StoreAccountAsync();
        var own = await StoreTenantAsync(mine.Id);
        var (client, userId) = await ManagerOfAsync(mine.Id);
        (await client.GetAsync($"/api/tenants", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await PlatformManagerAsync();
        (await admin.DeleteAsync($"/api/accounts/{mine.Slug}/managers/{userId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PutAsJsonAsync($"/api/tenants/{own.Slug}", new { name = "Late" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_branding_endpoint_answers_the_name_logo_and_colours_of_the_tenants_account()
    {
        var account = await StoreAccountAsync("Agency Co");
        account.LogoUrl = "https://example.com/agency.png";
        account.Colours = new Dictionary<string, string> { ["primary"] = "#abcdef" };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(account);
            await session.SaveChangesAsync(Ct);
        }

        var tenant = await StoreTenantAsync(account.Id);
        var client = await InTenantAsync(tenant.Slug);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/accounts/branding", Ct);

        body.GetProperty("name").GetString().Should().Be("Agency Co");
        body.GetProperty("logoUrl").GetString().Should().Be("https://example.com/agency.png");
        body.GetProperty("colours").GetProperty("primary").GetString().Should().Be("#abcdef");
        body.GetProperty("accountSlug").GetString().Should().Be(account.Slug);
    }

    [Fact]
    public async Task The_branding_endpoint_falls_back_to_the_tenants_own_branding_when_the_account_has_none()
    {
        var tenant = await StoreTenantAsync(null, t =>
        {
#pragma warning disable CS0618
            t.Branding = new Dictionary<string, string> { ["primary"] = "#010203" };
#pragma warning restore CS0618
        });
        var client = await InTenantAsync(tenant.Slug);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/accounts/branding", Ct);

        body.GetProperty("accountSlug").GetString().Should().Be(TenantAccount.DefaultSlug);
        body.GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null, "the console keeps its own wordmark");
        body.GetProperty("colours").GetProperty("primary").GetString().Should().Be("#010203");
    }

    /// <summary>A signed-in member of the tenant with no roles, which is all the branding route asks for.</summary>
    private async Task<HttpClient> InTenantAsync(string slug)
    {
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"br-{userId:N}"[..14],
                Email = $"br-{userId:N}@example.com",
                RoleIds = [],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = Bearer(_fixture.CreateToken(
            roles: ["Viewer"],
            userId: userId.ToString(),
            additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return client;
    }
}
