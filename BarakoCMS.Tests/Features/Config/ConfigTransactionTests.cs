using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// What a plan or a refused apply leaves behind outside the transaction it rolls back: no event, no
/// listener woken, no cached permission, and the audit entries a committed apply writes (#921).
/// </summary>
[Collection("Sequential")]
public class ConfigTransactionTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public ConfigTransactionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static ContentTypeDefinition DeliveredSite()
    {
        var site = Type("site", ("Name", "string"), ("Tagline", "string"));
        site.IsSingleton = true;
        site.IsPubliclyDeliverable = true;
        return site;
    }

    private static object SiteDocument(string tagline) => new
    {
        version = 1,
        siteSettings = new Dictionary<string, object> { ["site"] = new { status = "Published", data = new Dictionary<string, object> { ["Name"] = "Clinic", ["Tagline"] = tagline } } },
    };

    [Fact]
    public async Task A_planned_site_change_writes_no_event_and_wakes_no_listener_and_an_apply_does()
    {
        var tenant = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(tenant, DeliveredSite());
        var client = await _callers.SuperAdminAsync(tenant);
        (await client.PostAsJsonAsync("/api/contents", new { contentType = "site", status = "Published", data = new Dictionary<string, object> { ["Name"] = "Clinic", ["Tagline"] = "Before" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = (await _callers.InTenantAsync(tenant, s => s.Query<Content>().SingleAsync(c => c.ContentType == "site", Ct)))!.Id;
        var versionBefore = await StreamVersionAsync(tenant, entryId);

        var broadcaster = _fixture.Services.GetRequiredService<barakoCMS.Features.Public.Events.ContentChangeBroadcaster>();
        using var subscription = broadcaster.TrySubscribe(tenant, ["site"], $"cfg-{Guid.NewGuid():n}", 1000, 0, out _);
        subscription.Should().NotBeNull();

        var (planned, plan) = await PlanAsync(client, SiteDocument("Planned"));
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Equal("siteSettings/site change");
        (await StreamVersionAsync(tenant, entryId)).Should().Be(versionBefore, "a plan appends no event");
        subscription!.Reader.TryRead(out _).Should().BeFalse("nothing a plan staged is announced");

        var (applied, body) = await ApplyAsync(client, SiteDocument("Applied"));
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StreamVersionAsync(tenant, entryId)).Should().Be(versionBefore + 1, "the apply appends one event, at the commit");
        subscription.Reader.TryRead(out _).Should().BeTrue("the control: a committed change is announced");
    }

    private async Task<long> StreamVersionAsync(string tenant, Guid id) =>
        await _callers.InTenantAsync(tenant, async s => (await s.Events.FetchStreamStateAsync(id, Ct))?.Version ?? 0);

    [Fact]
    public async Task A_plan_leaves_no_permission_decision_cached_from_what_it_staged()
    {
        var tenant = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(tenant, DeliveredSite());
        // Writing a rule takes manage_roles held globally, and the rule goes on a plain role the caller
        // holds through the membership, since a role carrying manage_roles takes a SuperAdmin to change.
        var writerId = await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles, SystemCapabilities.ManageContentTypes);
        var roleId = await _callers.RoleAsync();
        var roleName = (await _callers.InTenantAsync(null, s => s.LoadAsync<Role>(roleId, Ct)))!.Name;
        var client = await _callers.ClientAsync(tenant, [writerId], memberRoles: [roleId]);

        var (planned, plan) = await PlanAsync(client, new
        {
            version = 1,
            permissionRules = new Dictionary<string, object> { [$"{roleName}/site"] = new { create = new { enabled = true } } },
            siteSettings = new Dictionary<string, object> { ["site"] = new { status = "Published", data = new Dictionary<string, object> { ["Name"] = "Clinic" } } },
        });
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        plan.GetProperty("sections")[1].GetProperty("refusals").GetArrayLength()
            .Should().Be(0, "inside the plan the staged rule lets the caller create the entry. " + plan);

        var user = await _callers.InTenantAsync(null, s => s.Query<User>().SingleAsync(u => u.RoleIds.Contains(writerId), Ct));
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>().Slug = tenant;
        var permissions = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IPermissionResolver>();

        (await permissions.CanPerformActionAsync(user!, "site", "create", null, Ct))
            .Should().BeFalse("the rule was never stored, so no answer cached while it was staged may outlive the plan");
    }

    [Fact]
    public async Task A_committed_apply_writes_the_routes_audit_entries_and_a_refused_one_is_recorded()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("record");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string"), ("Notes", "text")));
        var role = Unique("Audited ");
        var roleId = Guid.NewGuid();
        await _callers.StoreInTenantAsync(null, new Role { Id = roleId, Name = role });
        var client = await _callers.SuperAdminAsync(tenant);
        var delivered = Type(type, ("Title", "string"), ("Notes", "text"));
        delivered.IsPubliclyDeliverable = true;

        var (applied, body) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [type] = delivered },
            permissionRules = new Dictionary<string, object> { [$"{role}/{type}"] = new { read = new { enabled = true } } },
        });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var audit = await _callers.InTenantAsync(null, s => s.Query<AuditEvent>().Where(e => e.TenantSlug == tenant).ToListAsync(Ct));
        audit.Should().NotBeEmpty();
        audit.Should().Contain(e => e.Action == "role.updated" && e.TargetId == roleId.ToString());
        audit.Should().Contain(e => e.Action == "contenttype.publicdelivery.enabled");

        var (refused, refusedBody) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [type] = Type(type, ("Title", "string")) },
        });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, refusedBody.ToString());
        (await _callers.InTenantAsync(null, s => s.Query<AuditEvent>().AnyAsync(e => e.TenantSlug == tenant && e.Action == "config.refused", Ct)))
            .Should().BeTrue("an apply that wrote nothing is in the log too");
    }
}
