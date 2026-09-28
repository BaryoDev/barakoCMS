using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using BarakoCMS.Diagnostics.Features.Report;
using BarakoCMS.Email.Resend;
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
[Collection("Sequential")]
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
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
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

    [Fact]
    public async Task A_tenant_admin_sees_only_email_events_for_its_own_members()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var (admin, _) = await TenantAdminAsync(tenantA);
        var (_, memberOfA) = await MemberAsync(tenantA, SystemRoles.UserRoleId);
        var (_, memberOfB) = await MemberAsync(tenantB, SystemRoles.UserRoleId);
        var type = Unique("bounced");
        var at = DateTime.UtcNow.AddDays(30);
        await StoreAsync(
            new EmailEvent { Email = memberOfA, Type = type, Reason = "a", At = at },
            new EmailEvent { Email = memberOfB, Type = type, Reason = "b", At = at },
            new EmailEvent { Email = $"{Unique("nobody")}@example.com", Type = type, Reason = "none", At = at });

        var mine = await ItemsAsync(await admin.GetAsync($"/api/email-events?type={type}"));
        mine.Should().ContainSingle()
            .Which.GetProperty("email").GetString().Should().Be(memberOfA);

        var all = await ItemsAsync(await (await GlobalAsync("SuperAdmin")).GetAsync($"/api/email-events?type={type}"));
        all.Should().HaveCount(3, "a SuperAdmin keeps the deployment-wide view");
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
}
