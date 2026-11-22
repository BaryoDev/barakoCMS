using System.Diagnostics.Metrics;
using System.Text;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A test host that starts runtime collectors stops them when it is disposed, so a run of many
/// hosts does not pile up event listeners. Needs no database: only the metrics registration is built.
/// </summary>
public class TestHostMetricsDisposalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ExportAsync(HostMetrics host)
    {
        using var stream = new MemoryStream();
        await host.Registry.CollectAndExportAsTextAsync(stream, Ct);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Three_hosts_started_and_disposed_leave_no_adapter_and_no_listener_behind()
    {
        for (var round = 0; round < 3; round++)
        {
            var provider = new ServiceCollection().UseIsolatedMetricsRegistry().BuildServiceProvider();
            var host = provider.GetRequiredService<HostMetrics>();
            host.LiveAdapters.Should().Be(2, "an event counter adapter and a meter adapter are running");

            using var meter = new Meter("disposal.test." + Guid.NewGuid().ToString("N"));
            var whileAlive = "alive" + Guid.NewGuid().ToString("N");
            meter.CreateCounter<int>(whileAlive).Add(1);
            (await ExportAsync(host)).Should().Contain(whileAlive, "the positive control: the meter adapter is listening");

            await provider.DisposeAsync();

            host.LiveAdapters.Should().Be(0);
            var afterDispose = "gone" + Guid.NewGuid().ToString("N");
            meter.CreateCounter<int>(afterDispose).Add(1);
            (await ExportAsync(host)).Should().NotContain(afterDispose, "a disposed host's meter adapter no longer listens");
        }
    }

    [Fact]
    public void Every_isolated_host_gets_a_registry_of_its_own()
    {
        using var first = HostMetrics.Isolated();
        using var second = HostMetrics.Isolated();

        first.Registry.Should().NotBeSameAs(second.Registry);
        first.Registry.Should().NotBeSameAs(Prometheus.Metrics.DefaultRegistry);
    }
}
