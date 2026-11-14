using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class QueryTimestampsTests
{
    private static readonly DateTime Instant = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_local_value_is_converted_to_the_same_instant_in_utc()
    {
        var local = Instant.ToLocalTime();

        var bound = QueryTimestamps.AsUtc(local);

        bound.Kind.Should().Be(DateTimeKind.Utc);
        bound.Should().Be(Instant, "relabelling a local value as UTC would shift it by the host's offset");
    }

    [Fact]
    public void A_value_without_an_offset_is_taken_as_utc()
    {
        var bound = QueryTimestamps.AsUtc(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Unspecified));

        bound.Should().Be(Instant);
        bound.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void A_utc_value_is_unchanged()
    {
        QueryTimestamps.AsUtc(Instant).Should().Be(Instant);
    }

    [Theory]
    [InlineData("2026-10-19T20:00:00+08:00")]
    [InlineData("2026-10-19T04:00:00-08:00")]
    [InlineData("2026-10-19T12:00:00Z")]
    public void A_parsed_timestamp_with_an_explicit_offset_binds_as_that_instant_in_utc(string text)
    {
        // DateTime.Parse turns an offset into the host's local time, so this is Local on any host
        // that is not at UTC+0 and still has to land on the offset's own instant.
        var parsed = DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        var bound = QueryTimestamps.AsUtc(parsed);

        bound.Kind.Should().Be(DateTimeKind.Utc);
        bound.Should().Be(Instant);
    }

    [Fact]
    public void A_value_without_an_offset_keeps_its_clock_reading_and_does_not_move_to_the_host_zone()
    {
        // 20:00 with no offset is 20:00 UTC, not 12:00, and not shifted by whatever zone the host is in.
        var unspecified = DateTime.Parse("2026-10-19T20:00:00", System.Globalization.CultureInfo.InvariantCulture);
        unspecified.Kind.Should().Be(DateTimeKind.Unspecified);

        var bound = QueryTimestamps.AsUtc(unspecified);

        bound.Kind.Should().Be(DateTimeKind.Utc);
        bound.Should().Be(new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc));
    }
}
