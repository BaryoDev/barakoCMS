using barakoCMS.Features.Queries.Run;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>The relative day words, the days each stands for, and the UTC bounds of those days in a zone.</summary>
public class RelativeDateWindowTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Theory]
    [InlineData("today", "2026-11-18", "2026-11-18")]
    [InlineData("last7Days", "2026-11-12", "2026-11-18")]
    [InlineData("last30Days", "2026-10-20", "2026-11-18")]
    [InlineData("thisMonth", "2026-11-01", "2026-11-30")]
    [InlineData("lastMonth", "2026-10-01", "2026-10-31")]
    [InlineData("thisYear", "2026-01-01", "2026-12-31")]
    public void Every_word_resolves_to_its_days(string word, string from, string to)
    {
        RelativeDateWindow.Resolve(word, Today).Should().Be((DateOnly.Parse(from), DateOnly.Parse(to)));
        RelativeDateWindow.Resolve(word.ToUpperInvariant(), Today).Should().Be((DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    [Fact]
    public void The_list_holds_the_six_words_and_each_is_a_word()
    {
        RelativeDateWindow.Words.Should().HaveCount(6);
        RelativeDateWindow.Words.Should().OnlyContain(w => RelativeDateWindow.IsWord(w));
        RelativeDateWindow.IsWord(" ThisMonth ").Should().BeTrue("a word is matched without case and trimmed");
    }

    [Theory]
    [InlineData("nextMonth")]
    [InlineData("")]
    [InlineData("this month")]
    public void Another_word_is_not_a_word_and_does_not_resolve(string text)
    {
        RelativeDateWindow.IsWord(text).Should().BeFalse();
        RelativeDateWindow.Canonical(text).Should().BeNull();
        var resolve = () => RelativeDateWindow.Resolve(text, Today);
        resolve.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_bounds_are_the_local_midnights_in_utc_wall_time()
    {
        var manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

        var (from, to) = RelativeDateWindow.UtcBounds(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), manila);

        from.Should().Be(new DateTime(2026, 10, 31, 16, 0, 0));
        to.Should().Be(new DateTime(2026, 11, 30, 16, 0, 0));
        from.Kind.Should().Be(DateTimeKind.Unspecified, "it binds as a timestamp, the type the CreatedAt index is on");
        to.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void A_midnight_a_summer_time_change_skips_resolves_to_the_first_valid_instant()
    {
        var havana = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        var skipped = new DateOnly(2026, 3, 8);
        havana.IsInvalidTime(skipped.ToDateTime(TimeOnly.MinValue)).Should().BeTrue("the zone skips its own midnight that day");

        var (from, _) = RelativeDateWindow.UtcBounds(skipped, skipped, havana);

        from.Should().Be(new DateTime(2026, 3, 8, 5, 0, 0), "01:00 local, the first valid instant, at UTC-4");
    }
}
