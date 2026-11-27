using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Models;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// Which entries a windowed rollup counts, with the day and the zone of the site: createdAt, a
/// date field and a datetime field, and what marks a header when the window field is edited.
/// </summary>
/// <remarks>
/// Each test works in a tenant of its own, so no other test's drain counts its headers. The drain
/// takes the fixed clock the test gives it, and the drain job a write queued is taken out of the
/// queue first so the system clock never counts them.
/// </remarks>
[Collection("Integration1")]
public class RollupWindowTests
{
    private readonly IntegrationTestFixture _fixture;

    public RollupWindowTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    private static FakeTimeProvider At(string utc) => new(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

    private static DateTime Utc(string utc) =>
        DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);

    private sealed record Setup(string Tenant, string Header, string Line, HttpClient Client);

    private static (string Key, string Word, string Fn) Roll(string key, string word, string fn = "count") => (key, word, fn);

    /// <summary>
    /// A tenant with a line type (Teacher, Hours, Day as a date and At as a datetime) and a header
    /// type with one rollup, named R0, R1 and so on, for each window given.
    /// </summary>
    private async Task<Setup> SetupAsync(string? zone, params (string Key, string Word, string Fn)[] rollups)
    {
        var (tenant, client) = await R.OwnTenantAsync(_fixture);
        var header = "wh" + Guid.NewGuid().ToString("n")[..10];
        var line = "wl" + Guid.NewGuid().ToString("n")[..10];
        using var scope = R.ScopeOf(_fixture, tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = line, DisplayName = line,
            Fields = [R.Reference("Teacher", header), R.Plain("Hours", "decimal"), R.Plain("Day", "date"), R.Plain("At", "datetime")],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = header, DisplayName = header,
            Fields =
            [
                R.Plain("Name", "string"),
                .. rollups.Select((r, i) => R.Rollup(
                    "R" + i, r.Fn == "sum" ? "decimal" : "int", line, "Teacher", r.Fn, r.Fn == "count" ? null : "Hours",
                    windowKey: r.Key, window: r.Word)),
            ],
        });
        if (zone is not null)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = ShareLinkKeys.SiteType, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["TimeZone"] = zone },
            });
        }

        await session.SaveChangesAsync(Ct);
        return new Setup(tenant, header, line, client);
    }

    private Task<Guid> HeaderAsync(Setup s) => R.SeedAsync(_fixture, s.Header, new() { ["Name"] = "h" }, tenant: s.Tenant);

    private Task<Guid> LineAsync(Setup s, Guid header, Dictionary<string, object>? more = null, DateTime? createdAt = null)
    {
        var data = new Dictionary<string, object> { ["Teacher"] = header.ToString(), ["Hours"] = 1m };
        foreach (var (key, value) in more ?? [])
        {
            data[key] = value;
        }

        return R.SeedAsync(_fixture, s.Line, data, tenant: s.Tenant, createdAt: createdAt);
    }

    private async Task DrainAsync(Setup s, FakeTimeProvider clock)
    {
        await R.DropQueuedDrainsAsync(_fixture, s.Tenant);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RollupDrain.RunAsync(store, configuration, s.Tenant, Ct, clock: clock);
                return;
            }
            catch (Exception e) when (attempt < 5 && TestHelpers.LostConcurrencyCheck(e))
            {
                // A write to the header committed after the drain read it. The next run reads it again.
            }
        }
    }

    private async Task MarkAsync(Setup s, Guid header)
    {
        using var scope = R.ScopeOf(_fixture, s.Tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new RollupMark { Id = header, HeaderType = s.Header, MarkedAt = DateTimeOffset.UtcNow });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<decimal?> ValueAsync(Setup s, Guid header, string field = "R0") =>
        R.Num(await R.StoredAsync(_fixture, header, s.Tenant), field);

    [Fact]
    public async Task A_createdat_window_counts_by_the_day_in_the_site_zone_and_the_next_day_moves_it()
    {
        var s = await SetupAsync("Asia/Manila", Roll("createdAt", "thisMonth"), Roll("createdAt", "lastMonth"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, createdAt: Utc("2026-11-30T15:30:00Z"));

        await DrainAsync(s, At("2026-11-30T15:40:00Z"));
        (await ValueAsync(s, header, "R0")).Should().Be(1);
        (await ValueAsync(s, header, "R1")).Should().Be(0);

        await MarkAsync(s, header);
        await DrainAsync(s, At("2026-11-30T16:10:00Z"));
        (await ValueAsync(s, header, "R0")).Should().Be(0, "it is 1 December in Manila");
        (await ValueAsync(s, header, "R1")).Should().Be(1, "the same entry is last month's now");
    }

    [Fact]
    public async Task The_same_instant_falls_on_another_day_in_a_utc_site()
    {
        var utc = await SetupAsync(null, Roll("createdAt", "thisMonth"));
        var manila = await SetupAsync("Asia/Manila", Roll("createdAt", "thisMonth"));
        var inUtc = await HeaderAsync(utc);
        var inManila = await HeaderAsync(manila);
        var created = Utc("2026-11-30T15:00:00Z");
        await LineAsync(utc, inUtc, createdAt: created);
        await LineAsync(manila, inManila, createdAt: created);

        await DrainAsync(utc, At("2026-11-30T20:30:00Z"));
        await DrainAsync(manila, At("2026-11-30T20:30:00Z"));

        (await ValueAsync(utc, inUtc)).Should().Be(1, "the entry and the drain are both on 30 November in UTC");
        (await ValueAsync(manila, inManila)).Should().Be(0,
            "the entry is 23:00 on 30 November in Manila and the drain runs on 1 December, so the entry is last month's");
    }

    [Fact]
    public async Task A_zone_that_skips_local_midnight_resolves_its_window_and_the_drain_completes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        zone.IsInvalidTime(new DateTime(2026, 3, 8, 0, 30, 0, DateTimeKind.Unspecified))
            .Should().BeTrue("Havana skips 00:00 to 01:00 on 8 March 2026");
        var s = await SetupAsync("America/Havana", Roll("createdAt", "today", "count"), Roll("createdAt", "today", "sum"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, createdAt: Utc("2026-03-08T05:30:00Z"));
        await LineAsync(s, header, createdAt: Utc("2026-03-08T04:30:00Z"));

        await DrainAsync(s, At("2026-03-08T12:00:00Z"));

        (await ValueAsync(s, header)).Should().Be(1, "the first valid instant of the day is 05:00 UTC");
        (await ValueAsync(s, header, "R1")).Should().Be(1);
    }

    [Fact]
    public async Task A_date_field_window_counts_for_sum_and_count_and_editing_the_date_moves_the_entry()
    {
        var s = await SetupAsync("Asia/Manila", Roll("Day", "thisMonth"), Roll("Day", "thisMonth", "sum"));
        var header = await HeaderAsync(s);
        var inside = await LineAsync(s, header, new() { ["Day"] = "2026-11-10", ["Hours"] = 2m });
        await LineAsync(s, header, new() { ["Day"] = "2026-10-31", ["Hours"] = 4m });
        await LineAsync(s, header, new() { ["Day"] = "2026-12-01", ["Hours"] = 8m });
        var clock = At("2026-11-20T04:00:00Z");

        await DrainAsync(s, clock);
        (await ValueAsync(s, header)).Should().Be(1);
        (await ValueAsync(s, header, "R1")).Should().Be(2);

        await EditAsync(s, inside, new() { ["Day"] = "2026-10-15" });
        (await R.MarkedAsync(_fixture, s.Tenant)).Should().Contain(header, "editing the window field is a write that moves the rollup");
        await DrainAsync(s, clock);
        (await ValueAsync(s, header)).Should().Be(0);
        (await ValueAsync(s, header, "R1")).Should().Be(0);

        await EditAsync(s, inside, new() { ["Day"] = "2026-11-02" });
        (await R.MarkedAsync(_fixture, s.Tenant)).Should().Contain(header);
        await DrainAsync(s, clock);
        (await ValueAsync(s, header)).Should().Be(1);
        (await ValueAsync(s, header, "R1")).Should().Be(2);
    }

    private async Task EditAsync(Setup s, Guid line, Dictionary<string, object> changes)
    {
        var content = await R.StoredAsync(_fixture, line, s.Tenant);
        var data = new Dictionary<string, object>(content.Data);
        foreach (var (key, value) in changes)
        {
            data[key] = value;
        }

        var res = await s.Client.PutAsJsonAsync($"/api/contents/{line}", new { id = line, data }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_datetime_field_window_counts_the_instant_in_the_site_zone()
    {
        var s = await SetupAsync("Asia/Manila", Roll("At", "today"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, new() { ["At"] = "2026-11-20T00:30:00+08:00" });
        await LineAsync(s, header, new() { ["At"] = "2026-11-20T15:59:00Z" });
        await LineAsync(s, header, new() { ["At"] = "2026-11-20T16:01:00Z" });
        await LineAsync(s, header, new() { ["At"] = "2026-11-20T09:00:00" });

        await DrainAsync(s, At("2026-11-20T04:00:00Z"));

        (await ValueAsync(s, header)).Should().Be(3, "20 November in Manila is 16:00 UTC on the 19th to 16:00 UTC on the 20th");
    }

    [Fact]
    public async Task A_window_value_that_does_not_parse_is_outside_the_window_and_the_drain_completes()
    {
        var s = await SetupAsync(null, Roll("Day", "thisMonth"), Roll("At", "thisMonth"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, new() { ["Day"] = "2026-11-10", ["At"] = "2026-11-10T01:00:00Z" });
        await LineAsync(s, header, new() { ["Day"] = "not a date", ["At"] = "2026-02-31T01:00:00Z" });
        await LineAsync(s, header, new() { ["Day"] = 12, ["At"] = true });
        await LineAsync(s, header);

        await DrainAsync(s, At("2026-11-20T04:00:00Z"));

        (await ValueAsync(s, header)).Should().Be(1);
        (await ValueAsync(s, header, "R1")).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_site_zone_counts_in_utc_and_warns_once_for_the_run()
    {
        var s = await SetupAsync("Mars/Olympus", Roll("createdAt", "today"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, createdAt: Utc("2026-11-20T23:30:00Z"));
        var log = new ListLogger();
        var clock = new RollupClock(At("2026-11-20T23:40:00Z"), log);

        using (var scope = R.ScopeOf(_fixture, s.Tenant))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var types = await session.Query<ContentTypeDefinition>().ToListAsync(Ct);
            var definition = types.Single(t => t.Name == s.Header);
            var values = new List<object?>();
            for (var i = 0; i < 3; i++)
            {
                var counted = await RollupAggregate.ValuesAsync(session, s.Header, definition.Fields.Single(f => f.Name == "R0"), null, [header], Ct, clock: clock);
                values.Add(counted[header]);
            }

            values.Should().OnlyContain(v => Convert.ToInt64(v) == 1, "20 November in UTC holds 23:30 UTC");
        }

        log.Lines.Should().ContainSingle().Which.Should().Contain(s.Tenant).And.Contain("Mars/Olympus");
        await DrainAsync(s, At("2026-11-20T23:40:00Z"));
        (await ValueAsync(s, header)).Should().Be(1, "the drain completes with the zone it could not read as UTC");
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_drain_counts_every_header_of_a_job_by_the_day_it_started_on()
    {
        var s = await SetupAsync(null, Roll("createdAt", "today"));
        var header = await HeaderAsync(s);
        await LineAsync(s, header, createdAt: Utc("2026-11-20T23:59:00Z"));
        var clock = At("2026-11-20T23:59:30Z");

        await DrainAsync(s, clock);
        clock.Advance(TimeSpan.FromMinutes(5));

        (await ValueAsync(s, header)).Should().Be(1);
    }
}
