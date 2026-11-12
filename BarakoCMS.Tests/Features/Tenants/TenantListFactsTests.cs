using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Tenants;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// The tenant list carries memberCount and siteMode for each tenant, each read with one statement
/// for the page, inside the caller's account reach.
/// </summary>
[Collection("Integration1")]
public class TenantListFactsTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly CapturingMartenLogger _logger = new();
    private readonly Func<HttpClient> _newClient;
    private readonly IServiceProvider _services;

    public TenantListFactsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        var derived = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureMarten(options => options.Logger(_logger))));
        _newClient = derived.CreateClient;
        _services = derived.Services;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task<Guid> AccountAsync()
    {
        var account = new TenantAccount { Id = Guid.NewGuid(), Slug = Unique("ac"), Name = "Account" };
        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(account);
        await session.SaveChangesAsync(Ct);
        return account.Id;
    }

    private async Task<string> TenantAsync(Guid accountId)
    {
        var slug = Unique("tn");
        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"Name of {slug}", AccountId = accountId });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task MembersAsync(string slug, int active, int removed = 0)
    {
        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        for (var i = 0; i < active + removed; i++)
        {
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                TenantSlug = slug,
                Status = i < active ? MembershipStatus.Active : MembershipStatus.Removed,
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    private Task SiteAsync(string slug, string mode, ContentStatus status = ContentStatus.Published) =>
        SiteEntryAsync(slug, new Dictionary<string, object> { ["Mode"] = mode }, status: status);

    private async Task SiteEntryAsync(
        string slug, Dictionary<string, object> data, DateTime? createdAt = null, ContentStatus status = ContentStatus.Published)
    {
        var store = _services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(slug);
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "site",
            Status = status,
            Data = data,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>A caller who manages exactly the one account, so the page holds that account's tenants and no others.</summary>
    private async Task<HttpClient> ManagerOfAsync(Guid accountId)
    {
        var userId = Guid.NewGuid();
        using (var scope = _services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"tf-{userId:N}"[..14],
                Email = $"tf-{userId:N}@example.com",
                RoleIds = [],
            });
            session.Store(new TenantAccountManager { Id = Guid.NewGuid(), AccountId = accountId, UserId = userId });
            await session.SaveChangesAsync(Ct);
        }

        var client = _newClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [$"Account Manager {userId:N}"], userId: userId.ToString()));
        return client;
    }

    private async Task<HttpClient> PlatformAdminAsync()
    {
        var client = _newClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static async Task<Dictionary<string, JsonElement>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tenants?pageSize=100", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("slug").GetString()!, i => i.Clone());
    }

    [Fact]
    public async Task The_list_gives_each_tenant_its_own_member_count_and_leaves_out_removed_members()
    {
        var account = await AccountAsync();
        var three = await TenantAsync(account);
        var one = await TenantAsync(account);
        var none = await TenantAsync(account);
        await MembersAsync(three, active: 3, removed: 2);
        await MembersAsync(one, active: 1);

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows.Should().HaveCount(3);
        rows[three].GetProperty("memberCount").GetInt32().Should().Be(3);
        rows[one].GetProperty("memberCount").GetInt32().Should().Be(1);
        rows[none].GetProperty("memberCount").GetInt32().Should().Be(0, "a tenant nobody joined has zero, not null");
    }

    [Fact]
    public async Task The_list_gives_the_stored_site_mode_and_null_without_a_published_site_entry()
    {
        var account = await AccountAsync();
        var priv = await TenantAsync(account);
        var live = await TenantAsync(account);
        var bare = await TenantAsync(account);
        var draftOnly = await TenantAsync(account);
        await SiteAsync(priv, "Private");
        await SiteAsync(live, "Live");
        await SiteAsync(draftOnly, "Private", ContentStatus.Draft);

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows.Should().HaveCount(4);
        rows[priv].GetProperty("siteMode").GetString().Should().Be("Private");
        rows[live].GetProperty("siteMode").GetString().Should().Be("Live");
        rows[bare].GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
        rows[draftOnly].GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null, "a draft entry is not the tenant's site");
    }

    [Fact]
    public async Task One_site_entry_does_not_set_the_mode_of_another_tenant()
    {
        var account = await AccountAsync();
        var withSite = await TenantAsync(account);
        var other = await TenantAsync(account);
        await SiteAsync(withSite, "Private");
        await MembersAsync(withSite, active: 2);

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows[withSite].GetProperty("siteMode").GetString().Should().Be("Private", "the control: the entry is read");
        rows[other].GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
        rows[other].GetProperty("memberCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_scoped_caller_does_not_see_the_tenants_or_counts_of_another_account()
    {
        var mine = await AccountAsync();
        var theirs = await AccountAsync();
        var own = await TenantAsync(mine);
        var foreign = await TenantAsync(theirs);
        await MembersAsync(own, active: 2);
        await MembersAsync(foreign, active: 5);
        await SiteAsync(foreign, "Private");

        var rows = await ListAsync(await ManagerOfAsync(mine));

        rows.Should().ContainKey(own);
        rows[own].GetProperty("memberCount").GetInt32().Should().Be(2);
        rows.Should().NotContainKey(foreign);
    }

    private async Task<(int Counts, int Modes)> StatementsForAListOfAsync(int tenants)
    {
        var account = await AccountAsync();
        for (var i = 0; i < tenants; i++)
        {
            var slug = await TenantAsync(account);
            await MembersAsync(slug, active: i + 1);
            await SiteAsync(slug, i % 2 == 0 ? "Live" : "Private");
        }

        var client = await ManagerOfAsync(account);
        _logger.Commands.Clear();
        var rows = await ListAsync(client);
        rows.Should().HaveCount(tenants);
        rows.Values.Should().OnlyContain(r => r.GetProperty("memberCount").GetInt32() > 0);

        var sql = _logger.Commands.Select(c => c.Sql).ToList();
        return (sql.Count(s => s.Contains("mt_doc_memberships") && s.Contains("group by")),
                sql.Count(s => s.Contains("mt_doc_contents") && s.Contains("jsonb_each_text")));
    }

    [Fact]
    public async Task The_statements_for_counts_and_modes_stay_at_one_each_as_the_page_grows()
    {
        var small = await StatementsForAListOfAsync(3);
        var large = await StatementsForAListOfAsync(9);

        small.Should().Be((1, 1));
        large.Should().Be((1, 1));
    }

    [Fact]
    public async Task Create_answers_with_the_creators_membership_counted_and_no_site_mode()
    {
        var admin = await PlatformAdminAsync();
        var handle = Unique("cr");

        var response = await admin.PostAsJsonAsync("/api/tenants", new { handle, name = "Created" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("memberCount").GetInt32().Should().Be(1);
        body.GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Update_answers_with_the_member_count_and_the_site_mode()
    {
        var account = await AccountAsync();
        var slug = await TenantAsync(account);
        await MembersAsync(slug, active: 4);
        await SiteAsync(slug, "Private");

        var response = await (await PlatformAdminAsync()).PutAsJsonAsync($"/api/tenants/{slug}", new { name = "Renamed" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("memberCount").GetInt32().Should().Be(4);
        body.GetProperty("siteMode").GetString().Should().Be("Private");
    }

    [Fact]
    public async Task A_newer_site_entry_without_a_mode_answers_null_and_not_an_older_entrys_mode()
    {
        var account = await AccountAsync();
        var slug = await TenantAsync(account);
        await SiteEntryAsync(slug, new() { ["Mode"] = "Live" }, DateTime.UtcNow.AddDays(-2));
        await SiteEntryAsync(slug, new() { ["Name"] = "Newer" }, DateTime.UtcNow.AddDays(-1));
        var control = await TenantAsync(account);
        await SiteAsync(control, "Live");

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows[control].GetProperty("siteMode").GetString().Should().Be("Live", "the control: a mode is read at all");
        rows[slug].GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_published_private_entry_wins_over_a_newer_live_one()
    {
        var account = await AccountAsync();
        var slug = await TenantAsync(account);
        await SiteEntryAsync(slug, new() { ["Mode"] = "Private" }, DateTime.UtcNow.AddDays(-2));
        await SiteEntryAsync(slug, new() { ["Mode"] = "Live" }, DateTime.UtcNow.AddDays(-1));

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows[slug].GetProperty("siteMode").GetString().Should().Be("Private");
    }

    [Fact]
    public async Task The_mode_key_is_matched_without_regard_to_case_and_a_blank_mode_answers_null()
    {
        var account = await AccountAsync();
        var lower = await TenantAsync(account);
        var upper = await TenantAsync(account);
        var blank = await TenantAsync(account);
        await SiteEntryAsync(lower, new() { ["mode"] = "Live" });
        await SiteEntryAsync(upper, new() { ["MODE"] = "Private" });
        await SiteEntryAsync(blank, new() { ["Mode"] = "  " });

        var rows = await ListAsync(await ManagerOfAsync(account));

        rows[lower].GetProperty("siteMode").GetString().Should().Be("Live");
        rows[upper].GetProperty("siteMode").GetString().Should().Be("Private");
        rows[blank].GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Import_answers_with_the_member_count_and_no_site_mode()
    {
        var platform = await PlatformAdminAsync();
        var source = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var exported = await platform.GetAsync($"/api/tenants/{source}/export", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, await exported.Content.ReadAsStringAsync(Ct));
        using var bundle = JsonDocument.Parse(await exported.Content.ReadAsStringAsync(Ct));

        var imported = await platform.PostAsJsonAsync("/api/tenants/import",
            new { handle = Unique("im"), name = "Imported", bundle = bundle.RootElement.Clone() }, Ct);

        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));
        var tenant = (await imported.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("tenant");
        tenant.GetProperty("memberCount").GetInt32().Should().Be(1);
        tenant.GetProperty("siteMode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The per-tenant-session path taken when the database enforces the tenant filter, driven on the
    /// shared fixture. The fixture connects as a superuser, which row level security never binds, so
    /// this proves the path reads the same answers as the one statement, not that it survives
    /// enforcement.
    /// </summary>
    [Fact]
    public async Task The_per_tenant_path_used_under_database_enforcement_gives_the_same_answers()
    {
        var account = await AccountAsync();
        var priv = await TenantAsync(account);
        var live = await TenantAsync(account);
        var bare = await TenantAsync(account);
        await SiteAsync(priv, "Private");
        await SiteAsync(live, "Live");
        await MembersAsync(live, active: 2);

        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var facts = await TenantListFacts.ForAsync(session, enforced: true, [priv, live, bare], Ct);

        facts.SiteMode(priv).Should().Be("Private");
        facts.SiteMode(live).Should().Be("Live");
        facts.SiteMode(bare).Should().BeNull();
        facts.MemberCount(live).Should().Be(2);
    }
}
