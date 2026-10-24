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
[Collection("IntegrationSerial")]
public class DerivedHostReleaseTests(IntegrationTestFixture fixture)
{
    private const string AnotherClass = "Some.Other.Class";

    private static string ThisClass => typeof(DerivedHostReleaseTests).FullName!;

    [Fact]
    public void A_host_a_class_derived_is_disposed_once_another_class_derives_one()
    {
        var host = fixture.WithSetting("Release:Probe", "one");
        var services = host.Services;
        fixture.LiveDerivedHosts(ThisClass).Should().BeGreaterThan(0, "the host was derived by this class, so the count below means something");

        var released = fixture.ReleaseDerivedHostsExcept(AnotherClass);

        released.Should().BeGreaterThan(0);
        fixture.LiveDerivedHosts(ThisClass).Should().Be(0);
        var probe = () => services.GetRequiredService<IServiceProvider>();
        probe.Should().Throw<ObjectDisposedException>("a derived host left up keeps polling the shared database");
    }

    [Fact]
    public void A_released_host_handed_out_again_names_the_class_that_built_it()
    {
        var host = fixture.WithSetting("Release:Probe", "six");
        _ = host.Services;

        fixture.ReleaseDerivedHostsExcept(AnotherClass);

        var services = () => host.Services;
        services.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ThisClass}*{AnotherClass}*WithRunScopedHost*")
            .Which.Should().NotBeOfType<ObjectDisposedException>();
        var client = () => host.CreateClient();
        var neverStarted = fixture.WithSetting("Release:Probe", "seven");
        fixture.ReleaseDerivedHostsExcept(AnotherClass);
        var startAfterRelease = () => neverStarted.CreateClient();
        startAfterRelease.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithRunScopedHost*")
            .Which.Should().NotBeOfType<ObjectDisposedException>();
        client.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithRunScopedHost*")
            .Which.Should().NotBeOfType<ObjectDisposedException>();
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
    public void Asking_for_a_run_scoped_host_leaves_the_callers_own_host_alive()
    {
        var own = fixture.WithSetting("Release:Probe", "four");
        _ = own.Services;

        var shared = fixture.WithRunScopedHost(_ => { });
        _ = shared.Services;

        fixture.LiveDerivedHosts(ThisClass).Should().BeGreaterThan(0);
        own.Services.GetRequiredService<IServiceProvider>().Should().NotBeNull(
            "the class still uses the host it derived before it asked for the shared one");
    }

    [Fact]
    public void Restoring_a_resolver_captured_from_a_host_since_disposed_lands_on_a_live_host()
    {
        var host = fixture.WithSetting("Release:Probe", "five");
        var hostResolver = host.Services.GetRequiredService<FastEndpoints.IServiceResolver>();
        FastEndpointsResolver.Current().Should().BeSameAs(hostResolver,
            "the resolver Keep captures must be the derived host's, or this test proves nothing about restoring it");
        var keep = FastEndpointsResolver.Keep();

        fixture.ReleaseDerivedHostsExcept(AnotherClass);
        var written = new List<FastEndpoints.IServiceResolver?>();
        FastEndpointsResolver.OnWrite = written.Add;
        try
        {
            keep.Dispose();
        }
        finally
        {
            FastEndpointsResolver.OnWrite = null;
        }

        // The end state alone cannot tell: writing the disposed resolver back and then repairing
        // lands on a live host too, but leaves a window where another thread reads a dead one.
        written.Should().NotContain(r => ReferenceEquals(r, hostResolver),
            "Restore must not put the static back on the disposed host, even for a moment");
        FastEndpointsResolver.Current().Should().NotBeSameAs(hostResolver);
        FastEndpointsResolver.IsUsable().Should().BeTrue();
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
