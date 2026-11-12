using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Overview;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Overview;

/// <summary>
/// <see cref="INeedsYouSource"/>: the same items the overview route returns, read with no request.
/// Every test works in a tenant of its own.
/// </summary>
[Collection("Integration1")]
public class NeedsYouSourceTests
{
    private const string Route = "/api/overview";

    private readonly IntegrationTestFixture _fixture;
    private static int _ip;

    public NeedsYouSourceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private sealed class FakeBounces : IMailBounceSource
    {
        private readonly ConcurrentDictionary<string, int> _unresolved = new();

        public void Add(string tenant, int count) => _unresolved.AddOrUpdate(tenant, count, (_, n) => n + count);

        public Task<int> CountAsync(string tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(_unresolved.GetValueOrDefault(tenant));

        public Task<int> CountUnresolvedAsync(string tenant, CancellationToken ct = default) =>
            Task.FromResult(_unresolved.GetValueOrDefault(tenant));

        public Task<int> ResolveAllAsync(string tenant, CancellationToken ct = default) =>
            Task.FromResult(_unresolved.TryRemove(tenant, out var n) ? n : 0);
    }

    private sealed class Host(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        public HttpClient CreateClient() => factory.CreateClient();

        public INeedsYouSource Source => factory.Services.GetRequiredService<INeedsYouSource>();

        public IServiceProvider Services => factory.Services;
    }

    private Host HostWith(FakeBounces bounces)
    {
        var host = _fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IMailBounceSource>(bounces)));
        host.CreateClient().Dispose();
        return new Host(host);
    }

    private async Task<string> TenantAsync()
    {
        var slug = "needs-you-" + Guid.NewGuid().ToString("N")[..16];
        await using var session = Store.LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task SeedRunsAsync(string tenant, RunStatus status, int count)
    {
        var workflow = Guid.NewGuid();
        await using var session = Store.LightweightSession(tenant);
        for (var i = 0; i < count; i++)
        {
            session.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow,
                WorkflowName = "ns",
                ContentId = Guid.NewGuid(),
                ContentType = "article",
                TriggerEvent = WorkflowEvents.Published,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(i),
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    private async Task SeedOverdueAsync(string tenant, int count)
    {
        var type = "nstype_" + Guid.NewGuid().ToString("N")[..10];
        await using var session = Store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = [new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public }],
        });
        for (var i = 0; i < count; i++)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Scheduled,
                ScheduledPublishAt = DateTime.UtcNow.AddHours(-2),
                Data = new Dictionary<string, object> { ["Title"] = "x" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    private async Task<HttpClient> CallerAsync(Host host, string tenant, string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "ns-" + Guid.NewGuid().ToString("N"),
            TenantSlug = tenant,
            SystemCapabilities = [.. capabilities],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "ns-" + Guid.NewGuid().ToString("N")[..12],
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

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: [role.Name],
                userId: user.Id.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant, ["Username"] = user.Username }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}");
        return client;
    }

    private static async Task<List<NeedsYouEntry>> RouteItemsAsync(HttpClient client)
    {
        var response = await client.GetAsync(Route, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("needsYou").EnumerateArray().Select(i =>
        {
            var reference = i.GetProperty("ref");
            return new NeedsYouEntry(
                i.GetProperty("id").GetString()!,
                i.GetProperty("severity").GetString()!,
                i.GetProperty("text").GetString()!,
                reference.GetProperty("kind").GetString()!,
                reference.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                i.GetProperty("resolvable").GetBoolean());
        }).ToList();
    }

    private static HashSet<string> Caps(params string[] capabilities) =>
        new(capabilities, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Everything =
    [
        SystemCapabilities.ViewWorkflowRuns,
        SystemCapabilities.ViewMonitoring,
        SystemCapabilities.ViewAuditLog,
        SystemCapabilities.ManageEmailSettings,
    ];

    [Fact]
    public async Task Returns_the_same_items_the_overview_route_returns_for_the_same_capabilities()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10);
        var bounces = new FakeBounces();
        bounces.Add(tenant, 3);
        var host = HostWith(bounces);
        var client = await CallerAsync(host, tenant, Everything);

        var fromRoute = await RouteItemsAsync(client);
        var fromSource = await host.Source.ReadAsync(tenant, Caps(Everything), Ct);

        fromRoute.Select(i => i.Id).Should().Contain(["mail.bounces", "workflows.failing"]);
        fromSource.Should().BeEquivalentTo(fromRoute, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(SystemCapabilities.ViewWorkflowRuns, "workflows.failing")]
    [InlineData(SystemCapabilities.ManageEmailSettings, "mail.bounces")]
    public async Task Leaves_out_an_item_whose_capability_is_not_held(string capability, string itemId)
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10);
        var bounces = new FakeBounces();
        bounces.Add(tenant, 3);
        var host = HostWith(bounces);

        var with = await host.Source.ReadAsync(tenant, Caps(capability), Ct);
        var without = await host.Source.ReadAsync(tenant, Caps(Everything.Where(c => c != capability).ToArray()), Ct);

        with.Select(i => i.Id).Should().Contain(itemId);
        without.Select(i => i.Id).Should().NotContain(itemId);
    }

    [Fact]
    public async Task Leaves_out_the_runner_item_without_view_monitoring()
    {
        var tenant = await TenantAsync();
        var host = HostWith(new FakeBounces());

        var entries = await host.Source.ReadAsync(tenant, Caps(Everything.Where(c => c != SystemCapabilities.ViewMonitoring).ToArray()), Ct);

        entries.Select(i => i.Id).Should().NotContain("runner.stalled");
    }

    [Fact]
    public async Task Leaves_out_the_overdue_item_unless_the_set_holds_everything()
    {
        var tenant = await TenantAsync();
        await SeedOverdueAsync(tenant, 2);
        var host = HostWith(new FakeBounces());

        var named = await host.Source.ReadAsync(tenant, Caps(Everything), Ct);
        var all = await host.Source.ReadAsync(tenant, Caps(SystemCapabilities.All), Ct);

        named.Select(i => i.Id).Should().NotContain("content.overdue");
        all.Should().ContainSingle(i => i.Id == "content.overdue" && i.RefKind == "content");
    }

    [Fact]
    public async Task A_set_with_nothing_held_returns_no_items()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10);
        var bounces = new FakeBounces();
        bounces.Add(tenant, 3);
        var host = HostWith(bounces);

        var entries = await host.Source.ReadAsync(tenant, Caps(), Ct);

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Item_ids_are_stable_across_two_reads_while_the_cause_lasts()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10);
        var bounces = new FakeBounces();
        bounces.Add(tenant, 3);
        var host = HostWith(bounces);

        var first = await host.Source.ReadAsync(tenant, Caps(Everything), Ct);
        var second = await host.Source.ReadAsync(tenant, Caps(Everything), Ct);

        first.Select(i => i.Id).Should().BeEquivalentTo(new[] { "mail.bounces", "workflows.failing" });
        second.Select(i => i.Id).Should().Equal(first.Select(i => i.Id));
    }

    [Fact]
    public async Task Reads_only_the_named_tenant()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        await SeedRunsAsync(theirs, RunStatus.Failed, 10);
        var bounces = new FakeBounces();
        bounces.Add(theirs, 3);
        var host = HostWith(bounces);

        var mineEntries = await host.Source.ReadAsync(mine, Caps(Everything), Ct);
        var theirEntries = await host.Source.ReadAsync(theirs, Caps(Everything), Ct);

        mineEntries.Should().BeEmpty();
        theirEntries.Select(i => i.Id).Should().Contain(["mail.bounces", "workflows.failing"]);
    }

    [Fact]
    public async Task Works_with_no_request_scope()
    {
        var tenant = await TenantAsync();
        await SeedRunsAsync(tenant, RunStatus.Failed, 10);
        var host = HostWith(new FakeBounces());

        // Nothing is serving a request: the accessor holds no context, and the source is the
        // singleton from the root provider.
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull();
        var entries = await host.Source.ReadAsync(tenant, Caps(Everything), Ct);

        entries.Select(i => i.Id).Should().Contain("workflows.failing");
    }

    [Fact]
    public async Task Counts_stop_at_the_cap_per_source()
    {
        var tenant = await TenantAsync();
        await SeedOverdueAsync(tenant, 6);
        await using var session = Store.QuerySession(tenant);
        var sources = await OverviewReads.AllContentAsync(session, Ct);

        var (due, overdue) = await OverviewReads.CountDueAsync(sources, DateTimeOffset.UtcNow, Ct, cap: 4);

        due.Should().Be(4);
        overdue.Should().Be(4);
        OverviewReads.MaxDuePerSource.Should().BeGreaterThan(4);
    }
}
