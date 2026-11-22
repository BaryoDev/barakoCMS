using Prometheus;

namespace BarakoCMS.Tests;

/// <summary>A metric factory on a registry nothing else writes to, for a test that builds a service by hand.</summary>
internal static class TestMetricFactory
{
    public static IMetricFactory New() => Metrics.WithCustomRegistry(Metrics.NewCustomRegistry());
}
