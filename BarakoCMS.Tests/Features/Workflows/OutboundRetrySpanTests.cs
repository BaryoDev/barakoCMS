using System.Diagnostics;
using System.Net;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #691: a retried outbound call shows its tries. One span for the call, one child per try,
/// and an event from Carom's retry hook for each retry it decided to make.
/// </summary>
/// <remarks>
/// Driven through a scripted inner handler, so nothing leaves the process. Every host is a fresh
/// name, which is also how the call's spans are picked out of the shared source.
/// </remarks>
public class OutboundRetrySpanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retried_call_shows_each_try_and_each_retry()
    {
        using var capture = new SpanCapture(BarakoTracing.SourceName);
        var host = $"{Guid.NewGuid():N}.example";
        var stub = new Scripted(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = new HttpClient(new OutboundResilienceHandler(new OutboundResilience(Fast()))
        {
            InnerHandler = stub,
        });

        using var response = await client.PostAsync($"https://{host}/hook?token=a-secret", new StringContent("payload"), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = capture.Exported.Where(s => s.OperationName == BarakoTracing.OutboundCallSpan
            && (string?)s.GetTagItem(BarakoTracing.OutboundDestinationTag) == host).ToList();
        calls.Should().HaveCount(1);
        var call = calls[0];

        call.GetTagItem(BarakoTracing.OutboundTriesTag).Should().Be(3);
        call.GetTagItem(BarakoTracing.OutboundOutcomeTag).Should().Be(BarakoTracing.OutboundOk);
        call.GetTagItem(BarakoTracing.OutboundScopeTag).Should().Be(OutboundResilienceHandler.Scope);

        var tries = capture.Exported
            .Where(s => s.OperationName == BarakoTracing.OutboundTrySpan && s.ParentSpanId == call.SpanId)
            .OrderBy(s => (int)s.GetTagItem(BarakoTracing.OutboundTryTag)!)
            .ToList();
        tries.Should().HaveCount(3);
        tries.Select(s => s.GetTagItem(BarakoTracing.OutboundTryTag)).Should().Equal(1, 2, 3);
        tries.Select(s => s.GetTagItem(BarakoTracing.OutboundOutcomeTag)).Should().Equal(
            BarakoTracing.OutboundFailed, BarakoTracing.OutboundFailed, BarakoTracing.OutboundOk);

        var retries = call.Events.Where(e => e.Name == BarakoTracing.RetryEvent).ToList();
        retries.Should().HaveCount(2, "Carom raised its retry hook before the second and the third try");
        retries.Select(e => e.Tags.Single(t => t.Key == BarakoTracing.OutboundTryTag).Value).Should().Equal(2, 3);
        retries.Should().AllSatisfy(e =>
            e.Tags.Select(t => t.Key).Should().BeEquivalentTo(
                BarakoTracing.OutboundTryTag, BarakoTracing.OutboundDelayTag, BarakoTracing.ExceptionTypeTag));

        capture.Exported.Where(s => s.TraceId == call.TraceId)
            .SelectMany(s => s.TagObjects.Concat(s.Events.SelectMany(e => e.Tags)))
            .Select(t => t.Value?.ToString() ?? "")
            .Should().NotContain(v => v.Contains("a-secret") || v.Contains("payload"), "only the host is exported, never the URL or the body");
    }

    [Fact]
    public async Task A_call_that_works_first_time_has_one_try_and_no_retry()
    {
        using var capture = new SpanCapture(BarakoTracing.SourceName);
        var host = $"{Guid.NewGuid():N}.example";
        using var client = new HttpClient(new OutboundResilienceHandler(new OutboundResilience(Fast()))
        {
            InnerHandler = new Scripted(HttpStatusCode.OK),
        });

        using var response = await client.GetAsync($"https://{host}/", Ct);

        var call = capture.Exported.Single(s => s.OperationName == BarakoTracing.OutboundCallSpan
            && (string?)s.GetTagItem(BarakoTracing.OutboundDestinationTag) == host);
        call.GetTagItem(BarakoTracing.OutboundTriesTag).Should().Be(1);
        call.Events.Should().BeEmpty();
    }

    private static OutboundResilienceOptions Fast() => new()
    {
        Retries = 2,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
        AttemptTimeout = TimeSpan.FromSeconds(5),
        MaxRetryAfter = TimeSpan.FromSeconds(2),
        BreakerFailures = 0,
        BreakerWindow = 1,
        BreakerSampling = TimeSpan.FromMinutes(5),
        BreakerOpen = TimeSpan.FromMinutes(5),
    };

    private sealed class Scripted(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Math.Min(Interlocked.Increment(ref _calls), answers.Length) - 1;
            return Task.FromResult(new HttpResponseMessage(answers[index]));
        }
    }
}
