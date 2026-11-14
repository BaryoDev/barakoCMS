using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// <c>GET /api/contents/counts</c> with a window, a previous window and a series.
/// </summary>
/// <remarks>
/// The window is given as explicit times in the past, so the entries sit at known stamps whatever
/// the clock says. Every entry is created at a stamp on purpose: one before the previous window, one in it,
/// and several in the window, in statuses that differ, so a count that read the wrong window or the
/// wrong column comes out different.
/// </remarks>
[Collection("Integration1")]
public class ContentCountsWindowTests
{
    private const string Route = "/api/contents/counts";

    private static readonly DateTime From = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc);

    private readonly IntegrationTestFixture _factory;

    public ContentCountsWindowTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Q(DateTime t) => Uri.EscapeDataString(t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

    private static string WindowQuery => $"from={Q(From)}&to={Q(To)}";

    /// <summary>
    /// Three days, 10 to 13 March, with the three days before them. Created at:
    /// 6 March (before both windows): published.
    /// 8 March (previous window): published, draft.
    /// 10 March 00:00 (first instant of the window): draft.
    /// 10 March 23:00: published.
    /// 11 March 05:00: published, archived.
    /// 12 March 23:59: scheduled.
    /// 13 March 00:00 (the end, excluded): published.
    /// </summary>
    private static readonly (DateTime At, ContentStatus Status, bool Mine)[] Rows =
    [
        (new(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc), ContentStatus.Published, true),
        (new(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc), ContentStatus.Published, true),
        (new(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), ContentStatus.Draft, true),
        (new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), ContentStatus.Draft, true),
        (new(2026, 3, 10, 23, 0, 0, DateTimeKind.Utc), ContentStatus.Published, true),
        (new(2026, 3, 11, 5, 0, 0, DateTimeKind.Utc), ContentStatus.Published, true),
        (new(2026, 3, 11, 5, 30, 0, DateTimeKind.Utc), ContentStatus.Archived, true),
        (new(2026, 3, 12, 23, 59, 0, DateTimeKind.Utc), ContentStatus.Scheduled, true),
        (new(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc), ContentStatus.Published, true),
        // Not the reader's: inside the window, in a status the reader's own entries have.
        (new(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), ContentStatus.Published, false),
        (new(2026, 3, 11, 10, 0, 0, DateTimeKind.Utc), ContentStatus.Published, false),
        (new(2026, 3, 9, 10, 0, 0, DateTimeKind.Utc), ContentStatus.Draft, false),
    ];

    private static PermissionRule Owned() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["OwnerId"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        },
    };

    private async Task<(Guid Reader, string Type)> SeedAsync(bool ownerScoped, (DateTime At, ContentStatus Status, bool Mine)[]? rows = null)
    {
        var type = $"windowed_{Guid.NewGuid():N}"[..20];
        var other = Guid.NewGuid();
        var rule = ownerScoped ? Owned() : new PermissionRule { Enabled = true };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"window_{Guid.NewGuid():N}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = rule }],
        };
        var reader = new User
        {
            Id = Guid.NewGuid(),
            Username = $"window_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        session.Store(reader);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "OwnerId", Type = "string", Sensitivity = SensitivityLevel.Public },
            ],
        });
        foreach (var (at, status, mine) in rows ?? Rows)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                ScheduledPublishAt = status == ContentStatus.Scheduled ? DateTime.UtcNow.AddDays(3) : null,
                Data = new Dictionary<string, object>
                {
                    ["Title"] = "x",
                    ["OwnerId"] = (mine ? reader.Id : other).ToString(),
                },
                CreatedAt = at,
                UpdatedAt = at,
            });
        }

        await session.SaveChangesAsync(Ct);
        return (reader.Id, type);
    }

    private HttpClient Client(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["Viewer"], userId.ToString()));
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{Route}?{query}", Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> CountsAsync(HttpClient client, string query)
    {
        var (status, body) = await GetAsync(client, query);
        status.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static (int Total, int Draft, int Published, int Archived, int Scheduled) Numbers(JsonElement counts) => (
        counts.GetProperty("total").GetInt32(),
        counts.GetProperty("draft").GetInt32(),
        counts.GetProperty("published").GetInt32(),
        counts.GetProperty("archived").GetInt32(),
        counts.GetProperty("scheduled").GetInt32());

    [Fact]
    public async Task Without_a_window_the_answer_is_exactly_the_five_numbers_and_the_list_of_uncounted_types()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var (status, body) = await GetAsync(Client(reader), $"contentType={type}");

        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().Be("""{"total":12,"draft":3,"published":7,"archived":1,"scheduled":1,"uncountedTypes":[]}""",
            "a response with no window parameter carries none of the windowed fields, not even as null");
    }

    [Fact]
    public async Task A_window_counts_the_entries_created_in_it_and_matches_a_direct_query()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var counts = await CountsAsync(Client(reader), $"contentType={type}&{WindowQuery}");

        // 10 March 00:00 (draft), 10 March 10:00 and 23:00, 11 March 05:00 (x2) and 10:00, 12 March 23:59.
        Numbers(counts).Should().Be((7, 1, 4, 1, 1), "the entry at the end instant is outside, the one at the start is inside");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var direct = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == type && c.CreatedAt >= From && c.CreatedAt < To)
            .CountAsync(Ct);
        counts.GetProperty("total").GetInt32().Should().Be(direct);

        var window = counts.GetProperty("window");
        window.GetProperty("from").GetDateTimeOffset().UtcDateTime.Should().Be(From);
        window.GetProperty("to").GetDateTimeOffset().UtcDateTime.Should().Be(To);
    }

    [Fact]
    public async Task The_previous_window_of_the_same_length_is_counted_beside_it_for_a_delta()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var counts = await CountsAsync(Client(reader), $"contentType={type}&{WindowQuery}");

        var previous = counts.GetProperty("previous");
        // 7 to 10 March: 8 March published and draft, 9 March draft. The 6 March entry is before it.
        (previous.GetProperty("total").GetInt32(), previous.GetProperty("draft").GetInt32(), previous.GetProperty("published").GetInt32())
            .Should().Be((3, 2, 1));
    }

    [Fact]
    public async Task A_day_series_returns_one_bucket_per_day_with_the_counts_by_status()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var counts = await CountsAsync(Client(reader), $"contentType={type}&{WindowQuery}&series=day");

        var series = counts.GetProperty("series").EnumerateArray().ToList();
        series.Should().NotBeEmpty();
        series.Should().HaveCount(3, "three days, a bucket each, whatever they hold");
        series.Select(b => b.GetProperty("start").GetDateTimeOffset().UtcDateTime)
            .Should().Equal(From, From.AddDays(1), From.AddDays(2));

        Bucket(series[0]).Should().Be((3, 1, 2, 0, 0), "10 March: the draft at midnight, the reader's entry at 23:00 and another's at 10:00");
        Bucket(series[1]).Should().Be((3, 0, 2, 1, 0), "11 March");
        Bucket(series[2]).Should().Be((1, 0, 0, 0, 1), "12 March, the last minute of it");
        series.Sum(b => b.GetProperty("total").GetInt32()).Should().Be(counts.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task An_hour_series_has_a_bucket_for_every_hour_including_the_empty_ones()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var counts = await CountsAsync(Client(reader), $"contentType={type}&{WindowQuery}&series=hour");

        var series = counts.GetProperty("series").EnumerateArray().ToList();
        series.Should().NotBeEmpty();
        series.Should().HaveCount(72);
        Bucket(series[0]).Should().Be((1, 1, 0, 0, 0), "midnight on the 10th");
        Bucket(series[10]).Should().Be((1, 0, 1, 0, 0), "10:00 on the 10th, another reader's entry");
        Bucket(series[1]).Should().Be((0, 0, 0, 0, 0));
        series.Last().GetProperty("start").GetDateTimeOffset().UtcDateTime.Should().Be(To.AddHours(-1));
    }

    [Fact]
    public async Task A_windowed_count_runs_under_the_read_predicate_and_agrees_with_the_list()
    {
        var (reader, type) = await SeedAsync(ownerScoped: true);
        var client = Client(reader);

        var counts = await CountsAsync(client, $"contentType={type}&{WindowQuery}&series=day");

        // The reader's own entries in the window: 10 March 00:00 draft and 23:00 published, 11 March two, 12 March one.
        Numbers(counts).Should().Be((5, 1, 2, 1, 1), "the other owner's three entries are in the window and are not counted");
        counts.GetProperty("previous").GetProperty("total").GetInt32().Should().Be(2, "the other owner's 9 March entry is not counted either");
        counts.GetProperty("series").EnumerateArray().Sum(b => b.GetProperty("total").GetInt32()).Should().Be(5);

        var response = await client.GetAsync($"/api/contents?contentType={type}&pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var listed = JsonDocument.Parse(body);
        var inWindow = listed.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("createdAt").GetDateTimeOffset().UtcDateTime is var at && at >= From && at < To)
            .ToList();

        inWindow.Should().NotBeEmpty();
        inWindow.Should().HaveCount(counts.GetProperty("total").GetInt32(), "the numbers agree with what GET /api/contents lists");
        inWindow.Count(i => i.GetProperty("status").GetString() == "Published").Should().Be(counts.GetProperty("published").GetInt32());
    }

    [Fact]
    public async Task A_range_counts_back_from_now()
    {
        var now = DateTime.UtcNow;
        var (reader, type) = await SeedAsync(ownerScoped: false,
        [
            (now.AddHours(-2), ContentStatus.Published, true),
            (now.AddHours(-30), ContentStatus.Published, true),
            (now.AddDays(-5), ContentStatus.Draft, true),
        ]);
        var client = Client(reader);

        Numbers(await CountsAsync(client, $"contentType={type}&range=24h")).Should().Be((1, 0, 1, 0, 0));
        Numbers(await CountsAsync(client, $"contentType={type}&range=7d")).Should().Be((3, 1, 2, 0, 0));

        var day = await CountsAsync(client, $"contentType={type}&range=24h");
        day.GetProperty("previous").GetProperty("published").GetInt32().Should().Be(1, "the entry from 30 hours ago is in the 24 hours before");
    }

    [Fact]
    public async Task A_window_of_ninety_days_and_a_series_of_two_hundred_buckets_are_allowed()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);
        var client = Client(reader);

        var (days, _) = await GetAsync(client, $"contentType={type}&from={Q(To.AddDays(-90))}&to={Q(To)}");
        var (hours, body) = await GetAsync(client, $"contentType={type}&from={Q(To.AddHours(-200))}&to={Q(To)}&series=hour");

        days.Should().Be(HttpStatusCode.OK);
        hours.Should().Be(HttpStatusCode.OK, body);
        JsonDocument.Parse(body).RootElement.GetProperty("series").GetArrayLength().Should().Be(200);
    }

    [Theory]
    [InlineData("range=1h", "Range must be one of")]
    [InlineData("range=7d&from=2026-03-10T00:00:00Z", "not both")]
    [InlineData("to=2026-03-13T00:00:00Z", "To needs a from")]
    [InlineData("from=2026-03-13T00:00:00Z&to=2026-03-10T00:00:00Z", "From must be before to")]
    [InlineData("from=2026-03-10T00:00:00Z&to=2026-03-10T00:00:00Z", "From must be before to")]
    [InlineData("from=2025-12-01T00:00:00Z&to=2026-03-13T00:00:00Z", "at most 90 days")]
    [InlineData("series=week&range=7d", "Series must be one of")]
    [InlineData("series=day", "Series needs a window")]
    [InlineData("from=2026-03-01T00:00:00Z&to=2026-03-13T00:00:00Z&series=hour", "at most 200 buckets")]
    [InlineData("range=30d&series=hour", "at most 200 buckets")]
    [InlineData("from=0001-01-01T00:00:00Z&to=0001-01-02T00:00:00Z", "on or after 2000-01-01")]
    [InlineData("from=yesterday", "From must be a date and time")]
    public async Task A_window_past_a_cap_or_badly_formed_is_refused_with_400(string query, string reason)
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var (status, body) = await GetAsync(Client(reader), $"contentType={type}&{query}");

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(reason);
    }

    [Fact]
    public async Task A_time_with_an_offset_counts_the_same_window_as_the_same_instant_in_utc()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);
        var client = Client(reader);
        string Eight(DateTime utc) => Uri.EscapeDataString(utc.AddHours(8).ToString("yyyy-MM-dd'T'HH:mm:ss") + "+08:00");

        var counts = await CountsAsync(client, $"contentType={type}&from={Eight(From)}&to={Eight(To)}");

        Numbers(counts).Should().Be((7, 1, 4, 1, 1));
        counts.GetProperty("window").GetProperty("from").GetDateTimeOffset().UtcDateTime.Should().Be(From);
    }

    [Fact]
    public async Task A_time_with_no_zone_is_read_as_utc()
    {
        var (reader, type) = await SeedAsync(ownerScoped: false);

        var counts = await CountsAsync(Client(reader), $"contentType={type}&from=2026-03-10T00:00:00&to=2026-03-13T00:00:00");

        Numbers(counts).Should().Be((7, 1, 4, 1, 1));
    }

    [Fact]
    public async Task One_call_over_a_type_read_in_full_and_a_type_read_by_owner_counts_both_in_one_answer()
    {
        var type = $"all_{Guid.NewGuid():N}"[..16];
        var scoped = $"own_{Guid.NewGuid():N}"[..16];
        var other = Guid.NewGuid();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"window_{Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = scoped, Read = Owned() },
            ],
        };
        var reader = new User
        {
            Id = Guid.NewGuid(),
            Username = $"window_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(reader);
            foreach (var name in new[] { type, scoped })
            {
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DisplayName = name,
                    Fields =
                    [
                        new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                        new() { Name = "OwnerId", Type = "string", Sensitivity = SensitivityLevel.Public },
                    ],
                });
            }

            // Read in full: three in the window, one of them somebody else's. Read by owner: two in the
            // window of the reader's, one of somebody else's.
            var rows = new (string Type, DateTime At, ContentStatus Status, Guid Owner)[]
            {
                (type, From.AddHours(1), ContentStatus.Published, reader.Id),
                (type, From.AddHours(2), ContentStatus.Draft, other),
                (type, From.AddDays(1), ContentStatus.Published, other),
                (scoped, From.AddHours(3), ContentStatus.Archived, reader.Id),
                (scoped, From.AddDays(1), ContentStatus.Published, reader.Id),
                (scoped, From.AddDays(1), ContentStatus.Published, other),
            };
            foreach (var (t, at, status, owner) in rows)
            {
                session.Store(new barakoCMS.Models.Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = t,
                    Status = status,
                    Data = new Dictionary<string, object> { ["Title"] = "x", ["OwnerId"] = owner.ToString() },
                    CreatedAt = at,
                    UpdatedAt = at,
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var counts = await CountsAsync(Client(reader.Id), $"{WindowQuery}&series=day");

        Numbers(counts).Should().Be((5, 1, 3, 1, 0), "three from the full type and two of the reader's from the owned one, not the sixth");
        var series = counts.GetProperty("series").EnumerateArray().ToList();
        series.Should().NotBeEmpty();
        Bucket(series[0]).Should().Be((3, 1, 1, 1, 0));
        Bucket(series[1]).Should().Be((2, 0, 2, 0, 0));
        counts.GetProperty("previous").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Another_tenant_never_counts_a_tenants_entries()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();
        var type = $"xt_{Guid.NewGuid():N}"[..12];
        await ContentInAsync(acme, type, From.AddHours(1), 3);
        await ContentInAsync(globex, type, From.AddHours(1), 5);
        await ContentInAsync(globex, type, From.AddDays(-1), 2);

        var inAcme = await CountsAsync(await MemberAsync(acme, "203.0.113.141"), $"contentType={type}&{WindowQuery}&series=day");
        var inGlobex = await CountsAsync(await MemberAsync(globex, "203.0.113.142"), $"contentType={type}&{WindowQuery}&series=day");

        inAcme.GetProperty("total").GetInt32().Should().Be(3);
        inAcme.GetProperty("previous").GetProperty("total").GetInt32().Should().Be(0);
        inAcme.GetProperty("series")[0].GetProperty("total").GetInt32().Should().Be(3);
        inGlobex.GetProperty("total").GetInt32().Should().Be(5);
        inGlobex.GetProperty("previous").GetProperty("total").GetInt32().Should().Be(2);
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug, string ip)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"xt-{Guid.NewGuid():n}"[..14],
                Email = $"xt-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    private async Task ContentInAsync(string tenantSlug, string type, DateTime at, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        if (await session.Query<ContentTypeDefinition>().Where(d => d.Name == type).AnyAsync(Ct) is false)
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
        }

        for (var i = 0; i < count; i++)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "t" },
                CreatedAt = at, UpdatedAt = at,
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_a_windowed_count_too()
    {
        var response = await _factory.CreateClient().GetAsync($"{Route}?{WindowQuery}&series=day", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static (int Total, int Draft, int Published, int Archived, int Scheduled) Bucket(JsonElement b) => (
        b.GetProperty("total").GetInt32(),
        b.GetProperty("draft").GetInt32(),
        b.GetProperty("published").GetInt32(),
        b.GetProperty("archived").GetInt32(),
        b.GetProperty("scheduled").GetInt32());
}
