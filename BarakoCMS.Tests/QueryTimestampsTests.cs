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
}
