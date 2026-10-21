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
        siteSettings = new Dictionary<string, object> { ["site"] = new { status = "Published", data = new Dictionary<string, object> { ["Name"] = "Shop", ["Tagline"] = tagline } } },
    };

    [Fact]
    public async Task A_planned_site_change_writes_no_event_and_wakes_no_listener_and_an_apply_does()
    {
        var tenant = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(tenant, DeliveredSite());
        var client = await _callers.SuperAdminAsync(tenant);
        (await client.PostAsJsonAsync("/api/contents", new { contentType = "site", status = "Published", data = new Dictionary<string, object> { ["Name"] = "Shop", ["Tagline"] = "Before" } }, Ct))
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
        // The rule goes on a plain role of the tenant the caller holds through the membership, so only
        // the staged rule can let the caller create the entry.
        var writerId = await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles, SystemCapabilities.ManageContentTypes);
        var roleId = await _callers.TenantRoleAsync(tenant);
        var roleName = (await _callers.InTenantAsync(null, s => s.LoadAsync<Role>(roleId, Ct)))!.Name;
        var client = await _callers.ClientAsync(tenant, [writerId], memberRoles: [roleId]);

        var (planned, plan) = await PlanAsync(client, new
        {
            version = 1,
            permissionRules = new Dictionary<string, object> { [$"{roleName}/site"] = new { create = new { enabled = true } } },
            siteSettings = new Dictionary<string, object> { ["site"] = new { status = "Published", data = new Dictionary<string, object> { ["Name"] = "Shop" } } },
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
        await _callers.StoreInTenantAsync(null, new Role { Id = roleId, Name = role, TenantSlug = tenant });
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
    /// <summary>The real runner, with another write committed after the work and before the commit.</summary>
    private sealed class InterleavingRunner(barakoCMS.Infrastructure.Services.IContentBatchRunner inner, Func<Task> between)
        : barakoCMS.Infrastructure.Services.IContentBatchRunner
    {
        public Task<T> RunAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<barakoCMS.Infrastructure.Services.ContentBatchOutcome<T>>> work, CancellationToken ct) =>
            inner.RunAsync(async (scope, token) =>
            {
                var outcome = await work(scope, token);
                await between();
                return outcome;
            }, ct);
    }

    private sealed class CountingPermissions(barakoCMS.Infrastructure.Services.IPermissionResolver inner)
        : barakoCMS.Infrastructure.Services.IPermissionResolver
    {
        public int Invalidated { get; private set; }

        public Task<bool> CanPerformActionAsync(User user, string contentTypeSlug, string action, Content? content = null, CancellationToken cancellationToken = default) =>
            inner.CanPerformActionAsync(user, contentTypeSlug, action, content, cancellationToken);

        public Task<bool> HasCapabilityAsync(Guid userId, string capability, CancellationToken cancellationToken = default) =>
            inner.HasCapabilityAsync(userId, capability, cancellationToken);

        public void InvalidateUserPermissions(Guid userId) => inner.InvalidateUserPermissions(userId);

        public void InvalidateAllPermissions()
        {
            Invalidated++;
            inner.InvalidateAllPermissions();
        }
    }

    [Fact]
    public async Task A_write_that_lands_between_staging_and_commit_answers_409_commits_nothing_and_is_recorded()
    {
        var tenant = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(tenant, DeliveredSite());
        using (var sourcing = _fixture.Services.CreateScope())
        {
            // Event sourced, so the entry's stream version is what the commit is bound to. A document
            // type is last write wins, on the route as here.
            sourcing.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>().Slug = tenant;
            await sourcing.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentSourcingPolicy>().DecideAsync("site", true, Ct);
            await sourcing.ServiceProvider.GetRequiredService<IDocumentSession>().SaveChangesAsync(Ct);
        }

        var client = await _callers.SuperAdminAsync(tenant);
        (await client.PostAsJsonAsync("/api/contents", new { contentType = "site", status = "Published", data = new Dictionary<string, object> { ["Name"] = "Shop", ["Tagline"] = "Before" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = (await _callers.InTenantAsync(tenant, s => s.Query<Content>().SingleAsync(c => c.ContentType == "site", Ct)))!.Id;

        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>().Slug = tenant;
        var permissions = new CountingPermissions(services.GetRequiredService<barakoCMS.Infrastructure.Services.IPermissionResolver>());
        var runner = new InterleavingRunner(services.GetRequiredService<barakoCMS.Infrastructure.Services.IContentBatchRunner>(), async () =>
        {
            var version = await StreamVersionAsync(tenant, entryId);
            (await client.PutAsJsonAsync($"/api/contents/{entryId}", new { data = new Dictionary<string, object> { ["Name"] = "Shop", ["Tagline"] = "Concurrent" }, version }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK, "the other write commits while the apply holds its transaction open");
        });
        var engine = new barakoCMS.Features.Config.ConfigEngine(
            services.GetServices<barakoCMS.Core.Interfaces.IConfigProvider>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.ICallerCapabilities>(),
            runner,
            services.GetRequiredService<IDocumentSession>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Multitenancy.TenantContext>(),
            permissions,
            services.GetRequiredService<barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache>());
        var userId = (await _callers.InTenantAsync(null, s => s.Query<Membership>().SingleAsync(m => m.TenantSlug == tenant, Ct)))!.UserId;
        var caller = barakoCMS.Core.Interfaces.ConfigCaller.ForUser(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("UserId", userId.ToString()), new System.Security.Claims.Claim("Username", $"cfg-{userId:n}"),
             new System.Security.Claims.Claim("tenant", tenant)], "test")));
        // The site entry is written the way the route writes it, which reads the request.
        services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext { User = caller.Principal!, RequestServices = services };

        var result = await engine.RunAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(SiteDocument("From the file")), caller, apply: true, allowDestructive: false, Ct);

        result.StatusCode.Should().Be(409, System.Text.Json.JsonSerializer.Serialize(result.Response));
        result.Response.Applied.Should().BeFalse();
        result.Response.Errors.Should().ContainSingle(e => e.Contains("changed by another write"));
        var site = await _callers.InTenantAsync(tenant, s => s.LoadAsync<Content>(entryId, Ct));
        site!.Data["Tagline"].ToString().Should().Be("Concurrent", "the apply committed nothing");
        permissions.Invalidated.Should().BeGreaterThan(0, "a decision cached from what was staged does not outlive the failed commit");
        (await _callers.InTenantAsync(null, s => s.Query<AuditEvent>().AnyAsync(e => e.TenantSlug == tenant && e.Action == "config.refused", Ct)))
            .Should().BeTrue("the refused apply is in the log");
    }
    [Fact]
    public async Task An_apply_touching_two_roles_keeps_the_audit_chain_unbroken()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("chain");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var first = Unique("Chain One ");
        var second = Unique("Chain Two ");
        await _callers.StoreInTenantAsync(null,
            new Role { Id = Guid.NewGuid(), Name = first, TenantSlug = tenant }, new Role { Id = Guid.NewGuid(), Name = second, TenantSlug = tenant });
        var client = await _callers.SuperAdminAsync(tenant);

        var (applied, body) = await ApplyAsync(client, new
        {
            version = 1,
            permissionRules = new Dictionary<string, object>
            {
                [$"{first}/{type}"] = new { read = new { enabled = true } },
                [$"{second}/{type}"] = new { read = new { enabled = true } },
            },
        });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var chain = (await _callers.InTenantAsync(null, s => s.Query<AuditEvent>().Where(e => e.TenantSlug == tenant).ToListAsync(Ct)))!
            .OrderBy(e => e.CreatedAt).ToList();
        chain.Count(e => e.Action == "role.updated").Should().Be(2, "one entry for each role the apply changed");
        chain[0].PrevHash.Should().Be(barakoCMS.Infrastructure.Audit.AuditChain.GenesisHash);
        for (var i = 1; i < chain.Count; i++)
        {
            chain[i].PrevHash.Should().Be(chain[i - 1].Hash, $"entry {i} ({chain[i].Action}) follows entry {i - 1} ({chain[i - 1].Action})");
        }
    }
}
