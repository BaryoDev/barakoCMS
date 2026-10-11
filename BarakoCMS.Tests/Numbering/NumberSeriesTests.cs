using barakoCMS.Core.Interfaces;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

public class NumberSeriesTests
{
    private static readonly NumberSeries ManilaDaily =
        new("receipts", "R-", 5, NumberReset.Daily, "Asia/Manila");

    [Fact]
    public void NumberSeries_DailyInManila_RollsAtLocalMidnight()
    {
        // 15:59:59 UTC is 23:59:59 in Manila (UTC+8); one second later is the next local day.
        ManilaDaily.PeriodFor(new DateTimeOffset(2026, 10, 11, 15, 59, 59, TimeSpan.Zero)).Should().Be("2026-10-11");
        ManilaDaily.PeriodFor(new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero)).Should().Be("2026-10-12");
    }

    [Theory]
    [InlineData(NumberReset.Never, "")]
    [InlineData(NumberReset.Yearly, "2026")]
    [InlineData(NumberReset.Monthly, "2026-10")]
    [InlineData(NumberReset.Daily, "2026-10-11")]
    public void NumberSeries_ResetKinds_GivePeriod(NumberReset reset, string expected)
    {
        new NumberSeries("s", Reset: reset).PeriodFor(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero))
            .Should().Be(expected);
    }

    [Fact]
    public void NumberSeries_NoTimeZone_UsesUtc()
    {
        new NumberSeries("s", Reset: NumberReset.Daily)
            .PeriodFor(new DateTimeOffset(2026, 10, 11, 23, 30, 0, TimeSpan.FromHours(-5)))
            .Should().Be("2026-10-12");
    }

    [Fact]
    public void Format_WithPeriod_JoinsPrefixPeriodAndPaddedValue()
    {
        ManilaDaily.Format(42, "2026-10-11").Should().Be("R-2026-10-11-00042");
    }

    [Fact]
    public void Format_NoPeriod_PrefixAndPaddedValue()
    {
        new NumberSeries("s", "INV-", 6).Format(7, "").Should().Be("INV-000007");
    }

    [Fact]
    public void Format_ValueWiderThanPadding_IsNotTruncated()
    {
        new NumberSeries("s", Padding: 2).Format(12345, "").Should().Be("12345");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("a/b")]
    public void Validate_BadName_Throws(string name)
    {
        var act = () => new NumberSeries(name).Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*series name*");
    }

    [Fact]
    public void Validate_PaddingOutOfRange_Throws()
    {
        var act = () => new NumberSeries("s", Padding: 0).Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Padding*");
    }

    [Fact]
    public void Validate_UnknownTimeZone_Throws()
    {
        var act = () => new NumberSeries("s", TimeZoneId: "Mars/Olympus").Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Mars/Olympus*");
    }
}
