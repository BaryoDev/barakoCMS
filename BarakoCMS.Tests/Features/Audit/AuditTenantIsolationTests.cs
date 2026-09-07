using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// AuditEvent is SingleTenanted (one global table), so the conjoined session gives GET /api/audit no
/// tenant isolation on its own. A tenant admin must see only its own tenant's trail; only a
/// SuperAdmin, whose reach is global, may read across tenants with ?tenant=. Without the endpoint's
/// own filter, any tenant admin reads every tenant's audit log by leaving ?tenant unset or by
/// naming a victim tenant.
/// </summary>
[Collection("Sequential")]
public class AuditTenantIsolationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public AuditTenantIsolationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync(params AuditEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        foreach (var e in events) session.Store(e);
        await session.SaveChangesAsync();
    }

    private async Task<barakoCMS.Models.PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>>
        ListAsync(string role, string query)
    {
        var token = await _factory.StoredUserTokenAsync(role);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync($"/api/audit{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<
            barakoCMS.Models.PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>>())!;
    }

    [Fact]
    public async Task Tenant_admin_cannot_read_another_tenants_audit_via_tenant_param()
    {
        // A stored "Admin" caller holds view_audit_log but not SuperAdmin, and resolves to the
        // default tenant. The victim events live in another tenant under a unique action.
        var otherTenant = $"victim_{Guid.NewGuid():N}";
        var victimAction = $"test.iso.other.{Guid.NewGuid():N}";
        await SeedAsync(new AuditEvent
        {
            TenantSlug = otherTenant, Action = victimAction, CreatedAt = DateTime.UtcNow
        });

        // Admin explicitly asks for the victim tenant; the endpoint must ignore that and scope to
        // the caller's own (default) tenant, so the victim action is not returned.
        var result = await ListAsync("Admin", $"?tenant={otherTenant}&action={victimAction}");

        result.Items.Should().BeEmpty("a tenant admin's ?tenant is not honoured across tenants");
    }

    [Fact]
    public async Task Tenant_admin_still_sees_its_own_tenants_audit()
    {
        var mineAction = $"test.iso.mine.{Guid.NewGuid():N}";
        await SeedAsync(new AuditEvent
        {
            TenantSlug = Tenant.DefaultSlug, Action = mineAction, CreatedAt = DateTime.UtcNow
        });

        var result = await ListAsync("Admin", $"?action={mineAction}");

        result.Items.Should().ContainSingle().Which.Action.Should().Be(mineAction);
    }

    [Fact]
    public async Task SuperAdmin_may_read_across_tenants()
    {
        var otherTenant = $"other_{Guid.NewGuid():N}";
        var action = $"test.iso.super.{Guid.NewGuid():N}";
        await SeedAsync(new AuditEvent
        {
            TenantSlug = otherTenant, Action = action, CreatedAt = DateTime.UtcNow
        });

        var result = await ListAsync("SuperAdmin", $"?tenant={otherTenant}&action={action}");

        result.Items.Should().ContainSingle().Which.Action.Should().Be(action);
    }
}
