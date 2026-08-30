using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests;

/// <summary>
/// /metrics over the real pipeline. The guard sits in UseBarakoCMS rather than on the mapping, so
/// the thing worth pinning down is that a request reaching the host is refused before the endpoint
/// runs, and that a scraper holding the key still gets its numbers.
/// </summary>
[Collection("Sequential")]
public class MetricsEndpointTests
{
    private const string ScrapeKey = "test-scrape-key-9f2a";

    private readonly IntegrationTestFixture _factory;

    public MetricsEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    private HttpClient Scrapeable() =>
        _factory.WithSetting(MetricsScrapeAccess.ConfigurationKey, ScrapeKey).CreateClient();

    [Fact]
    public async Task An_anonymous_scrape_is_refused_when_a_key_is_configured()
    {
        var res = await Scrapeable().GetAsync("/metrics", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("process_", "a refused scrape must not return the metrics anyway");
    }

    // The positive control. Without it every assertion here is satisfied by an endpoint that is
    // simply broken for everyone.
    [Fact]
    public async Task A_scraper_presenting_the_key_gets_the_metrics()
    {
        var client = Scrapeable();
        client.DefaultRequestHeaders.Add(MetricsScrapeAccess.HeaderName, ScrapeKey);

        var res = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("process_", "this is a real Prometheus exposition, not an empty 200");
    }

    [Fact]
    public async Task A_bearer_token_works_because_that_is_what_a_prometheus_scrape_config_sends()
    {
        var client = Scrapeable();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeKey);

        var res = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("process_");
    }

    [Fact]
    public async Task The_wrong_key_is_refused()
    {
        var client = Scrapeable();
        client.DefaultRequestHeaders.Add(MetricsScrapeAccess.HeaderName, "not-the-key");

        var res = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_no_key_configured_the_endpoint_is_not_there_at_all()
    {
        // The fixture configures no Metrics:ScrapeKey, which is the state every deployment upgrades
        // into. It must be closed, not open.
        var res = await _factory.CreateClient().GetAsync("/metrics", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("process_");
    }

    [Fact]
    public async Task The_guard_does_not_touch_the_rest_of_the_api()
    {
        var res = await _factory.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "only /metrics is behind the scrape key");
    }
}
