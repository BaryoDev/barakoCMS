using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The connection and header limits Kestrel starts with. Read from the options the test host
/// resolves, so they prove what the host registered rather than what a helper would compute.
/// </summary>
[Collection("Integration1")]
public class KestrelLimitsTests
{
    private readonly IntegrationTestFixture _factory;

    public KestrelLimitsTests(IntegrationTestFixture factory) => _factory = factory;

    private static KestrelServerLimits LimitsOf(IServiceProvider services) =>
        services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

    [Fact]
    public void Kestrel_SetsRequestHeadersTimeout()
    {
        LimitsOf(_factory.Services).RequestHeadersTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Kestrel_SetsHeaderSizeAndCount()
    {
        var limits = LimitsOf(_factory.Services);
        limits.MaxRequestHeadersTotalSize.Should().Be(32 * 1024);
        limits.MaxRequestHeaderCount.Should().Be(100);
    }

    [Fact]
    public void Kestrel_SetsKeepAliveTimeout()
    {
        LimitsOf(_factory.Services).KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(130),
            "longer than a proxy's 120 second idle timeout");
    }

    [Fact]
    public void Kestrel_BoundsConcurrentConnections()
    {
        var limits = LimitsOf(_factory.Services);
        limits.MaxConcurrentConnections.Should().Be(10_000);
        limits.MaxConcurrentUpgradedConnections.Should().Be(1_000);
        limits.MinRequestBodyDataRate.Should().NotBeNull();
        limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(240);
    }

    [Fact]
    public void Kestrel_LimitsComeFromConfigurationWhenSet()
    {
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Kestrel:Limits:RequestHeadersTimeout", "00:00:07");
            builder.UseSetting("Kestrel:Limits:MaxRequestHeaderCount", "55");
            builder.UseSetting("Kestrel:Limits:MaxConcurrentConnections", "77");
            builder.UseSetting("Kestrel:Limits:KeepAliveTimeout", "00:03:00");
        });

        var limits = LimitsOf(host.Services);

        limits.RequestHeadersTimeout.Should().Be(TimeSpan.FromSeconds(7));
        limits.MaxRequestHeaderCount.Should().Be(55);
        limits.MaxConcurrentConnections.Should().Be(77);
        limits.KeepAliveTimeout.Should().Be(TimeSpan.FromMinutes(3));
        limits.MaxRequestHeadersTotalSize.Should().Be(32 * 1024, "a key left unset keeps the code default");
    }
}
