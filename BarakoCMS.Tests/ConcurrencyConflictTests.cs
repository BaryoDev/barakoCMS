using FluentAssertions;
using Xunit;
using barakoCMS;

namespace BarakoCMS.Tests;

/// <summary>
/// A save that loses on more than one versioned document is reported as an AggregateException of
/// conflicts, and every place that handles a lost race has to see it as one.
/// </summary>
public class ConcurrencyConflictTests
{
    private static Exception Conflict() => new JasperFx.ConcurrencyException("lost", new InvalidOperationException("inner"));

    [Fact]
    public void A_single_conflict_is_one()
    {
        ConcurrencyConflict.Is(Conflict()).Should().BeTrue();
    }

    [Fact]
    public void An_aggregate_of_two_conflicts_is_one()
    {
        ConcurrencyConflict.Is(new AggregateException(Conflict(), Conflict())).Should().BeTrue();
    }

    [Fact]
    public void A_nested_aggregate_of_conflicts_is_one()
    {
        var nested = new AggregateException(new AggregateException(Conflict(), Conflict()), Conflict());

        ConcurrencyConflict.Is(nested).Should().BeTrue();
    }

    [Fact]
    public void An_aggregate_that_also_holds_a_real_failure_is_not_one()
    {
        ConcurrencyConflict.Is(new AggregateException(Conflict(), new InvalidOperationException("boom"))).Should().BeFalse();
    }

    [Fact]
    public void An_empty_aggregate_is_not_one()
    {
        ConcurrencyConflict.Is(new AggregateException()).Should().BeFalse();
    }

    [Fact]
    public void A_plain_exception_is_not_one()
    {
        ConcurrencyConflict.Is(new InvalidOperationException("boom")).Should().BeFalse();
    }
}
