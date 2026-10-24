using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Services;

namespace BarakoCMS.Tests;

/// <summary>
/// The trailing 24 hour numbers in the metrics summary (#677): the totals are since the process
/// started, which on a long-running host says nothing about today.
/// </summary>
public class MetricsWindowTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 30, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Requests_older_than_a_day_leave_the_window_but_stay_in_the_totals()
    {
        var clock = new Clock();
        var metrics = new MetricsService(clock);

        metrics.TrackRequest(500, 10);
        metrics.TrackRequest(500, 10);
        clock.Now = clock.Now.AddHours(25);
        metrics.TrackRequest(200, 30);

        var summary = metrics.GetSummary();

        summary.TotalRequests.Should().Be(3);
        summary.TotalErrors.Should().Be(2);
        summary.Last24Hours.Requests.Should().Be(1);
        summary.Last24Hours.Errors.Should().Be(0, "both errors were more than a day ago");
        summary.Last24Hours.ErrorRate.Should().Be(0);
        summary.Last24Hours.AverageResponseTime.Should().Be(30);
    }

    [Fact]
    public void A_request_just_inside_the_day_still_counts()
    {
        var clock = new Clock();
        var metrics = new MetricsService(clock);

        metrics.TrackRequest(404, 5);
        clock.Now = clock.Now.AddHours(24).AddMinutes(-1);

        var window = metrics.GetSummary().Last24Hours;

        window.Requests.Should().Be(1);
        window.Errors.Should().Be(1);
        window.ErrorRate.Should().Be(1);
    }

    [Fact]
    public void The_p95_is_the_bucket_the_95th_percentile_falls_in()
    {
        var metrics = new MetricsService(new Clock());

        for (var i = 0; i < 94; i++)
        {
            metrics.TrackRequest(200, 3);
        }
        for (var i = 0; i < 6; i++)
        {
            metrics.TrackRequest(200, 420);
        }

        var window = metrics.GetSummary().Last24Hours;

        window.Requests.Should().Be(100);
        window.P95ResponseTime.Should().Be(500, "the 95th of 100 is one of the six slow ones, in the 300 to 500 bucket");
    }

    [Fact]
    public void The_p95_stays_low_when_only_the_slowest_five_percent_are_slow()
    {
        var metrics = new MetricsService(new Clock());

        for (var i = 0; i < 95; i++)
        {
            metrics.TrackRequest(200, 3);
        }
        for (var i = 0; i < 5; i++)
        {
            metrics.TrackRequest(200, 9000);
        }

        metrics.GetSummary().Last24Hours.P95ResponseTime.Should().Be(5);
    }

    [Fact]
    public void Cache_hits_are_counted_against_successful_reads_only()
    {
        var metrics = new MetricsService(new Clock());

        metrics.TrackRequest(304, 1, cacheableRead: true, cacheHit: false);
        metrics.TrackRequest(200, 1, cacheableRead: true, cacheHit: true);
        metrics.TrackRequest(200, 1, cacheableRead: true, cacheHit: false);
        metrics.TrackRequest(200, 1, cacheableRead: true, cacheHit: false);
        metrics.TrackRequest(500, 1, cacheableRead: true, cacheHit: true);
        metrics.TrackRequest(404, 1, cacheableRead: true, cacheHit: false);
        metrics.TrackRequest(200, 1, cacheableRead: false, cacheHit: false);
        metrics.TrackRequest(201, 1);

        var window = metrics.GetSummary().Last24Hours;

        window.Requests.Should().Be(8);
        window.CacheHits.Should().Be(2, "a failure is never a hit, whatever the caller says");
        window.CacheHitRate.Should().Be(0.5, "two of the four successful reads; a probe or a write is not a read");
    }

    [Fact]
    public void A_minute_recorded_before_the_clock_was_set_back_is_not_in_the_window()
    {
        var clock = new Clock();
        var metrics = new MetricsService(clock);

        metrics.TrackRequest(200, 1);
        clock.Now = clock.Now.AddHours(-2);
        metrics.TrackRequest(200, 1);

        metrics.GetSummary().Last24Hours.Requests.Should().Be(1, "the request stamped two hours ahead of now is not counted");
    }

    [Fact]
    public void Uptime_is_measured_from_when_the_service_started()
    {
        var clock = new Clock();
        var metrics = new MetricsService(clock);
        var started = clock.Now;
        clock.Now = clock.Now.AddMinutes(90).AddSeconds(5);

        var summary = metrics.GetSummary();

        summary.StartedAt.Should().Be(started);
        summary.UptimeSeconds.Should().Be(90 * 60 + 5);
    }

    [Fact]
    public void An_empty_window_reports_zeros_rather_than_dividing_by_zero()
    {
        var window = new MetricsService(new Clock()).GetSummary().Last24Hours;

        window.Requests.Should().Be(0);
        window.ErrorRate.Should().Be(0);
        window.AverageResponseTime.Should().Be(0);
        window.P95ResponseTime.Should().Be(0);
        window.CacheHitRate.Should().Be(0);
    }
}

/// <summary>The window over the real pipeline: the route returns it, and the output cache feeds it.</summary>
[Collection("Integration2")]
public class MetricsWindowEndpointTests
{
    private readonly IntegrationTestFixture _factory;

    public MetricsWindowEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_monitoring_summary_carries_the_window_and_the_uptime()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        // A request recorded before the one under test, since a request is counted after its answer.
        (await client.GetAsync("/api/me", Ct)).IsSuccessStatusCode.Should().BeTrue();
        await RequestsAsync(_factory.Services.GetRequiredService<IMetricsService>(), 1);

        var response = await client.GetAsync("/api/monitoring/metrics", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("uptimeSeconds").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.TryGetProperty("startedAt", out _).Should().BeTrue();
        var window = root.GetProperty("last24Hours");
        window.GetProperty("requests").GetInt64().Should().BeGreaterThan(0, "this host has served requests");
        foreach (var name in new[] { "errors", "errorRate", "averageResponseTime", "p95ResponseTime", "cacheHitRate", "cacheHits" })
        {
            window.TryGetProperty(name, out _).Should().BeTrue(name);
        }
    }

    [Fact]
    public async Task A_response_the_output_cache_serves_counts_as_a_hit_and_the_first_does_not()
    {
        var metrics = _factory.Services.GetRequiredService<IMetricsService>();
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var from = "/old-" + Guid.NewGuid().ToString("N")[..10];

        using var authenticated = _factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var save = await authenticated.PostAsJsonAsync("/api/redirects",
            new { fromPath = from, toPath = "/new-home", permanent = true }, Ct);
        save.IsSuccessStatusCode.Should().BeTrue(await save.Content.ReadAsStringAsync(Ct));

        using var anonymous = _factory.CreateClient();
        var query = $"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}";

        var before = metrics.GetSummary().Last24Hours.CacheHits;
        (await anonymous.GetAsync(query, Ct)).IsSuccessStatusCode.Should().BeTrue();
        (await HitsAsync(metrics, before + 1, TimeSpan.FromSeconds(1)))
            .Should().Be(before, "the first resolve ran the endpoint");

        (await anonymous.GetAsync(query, Ct)).IsSuccessStatusCode.Should().BeTrue();
        (await HitsAsync(metrics, before + 1, TimeSpan.FromSeconds(10)))
            .Should().Be(before + 1, "the second was served by the output cache");
    }

    private static async Task RequestsAsync(IMetricsService metrics, long target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (metrics.GetSummary().Last24Hours.Requests < target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>The window's cache hits once they reach <paramref name="target"/>, or after <paramref name="wait"/>.</summary>
    /// <remarks>
    /// The middleware records a request after the response is written, so the client can hold the
    /// answer a moment before it is counted.
    /// </remarks>
    private static async Task<long> HitsAsync(IMetricsService metrics, long target, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var hits = metrics.GetSummary().Last24Hours.CacheHits;
            if (hits >= target || DateTime.UtcNow > deadline)
            {
                return hits;
            }
            await Task.Delay(20, Ct);
        }
    }
}
