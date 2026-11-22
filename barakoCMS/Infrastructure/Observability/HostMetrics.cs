using Prometheus;

namespace barakoCMS.Infrastructure.Observability;

/// <summary>
/// The registry and metric factory one host publishes to. Production hosts use prometheus-net's
/// default registry. A test factory swaps in an isolated one (see
/// <see cref="barakoCMS.Extensions.ServiceCollectionExtensions.UseIsolatedMetricsRegistry"/>) so
/// two hosts in one process do not write to the same series.
/// </summary>
/// <remarks>
/// Every metric holder and counter is built from <see cref="Factory"/>, and <c>UseHttpMetrics</c>
/// and <c>MapMetrics</c> use <see cref="Registry"/>. The isolated variant owns its own runtime
/// collectors and stops them in <see cref="Dispose"/>.
/// </remarks>
internal sealed class HostMetrics : IDisposable
{
    private readonly List<IDisposable> _adapters = [];
    private readonly bool _owned;

    private HostMetrics(CollectorRegistry registry, IMetricFactory factory, bool owned)
    {
        Registry = registry;
        Factory = factory;
        _owned = owned;
    }

    public CollectorRegistry Registry { get; }

    public IMetricFactory Factory { get; }

    /// <summary>Runtime collectors this host started and has not yet stopped. Always 0 for the default registry.</summary>
    public int LiveAdapters => _adapters.Count;

    /// <summary>The process-wide default registry. Nothing is started or stopped; prometheus-net does that itself.</summary>
    public static HostMetrics Default() =>
        new(Prometheus.Metrics.DefaultRegistry, Prometheus.Metrics.DefaultFactory, owned: false);

    /// <summary>A registry of its own with its own runtime, event counter and meter collectors.</summary>
    public static HostMetrics Isolated()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var host = new HostMetrics(registry, factory, owned: true);

        DotNetStats.Register(registry);
        host._adapters.Add(EventCounterAdapter.StartListening(new EventCounterAdapterOptions
        {
            Registry = registry,
            MetricFactory = factory,
        }));
        host._adapters.Add(MeterAdapter.StartListening(new MeterAdapterOptions
        {
            Registry = registry,
            MetricFactory = factory,
        }));
        return host;
    }

    public void Dispose()
    {
        if (!_owned)
            return;

        foreach (var adapter in _adapters)
            adapter.Dispose();
        _adapters.Clear();
    }
}
