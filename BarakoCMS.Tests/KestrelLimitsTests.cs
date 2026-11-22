using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
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
        // These match the framework values, so this pins them rather than failing on master.
        var limits = LimitsOf(_factory.Services);
        limits.MaxRequestHeadersTotalSize.Should().Be(32 * 1024);
        limits.MaxRequestHeaderCount.Should().Be(100);
    }

    [Fact]
    public void Kestrel_SetsKeepAliveTimeout()
    {
        // This matches the framework value, so this pins it rather than failing on master.
        LimitsOf(_factory.Services).KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(130),
            "longer than a proxy's 120 second idle timeout");
    }

    [Fact]
    public void Kestrel_BoundsConcurrentConnections()
    {
        var limits = LimitsOf(_factory.Services);
        limits.MaxConcurrentConnections.Should().Be(10_000);
        limits.MaxConcurrentUpgradedConnections.Should().Be(1_000);
        // The framework default, pinned. The assertion that fails without the explicit line in
        // ApplyKestrelLimits is the configured one below.
        limits.MinRequestBodyDataRate.Should().NotBeNull();
        limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(240);
        limits.MinRequestBodyDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Kestrel_DataRatesComeFromConfigurationWhenSet()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Kestrel:Limits:MinRequestBodyDataRate:BytesPerSecond", "999");
            builder.UseSetting("Kestrel:Limits:MinRequestBodyDataRate:GracePeriod", "00:00:09");
            builder.UseSetting("Kestrel:Limits:MinResponseDataRate:BytesPerSecond", "555");
            builder.UseSetting("Kestrel:Limits:MinResponseDataRate:GracePeriod", "00:00:11");
        });

        var limits = LimitsOf(host.Services);

        limits.MinRequestBodyDataRate.Should().NotBeNull();
        limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(999);
        limits.MinRequestBodyDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(9));
        limits.MinResponseDataRate.Should().NotBeNull();
        limits.MinResponseDataRate!.BytesPerSecond.Should().Be(555);
        limits.MinResponseDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(11));
    }

    [Fact]
    public void Kestrel_ADataRateKeyLeftOutKeepsTheDefault()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Kestrel:Limits:MinRequestBodyDataRate:BytesPerSecond", "500"));

        var rate = LimitsOf(host.Services).MinRequestBodyDataRate;

        rate!.BytesPerSecond.Should().Be(500);
        rate.GracePeriod.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Kestrel_RequestBodyLimitAndFormLimitBothFollowRequestLimitsMaxBodyBytes()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RequestLimits:MaxBodyBytes", "2048"));

        LimitsOf(host.Services).MaxRequestBodySize.Should().Be(2048);
        host.Services.GetRequiredService<IOptions<FormOptions>>().Value.MultipartBodyLengthLimit.Should().Be(2048);
    }

    [Fact]
    public void Kestrel_ARequestBodySizeUnderKestrelLimitsIsRefusedAndNamesTheOneKey()
    {
        var act = () =>
        {
            using var host = _factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Kestrel:Limits:MaxRequestBodySize", "1000"));
            return LimitsOf(host.Services);
        };

        act.Should().Throw<Exception>().Where(e => e.ToString().Contains("RequestLimits:MaxBodyBytes"));
    }

    [Fact]
    public void Kestrel_LimitsComeFromConfigurationWhenSet()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
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
