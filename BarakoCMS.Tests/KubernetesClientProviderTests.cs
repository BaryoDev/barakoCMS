using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using barakoCMS.Infrastructure.Services;
using k8s;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A transient failure at pod start must not disable Kubernetes monitoring for the process lifetime.
/// </summary>
/// <remarks>
/// The monitor built its client once in the constructor and latched a static bool on failure. The
/// service is a singleton, so one API-server hiccup, which is normal at pod start, left monitoring
/// off until someone restarted the process. Nothing ever cleared the flag.
///
/// The clock is injected so the backoff is asserted rather than slept through. The recovery test is
/// paired with the one below it: a provider that simply retried on every call would pass "it can
/// succeed later" and still hammer a dead API server, so the backoff has to be shown to hold.
///
/// See issue #264.
/// </remarks>
public class KubernetesClientProviderTests
{
    private static Kubernetes AnyClient() =>
        new(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public Func<DateTimeOffset> Read => () => Now;
    }

    private static KubernetesClientProvider Provider(Func<Kubernetes?> factory, Clock clock) =>
        new(factory, NullLogger.Instance, clock.Read);

    [Fact]
    public void A_client_that_failed_to_build_is_rebuilt_on_a_later_call()
    {
        var clock = new Clock();
        var calls = 0;

        var provider = Provider(() =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("API server not up yet");
            return AnyClient();
        }, clock);

        provider.GetClient().Should().BeNull("the first build failed");

        clock.Now += KubernetesClientProvider.MinBackoff;

        provider.GetClient().Should().NotBeNull(
            "a transient failure must not disable monitoring until the process restarts");
    }

    [Fact]
    public void A_failed_build_is_not_retried_before_the_backoff_elapses()
    {
        var clock = new Clock();
        var provider = Provider(() => throw new HttpRequestException("still down"), clock);

        provider.GetClient().Should().BeNull();
        provider.Attempts.Should().Be(1);

        clock.Now += KubernetesClientProvider.MinBackoff - TimeSpan.FromMilliseconds(1);

        provider.GetClient().Should().BeNull();
        provider.Attempts.Should().Be(1, "retrying on every call would hammer a failing API server");
    }

    [Fact]
    public void Repeated_failures_back_off_further_each_time()
    {
        var clock = new Clock();
        var provider = Provider(() => throw new HttpRequestException("still down"), clock);

        provider.GetClient();
        clock.Now += KubernetesClientProvider.MinBackoff;
        provider.GetClient();
        provider.Attempts.Should().Be(2);

        clock.Now += KubernetesClientProvider.MinBackoff;

        provider.GetClient().Should().BeNull();
        provider.Attempts.Should().Be(2, "the second failure doubles the wait");
    }

    [Fact]
    public void No_configuration_at_all_is_retried_on_the_slow_interval()
    {
        var clock = new Clock();
        var calls = 0;

        var provider = Provider(() =>
        {
            calls++;
            return calls == 1 ? null : AnyClient();
        }, clock);

        provider.GetClient().Should().BeNull("nothing is configured yet");

        clock.Now += KubernetesClientProvider.MinBackoff;
        provider.GetClient().Should().BeNull(
            "no kubeconfig and no service account is a steady state, not a fault to retry hard");

        clock.Now += KubernetesClientProvider.UnconfiguredInterval;
        provider.GetClient().Should().NotBeNull("a cluster that appears later is picked up");
    }

    [Fact]
    public void A_client_that_built_successfully_is_reused()
    {
        var clock = new Clock();
        var provider = Provider(AnyClient, clock);

        var first = provider.GetClient();

        clock.Now += TimeSpan.FromHours(1);

        provider.GetClient().Should().BeSameAs(first);
        provider.Attempts.Should().Be(1);
    }
}
