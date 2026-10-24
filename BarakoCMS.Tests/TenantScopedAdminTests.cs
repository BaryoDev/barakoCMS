using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using BarakoCMS.Diagnostics.Features.Report;
using BarakoCMS.FeatureFlags;
using BarakoCMS.Pwa;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The admin screens backed by one global table answer a tenant's administrator for that tenant.
/// </summary>
/// <remarks>
/// Settings, feature flags, client errors, email events and PWA installs are stored once for the
/// whole deployment, so the conjoined session filters none of them. The seeded Admin role holds the
/// capability for each, and the creator of a tenant holds Admin there through a membership. Each
/// test sets up two tenants, an administrator of tenant A through a membership only, and data that
/// belongs to tenant B, then checks the administrator of A cannot read or change B's data while
/// still reaching A's. A SuperAdmin keeps the deployment-wide view, and an Admin whose role was
/// granted globally (the single-tenant shape) keeps the deployment settings and flags.
/// </remarks>
[Collection("Integration4")]
public class TenantScopedAdminTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public TenantScopedAdminTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp() =>
        $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync();
    }

    private async Task<string> TenantAsync()
    {
        var slug = Unique("scope");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    /// <summary>A user who holds Admin in <paramref name="slug"/> through a membership and nowhere else.</summary>
    private async Task<(HttpClient Client, string Email)> TenantAdminAsync(string slug)
    {
        var (_, email) = await MemberAsync(slug, SystemRoles.AdminRoleId);
        var user = await UserByEmailAsync(email);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(
                roles: ["Admin"],
                userId: user.Id.ToString(),
                additionalClaims: new Dictionary<string, string>
                {
                    ["tenant"] = slug,
                    ["Username"] = user.Username,
                }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return (client, email);
    }

    private async Task<(Guid Id, string Email)> MemberAsync(string slug, params Guid[] roleIds)
    {
        var id = Guid.NewGuid();
        var email = $"{Unique("m")}@example.com";
        await StoreAsync(
            new User { Id = id, Username = Unique("u"), Email = email },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = id, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = roleIds.ToList(),
            });
        return (id, email);
    }

    private async Task<User> UserByEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<User>().FirstOrDefaultAsync(u => u.Email == email))!;
    }

    /// <summary>A caller whose roles are global, in the default tenant, the way a single-tenant deployment signs in.</summary>
    private async Task<HttpClient> GlobalAsync(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync(role));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task<T?> LoadAsync<T>(Func<IQueryable<T>, IQueryable<T>> where) where T : notnull
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().QuerySession();
        return await where(session.Query<T>()).FirstOrDefaultAsync();
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
        return array.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // ---- Settings: one table that configures the whole deployment. ----

    [Fact]
    public async Task A_tenant_admin_cannot_change_a_deployment_setting()
    {
        var tenantA = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var key = $"Scope__{Guid.NewGuid():N}";
        await StoreAsync(new SystemSetting { Id = Guid.NewGuid(), Key = key, Value = "false" });

        var response = await admin.PostAsJsonAsync("/api/settings", new { key, value = "true" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoadAsync<SystemSetting>(q => q.Where(s => s.Key == key)))!.Value.Should().Be("false");
    }

    [Fact]
    public async Task A_tenant_admin_cannot_read_the_deployment_settings()
    {
        var tenantA = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        await StoreAsync(new SystemSetting { Id = Guid.NewGuid(), Key = $"Scope__{Guid.NewGuid():N}", Value = "x" });

        (await admin.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/settings/email")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_global_role_stored_without_a_capability_list_grants_nothing_and_breaks_nothing()
    {
        var tenantA = await TenantAsync();
        var (admin, email) = await TenantAdminAsync(tenantA);
        var roleId = Guid.NewGuid();
        var user = await UserByEmailAsync(email);
        user.RoleIds = [roleId];
        await StoreAsync(new Role { Id = roleId, Name = Unique("nocaps"), SystemCapabilities = null! }, user);

        (await admin.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role document written without the list holds no capability, and must not fail the check");
    }

    [Fact]
    public async Task An_admin_granted_globally_still_manages_the_deployment_settings()
    {
        var admin = await GlobalAsync("Admin");
        var key = $"Scope__{Guid.NewGuid():N}";

        (await admin.PostAsJsonAsync("/api/settings", new { key, value = "on" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("/api/settings/email")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync<SystemSetting>(q => q.Where(s => s.Key == key)))!.Value.Should().Be("on");
    }

    // ---- Feature flags: global, with other tenants named in the targeting list. ----

    [Fact]
    public async Task A_tenant_admin_cannot_read_or_change_the_feature_flags()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var key = Unique("flag");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, TenantSlugs = [tenantB] });

        (await admin.GetAsync("/api/feature-flags/admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/feature-flags/admin",
                new { key, enabled = false, tenantSlugs = new[] { tenantA } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"/api/feature-flags/admin/{key}/toggle", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/feature-flags/admin/{key}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var flag = await LoadAsync<FeatureFlag>(q => q.Where(f => f.Key == key));
        flag.Should().NotBeNull("a refused delete leaves the flag in place");
        flag!.Enabled.Should().BeTrue();
        flag.TenantSlugs.Should().Equal(tenantB);
    }

    [Fact]
    public async Task An_admin_granted_globally_and_a_SuperAdmin_still_manage_feature_flags()
    {
        foreach (var role in new[] { "Admin", "SuperAdmin" })
        {
            var client = await GlobalAsync(role);
            var key = Unique("flag");

            (await client.PostAsJsonAsync("/api/feature-flags/admin", new { key, enabled = true }))
                .StatusCode.Should().Be(HttpStatusCode.OK, role);
            (await client.PostAsJsonAsync($"/api/feature-flags/admin/{key}/toggle", new { }))
                .StatusCode.Should().Be(HttpStatusCode.OK, role);

            var flags = await ItemsAsync(await client.GetAsync("/api/feature-flags/admin"));
            flags.Should().NotBeEmpty();
            flags.Should().Contain(f => f.GetProperty("key").GetString() == key, role);
        }
    }

    // ---- Client errors: stored globally, each row tagged with the tenant it came from. ----

    [Fact]
    public async Task A_tenant_admin_sees_only_its_own_tenants_client_errors()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var marker = Unique("err");
        await StoreAsync(
            new ClientError { Fingerprint = Unique("fp"), Message = $"{marker} in A", Tenant = tenantA },
            new ClientError { Fingerprint = Unique("fp"), Message = $"{marker} in B", Tenant = tenantB },
            new ClientError { Fingerprint = Unique("fp"), Message = $"{marker} unattributed", Tenant = null });

        var mine = await ItemsAsync(await admin.GetAsync($"/api/client-errors?q={marker}"));
        mine.Should().ContainSingle()
            .Which.GetProperty("tenant").GetString().Should().Be(tenantA);

        var all = await ItemsAsync(await (await GlobalAsync("SuperAdmin")).GetAsync($"/api/client-errors?q={marker}"));
        all.Should().HaveCount(3, "a SuperAdmin keeps the deployment-wide view");
    }

    [Fact]
    public async Task A_tenant_admin_can_resolve_its_own_client_error_but_not_another_tenants()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var own = new ClientError { Fingerprint = Unique("fp"), Message = "own", Tenant = tenantA };
        var other = new ClientError { Fingerprint = Unique("fp"), Message = "other", Tenant = tenantB };
        await StoreAsync(own, other);

        (await admin.PostAsJsonAsync($"/api/client-errors/{other.Id}/resolve", new { resolved = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync($"/api/client-errors/{own.Id}/resolve", new { resolved = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoadAsync<ClientError>(q => q.Where(e => e.Id == other.Id)))!.Resolved.Should().BeFalse();
        (await LoadAsync<ClientError>(q => q.Where(e => e.Id == own.Id)))!.Resolved.Should().BeTrue();

        (await (await GlobalAsync("SuperAdmin")).PostAsJsonAsync(
                $"/api/client-errors/{other.Id}/resolve", new { resolved = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_same_fault_from_two_tenants_is_recorded_once_per_tenant()
    {
        var tenantA = Unique("ta");
        var tenantB = Unique("tb");
        var message = Unique("same fault");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
            await ClientErrorRecorder.RecordAsync(session, new ReportItem { Message = message, Tenant = tenantA },
                "ua", Guid.NewGuid(), "user-a");
            await session.SaveChangesAsync();
            await ClientErrorRecorder.RecordAsync(session, new ReportItem { Message = message, Tenant = tenantB },
                "ua", Guid.NewGuid(), "user-b");
            await session.SaveChangesAsync();
        }

        using var read = _factory.Services.CreateScope();
        var rows = await read.ServiceProvider.GetRequiredService<IDocumentStore>().QuerySession()
            .Query<ClientError>().Where(e => e.Message == message).ToListAsync();

        rows.Should().HaveCount(2, "a fault in tenant B must not fold into tenant A's row and carry B's user onto it");
        rows.Select(r => r.Tenant).Should().BeEquivalentTo([tenantA, tenantB]);
        rows.Single(r => r.Tenant == tenantA).Username.Should().Be("user-a");
    }

    [Fact]
    public async Task An_error_reported_without_a_tenant_is_kept_on_the_tenant_the_request_resolved_to()
    {
        var tenantA = await TenantAsync();
        var message = Unique("pre sign-in");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenantA);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());

        (await client.PostAsJsonAsync("/api/client-errors", new { items = new[] { new { message } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoadAsync<ClientError>(q => q.Where(e => e.Message == message)))!
            .Tenant.Should().Be(tenantA, "otherwise only a SuperAdmin would ever see it");
    }

    // ---- Email events: one mail provider, recipients belong to whichever tenant they are members of. ----

    private sealed class TenantCapturingEmail : barakoCMS.Core.Interfaces.IEmailService
    {
        public List<string?> Tenants { get; } = [];

        public Task SendEmailAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            Tenants.Add(null);
            return Task.CompletedTask;
        }

        public Task SendForTenantAsync(string tenant, string to, string subject, string body, CancellationToken ct = default)
        {
            Tenants.Add(tenant);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_workflow_email_is_sent_on_the_workflows_tenants_behalf()
    {
        var email = new TenantCapturingEmail();
        var action = new barakoCMS.Features.Workflows.Actions.EmailAction(
            email,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<barakoCMS.Features.Workflows.Actions.EmailAction>.Instance,
            new barakoCMS.Infrastructure.Multitenancy.TenantContext { Slug = "club-a" });

        await action.RunAsync(
            new Dictionary<string, string> { ["To"] = "someone@example.com", ["Subject"] = "s", ["Body"] = "b" },
            new Content { Id = Guid.NewGuid(), ContentType = "article" },
            CancellationToken.None);

        email.Tenants.Should().Equal("club-a");
    }

    // ---- PWA installs: stored globally, each device tagged with the tenant it reported from. ----

    [Fact]
    public async Task A_tenant_admin_sees_only_its_own_tenants_app_installs()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var future = DateTime.UtcNow.AddDays(3650);
        var inA = new PwaInstall { DeviceId = Unique("dev"), Tenant = tenantA, LastSeenAt = future };
        var inB = new PwaInstall { DeviceId = Unique("dev"), Tenant = tenantB, LastSeenAt = future };
        await StoreAsync(inA, inB);

        var mine = await ItemsAsync(await admin.GetAsync("/api/pwa/installs?pageSize=100"));
        mine.Should().NotBeEmpty();
        mine.Should().OnlyContain(i => i.GetProperty("tenant").GetString() == tenantA);

        var all = await ItemsAsync(await (await GlobalAsync("SuperAdmin")).GetAsync("/api/pwa/installs?pageSize=100"));
        all.Select(i => i.GetProperty("tenant").GetString()).Should().Contain([tenantA, tenantB]);
    }

    // ---- A global role reaches every tenant, whatever tenant the request resolved to. ----

    // ---- Email events follow the tenant that sent the email, not the recipient's memberships. ----

    // ---- Tenants are compared lowercased, so a report's casing does not hide it. ----

    [Fact]
    public async Task A_client_error_reported_with_a_mixed_case_tenant_header_reaches_that_tenant()
    {
        var tenantA = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var message = Unique("mixed");
        var reporter = _factory.CreateClient();
        reporter.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        reporter.DefaultRequestHeaders.Add("X-Tenant", tenantA.ToUpperInvariant());

        (await reporter.PostAsJsonAsync("/api/client-errors", new { items = new[] { new { message } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ItemsAsync(await admin.GetAsync($"/api/client-errors?q={message}"))).Should().ContainSingle();
    }

    [Fact]
    public async Task A_client_error_naming_another_tenant_in_its_body_is_stored_under_the_callers_tenant()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (adminA, _) = await TenantAdminAsync(tenantA);
        var (adminB, _) = await TenantAdminAsync(tenantB);
        var message = Unique("named");
        var reporter = _factory.CreateClient();
        reporter.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        reporter.DefaultRequestHeaders.Add("X-Tenant", tenantA);

        (await reporter.PostAsJsonAsync("/api/client-errors",
                new { items = new[] { new { message, tenant = tenantB } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ItemsAsync(await adminA.GetAsync($"/api/client-errors?q={message}"))).Should().ContainSingle();
        (await ItemsAsync(await adminB.GetAsync($"/api/client-errors?q={message}"))).Should().BeEmpty();
    }

    [Fact]
    public async Task An_install_reported_with_a_mixed_case_tenant_header_reaches_that_tenant()
    {
        var tenantA = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var reporter = _factory.CreateClient();
        reporter.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        reporter.DefaultRequestHeaders.Add("X-Tenant", tenantA.ToUpperInvariant());

        (await reporter.PostAsJsonAsync("/api/pwa/report", new { deviceId = Unique("dev") }))
            .IsSuccessStatusCode.Should().BeTrue();

        (await ItemsAsync(await admin.GetAsync("/api/pwa/installs?pageSize=100"))).Should().NotBeEmpty();
    }

    // ---- Analytics: one Umami account for the whole deployment. ----

    private static IEnumerable<Func<HttpClient, Task<HttpResponseMessage>>> AnalyticsCalls() =>
    [
        c => c.GetAsync("/api/analytics/websites"),
        c => c.GetAsync("/api/analytics/site-1/summary"),
        c => c.GetAsync("/api/analytics/site-1/series"),
        c => c.GetAsync("/api/analytics/site-1/metric?type=url"),
        c => c.GetAsync("/api/analytics/site-1/status"),
        c => c.PostAsJsonAsync("/api/analytics/websites", new { name = "Someone", domain = "someone.example.com" }),
    ];

    [Fact]
    public async Task A_tenant_admin_cannot_reach_the_deployments_analytics_account()
    {
        var tenantA = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);

        var codes = new List<HttpStatusCode>();
        foreach (var call in AnalyticsCalls()) codes.Add((await call(admin)).StatusCode);

        codes.Should().HaveCount(6);
        codes.Should().OnlyContain(c => c == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_granted_globally_still_reaches_analytics()
    {
        var admin = await GlobalAsync("Admin");

        var codes = new List<HttpStatusCode>();
        foreach (var call in AnalyticsCalls()) codes.Add((await call(admin)).StatusCode);

        codes.Should().HaveCount(6);
        codes.Should().NotContain(HttpStatusCode.Forbidden);
    }
}
