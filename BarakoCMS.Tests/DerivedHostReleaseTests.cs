using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host a test derives from the shared fixture is disposed once its class is done, and
/// FastEndpoints' static resolver is not left on it (#1171).
/// </summary>
/// <remarks>
/// Undisposed, each derived host kept Marten, Wolverine and the hosted services polling on unpooled
/// connections for the rest of the run, which wedged the full suite against the Docker port-forward.
/// </remarks>
[Collection("Sequential")]
public class DerivedHostReleaseTests(IntegrationTestFixture fixture)
{
    private const string AnotherClass = "Some.Other.Class";

    private static string ThisClass => typeof(DerivedHostReleaseTests).FullName!;

    [Fact]
    public void A_host_a_class_derived_is_disposed_once_another_class_derives_one()
    {
        var host = fixture.WithSetting("Release:Probe", "one");
        _ = host.Services;
        fixture.LiveDerivedHosts(ThisClass).Should().BeGreaterThan(0, "the host was derived by this class, so the count below means something");

        var released = fixture.ReleaseDerivedHostsExcept(AnotherClass);

        released.Should().BeGreaterThan(0);
        fixture.LiveDerivedHosts(ThisClass).Should().Be(0);
        var probe = () => host.Services.GetRequiredService<IServiceProvider>();
        probe.Should().Throw<ObjectDisposedException>("a derived host left up keeps polling the shared database");
    }

    [Fact]
    public void A_host_a_class_is_still_using_is_not_disposed()
    {
        var host = fixture.WithSetting("Release:Probe", "two");
        _ = host.Services;

        fixture.ReleaseDerivedHostsExcept(ThisClass);

        fixture.LiveDerivedHosts(ThisClass).Should().BeGreaterThan(0);
        host.Services.GetRequiredService<IServiceProvider>().Should().NotBeNull();
    }

    [Fact]
    public void A_host_built_for_the_whole_run_survives_a_release()
    {
        var host = fixture.WithRunScopedHost(_ => { });
        _ = host.Services;

        fixture.ReleaseDerivedHostsExcept(AnotherClass);

        host.Services.GetRequiredService<IServiceProvider>().Should().NotBeNull();
    }

    [Fact]
    public void FastEndpoints_resolver_is_left_on_a_live_host_after_the_host_it_named_is_disposed()
    {
        var host = fixture.WithSetting("Release:Probe", "three");
        _ = host.Services;
        FastEndpointsResolver.IsUsable().Should().BeTrue("the host just started owns the resolver");

        fixture.ReleaseDerivedHostsExcept(AnotherClass);

        FastEndpointsResolver.IsUsable().Should().BeTrue(
            "a resolver left on the disposed host fails every lookup made outside a request");
    }
}
