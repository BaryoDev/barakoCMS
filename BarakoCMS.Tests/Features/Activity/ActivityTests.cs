using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Activity;

/// <summary>
/// <c>GET /api/activity</c>. Every test works in a tenant of its own, so no other test's rows show
/// up, and a caller sees only what its capabilities open.
/// </summary>
[Collection("Integration1")]
public class ActivityTests
{
    private const string Route = "/api/activity";

    private readonly IntegrationTestFixture _fixture;
    private static int _ip;

    public ActivityTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private static readonly string[] AllCapabilities =
        [SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ManageEmailSettings];

    private sealed class FakeBounces : IMailBounceSource
    {
        private readonly ConcurrentDictionary<string, List<MailBounce>> _byTenant = new();

        public MailBounce Add(string tenant, DateTimeOffset at, string title)
        {
            var bounce = new MailBounce(Guid.NewGuid(), at, title);
            var list = _byTenant.GetOrAdd(tenant, _ => []);
            lock (list) list.Add(bounce);
            return bounce;
        }

        public Task<int> CountAsync(string tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(Snapshot(tenant).Count(b => b.At >= from && b.At < to));

        public Task<int> CountUnresolvedAsync(string tenant, CancellationToken ct = default) =>
            Task.FromResult(Snapshot(tenant).Count);

        public Task<int> ResolveAllAsync(string tenant, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<MailBounce>> ListAsync(
            string tenant, DateTimeOffset after, Guid? afterId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MailBounce>>(Snapshot(tenant)
                .OrderBy(b => b.At).ThenBy(b => b.Id)
                .Where(b => b.At > after || (afterId is { } id && b.At == after && b.Id.CompareTo(id) > 0))
                .Take(limit).ToList());

        public Task<IReadOnlyList<MailBounce>> ListNewestAsync(string tenant, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MailBounce>>(Snapshot(tenant).OrderByDescending(b => b.At).Take(limit).ToList());

        private List<MailBounce> Snapshot(string tenant)
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

    private async Task<string> TenantAsync()
    {
        var slug = "activity-" + Guid.NewGuid().ToString("N")[..16];
        await using var session = Store.LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task StoreAsync(string tenant, params object[] documents)
    {
        await using var session = Store.LightweightSession(tenant);
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>A member of the tenant whose one role holds exactly these capabilities, and may read the type if one is named.</summary>
    private async Task<(HttpClient Client, User User)> CallerAsync(
        string tenant, string[] capabilities, string? readableType = null, Func<HttpClient>? create = null)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "activity-" + Guid.NewGuid().ToString("N"),
            TenantSlug = tenant,
            SystemCapabilities = [.. capabilities],
            Permissions = readableType is null
                ? []
                : [new ContentTypePermission { ContentTypeSlug = readableType, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "act-" + Guid.NewGuid().ToString("N")[..12],
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
        return (client, user);
    }

    private async Task<HttpClient> SuperAdminInAsync(string tenant)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static async Task<(string Body, JsonElement Page)> ReadAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(Route + query, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return (body, document.RootElement.Clone());
    }

    private static List<JsonElement> Rows(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static string[] Ids(JsonElement page) => Rows(page).Select(r => r.GetProperty("id").GetString()!).ToArray();

    // ---- seeding ----

    private static DateTime Micro(DateTime at) => new(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);

    private async Task<AuditEvent> AuditAsync(string tenant, string action, DateTime at, User? actor = null,
        string? targetId = null, Dictionary<string, object>? metadata = null)
    {
        var e = new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantSlug = tenant,
            Action = action,
            ActorUserId = actor?.Id,
            ActorUsername = actor?.Username,
            TargetType = targetId is null ? null : "ContentType",
            TargetId = targetId,
            Metadata = metadata,
            CreatedAt = Micro(at),
        };
        await using var session = Store.LightweightSession();
        session.Store(e);
        await session.SaveChangesAsync(Ct);
        return e;
    }

    private async Task<WorkflowRun> RunAsync(string tenant, RunStatus status, string name = "Notify editors")
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = name,
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await StoreAsync(tenant, run);
        return run;
    }

    private async Task<string> TypeAsync(string tenant, SensitivityLevel titleSensitivity = SensitivityLevel.Public)
    {
        var type = "acttype_" + Guid.NewGuid().ToString("N")[..10];
        await StoreAsync(tenant, new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = [new() { Name = "Title", Type = "string", Sensitivity = titleSensitivity }],
        });
        return type;
    }

    /// <summary>An entry with a stream and a document, the events appended after the first one.</summary>
    private async Task<barakoCMS.Models.Content> EntryAsync(
        string tenant, string type, string title, Guid creator, SensitivityLevel level, params Func<Guid, object>[] later)
    {
        var id = Guid.NewGuid();
        var entry = new barakoCMS.Models.Content
        {
            Id = id,
            ContentType = type,
            Data = new Dictionary<string, object> { ["Title"] = title },
            Status = ContentStatus.Draft,
            Sensitivity = level,
            CreatedBy = creator,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await using var session = Store.LightweightSession(tenant);
        session.Events.StartStream<barakoCMS.Models.Content>(id, new ContentCreated(
            id, type, entry.Data, ContentStatus.Draft, creator, null, level, DateTime.UtcNow));
        foreach (var make in later) session.Events.Append(id, make(id));
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry;
    }

    private Task<barakoCMS.Models.Content> EntryAsync(
        string tenant, string type, string title, Guid creator, params Func<Guid, object>[] later) =>
        EntryAsync(tenant, type, title, creator, SensitivityLevel.Public, later);

    private static void AssertRow(JsonElement row, string kind, string severity)
    {
        row.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("at").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddDays(-1));
        row.GetProperty("kind").GetString().Should().Be(kind);
        row.GetProperty("severity").GetString().Should().Be(severity);
        row.GetProperty("actor").GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("actor").GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("subject").GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("subject").GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("subject").GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("ref").GetString().Should().StartWith("/");
    }

    // ---- the nine kinds ----

    [Fact]
    public async Task A_publish_is_read_back_as_published()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        var entry = await EntryAsync(tenant, type, "Hello world", user.Id,
            id => new ContentStatusChanged(id, ContentStatus.Published, user.Id, DateTime.UtcNow));

        var (body, page) = await ReadAsync(client, "?kinds=published");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "published", "info");
        rows[0].GetProperty("actor").GetProperty("kind").GetString().Should().Be("user");
        rows[0].GetProperty("actor").GetProperty("name").GetString().Should().Be(user.Username);
        rows[0].GetProperty("subject").GetProperty("id").GetString().Should().Be(entry.Id.ToString());
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Hello world");
        rows[0].GetProperty("text").GetString().Should().NotContain("Hello world");
        rows[0].GetProperty("ref").GetString().Should().Be($"/content/{type}/{entry.Id}");
        body.Should().NotContain("ContentStatusChanged").And.NotContain("StreamId").And.NotContain("streamId");
    }

    [Fact]
    public async Task A_schedule_is_read_back_as_scheduled()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        await EntryAsync(tenant, type, "Later", user.Id,
            id => new ContentScheduled(id, DateTime.UtcNow.AddDays(1), null, user.Id, DateTime.UtcNow));

        var (_, page) = await ReadAsync(client, "?kinds=scheduled");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "scheduled", "info");
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Later");
    }

    [Fact]
    public async Task An_edit_is_read_back_as_edited()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        await EntryAsync(tenant, type, "Changed", user.Id,
            id => new ContentUpdated(id, new Dictionary<string, object> { ["Title"] = "Changed" }, user.Id, null, DateTime.UtcNow));

        var (_, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "edited", "info");
    }

    [Fact]
    public async Task A_succeeded_run_is_read_back_as_run_succeeded()
    {
        var tenant = await TenantAsync();
        var run = await RunAsync(tenant, RunStatus.Succeeded);
        var (client, _) = await CallerAsync(tenant, AllCapabilities);

        var (_, page) = await ReadAsync(client, "?kinds=run.succeeded");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "run.succeeded", "info");
        rows[0].GetProperty("subject").GetProperty("id").GetString().Should().Be(run.Id.ToString());
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Notify editors");
        rows[0].GetProperty("ref").GetString().Should().Be($"/workflows/runs/{run.Id}");
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.PartiallyFailed)]
    public async Task A_failed_run_is_read_back_as_run_failed_with_error_severity(RunStatus status)
    {
        var tenant = await TenantAsync();
        await RunAsync(tenant, status);
        var (client, _) = await CallerAsync(tenant, AllCapabilities);

        var (_, page) = await ReadAsync(client, "?kinds=run.failed");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "run.failed", "error");
    }

    [Fact]
    public async Task A_bounce_is_read_back_as_bounced()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        var bounce = bounces.Add(tenant, DateTimeOffset.UtcNow.AddMinutes(-3), "Welcome email");
        var (client, _) = await CallerAsync(tenant, AllCapabilities, create: HostWith(bounces));

        var (_, page) = await ReadAsync(client, "?kinds=bounced");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "bounced", "warning");
        rows[0].GetProperty("subject").GetProperty("id").GetString().Should().Be(bounce.Id.ToString());
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Welcome email");
    }

    [Fact]
    public async Task A_sign_in_is_read_back_as_signin()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1), user);
        await AuditAsync(tenant, "auth.login.failed", DateTime.UtcNow.AddMinutes(-1), user);

        var (_, page) = await ReadAsync(client, "?kinds=signin");

        var rows = Rows(page);
        rows.Should().HaveCount(1, "only a successful sign-in is this kind");
        AssertRow(rows[0], "signin", "info");
        rows[0].GetProperty("actor").GetProperty("name").GetString().Should().Be(user.Username);
        rows[0].GetProperty("subject").GetProperty("id").GetString().Should().Be(user.Id.ToString());
    }

    [Theory]
    [InlineData("redirect.imported", "Redirects")]
    [InlineData("tenant.imported", "Tenant bundle")]
    public async Task An_import_is_read_back_as_imported(string action, string title)
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        await AuditAsync(tenant, action, DateTime.UtcNow.AddMinutes(-1), user);

        var (_, page) = await ReadAsync(client, "?kinds=imported");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "imported", "info");
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be(title);
    }

    [Fact]
    public async Task A_content_type_change_is_read_back_as_type_changed()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        await AuditAsync(tenant, "contenttype.field_added", DateTime.UtcNow.AddMinutes(-1), user, targetId: "article");

        var (_, page) = await ReadAsync(client, "?kinds=type.changed");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        AssertRow(rows[0], "type.changed", "info");
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("article");
        rows[0].GetProperty("ref").GetString().Should().Be("/content-types/article");
    }

    // ---- filtering ----

    [Fact]
    public async Task Kinds_keeps_only_the_kinds_asked_for_and_ignores_unknown_names()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1), user);
        await AuditAsync(tenant, "redirect.imported", DateTime.UtcNow.AddMinutes(-1), user);
        await RunAsync(tenant, RunStatus.Failed);

        var (_, everything) = await ReadAsync(client);
        var (_, some) = await ReadAsync(client, "?kinds=signin,run.failed,not.a.kind");
        var (_, none) = await ReadAsync(client, "?kinds=not.a.kind");

        Rows(everything).Select(r => r.GetProperty("kind").GetString()).Should()
            .BeEquivalentTo("signin", "imported", "run.failed");
        Rows(some).Select(r => r.GetProperty("kind").GetString()).Should().BeEquivalentTo("signin", "run.failed");
        Rows(none).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_is_capped_at_one_hundred_rows()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        var start = DateTime.UtcNow.AddHours(-2);
        await using (var session = Store.LightweightSession())
        {
            for (var i = 0; i < 105; i++)
            {
                session.Store(new AuditEvent
                {
                    TenantSlug = tenant, Action = "auth.login.succeeded", ActorUserId = user.Id,
                    ActorUsername = user.Username, CreatedAt = Micro(start.AddSeconds(i)),
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var (_, page) = await ReadAsync(client, "?pageSize=500");

        Rows(page).Should().HaveCount(100);
        page.GetProperty("hasMore").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_since_that_is_not_a_cursor_is_a_400()
    {
        var tenant = await TenantAsync();
        var (client, _) = await CallerAsync(tenant, AllCapabilities);

        var response = await client.GetAsync($"{Route}?since=banana", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- the cursor ----

    [Fact]
    public async Task Following_the_cursor_reads_every_row_once_while_rows_are_written()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        var start = DateTime.UtcNow.AddHours(-3);
        var expected = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var e = await AuditAsync(tenant, "auth.login.succeeded", start.AddSeconds(i), user);
            expected.Add($"audit-{e.Id:N}");
        }

        var seen = new List<string>();
        var cursor = start.AddSeconds(-1).ToString("O");
        var written = false;
        for (var turn = 0; turn < 20; turn++)
        {
            var (_, page) = await ReadAsync(client, $"?pageSize=5&since={Uri.EscapeDataString(cursor)}");
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("cursor").GetString()!;

            if (!written)
            {
                // Written while a reader is part way through, and after everything it has read.
                for (var i = 12; i < 17; i++)
                {
                    var e = await AuditAsync(tenant, "auth.login.succeeded", start.AddSeconds(i), user);
                    expected.Add($"audit-{e.Id:N}");
                }

                written = true;
            }

            if (!page.GetProperty("hasMore").GetBoolean()) break;
        }

        seen.Should().NotBeEmpty();
        seen.Should().HaveCount(17);
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Equal(expected, "oldest first, none missing");
    }

    [Fact]
    public async Task Rows_of_several_sources_are_all_reached_by_following_the_cursor()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var bounces = new FakeBounces();
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type, HostWith(bounces));
        var expected = new HashSet<string>();
        for (var i = 0; i < 4; i++)
        {
            var e = await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddHours(-1).AddSeconds(i), user);
            expected.Add($"audit-{e.Id:N}");
            expected.Add($"run-{(await RunAsync(tenant, RunStatus.Failed)).Id:N}");
            expected.Add($"bounce-{bounces.Add(tenant, DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(i), "m").Id:N}");
            await EntryAsync(tenant, type, "x" + i, user.Id,
                id => new ContentStatusChanged(id, ContentStatus.Published, user.Id, DateTime.UtcNow));
        }

        var seen = new HashSet<string>();
        var cursor = DateTime.UtcNow.AddHours(-2).ToString("O");
        for (var turn = 0; turn < 40; turn++)
        {
            var (_, page) = await ReadAsync(client, $"?pageSize=3&since={Uri.EscapeDataString(cursor)}");
            foreach (var id in Ids(page)) seen.Add(id);
            cursor = page.GetProperty("cursor").GetString()!;
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        }

        seen.Should().NotBeEmpty();
        seen.Where(id => !id.StartsWith("event-", StringComparison.Ordinal)).Should().BeEquivalentTo(expected,
            "a row can be read twice behind the cursor's margin, never missed");
        seen.Count(id => id.StartsWith("event-", StringComparison.Ordinal)).Should().Be(4);
    }

    // ---- tenants ----

    [Fact]
    public async Task A_tenant_admin_sees_only_its_own_rows()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var (client, user) = await CallerAsync(mine, AllCapabilities);
        var own = await AuditAsync(mine, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1), user);
        await AuditAsync(theirs, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        await RunAsync(theirs, RunStatus.Failed);
        var ownRun = await RunAsync(mine, RunStatus.Failed);

        var (_, page) = await ReadAsync(client);

        var ids = Ids(page);
        ids.Should().NotBeEmpty();
        ids.Should().BeEquivalentTo($"audit-{own.Id:N}", $"run-{ownRun.Id:N}");
    }

    [Fact]
    public async Task A_super_admin_sees_only_the_tenant_it_is_in()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var own = await AuditAsync(mine, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        await AuditAsync(theirs, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        await AuditAsync("default", "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        var client = await SuperAdminInAsync(mine);

        var (_, page) = await ReadAsync(client);

        var ids = Ids(page);
        ids.Should().NotBeEmpty();
        ids.Should().BeEquivalentTo($"audit-{own.Id:N}");
    }

    [Fact]
    public async Task Events_of_another_tenant_are_not_read()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var type = await TypeAsync(mine);
        await StoreAsync(theirs, new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type });
        var (client, user) = await CallerAsync(mine, AllCapabilities, type);
        await EntryAsync(mine, type, "Mine", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));
        await EntryAsync(theirs, type, "Theirs", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));

        var (_, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Mine");
    }

    // ---- capabilities ----

    [Fact]
    public async Task A_caller_without_view_audit_log_is_forbidden_not_given_an_empty_list()
    {
        var tenant = await TenantAsync();
        await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ViewWorkflowRuns, SystemCapabilities.ManageEmailSettings]);

        var response = await client.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Runs_are_left_out_without_view_workflow_runs()
    {
        var tenant = await TenantAsync();
        await RunAsync(tenant, RunStatus.Failed);
        var signIn = await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ViewAuditLog]);

        var (_, page) = await ReadAsync(client);

        Ids(page).Should().BeEquivalentTo($"audit-{signIn.Id:N}");
    }

    [Fact]
    public async Task Bounces_are_left_out_without_manage_email_settings()
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        bounces.Add(tenant, DateTimeOffset.UtcNow.AddMinutes(-3), "Welcome email");
        var signIn = await AuditAsync(tenant, "auth.login.succeeded", DateTime.UtcNow.AddMinutes(-1));
        var (client, _) = await CallerAsync(tenant, [SystemCapabilities.ViewAuditLog], create: HostWith(bounces));

        var (_, page) = await ReadAsync(client);

        Ids(page).Should().BeEquivalentTo($"audit-{signIn.Id:N}");
    }

    [Fact]
    public async Task An_entry_the_caller_may_not_read_gives_no_row()
    {
        var tenant = await TenantAsync();
        var readable = await TypeAsync(tenant);
        var closed = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, readable);
        await EntryAsync(tenant, readable, "Open", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));
        await EntryAsync(tenant, closed, "Closed", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));

        var (_, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Open");
    }

    // ---- review round 1 ----

    [Fact]
    public async Task A_title_that_sensitivity_masks_is_not_shown()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant, SensitivityLevel.Sensitive);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        await EntryAsync(tenant, type, "Secret plans", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));

        var (body, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Untitled");
        body.Should().NotContain("Secret plans");
    }

    [Fact]
    public async Task An_entry_hidden_by_sensitivity_gives_no_row()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        await EntryAsync(tenant, type, "Open", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));
        await EntryAsync(tenant, type, "Hidden one", user.Id, SensitivityLevel.Hidden,
            id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));

        var (body, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Open");
        body.Should().NotContain("Hidden one");
    }

    [Fact]
    public async Task An_entry_whose_document_is_gone_gives_no_row()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        var entry = await EntryAsync(tenant, type, "Erased", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));
        await EntryAsync(tenant, type, "Kept", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));
        await using (var session = Store.LightweightSession(tenant))
        {
            session.Delete<barakoCMS.Models.Content>(entry.Id);
            await session.SaveChangesAsync(Ct);
        }

        var (_, page) = await ReadAsync(client, "?kinds=edited");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        rows[0].GetProperty("subject").GetProperty("title").GetString().Should().Be("Kept");
    }

    [Fact]
    public async Task A_content_row_id_is_opaque_and_the_same_on_every_read()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        await EntryAsync(tenant, type, "Once", user.Id, id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow));

        var (_, first) = await ReadAsync(client, "?kinds=edited");
        var (_, second) = await ReadAsync(client, "?kinds=edited");

        Ids(first).Should().HaveCount(1);
        Ids(first)[0].Should().MatchRegex("^event-[0-9a-f]{24}$");
        Ids(second).Should().Equal(Ids(first));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0_0_00000000000000000000000000000000_0")]
    [InlineData("1999-12-31T23:59:59Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("3155378975999999999_0_00000000000000000000000000000000_0")]
    [InlineData("638000000000000000_0_00000000000000000000000000000000_0_3155378975999999999")]
    public async Task A_since_before_the_floor_is_a_400_and_not_a_500(string since)
    {
        var tenant = await TenantAsync();
        var bounces = new FakeBounces();
        var (client, _) = await CallerAsync(tenant, AllCapabilities, create: HostWith(bounces));

        var response = await client.GetAsync($"{Route}?since={Uri.EscapeDataString(since)}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rows_stamped_with_a_seventh_digit_are_read_once_each()
    {
        var tenant = await TenantAsync();
        var (client, user) = await CallerAsync(tenant, AllCapabilities);
        var start = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-3), DateTimeKind.Utc);
        start = new DateTime(start.Ticks - start.Ticks % 10_000_000, DateTimeKind.Utc);
        var expected = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var e = new AuditEvent
            {
                Id = Guid.NewGuid(), TenantSlug = tenant, Action = "auth.login.succeeded",
                ActorUserId = user.Id, ActorUsername = user.Username,
                CreatedAt = new DateTime(start.Ticks + i * 1_000_000 + 7, DateTimeKind.Utc),
            };
            await using var session = Store.LightweightSession();
            session.Store(e);
            await session.SaveChangesAsync(Ct);
            expected.Add($"audit-{e.Id:N}");
        }

        var seen = new List<string>();
        var cursor = start.AddSeconds(-1).ToString("O");
        for (var turn = 0; turn < 12; turn++)
        {
            var (_, page) = await ReadAsync(client, $"?pageSize=2&since={Uri.EscapeDataString(cursor)}");
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("cursor").GetString()!;
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        }

        seen.Should().NotBeEmpty();
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Equal(expected);
    }

    [Fact]
    public async Task A_long_title_is_cut_without_splitting_an_emoji()
    {
        var tenant = await TenantAsync();
        var name = new string('a', 199) + "\U0001F600" + "tail";
        await RunAsync(tenant, RunStatus.Failed, name);
        var (client, _) = await CallerAsync(tenant, AllCapabilities);

        var (_, page) = await ReadAsync(client, "?kinds=run.failed");

        var rows = Rows(page);
        rows.Should().HaveCount(1);
        var title = rows[0].GetProperty("subject").GetProperty("title").GetString()!;
        title.Should().NotBeEmpty();
        char.IsHighSurrogate(title[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task An_event_cursor_names_the_stream_and_version_not_the_global_sequence()
    {
        var tenant = await TenantAsync();
        var type = await TypeAsync(tenant);
        var (client, user) = await CallerAsync(tenant, AllCapabilities, type);
        var entries = new List<barakoCMS.Models.Content>();
        for (var i = 0; i < 5; i++)
        {
            entries.Add(await EntryAsync(tenant, type, "e" + i, user.Id,
                id => new ContentUpdated(id, [], user.Id, null, DateTime.UtcNow)));
        }

        await using var read = Store.LightweightSession(tenant);
        var stored = (await read.Events.QueryAllRawEvents().ToListAsync(Ct)).Where(e => e.Data is ContentUpdated).ToList();
        stored.Should().HaveCount(5);

        var seen = new List<string>();
        var cursors = new List<string>();
        var cursor = DateTime.UtcNow.AddHours(-1).ToString("O");
        for (var turn = 0; turn < 12; turn++)
        {
            var (_, page) = await ReadAsync(client, $"?kinds=edited&pageSize=2&since={Uri.EscapeDataString(cursor)}");
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("cursor").GetString()!;
            cursors.Add(cursor);
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        }

        seen.Should().NotBeEmpty();
        seen.Distinct().Should().HaveCount(5, "every event is reached");
        var streams = entries.Select(e => e.Id.ToString("N")).ToHashSet();
        var eventCursors = cursors.Select(c => c.Split('_')).Where(p => p[1] == "2").ToList();
        eventCursors.Should().NotBeEmpty();
        foreach (var parts in eventCursors)
        {
            streams.Should().Contain(parts[2]);
            var version = long.Parse(parts[3]);
            stored.Single(e => e.StreamId.ToString("N") == parts[2]).Version.Should().Be(version);
        }
    }
}
