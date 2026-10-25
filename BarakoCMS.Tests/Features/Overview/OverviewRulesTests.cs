using barakoCMS.Features.Overview;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace BarakoCMS.Tests.Features.Overview;

public class OverviewRulesTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, 10, "ok")]
    [InlineData(HealthStatus.Healthy, 500, "warn")]
    [InlineData(HealthStatus.Healthy, 2000, "bad")]
    [InlineData(HealthStatus.Degraded, 10, "warn")]
    [InlineData(HealthStatus.Unhealthy, 10, "bad")]
    public void Database_follows_its_check_and_its_speed(HealthStatus check, long ms, string expected)
    {
        var result = OverviewRules.Database(check, ms);

        result.Status.Should().Be(expected);
        result.Strip!.Ms.Should().Be(ms);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, "ok")]
    [InlineData(HealthStatus.Degraded, "warn")]
    [InlineData(HealthStatus.Unhealthy, "bad")]
    public void Storage_follows_the_disk_check(HealthStatus check, string expected) =>
        OverviewRules.Storage(check).Status.Should().Be(expected);

    [Fact]
    public void Runner_is_an_item_only_when_it_is_not_ok()
    {
        OverviewRules.Runner(HealthStatus.Healthy).Item.Should().BeNull();

        var stalled = OverviewRules.Runner(HealthStatus.Unhealthy);

        stalled.Status.Should().Be("bad");
        stalled.Item.Should().NotBeNull();
        stalled.Item!.Id.Should().Be("runner.stalled");
        stalled.Item.Resolvable.Should().BeFalse();
    }

    [Fact]
    public void Mail_is_delivering_until_a_bounce_waits()
    {
        var clear = OverviewRules.Mail(0);
        var waiting = OverviewRules.Mail(2);

        clear.Status.Should().Be("ok");
        clear.Strip!.Detail.Should().Be("Delivering.");
        waiting.Status.Should().Be("warn");
        waiting.Strip!.Detail.Should().Be("2 bounces waiting.");
    }

    [Fact]
    public void Bounces_is_a_resolvable_item_only_while_one_is_unresolved()
    {
        OverviewRules.Bounces(0).Item.Should().BeNull();

        var one = OverviewRules.Bounces(1);

        one.Item.Should().NotBeNull();
        one.Item!.Id.Should().Be("mail.bounces");
        one.Item.Resolvable.Should().BeTrue();
        one.Item.Text.Should().Be("1 email bounce to look at.");
    }

    [Theory]
    [InlineData("ok", false)]
    [InlineData("warn", true)]
    [InlineData("bad", true)]
    public void FailedRuns_is_an_item_when_the_day_is_not_ok(string verdict, bool item)
    {
        var result = OverviewRules.FailedRuns(verdict, 3, 10);

        (result.Item is not null).Should().Be(item);
        result.Status.Should().Be(verdict);
        if (item) result.Item!.Severity.Should().Be(verdict);
    }

    [Fact]
    public void OverduePublishes_is_an_item_only_when_some_are_overdue()
    {
        OverviewRules.OverduePublishes(0).Item.Should().BeNull();

        var two = OverviewRules.OverduePublishes(2);

        two.Status.Should().Be("warn");
        two.Item!.Text.Should().Be("2 scheduled publishes past due.");
    }

    [Fact]
    public void Verdict_is_the_worst_status_and_ok_for_nothing()
    {
        OverviewRules.Verdict([]).Should().Be("ok");
        OverviewRules.Verdict(["ok", "warn"]).Should().Be("warn");
        OverviewRules.Verdict(["warn", "bad", "ok"]).Should().Be("bad");
    }

    [Fact]
    public void Headline_counts_rules_once_and_names_problems_over_warnings()
    {
        OverviewRules.Headline([new("a", "ok")]).Should().Be("All systems normal");
        OverviewRules.Headline([new("mail", "warn"), new("mail", "warn")]).Should().Be("1 warning");
        OverviewRules.Headline([new("mail", "warn"), new("db", "warn")]).Should().Be("2 warnings");
        OverviewRules.Headline([new("mail", "warn"), new("db", "bad")]).Should().Be("2 problems");
    }

    [Fact]
    public void Order_puts_bad_first_then_by_id()
    {
        var items = new[]
        {
            new NeedsYouItem { Id = "b", Severity = "warn" },
            new NeedsYouItem { Id = "z", Severity = "bad" },
            new NeedsYouItem { Id = "a", Severity = "warn" },
        };

        OverviewRules.Order(items).Select(i => i.Id).Should().Equal("z", "a", "b");
    }
}
