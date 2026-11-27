using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Models;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// The daily rollup sweep: which headers it marks and when, its cursor across pages, its lock, and
/// that a recount after it moves a windowed value with no write.
/// </summary>
/// <remarks>
/// Each test works in a tenant of its own, except the default partition test, which uses a day no
/// other test uses. The sweep takes a fixed clock, and the drain jobs it queued are taken out of the
/// queue before a test reads the marks.
/// </remarks>
[Collection("Integration1")]
public class RollupSweepTests
{
    private readonly IntegrationTestFixture _fixture;

    public RollupSweepTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    private static FakeTimeProvider At(string utc) => new(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

    private static DateTime Utc(string utc) =>
        DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private sealed record Setup(string? Tenant, string Header, string Line);

    /// <summary>
    /// A tenant (or the default partition) with a line type and a header type that has one rollup,
    /// windowed on createdAt unless <paramref name="window"/> is null.
    /// </summary>
    private async Task<Setup> SetupAsync(string? zone, string? window = "thisMonth", bool ownTenant = true)
    {
        var tenant = ownTenant ? (await R.OwnTenantAsync(_fixture)).Slug : R.Tenant;
        var header = "sh" + Guid.NewGuid().ToString("n")[..10];
        var line = "sl" + Guid.NewGuid().ToString("n")[..10];
        using var scope = R.ScopeOf(_fixture, tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = line, DisplayName = line,
            Fields = [R.Reference("Teacher", header), R.Plain("Hours", "decimal")],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = header, DisplayName = header,
            Fields =
            [
                R.Plain("Name", "string"),
                R.Rollup("R0", "int", line, "Teacher", "count", null, windowKey: window is null ? null : "createdAt", window: window),
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
        return new Setup(ownTenant ? tenant : null, header, line);
    }

    private string Partition(Setup s) => s.Tenant ?? R.Tenant;

    private async Task<List<Guid>> HeadersAsync(Setup s, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        using var scope = R.ScopeOf(_fixture, Partition(s));
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        foreach (var id in ids)
        {
            await writer.CreateAsync(
                new barakoCMS.Events.ContentCreated(
                    id, s.Header, new() { ["Name"] = "h" }, ContentStatus.Published, Guid.NewGuid(), null,
                    SensitivityLevel.Public, DateTime.UtcNow), Ct);
        }

        await session.SaveChangesAsync(Ct);
        return ids;
    }

    private Task<Guid> LineAsync(Setup s, Guid header, DateTime createdAt) =>
        R.SeedAsync(_fixture, s.Line, new() { ["Teacher"] = header.ToString(), ["Hours"] = 1m }, tenant: Partition(s), createdAt: createdAt);

    /// <summary>Drains until no mark is left, on the clock given.</summary>
    private async Task DrainAllAsync(Setup s, FakeTimeProvider clock)
    {
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        for (var round = 0; round < 8; round++)
        {
            await R.DropQueuedDrainsAsync(_fixture, Partition(s));
            if ((await R.MarkedAsync(_fixture, Partition(s))).Count == 0)
            {
                return;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await RollupDrain.RunAsync(Store, configuration, Partition(s), Ct, clock: clock);
                    break;
                }
                catch (Exception e) when (attempt < 5 && TestHelpers.LostConcurrencyCheck(e))
                {
                    // A write to a header committed after the drain read it. The next run reads it again.
                }
            }
        }
    }

    private async Task<int> SweepAsync(Setup s, FakeTimeProvider clock, int? pages = null)
    {
        var marked = await RollupSweep.RunAsync(Store, s.Tenant, new RollupClock(clock), Ct, pages);
        await R.DropQueuedDrainsAsync(_fixture, Partition(s));
        return marked;
    }

    private async Task<RollupSweepState?> StateAsync(Setup s)
    {
        using var scope = R.ScopeOf(_fixture, Partition(s));
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<RollupSweepState>(RollupSweepState.Key, Ct);
    }

    private async Task StoreStateAsync(Setup s, RollupSweepState state)
    {
        using var scope = R.ScopeOf(_fixture, Partition(s));
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(state);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<decimal?> ValueAsync(Setup s, Guid header) => R.Num(await R.StoredAsync(_fixture, header, Partition(s)), "R0");

    [Fact]
    public async Task A_second_sweep_on_the_same_local_day_marks_nothing()
    {
        var s = await SetupAsync(null);
        var headers = await HeadersAsync(s, 3);
        var clock = At("2026-11-24T05:00:00Z");
        await DrainAllAsync(s, clock);

        (await SweepAsync(s, clock)).Should().Be(3);
        await DrainAllAsync(s, clock);

        (await SweepAsync(s, clock)).Should().Be(0);
        (await R.MarkedAsync(_fixture, Partition(s))).Should().BeEmpty();
        (await StateAsync(s))!.Done.Should().BeTrue();
        headers.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_sweep_over_many_headers_runs_in_pages_and_every_header_is_recounted()
    {
        var s = await SetupAsync(null);
        var headers = await HeadersAsync(s, 1200);
        foreach (var header in headers.Take(3))
        {
            await LineAsync(s, header, Utc("2026-11-10T01:00:00Z"));
        }

        await DrainAllAsync(s, At("2026-11-20T05:00:00Z"));
        (await ValueAsync(s, headers[0])).Should().Be(1);

        var december = At("2026-12-01T05:00:00Z");
        (await SweepAsync(s, december)).Should().Be(1200, "three pages of 500, 500 and 200");
        (await R.MarkedAsync(_fixture, Partition(s))).Should().BeEquivalentTo(headers);

        await DrainAllAsync(s, december);
        foreach (var header in headers.Take(3))
        {
            (await ValueAsync(s, header)).Should().Be(0, "no write moved the entry, the new month did");
        }
    }

    [Fact]
    public async Task A_sweep_stopped_after_its_first_page_resumes_from_the_cursor_and_marks_no_header_twice()
    {
        var s = await SetupAsync(null);
        var headers = await HeadersAsync(s, 1200);
        var clock = At("2026-11-24T05:00:00Z");
        await DrainAllAsync(s, clock);

        (await SweepAsync(s, clock, pages: 1)).Should().Be(RollupSweep.PageSize);
        var first = await R.MarkedAsync(_fixture, Partition(s));
        var state = (await StateAsync(s))!;
        state.Done.Should().BeFalse();
        state.AfterId.Should().NotBeNull();
        state.HeaderType.Should().Be(s.Header);
        await DrainAllAsync(s, clock);

        (await SweepAsync(s, clock)).Should().Be(700);
        var second = await R.MarkedAsync(_fixture, Partition(s));
        second.Should().NotIntersectWith(first);
        first.Concat(second).Should().BeEquivalentTo(headers);
        (await StateAsync(s))!.Done.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-11-29T05:00:00Z")]
    [InlineData("2026-11-19T05:00:00Z")]
    public async Task A_state_for_another_day_starts_over(string storedDay)
    {
        var s = await SetupAsync(null);
        var headers = await HeadersAsync(s, 3);
        var clock = At("2026-11-24T05:00:00Z");
        await DrainAllAsync(s, clock);
        await StoreStateAsync(s, new RollupSweepState
        {
            Day = DateOnly.FromDateTime(DateTime.Parse(storedDay, System.Globalization.CultureInfo.InvariantCulture)),
            HeaderType = s.Header, AfterId = Guid.NewGuid(), Done = true,
        });

        (await SweepAsync(s, clock)).Should().Be(3);

        (await R.MarkedAsync(_fixture, Partition(s))).Should().BeEquivalentTo(headers);
        (await StateAsync(s))!.Day.Should().Be(new DateOnly(2026, 11, 24));
    }

    [Fact]
    public async Task Two_sweeps_of_one_tenant_at_once_leave_the_second_stopped_by_the_lock()
    {
        var s = await SetupAsync(null);
        await HeadersAsync(s, 3);
        var clock = At("2026-11-24T05:00:00Z");
        await DrainAllAsync(s, clock);

        await using (var holder = Store.LightweightSession(s.Tenant!))
        {
            await holder.BeginTransactionAsync(Ct);
            await holder.QueryAsync<int>(
                "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", Ct, $"barakocms:rollup-sweep:{s.Tenant}");

            (await SweepAsync(s, clock)).Should().Be(0, "another instance holds the lock");
            (await StateAsync(s)).Should().BeNull();
        }

        (await SweepAsync(s, clock)).Should().Be(3);
    }

    [Fact]
    public async Task The_default_partition_is_swept()
    {
        var s = await SetupAsync(null, ownTenant: false);
        var headers = await HeadersAsync(s, 2);
        await StoreStateAsync(s, new RollupSweepState { Day = new DateOnly(2031, 3, 5), Done = true });
        await DrainAllAsync(s, At("2031-03-05T05:00:00Z"));
        (await R.MarkedAsync(_fixture, R.Tenant)).Should().NotContain(headers);

        await SweepAsync(s, At("2031-03-06T05:00:00Z"));

        (await R.MarkedAsync(_fixture, R.Tenant)).Should().Contain(headers);
        (await StateAsync(s))!.Day.Should().Be(new DateOnly(2031, 3, 6));
    }

    [Fact]
    public async Task A_type_with_no_window_is_swept_on_a_local_monday_and_not_on_a_tuesday()
    {
        var monday = At("2026-11-23T05:00:00Z");
        var tuesday = At("2026-11-24T05:00:00Z");
        monday.GetUtcNow().DayOfWeek.Should().Be(DayOfWeek.Monday);
        tuesday.GetUtcNow().DayOfWeek.Should().Be(DayOfWeek.Tuesday);
        var onTuesday = await SetupAsync(null, window: null);
        var onMonday = await SetupAsync(null, window: null);
        await HeadersAsync(onTuesday, 2);
        await HeadersAsync(onMonday, 2);
        await DrainAllAsync(onTuesday, tuesday);
        await DrainAllAsync(onMonday, monday);

        (await SweepAsync(onTuesday, tuesday)).Should().Be(0);
        (await SweepAsync(onMonday, monday)).Should().Be(2);
        (await R.MarkedAsync(_fixture, Partition(onTuesday))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_value_that_went_wrong_is_right_after_the_monday_sweep()
    {
        var s = await SetupAsync(null, window: null);
        var header = (await HeadersAsync(s, 1))[0];
        await R.SeedAsync(_fixture, s.Line, new() { ["Teacher"] = header.ToString(), ["Hours"] = 1m }, tenant: Partition(s));
        var monday = At("2026-11-23T05:00:00Z");
        await DrainAllAsync(s, monday);
        (await ValueAsync(s, header)).Should().Be(1);

        using (var scope = R.ScopeOf(_fixture, Partition(s)))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var entry = (await session.LoadAsync<Content>(header, Ct))!;
            entry.Data["R0"] = 77m;
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        (await ValueAsync(s, header)).Should().Be(77);
        await SweepAsync(s, monday);
        await DrainAllAsync(s, monday);

        (await ValueAsync(s, header)).Should().Be(1);
    }

    [Fact]
    public async Task A_tenant_with_no_rollups_writes_nothing_but_the_state()
    {
        var (tenant, _) = await R.OwnTenantAsync(_fixture);
        var s = new Setup(tenant, "none", "none");

        (await SweepAsync(s, At("2026-11-23T05:00:00Z"))).Should().Be(0);

        (await R.MarkedAsync(_fixture, tenant)).Should().BeEmpty();
        var state = await StateAsync(s);
        state!.Done.Should().BeTrue();
        state.HeaderType.Should().BeNull();
    }

    [Fact]
    public async Task A_createdat_month_window_drops_an_entry_when_the_sweep_finds_local_midnight_passed()
    {
        var s = await SetupAsync("Asia/Manila", "thisMonth");
        var header = (await HeadersAsync(s, 1))[0];
        await LineAsync(s, header, Utc("2026-11-30T15:30:00Z"));
        var before = At("2026-11-30T15:40:00Z");
        await DrainAllAsync(s, before);
        (await ValueAsync(s, header)).Should().Be(1);

        (await SweepAsync(s, before)).Should().Be(1, "a first sweep of 30 November marks the header, which counts the same");
        await DrainAllAsync(s, before);
        (await ValueAsync(s, header)).Should().Be(1);

        var after = At("2026-11-30T16:10:00Z");
        (await SweepAsync(s, after)).Should().Be(1, "it is 1 December in Manila, a new day");
        await DrainAllAsync(s, after);

        (await ValueAsync(s, header)).Should().Be(0);
    }

    [Fact]
    public async Task The_tick_lists_the_default_partition_when_it_is_served_and_each_active_tenant()
    {
        var s = await SetupAsync(null);
        var service = new RollupSweepService(
            Store, _fixture.Services.GetRequiredService<IConfiguration>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RollupSweepService>.Instance);

        var partitions = await service.PartitionsAsync(Ct);

        partitions.Should().Contain(s.Tenant);
        partitions.Contains(null).Should().Be(
            barakoCMS.Infrastructure.Multitenancy.TenantPartitions.ServesDefaultPartition(
                _fixture.Services.GetRequiredService<IConfiguration>()));
    }

    [Fact]
    public async Task A_tick_that_cannot_read_the_tenant_list_logs_the_failure_and_does_not_throw()
    {
        var store = Substitute.For<IDocumentStore>();
        store.QuerySession().Returns(_ => throw new InvalidOperationException("the database is not there"));
        var log = new ListLogger();
        var service = new RollupSweepService(store, new ConfigurationBuilder().Build(), log);

        // A throw out of ExecuteAsync stops the whole host, so a failed tick must end here.
        await service.Invoking(x => x.TickAsync(Ct)).Should().NotThrowAsync();

        log.Of(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("next one runs");
    }

    [Fact]
    public async Task The_service_warns_about_an_unknown_zone_once_a_day_not_once_a_tick()
    {
        var s = await SetupAsync("Mars/Olympus");
        var log = new ListLogger();
        var service = new RollupSweepService(Store, _fixture.Services.GetRequiredService<IConfiguration>(), log);

        using var scope = R.ScopeOf(_fixture, s.Tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var at in new[] { "2026-11-20T00:10:00Z", "2026-11-20T00:15:00Z", "2026-11-20T23:55:00Z", "2026-11-21T00:00:00Z" })
        {
            await service.NewClock(At(at)).TodayAsync(session, Ct);
        }

        log.Of(LogLevel.Warning).Should().HaveCount(2, "the unknown zone counts in UTC, and that is 20 and 21 November");
    }

    private sealed class ListLogger : ILogger<RollupSweepService>
    {
        private readonly List<(LogLevel Level, string Text)> _lines = [];

        public IEnumerable<string> Of(LogLevel level) => _lines.Where(l => l.Level == level).Select(l => l.Text);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Add((level, formatter(state, exception)));
    }
}
