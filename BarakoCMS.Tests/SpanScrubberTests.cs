using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: what a span may carry out of the process. The request and outbound call spans are
/// filled by the framework, so they are cut to a fixed list before export.
/// </summary>
/// <remarks>
/// No tracer is built and nothing listens. The spans are plain activities the test starts and
/// hands to the scrubber, so nothing here reaches a span of any other test in the process.
/// </remarks>
public class SpanScrubberTests
{
    [Fact]
    public void An_outbound_call_span_keeps_the_host_and_loses_the_url()
    {
        using var span = new Activity("POST");
        span.Start();
        span.SetTag("http.request.method", "POST");
        span.SetTag("server.address", "hooks.example.com");
        span.SetTag("server.port", 443);
        span.SetTag("http.response.status_code", 200);
        span.SetTag("url.full", "https://hooks.example.com/services/T000/B000/the-secret-part?token=abc");
        span.SetTag("some.attribute.a.later.version.adds", "anything");
        span.Stop();

        SpanScrubber.KeepOnly(span, SpanScrubber.AllowedFor(SpanScrubber.ClientSource)!);

        var kept = span.TagObjects.Select(tag => tag.Key).ToList();
        kept.Should().HaveCount(4);
        kept.Should().BeEquivalentTo(
            new[] { "http.request.method", "server.address", "server.port", "http.response.status_code" });
        span.TagObjects.Select(tag => tag.Value?.ToString()).Should().NotContain(value => value!.Contains("the-secret-part"),
            "a webhook URL is often the credential, so only where the call went is exported");
    }

    [Fact]
    public void A_request_span_keeps_the_route_and_loses_the_path_the_query_and_the_user_agent()
    {
        using var span = new Activity("GET /api/public/share/{token}");
        span.Start();
        span.SetTag("http.request.method", "GET");
        span.SetTag("http.route", "/api/public/share/{token}");
        span.SetTag("http.response.status_code", 200);
        span.SetTag(BarakoTracing.CorrelationIdTag, "4bf92f3577b34da6a3ce929d0e0e4736");
        span.SetTag("url.path", "/api/public/share/a-live-share-token");
        span.SetTag("url.query", "?email=someone@example.com");
        span.SetTag("user_agent.original", "whatever the caller typed");
        span.Stop();

        SpanScrubber.KeepOnly(span, SpanScrubber.AllowedFor(SpanScrubber.ServerSource)!);

        var kept = span.TagObjects.Select(tag => tag.Key).ToList();
        kept.Should().HaveCount(4);
        kept.Should().BeEquivalentTo(
            new[] { "http.request.method", "http.route", "http.response.status_code", BarakoTracing.CorrelationIdTag });
    }

    [Fact]
    public void The_status_keeps_its_code_and_loses_its_text()
    {
        using var span = new Activity("POST");
        span.Start();
        span.SetStatus(ActivityStatusCode.Error, "Key (email)=(someone@example.com) already exists");
        span.Stop();

        SpanScrubber.KeepOnly(span, SpanScrubber.ClientTags);

        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
    }

    [Fact]
    public void Neither_list_admits_a_url_a_header_or_a_user_agent()
    {
        SpanScrubber.ServerTags.Should().HaveCount(9);
        SpanScrubber.ClientTags.Should().HaveCount(7);

        foreach (var tag in SpanScrubber.ServerTags.Concat(SpanScrubber.ClientTags))
        {
            tag.Should().NotBe("url.full").And.NotBe("url.path").And.NotBe("url.query");
            tag.Should().NotStartWith("http.request.header").And.NotStartWith("http.response.header");
            tag.Should().NotStartWith("user_agent").And.NotStartWith("client.");
        }
    }

    [Fact]
    public void A_span_from_any_other_source_is_left_as_it_is()
    {
        SpanScrubber.AllowedFor(BarakoTracing.SourceName).Should().BeNull();
        SpanScrubber.AllowedFor("Some.Host.Source").Should().BeNull();

        using var span = new Activity("a host's own span");
        span.Start();
        span.SetTag("their.attribute", "their business");
        span.Stop();

        using var scrubber = new SpanScrubber();
        scrubber.OnEnd(span);

        span.TagObjects.Should().ContainSingle().Which.Key.Should().Be("their.attribute");
    }
}
