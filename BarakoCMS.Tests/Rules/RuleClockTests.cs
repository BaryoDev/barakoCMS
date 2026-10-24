using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Rules;

[Collection("Integration2")]
public class RuleClockTests(IntegrationTestFixture host)
{
    private static RuleDefinition Zoned(string? zone, RuleCondition condition)
    {
        var rule = Rule("clock", condition);
        rule.TimeZone = zone;
        return rule;
    }

    private async Task<bool> PassesAsync(RuleDefinition rule, string at, params (string, object?)[] data)
    {
        var type = await TypeAsync(host, rule);
        return (await RunAsync(host, type, Data(data), clock: new FixedClock(DateTimeOffset.Parse(at)))).Passed;
    }

    [Fact]
    public async Task Now_compares_a_time_with_the_clock()
    {
        var type = await TypeAsync(host, Zoned("UTC", Cond("Start", "gt", "$NOW")));
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-16T00:00:00Z"));

        (await RunAsync(host, type, Data(("Start", "2026-10-16T01:00:00+00:00")), clock: clock)).Passed.Should().BeTrue();
        var past = await RunAsync(host, type, Data(("Start", "2026-10-15T23:00:00Z")), clock: clock);
        past.Passed.Should().BeFalse();
        past.Message.Should().Be("clock failed");
    }

    [Fact]
    public async Task An_offset_moves_the_clock_in_calendar_units()
    {
        var rule = Zoned("UTC", Cond("Start", "lte", "$NOW+2y"));
        (await PassesAsync(rule, "2026-10-16T00:00:00Z", ("Start", "2028-10-15T00:00:00Z"))).Should().BeTrue();
        (await PassesAsync(rule, "2026-10-16T00:00:00Z", ("Start", "2028-10-17T00:00:00Z"))).Should().BeFalse();

        RuleClock.Fill("$NOW+1mo", new RuleValues(DateTimeOffset.Parse("2026-01-31T00:00:00Z"), TimeZoneInfo.Utc, []))
            .Should().Be("2026-02-28T00:00:00.0000000+00:00");
        RuleClock.Fill("$NOW-30m", new RuleValues(DateTimeOffset.Parse("2026-10-16T00:00:00Z"), TimeZoneInfo.Utc, []))
            .Should().Be("2026-10-15T23:30:00.0000000+00:00");
    }

    [Fact]
    public async Task Today_is_the_date_in_the_rule_zone()
    {
        const string at = "2026-10-16T17:00:00Z";
        var manila = Zoned("Asia/Manila", Cond("Day", "eq", "$TODAY"));
        (await PassesAsync(manila, at, ("Day", "2026-10-17"))).Should().BeTrue();
        (await PassesAsync(manila, at, ("Day", "2026-10-16"))).Should().BeFalse();

        var utc = Zoned("UTC", Cond("Day", "eq", "$TODAY"));
        (await PassesAsync(utc, at, ("Day", "2026-10-16"))).Should().BeTrue();
        (await PassesAsync(utc, at, ("Day", "2026-10-17"))).Should().BeFalse();
    }

    [Fact]
    public async Task The_site_zone_is_used_when_the_rule_has_none()
    {
        var site = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "site",
            Status = ContentStatus.Published,
            CreatedAt = DateTime.UtcNow.AddYears(-50),
            Data = new() { ["TimeZone"] = "Asia/Manila" },
        };
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(site);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            (await PassesAsync(Zoned(null, Cond("Day", "eq", "$TODAY")), "2026-10-16T17:00:00Z", ("Day", "2026-10-17")))
                .Should().BeTrue();
        }
        finally
        {
            using var scope = host.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<Content>(site.Id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_unknown_zone_fails_the_rule()
    {
        var type = await TypeAsync(host, Zoned("Mars/Base", Cond("Day", "eq", "$TODAY")));

        (await RunAsync(host, type, Data(("Day", "x")))).Message.Should().Be("Time zone 'Mars/Base' is not known on this server.");
    }

    [Fact]
    public void Dates_compare_as_instants()
    {
        RuleConditions.Compare("gt", "2026-10-16T09:00:00+08:00", "2026-10-16T00:30:00Z").Should().BeTrue();
        RuleConditions.Compare("lt", "2026-10-16T09:00:00+08:00", "2026-10-16T00:30:00Z").Should().BeFalse();
        RuleConditions.Compare("gt", "10", "9").Should().BeTrue();
        RuleConditions.Compare("gt", "garbage", "2026-10-16T00:30:00Z").Should().BeFalse("text has no order against a date");
    }
}
