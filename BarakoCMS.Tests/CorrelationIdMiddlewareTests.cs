using System.Diagnostics;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Middleware;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: the correlation id of a request is the caller's when that is a valid one, else the
/// request's trace id, and a header that is not an id is never echoed, logged or stored.
/// </summary>
/// <remarks>
/// The middleware is run on its own over a <see cref="DefaultHttpContext"/>, because a header with
/// a line break in it cannot be sent through an <c>HttpClient</c>. What is asserted is the id the
/// rest of the request sees (<see cref="Correlation.Id"/>, which is what is stamped on events and
/// what the log context carries) and the header written when the response starts.
///
/// A span here is a plain <see cref="Activity"/> started by the test. It is current for this test's
/// own flow only and nothing listens to it.
/// </remarks>
public class CorrelationIdMiddlewareTests
{
    private const string Header = "X-Correlation-ID";

    private static readonly Regex ThirtyTwoHex = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task A_valid_id_from_the_caller_is_kept()
    {
        var (seen, echoed) = await RunAsync("order-2026.10_02-A7");

        seen.Should().Be("order-2026.10_02-A7");
        echoed.Should().Be("order-2026.10_02-A7");
    }

    [Fact]
    public async Task A_10_KB_header_is_replaced_and_never_echoed()
    {
        var huge = new string('a', 10 * 1024);

        var (seen, echoed) = await RunAsync(huge);

        seen.Should().MatchRegex(ThirtyTwoHex, "a header past the length limit is not an id, so a fresh one is used");
        echoed.Should().Be(seen, "the response carries the id the request ran under, not what was sent");
    }

    [Theory]
    [InlineData("abc\r\nX-Injected: 1")]
    [InlineData("abc\ndef")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("quote\"d")]
    [InlineData("café")]
    public async Task A_header_with_anything_but_id_characters_is_replaced_and_never_echoed(string sent)
    {
        var (seen, echoed) = await RunAsync(sent);

        seen.Should().MatchRegex(ThirtyTwoHex);
        seen.Should().NotBe(sent);
        echoed.Should().Be(seen);
    }

    [Fact]
    public async Task With_no_id_from_the_caller_the_trace_id_of_the_request_is_used()
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new Activity("request under test");
        request.SetParentId(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        request.Start();

        var (seen, echoed) = await RunAsync(sent: null);

        seen.Should().Be(traceId.ToHexString(),
            "a caller that sent only traceparent should find its own trace id as the correlation id");
        echoed.Should().Be(traceId.ToHexString());
        request.GetTagItem(BarakoTracing.CorrelationIdTag).Should().Be(traceId.ToHexString(),
            "the request span carries the id, so a trace can be found from a log line and back");
    }

    [Fact]
    public async Task The_callers_id_wins_over_the_trace_id()
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new Activity("request under test");
        request.SetParentId(traceId, ActivitySpanId.CreateRandom());
        request.Start();

        var (seen, _) = await RunAsync("mine-123");

        seen.Should().Be("mine-123", "a client that already correlates by its own id keeps doing so");
        request.GetTagItem(BarakoTracing.CorrelationIdTag).Should().Be("mine-123");
    }

    /// <remarks>
    /// A <c>traceparent</c> or <c>Request-Id</c> that is not W3C reaches the request span as its
    /// parent id, unparsed. Whether the span then has a real trace id depends on a process-wide
    /// switch the OpenTelemetry SDK sets, so the assertion is the one that holds either way: what
    /// was sent is nowhere in the id.
    /// </remarks>
    [Fact]
    public async Task A_parent_id_that_is_not_W3C_never_becomes_the_correlation_id()
    {
        const string sent = "|forged-root\r\nline two.1.";
        using var request = new Activity("request under test");
        request.SetParentId(sent);
        request.Start();

        var (seen, echoed) = await RunAsync(sent: null);

        seen.Should().MatchRegex(ThirtyTwoHex);
        seen.Should().NotBe(new string('0', 32), "an all-zero trace id is the absence of one, not an id");
        echoed.Should().Be(seen);
    }

    /// <remarks>
    /// Read from inside the pipeline, after it has yielded, which is where an endpoint opens its
    /// session and saves. Reading it back out here after the request would prove nothing: a value
    /// set inside an awaited method never reaches its caller, with or without the middleware.
    /// </remarks>
    [Fact]
    public async Task The_id_is_still_ambient_after_the_rest_of_the_pipeline_has_awaited()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[Header] = "inside-1";

        string? before = null;
        string? after = null;
        var middleware = new CorrelationIdMiddleware(async _ =>
        {
            before = Correlation.Id;
            await Task.Yield();
            await Task.Delay(1);
            after = Correlation.Id;
        });

        await middleware.Invoke(context);

        before.Should().Be("inside-1");
        after.Should().Be("inside-1", "an endpoint saves after it has awaited, and the id has to still be there");
    }

    /// <summary>
    /// Runs the middleware once. Returns the id the rest of the pipeline saw and the header the
    /// response started with.
    /// </summary>
    private static async Task<(string? Seen, string? Echoed)> RunAsync(string? sent)
    {
        var response = new StartableResponse();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(response);
        if (sent is not null) context.Request.Headers[Header] = sent;

        string? seen = null;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            seen = Correlation.Id;
            return Task.CompletedTask;
        });

        await middleware.Invoke(context);
        await response.StartAsync();

        var echoed = context.Response.Headers[Header];
        echoed.Count.Should().Be(1, "the response carries exactly one correlation id");
        return (seen, echoed.ToString());
    }

    /// <summary>
    /// The default response feature drops <c>OnStarting</c> callbacks, and the header is written in
    /// one. This keeps them and runs them the way a server does when the response starts.
    /// </summary>
    private sealed class StartableResponse : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) =>
            _starting.Add((callback, state));

        public async Task StartAsync()
        {
            for (var i = _starting.Count - 1; i >= 0; i--)
            {
                await _starting[i].Callback(_starting[i].State);
            }
        }
    }
}
