using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: what counts as a correlation id and as a traceparent, and which span a scope
/// reports as the cause. Both values are stored and logged, and both are read back from the
/// database, so the rule is tested on its own.
/// </summary>
public class CorrelationTests
{
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Theory]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("order_17.retry-2")]
    [InlineData("a")]
    public void An_id_of_letters_digits_dot_underscore_and_hyphen_is_kept(string id)
    {
        Correlation.Normalise(id).Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    [InlineData("a,b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("<script>")]
    [InlineData("user@example.com")]
    [InlineData("café")]
    public void Anything_else_is_not_an_id(string? value)
    {
        Correlation.Normalise(value).Should().BeNull();
    }

    [Fact]
    public void The_length_limit_is_64_characters()
    {
        Correlation.Normalise(new string('a', 64)).Should().HaveLength(64);
        Correlation.Normalise(new string('a', 65)).Should().BeNull();
    }

    [Fact]
    public void A_minted_id_is_a_valid_one()
    {
        var id = Correlation.NewId();

        id.Should().MatchRegex("^[0-9a-f]{32}$");
        Correlation.Normalise(id).Should().Be(id);
    }

    [Fact]
    public void A_W3C_traceparent_is_kept()
    {
        Correlation.NormaliseTraceParent(TraceParent).Should().Be(TraceParent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-and-more")]
    [InlineData("00-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7\n01")]
    [InlineData("|4bf92f3577b34da6a3ce929d0e0e4736.00f067aa0ba902b7.1.2.3.")]
    public void Anything_else_is_not_a_traceparent(string? value)
    {
        Correlation.NormaliseTraceParent(value).Should().BeNull();
    }

    [Fact]
    public void A_stored_id_that_is_not_one_begins_a_scope_with_none()
    {
        using var scope = Correlation.BeginFrom("not an id\nat all", "not a traceparent");

        Correlation.Id.Should().BeNull("a value read back from the database is checked like one from a header");
        Correlation.Cause.Should().BeNull();
    }

    [Fact]
    public void A_scope_begun_for_a_request_reports_the_current_span_as_the_cause()
    {
        using var span = StartSpan();
        using var scope = Correlation.Begin("req-1");

        Correlation.Id.Should().Be("req-1");
        Correlation.Cause.Should().Be(span.Id);
        Correlation.Cause.Should().StartWith($"00-{span.TraceId.ToHexString()}-{span.SpanId.ToHexString()}-");
    }

    [Fact]
    public void A_scope_begun_from_something_stored_keeps_the_stored_cause_whatever_span_is_current()
    {
        using var span = StartSpan();
        using var scope = Correlation.BeginFrom("req-2", TraceParent);

        Correlation.Cause.Should().Be(TraceParent,
            "a run is caused by the request that wrote the event, not by the span that happened to queue it");
    }

    [Fact]
    public void A_scope_begun_from_something_stored_with_no_cause_borrows_none()
    {
        using var span = StartSpan();
        using var scope = Correlation.BeginFrom("req-3", null);

        Correlation.Cause.Should().BeNull(
            "an event written before causes were kept has none, and the current span did not cause it");
    }

    [Fact]
    public void Ending_a_scope_puts_back_the_one_it_was_begun_inside()
    {
        using (Correlation.Begin("outer"))
        {
            using (Correlation.BeginFrom("inner", TraceParent))
            {
                Correlation.Id.Should().Be("inner");
            }

            Correlation.Id.Should().Be("outer");
        }

        Correlation.Id.Should().BeNull();
    }

    [Fact]
    public void A_span_in_the_older_hierarchical_format_has_no_trace_id_and_is_no_cause()
    {
        using var span = new Activity("legacy");
        span.SetIdFormat(ActivityIdFormat.Hierarchical);
        span.Start();

        Correlation.TraceIdOf(span).Should().BeNull();
        Correlation.TraceParentOf(span).Should().BeNull();
    }

    private static Activity StartSpan()
    {
        var span = new Activity("span under test");
        span.SetIdFormat(ActivityIdFormat.W3C);
        span.Start();
        return span;
    }
}
