using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: tracing is off until an OTLP endpoint is configured, and off means nothing is
/// registered.
/// </summary>
/// <remarks>
/// Asserted on the service collection. No provider is built and no host is started, so no exporter
/// thread runs and no connection is attempted from this class.
/// </remarks>
public class TracingSetupTests
{
    private const string Endpoint = "http://collector.invalid:4317";

    [Fact]
    public void With_no_endpoint_AddBarakoCMS_registers_no_tracer()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, HostSettings(), modules => modules.Discover = false);

        services.Should().NotBeEmpty("AddBarakoCMS registered the rest of the host");
        services.Should().NotContain(d => d.ServiceType == typeof(TracerProvider),
            "a deployment that never asked for telemetry must not have a tracer waiting for an endpoint");
        services.Should().NotContain(
            d => d.ServiceType.Namespace != null && d.ServiceType.Namespace.StartsWith("OpenTelemetry", StringComparison.Ordinal),
            "nothing of the SDK is registered at all, so nothing of it starts with the host");
    }

    [Fact]
    public void With_an_endpoint_AddBarakoCMS_registers_the_tracer_and_starts_it_with_the_host()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(
            services, HostSettings((TracingOptions.EndpointKey, Endpoint)), modules => modules.Discover = false);

        services.Where(d => d.ServiceType == typeof(TracerProvider)).Should().HaveCount(1);
        services.Should().Contain(
            d => d.ServiceType == typeof(IHostedService)
              && !d.IsKeyedService
              && d.ImplementationType != null
              && d.ImplementationType.Namespace != null
              && d.ImplementationType.Namespace.StartsWith("OpenTelemetry", StringComparison.Ordinal),
            "the tracer is built when the host starts and flushed when it stops");
    }

    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        var options = TracingOptions.FromConfiguration(Settings((TracingOptions.EndpointKey, Endpoint)));

        options.Enabled.Should().BeTrue();
        options.Endpoint.Should().Be(new Uri(Endpoint));
        options.Protocol.Should().Be("grpc");
        options.Headers.Should().BeNull();
        options.TimeoutSeconds.Should().Be(10);
        options.ServiceName.Should().Be("barakocms");
        options.SampleRatio.Should().Be(1.0);
        options.MaxQueueSize.Should().Be(2048);
    }

    [Fact]
    public void Every_setting_is_read()
    {
        var options = TracingOptions.FromConfiguration(Settings(
            (TracingOptions.EndpointKey, "https://collector.invalid/v1/traces"),
            (TracingOptions.ProtocolKey, "HTTP/Protobuf"),
            (TracingOptions.HeadersKey, "x-api-key=abc"),
            (TracingOptions.TimeoutKey, "3"),
            (TracingOptions.ServiceNameKey, " cms-production "),
            (TracingOptions.SampleRatioKey, "0.25"),
            (TracingOptions.MaxQueueSizeKey, "100")));

        options.Endpoint.Should().Be(new Uri("https://collector.invalid/v1/traces"));
        options.Protocol.Should().Be("http/protobuf");
        options.Headers.Should().Be("x-api-key=abc");
        options.TimeoutSeconds.Should().Be(3);
        options.ServiceName.Should().Be("cms-production");
        options.SampleRatio.Should().Be(0.25);
        options.MaxQueueSize.Should().Be(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_endpoint_leaves_tracing_off_whatever_else_is_set(string? endpoint)
    {
        var options = TracingOptions.FromConfiguration(Settings(
            (TracingOptions.EndpointKey, endpoint),
            (TracingOptions.ProtocolKey, "not-a-protocol"),
            (TracingOptions.SampleRatioKey, "7")));

        options.Enabled.Should().BeFalse("the other settings mean nothing until there is somewhere to send spans");
    }

    [Theory]
    [InlineData("Tracing:Otlp:Endpoint", "collector:4317")]
    [InlineData("Tracing:Otlp:Endpoint", "/v1/traces")]
    [InlineData("Tracing:Otlp:Endpoint", "ftp://collector.invalid/")]
    [InlineData("Tracing:Otlp:Protocol", "thrift")]
    [InlineData("Tracing:Otlp:TimeoutSeconds", "0")]
    [InlineData("Tracing:Otlp:TimeoutSeconds", "61")]
    [InlineData("Tracing:SampleRatio", "-0.1")]
    [InlineData("Tracing:SampleRatio", "1.5")]
    [InlineData("Tracing:MaxQueueSize", "0")]
    [InlineData("Tracing:MaxQueueSize", "65537")]
    public void A_value_out_of_range_stops_the_host_and_names_the_setting(string key, string value)
    {
        var settings = new Dictionary<string, string?> { [TracingOptions.EndpointKey] = Endpoint };
        settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var read = () => TracingOptions.FromConfiguration(configuration);

        read.Should().Throw<InvalidOperationException>().WithMessage($"{key}*");
    }

    [Fact]
    public void A_refused_endpoint_is_not_quoted_in_the_message()
    {
        var read = () => TracingOptions.FromConfiguration(
            Settings((TracingOptions.EndpointKey, "ftp://user:a-password@collector.invalid/")));

        read.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain("a-password", "an endpoint can carry credentials");
    }

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/health/live", false)]
    [InlineData("/metrics", false)]
    [InlineData("/api/contents", true)]
    [InlineData("/api/healthy-eating", true)]
    [InlineData("/", true)]
    public void Probes_and_the_metrics_scrape_are_not_traced(string path, bool traced)
    {
        TracingSetup.IsTraced(path).Should().Be(traced);
    }

    private static IConfiguration Settings(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    /// <summary>What AddBarakoCMS refuses to register without, plus <paramref name="extra"/>.</summary>
    private static IConfiguration HostSettings(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=db;Username=u;Password=p;Database=d",
            ["ConnectionStrings:Postgres"] = "Host=db;Username=u;Password=p;Database=d",
            ["JWT:Key"] = "a-signing-key-that-is-comfortably-over-32-characters-long",
            ["JWT:Issuer"] = "test",
            ["JWT:Audience"] = "test",
        };

        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
