using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Overview;

/// <summary>
/// <c>GET /api/overview</c>. Every test works in a tenant of its own, so no other test's runs,
/// sign-ins or entries count, and a caller sees only what its capabilities open.
/// </summary>
[Collection("Integration1")]
public class OverviewTests
{
    private const string Route = "/api/overview";

    private readonly IntegrationTestFixture _fixture;
    private static int _ip;

    public OverviewTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private sealed class FakeBounces : IMailBounceSource
    {
        private readonly ConcurrentDictionary<string, List<(DateTimeOffset At, bool Resolved)>> _byTenant = new();

        public void Add(string tenant, DateTimeOffset at, int count = 1)
        {
            var list = _byTenant.GetOrAdd(tenant, _ => []);
            lock (list)
            {
                for (var i = 0; i < count; i++) list.Add((at, false));
            }
        }

        public Task<int> CountAsync(string tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(Snapshot(tenant).Count(b => b.At >= from && b.At < to));

        public Task<int> CountUnresolvedAsync(string tenant, CancellationToken ct = default) =>
            Task.FromResult(Snapshot(tenant).Count(b => !b.Resolved));

        public Task<int> ResolveAllAsync(string tenant, CancellationToken ct = default)
        {
            var list = _byTenant.GetOrAdd(tenant, _ => []);
            lock (list)
            {
                var open = list.Count(b => !b.Resolved);
                for (var i = 0; i < list.Count; i++) list[i] = (list[i].At, true);
                return Task.FromResult(open);
            }
        }

        private List<(DateTimeOffset At, bool Resolved)> Snapshot(string tenant)
        {
            var list = _byTenant.GetOrAdd(tenant, _ => []);
            lock (list) return [.. list];
        }
    }

    private Func<HttpClient> HostWith(FakeBounces bounces)
    {
        var host = _fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IMailBounceSource>(bounces)));
        return host.CreateClient;
    }

    private static string NewTenant() => "overview-" + Guid.NewGuid().ToString("N")[..16];

    private async Task StoreAsync(string tenant, params object[] documents)
    {
        await using var session = Store.LightweightSession(tenant);
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> TenantAsync()
    {
        var slug = NewTenant();
        await using var session = Store.LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    /// <summary>A member of the tenant whose one role holds exactly these capabilities, and may read the type if one is named.</summary>
    private async Task<(HttpClient Client, Guid UserId)> CallerAsync(
        string tenant, string[] capabilities, string? readableType = null, Func<HttpClient>? create = null)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "overview-" + Guid.NewGuid().ToString("N"),
            TenantSlug = tenant,
            SystemCapabilities = [.. capabilities],
            Permissions = readableType is null
                ? []
                : [new ContentTypePermission { ContentTypeSlug = readableType, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "ov-" + Guid.NewGuid().ToString("N")[..12],
            Email = $"{Guid.NewGuid():N}@example.com",
        };
        await using (var session = Store.LightweightSession())
        {
            session.Store(role);
            session.Store(user);
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = tenant,
                Status = MembershipStatus.Active, RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = (create ?? _fixture.CreateClient)();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: [role.Name],
                userId: user.Id.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant, ["Username"] = user.Username }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}");
        return (client, user.Id);
    }

    private async Task SeedRunsAsync(string tenant, RunStatus status, int count, TimeSpan age)
    {
        var workflow = Guid.NewGuid();
        var runs = Enumerable.Range(0, count).Select(i => new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow,
            WorkflowName = "ov",
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow - age - TimeSpan.FromSeconds(i),
        });
        await StoreAsync(tenant, [.. runs]);
    }

    private async Task SeedSignInsAsync(string tenant, int count, TimeSpan age)
    {
        var events = Enumerable.Range(0, count).Select(i => new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantSlug = tenant,
            Action = "auth.login.succeeded",
            CreatedAt = DateTime.UtcNow - age - TimeSpan.FromSeconds(i),
        });
        await using var session = Store.LightweightSession();
        foreach (var e in events) session.Store(e);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<(string Type, Guid Id)> SeedScheduledAsync(string tenant, TimeSpan fromNow, string? type = null)
    {
        type ??= "ovtype_" + Guid.NewGuid().ToString("N")[..10];
        var id = Guid.NewGuid();
        await StoreAsync(tenant,
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public }],
            },
            ScheduledEntry(id, type, fromNow));
        return (type, id);
    }

    private static barakoCMS.Models.Content ScheduledEntry(Guid id, string type, TimeSpan fromNow) => new()
    {
        Id = id,
        ContentType = type,
        Status = ContentStatus.Scheduled,
        ScheduledPublishAt = DateTime.UtcNow + fromNow,
        Data = new Dictionary<string, object> { ["Title"] = "x" },
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static async Task<JsonElement> ReadAsync(HttpClient client)
    {
        var response = await client.GetAsync(Route, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static bool Absent(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static string[] ItemIds(JsonElement overview) =>
        overview.GetProperty("needsYou").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

    private static JsonElement StripItem(JsonElement overview, string name) =>
        overview.GetProperty("health").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == name);

    // ---- counts and the prior period ----

    [Fact]
    public async Task Counts_give_this_period_and_the_one_before()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 3, TimeSpan.FromHours(1));
        await SeedRunsAsync(tenant, RunStatus.PartiallyFailed, 1, TimeSpan.FromHours(2));
        await SeedRunsAsync(tenant, RunStatus.Failed, 2, TimeSpan.FromHours(30));
        await SeedRunsAsync(tenant, RunStatus.Failed, 5, TimeSpan.FromHours(60));
        await SeedSignInsAsync(tenant, 4, TimeSpan.FromHours(1));
        await SeedSignInsAsync(tenant, 7, TimeSpan.FromHours(30));
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1), 2);
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-30), 1);
        await SeedScheduledAsync(tenant, TimeSpan.FromHours(3));
        var (client, _) = await CallerAsync(
            tenant, [SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ViewAuditLog, SystemCapabilities.ManageEmailSettings],
            create: HostWith(bounces));

        var counts = (await ReadAsync(client)).GetProperty("counts");

        counts.GetProperty("failedRuns").GetProperty("current").GetInt32().Should().Be(4);
        counts.GetProperty("failedRuns").GetProperty("prior").GetInt32().Should().Be(2);
        counts.GetProperty("signIns").GetProperty("current").GetInt32().Should().Be(4);
        counts.GetProperty("signIns").GetProperty("prior").GetInt32().Should().Be(7);
        counts.GetProperty("bounces").GetProperty("current").GetInt32().Should().Be(2);
        counts.GetProperty("bounces").GetProperty("prior").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Publishes_due_counts_scheduled_entries_in_the_next_day_only()
    {
        var tenant = await TenantAsync();
        var (type, _) = await SeedScheduledAsync(tenant, TimeSpan.FromHours(3));
        await StoreAsync(tenant,
            ScheduledEntry(Guid.NewGuid(), type, TimeSpan.FromHours(5)),
            ScheduledEntry(Guid.NewGuid(), type, TimeSpan.FromDays(4)));
        var (client, _) = await CallerAsync(tenant, [], type);

        var due = (await ReadAsync(client)).GetProperty("counts").GetProperty("publishesDue");

        due.GetProperty("current").GetInt32().Should().Be(2);
        Absent(due, "prior").Should().BeTrue("a publish clears its time when it runs, so there is no earlier period");
    }

    // ---- isolation ----

    [Fact]
    public async Task A_tenant_sees_only_its_own_counts()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        await SeedRunsAsync(mine, RunStatus.Failed, 2, TimeSpan.FromHours(1));
        await SeedRunsAsync(theirs, RunStatus.Failed, 9, TimeSpan.FromHours(1));
        await SeedSignInsAsync(mine, 1, TimeSpan.FromHours(1));
        await SeedSignInsAsync(theirs, 6, TimeSpan.FromHours(1));
        var bounces = new FakeBounces();
        bounces.Add(mine, DateTimeOffset.UtcNow.AddHours(-1), 1);
        bounces.Add(theirs, DateTimeOffset.UtcNow.AddHours(-1), 8);
        var (type, _) = await SeedScheduledAsync(mine, TimeSpan.FromHours(2));
        await SeedScheduledAsync(theirs, TimeSpan.FromHours(2), type);
        await StoreAsync(theirs, ScheduledEntry(Guid.NewGuid(), type, TimeSpan.FromHours(2)));
        var (client, _) = await CallerAsync(
            mine,
            [SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ViewAuditLog, SystemCapabilities.ManageEmailSettings],
            type, HostWith(bounces));

        var counts = (await ReadAsync(client)).GetProperty("counts");

        counts.GetProperty("failedRuns").GetProperty("current").GetInt32().Should().Be(2);
        counts.GetProperty("signIns").GetProperty("current").GetInt32().Should().Be(1);
        counts.GetProperty("bounces").GetProperty("current").GetInt32().Should().Be(1);
        counts.GetProperty("publishesDue").GetProperty("current").GetInt32().Should().Be(1);
    }

    // ---- capabilities: each part is left out for a caller without what reads it ----

    [Fact]
    public async Task A_caller_with_no_capability_gets_no_part_and_an_ok_verdict()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10, TimeSpan.FromHours(1));
        await SeedScheduledAsync(tenant, TimeSpan.FromHours(-2));
        var (client, _) = await CallerAsync(tenant, []);

        var overview = await ReadAsync(client);

        overview.GetProperty("verdict").GetString().Should().Be("ok");
        overview.GetProperty("headline").GetString().Should().Be("All systems normal");
        Absent(overview, "workflows").Should().BeTrue();
        Absent(overview, "health").Should().BeTrue();
        var counts = overview.GetProperty("counts");
        foreach (var name in new[] { "publishesDue", "failedRuns", "bounces", "signIns" })
        {
            Absent(counts, name).Should().BeTrue(name);
        }

        overview.GetProperty("needsYou").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_workflow_part_needs_view_workflow_runs()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 4, TimeSpan.FromHours(1));
        var (without, _) = await CallerAsync(tenant, [SystemCapabilities.ViewAuditLog]);
        var (with, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);

        var denied = await ReadAsync(without);
        var allowed = await ReadAsync(with);

        Absent(denied, "workflows").Should().BeTrue();
        Absent(denied.GetProperty("counts"), "failedRuns").Should().BeTrue();
        ItemIds(denied).Should().BeEmpty();
        allowed.GetProperty("workflows").GetProperty("runs24h").GetInt32().Should().Be(4);
        allowed.GetProperty("counts").GetProperty("failedRuns").GetProperty("current").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Content_counts_need_a_read_rule()
    {
        var tenant = await TenantAsync();
        var (type, _) = await SeedScheduledAsync(tenant, TimeSpan.FromHours(-2));
        var (without, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);
        var (with, _) = await CallerAsync(tenant, [], type);

        var denied = await ReadAsync(without);
        var allowed = await ReadAsync(with);

        Absent(denied.GetProperty("counts"), "publishesDue").Should().BeTrue();
        ItemIds(denied).Should().NotContain("content.overdue");
        allowed.GetProperty("counts").GetProperty("publishesDue").GetProperty("current").GetInt32().Should().Be(1);
        ItemIds(allowed).Should().Contain("content.overdue");
    }

    [Fact]
    public async Task Sign_ins_need_view_audit_log()
    {
        var tenant = await TenantAsync();
        await SeedSignInsAsync(tenant, 3, TimeSpan.FromHours(1));
        var (without, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);
        var (with, _) = await CallerAsync(tenant, [SystemCapabilities.ViewAuditLog]);

        Absent((await ReadAsync(without)).GetProperty("counts"), "signIns").Should().BeTrue();
        (await ReadAsync(with)).GetProperty("counts").GetProperty("signIns").GetProperty("current").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Bounces_need_manage_email_settings_and_a_module_that_reports_them()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1), 2);
        var (without, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns], create: HostWith(bounces));
        var (with, _) = await CallerAsync(tenant, [SystemCapabilities.ManageEmailSettings], create: HostWith(bounces));
        var (noModule, _) = await CallerAsync(tenant, [SystemCapabilities.ManageEmailSettings]);

        var denied = await ReadAsync(without);
        var allowed = await ReadAsync(with);
        var none = await ReadAsync(noModule);

        Absent(denied.GetProperty("counts"), "bounces").Should().BeTrue();
        ItemIds(denied).Should().NotContain("mail.bounces");
        allowed.GetProperty("counts").GetProperty("bounces").GetProperty("current").GetInt32().Should().Be(2);
        ItemIds(allowed).Should().Contain("mail.bounces");
        Absent(none.GetProperty("counts"), "bounces").Should().BeTrue("with no email module reporting bounces there is nothing to count");
        ItemIds(none).Should().BeEmpty();
    }

    [Fact]
    public async Task The_health_strip_needs_view_monitoring_and_names_the_cells()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        var (without, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);
        var (with, _) = await CallerAsync(tenant, [SystemCapabilities.ViewMonitoring], create: HostWith(bounces));
        var (noModule, _) = await CallerAsync(tenant, [SystemCapabilities.ViewMonitoring]);

        var denied = await ReadAsync(without);
        var allowed = await ReadAsync(with);
        var bare = await ReadAsync(noModule);

        Absent(denied, "health").Should().BeTrue();
        var names = allowed.GetProperty("health").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToArray();
        names.Should().HaveCountGreaterThanOrEqualTo(4);
        names.Should().Contain(["API", "Database", "Runner", "Mail"]);
        StripItem(allowed, "API").GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        StripItem(allowed, "Database").GetProperty("ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        StripItem(allowed, "Mail").GetProperty("status").GetString().Should().Be("ok");
        bare.GetProperty("health").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).Should().NotContain("Mail", "Mail is omitted, not reported ok, with no module");
    }

    // ---- each rule flips the verdict or an item on and off ----

    [Fact]
    public async Task Failing_workflow_runs_turn_the_item_and_the_verdict_on_and_recovery_turns_them_off()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Succeeded, 19, TimeSpan.FromMinutes(10));
        await SeedRunsAsync(tenant, RunStatus.Failed, 1, TimeSpan.FromMinutes(10));
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);

        var healthy = await ReadAsync(client);
        healthy.GetProperty("verdict").GetString().Should().Be("ok");
        ItemIds(healthy).Should().NotContain("workflows.failing");

        await SeedRunsAsync(tenant, RunStatus.Failed, 19, TimeSpan.FromMinutes(5));
        var failing = await ReadAsync(client);

        failing.GetProperty("verdict").GetString().Should().Be("bad");
        failing.GetProperty("headline").GetString().Should().Be("1 problem");
        var item = failing.GetProperty("needsYou").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "workflows.failing");
        item.GetProperty("severity").GetString().Should().Be("bad");
        item.GetProperty("ref").GetProperty("kind").GetString().Should().Be("workflowRuns");
        item.GetProperty("resolvable").GetBoolean().Should().BeFalse();
        failing.GetProperty("workflows").GetProperty("verdict").GetString().Should().Be("bad");
    }

    [Fact]
    public async Task An_overdue_publish_turns_the_item_on_and_publishing_it_turns_it_off()
    {
        var tenant = await TenantAsync();
        var (type, id) = await SeedScheduledAsync(tenant, TimeSpan.FromMinutes(-30));
        var (client, _) = await CallerAsync(tenant, [], type);

        var overdue = await ReadAsync(client);
        overdue.GetProperty("verdict").GetString().Should().Be("warn");
        overdue.GetProperty("headline").GetString().Should().Be("1 warning");
        ItemIds(overdue).Should().Equal("content.overdue");

        await using (var session = Store.LightweightSession(tenant))
        {
            var entry = (await session.LoadAsync<barakoCMS.Models.Content>(id, Ct))!;
            entry.Status = ContentStatus.Published;
            entry.ScheduledPublishAt = null;
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        var done = await ReadAsync(client);
        done.GetProperty("verdict").GetString().Should().Be("ok");
        ItemIds(done).Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_inside_the_grace_is_not_overdue()
    {
        var tenant = await TenantAsync();
        var (type, _) = await SeedScheduledAsync(tenant, TimeSpan.FromMinutes(-1));
        var (client, _) = await CallerAsync(tenant, [], type);

        var overview = await ReadAsync(client);

        ItemIds(overview).Should().BeEmpty();
        overview.GetProperty("counts").GetProperty("publishesDue").GetProperty("current").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Resolving_the_bounce_item_moves_mail_to_delivering_and_the_verdict_to_ok_in_the_next_response()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1), 3);
        var create = HostWith(bounces);
        var (client, _) = await CallerAsync(
            tenant, [SystemCapabilities.ManageEmailSettings, SystemCapabilities.ViewMonitoring], create: create);

        var before = await ReadAsync(client);
        StripItem(before, "Mail").GetProperty("status").GetString().Should().Be("warn");
        ItemIds(before).Should().Contain("mail.bounces");
        before.GetProperty("needsYou").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "mail.bounces")
            .GetProperty("resolvable").GetBoolean().Should().BeTrue();
        before.GetProperty("headline").GetString().Should().Contain("warning");

        var resolve = await client.PostAsync("/api/overview/items/mail.bounces/resolve", null, Ct);
        resolve.StatusCode.Should().Be(HttpStatusCode.OK, await resolve.Content.ReadAsStringAsync(Ct));

        var after = await ReadAsync(client);
        StripItem(after, "Mail").GetProperty("status").GetString().Should().Be("ok");
        StripItem(after, "Mail").GetProperty("detail").GetString().Should().Be("Delivering.");
        ItemIds(after).Should().NotContain("mail.bounces");
        after.GetProperty("counts").GetProperty("bounces").GetProperty("current").GetInt32().Should().Be(3, "resolved bounces still happened");
    }

    [Fact]
    public async Task Resolving_one_tenants_bounces_leaves_another_tenants_alone()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var bounces = new FakeBounces();
        bounces.Add(mine, DateTimeOffset.UtcNow.AddHours(-1));
        bounces.Add(theirs, DateTimeOffset.UtcNow.AddHours(-1));
        var create = HostWith(bounces);
        var (client, _) = await CallerAsync(mine, [SystemCapabilities.ManageEmailSettings], create: create);
        var (other, _) = await CallerAsync(theirs, [SystemCapabilities.ManageEmailSettings], create: create);

        (await client.PostAsync("/api/overview/items/mail.bounces/resolve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        ItemIds(await ReadAsync(client)).Should().BeEmpty();
        ItemIds(await ReadAsync(other)).Should().Equal("mail.bounces");
    }

    // ---- resolve is gated ----

    [Fact]
    public async Task Resolve_needs_manage_email_settings()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1));
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ViewMonitoring], create: HostWith(bounces));

        var response = await client.PostAsync("/api/overview/items/mail.bounces/resolve", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("workflows.failing")]
    [InlineData("nothing")]
    public async Task Resolving_an_item_that_is_not_resolvable_is_not_found(string id)
    {
        var tenant = await TenantAsync();
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ManageEmailSettings], create: HostWith(new FakeBounces()));

        var response = await client.PostAsync($"/api/overview/items/{id}/resolve", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolving_with_no_module_reporting_bounces_is_not_found()
    {
        var tenant = await TenantAsync();
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ManageEmailSettings]);

        var response = await client.PostAsync("/api/overview/items/mail.bounces/resolve", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- a read only key ----

    private async Task<string> KeyAsync(string tenant, Guid owner, params string[] scopes)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await using var session = Store.LightweightSession();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "overview",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = owner,
            TenantSlug = tenant,
            Scopes = [.. scopes],
        });
        await session.SaveChangesAsync(Ct);
        return secret;
    }

    private HttpRequestMessage WithKey(HttpMethod method, string path, string secret)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-Tenant", "unused");
        return request;
    }

    [Fact]
    public async Task A_monitor_read_key_reads_the_overview_as_its_owner_and_cannot_resolve()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 3, TimeSpan.FromHours(1));
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1));
        var create = HostWith(bounces);
        var (_, owner) = await CallerAsync(
            tenant, [SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ManageEmailSettings], create: create);
        var secret = await KeyAsync(tenant, owner, ApiKeyScopes.MonitorRead);
        var http = create();

        var read = await http.SendAsync(KeyRequest(HttpMethod.Get, Route, secret, tenant), Ct);
        var resolve = await http.SendAsync(KeyRequest(HttpMethod.Post, "/api/overview/items/mail.bounces/resolve", secret, tenant), Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("counts").GetProperty("failedRuns").GetProperty("current").GetInt32().Should().Be(3);
        resolve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await bounces.CountUnresolvedAsync(tenant, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task A_key_without_monitor_read_is_refused_the_overview()
    {
        var tenant = await TenantAsync();
        var (_, owner) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns]);
        var secret = await KeyAsync(tenant, owner, ApiKeyScopes.ContentRead);

        var response = await _fixture.CreateClient().SendAsync(KeyRequest(HttpMethod.Get, Route, secret, tenant), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static HttpRequestMessage KeyRequest(HttpMethod method, string path, string secret, string tenant)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-Tenant", tenant);
        request.Headers.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}");
        return request;
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _fixture.CreateClient().GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
