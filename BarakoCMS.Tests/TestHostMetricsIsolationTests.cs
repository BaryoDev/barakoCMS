using System.Globalization;
using System.Net;
using System.Text;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Observability;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Two test hosts alive in one process publish to registries of their own, so traffic on one does
/// not change what the other's /metrics shows.
/// </summary>
[Collection("Integration1")]
public class TestHostMetricsIsolationTests
{
    private readonly IntegrationTestFixture _factory;

    public TestHostMetricsIsolationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (WebApplicationFactory<Program> Host, string Key) NewHost()
    {
        var key = "scrape-" + Guid.NewGuid().ToString("N");
        return (_factory.WithSetting(MetricsScrapeAccess.ConfigurationKey, key), key);
    }

    private static async Task<string> ScrapeAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add(MetricsScrapeAccess.HeaderName, key);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, Ct);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The sum of every series of a family, so a label this test did not choose cannot hide a change.</summary>
    private static double Sum(string body, string family)
    {
        var values = body.Split('\n')
            .Where(l => l.StartsWith(family + "{", StringComparison.Ordinal) || l.StartsWith(family + " ", StringComparison.Ordinal))
            .Select(l => double.Parse(l[(l.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture))
            .ToList();
        return values.Sum();
    }

    [Fact]
    public async Task Traffic_on_one_host_does_not_change_the_request_counts_of_another()
    {
        var (hostA, keyA) = NewHost();
        var (hostB, keyB) = NewHost();
        var clientA = hostA.CreateClient();
        var clientB = hostB.CreateClient();

        // One request each first, so both families exist on both hosts before the baseline.
        (await clientA.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clientB.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var aBefore = Sum(await ScrapeAsync(clientA, keyA), "http_requests_received_total");
        var bBefore = Sum(await ScrapeAsync(clientB, keyB), "http_requests_received_total");
        aBefore.Should().BeGreaterThan(0, "the baseline has to have something that a leak could move");

        for (var i = 0; i < 3; i++)
            (await clientB.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var aAfter = Sum(await ScrapeAsync(clientA, keyA), "http_requests_received_total");
        var bAfter = Sum(await ScrapeAsync(clientB, keyB), "http_requests_received_total");
        bAfter.Should().Be(bBefore + 3, "the positive control: host B did count its own traffic");
        aAfter.Should().Be(aBefore, "host A saw none of host B's requests");
    }

    [Fact]
    public async Task A_counter_a_host_owns_is_written_to_that_hosts_registry_and_to_no_other()
    {
        var (hostA, _) = NewHost();
        var (hostB, _) = NewHost();
        var metricsA = hostA.Services.GetRequiredService<HostMetrics>();
        var metricsB = hostB.Services.GetRequiredService<HostMetrics>();
        metricsA.Registry.Should().NotBeSameAs(metricsB.Registry);
        metricsA.Registry.Should().NotBeSameAs(Metrics.DefaultRegistry);
        metricsB.Registry.Should().NotBeSameAs(Metrics.DefaultRegistry);

        const string Family = "barakocms_api_key_refusals_total";
        var aBefore = Sum(await ExportAsync(metricsA.Registry), Family);
        var defaultBefore = Sum(await ExportAsync(Metrics.DefaultRegistry), Family);
        var bBefore = Sum(await ExportAsync(metricsB.Registry), Family);

        hostB.Services.GetRequiredService<ApiKeyRefusals>().Record(Guid.NewGuid(), ApiKeyRefusalReason.Scope);

        Sum(await ExportAsync(metricsB.Registry), Family).Should().Be(bBefore + 1, "the positive control");
        Sum(await ExportAsync(metricsA.Registry), Family).Should().Be(aBefore);
        Sum(await ExportAsync(Metrics.DefaultRegistry), Family).Should().Be(defaultBefore);
    }

    [Fact]
    public void Each_host_has_holders_of_its_own_built_from_its_own_factory()
    {
        var (hostA, _) = NewHost();
        var (hostB, _) = NewHost();

        hostA.Services.GetRequiredService<WorkflowMetrics>()
            .Should().NotBeSameAs(hostB.Services.GetRequiredService<WorkflowMetrics>());
        hostA.Services.GetRequiredService<barakoCMS.Infrastructure.Jobs.JobMetrics>()
            .Should().NotBeSameAs(hostB.Services.GetRequiredService<barakoCMS.Infrastructure.Jobs.JobMetrics>());
        hostA.Services.GetRequiredService<IMetricFactory>()
            .Should().BeSameAs(hostA.Services.GetRequiredService<HostMetrics>().Factory);
    }
}
