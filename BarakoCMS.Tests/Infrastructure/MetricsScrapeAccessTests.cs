using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The decision behind the /metrics guard. Prometheus output names every route and counts every
/// request, so the endpoint has to refuse a caller it cannot identify, including the caller it
/// cannot identify because nobody configured a key.
/// </summary>
public class MetricsScrapeAccessTests
{
    // The control. Every negative below is satisfied by a guard that refuses everything, which is
    // the shape of check this project keeps shipping by accident.
    [Fact]
    public void The_configured_key_is_allowed()
    {
        MetricsScrapeAccess.Authorize("scrape-me", "scrape-me")
            .Should().Be(MetricsScrapeDecision.Allowed);
    }

    [Fact]
    public void No_configured_key_means_the_endpoint_serves_nobody()
    {
        MetricsScrapeAccess.Authorize(null, "anything")
            .Should().Be(MetricsScrapeDecision.NotConfigured, "an unset key must fail closed");

        MetricsScrapeAccess.Authorize("   ", "anything")
            .Should().Be(MetricsScrapeDecision.NotConfigured, "whitespace is not a credential");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("scrape-m")]
    [InlineData("scrape-mee")]
    public void A_caller_without_the_key_is_rejected(string? presented)
    {
        MetricsScrapeAccess.Authorize("scrape-me", presented)
            .Should().Be(MetricsScrapeDecision.Rejected);
    }

    [Fact]
    public void The_dedicated_header_carries_the_key()
    {
        MetricsScrapeAccess.PresentedKey("scrape-me", null).Should().Be("scrape-me");
    }

    [Fact]
    public void A_bearer_token_carries_the_key_because_that_is_what_prometheus_sends()
    {
        MetricsScrapeAccess.PresentedKey(null, "Bearer scrape-me").Should().Be("scrape-me");
        MetricsScrapeAccess.PresentedKey(null, "bearer scrape-me").Should().Be("scrape-me");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "Basic c2NyYXBl")]
    [InlineData(null, "Bearer ")]
    public void A_request_carrying_nothing_usable_presents_nothing(string? header, string? authorization)
    {
        MetricsScrapeAccess.PresentedKey(header, authorization).Should().BeNull();
    }

    [Fact]
    public void Only_the_metrics_path_is_guarded()
    {
        MetricsScrapeAccess.IsMetricsPath("/metrics").Should().BeTrue();
        MetricsScrapeAccess.IsMetricsPath("/api/contents").Should().BeFalse();
    }
}
