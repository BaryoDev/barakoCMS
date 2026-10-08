using barakoCMS.Infrastructure.Health;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace barakoCMS.Infrastructure.Tracing;

/// <summary>Registers OpenTelemetry tracing when an OTLP endpoint is configured, and nothing otherwise.</summary>
/// <remarks>
/// Read from the configuration handed to <c>AddBarakoCMS</c>, once, because whether the tracer
/// exists at all is decided at registration.
///
/// What is exported: the request span ASP.NET Core starts, which continues a caller's
/// <c>traceparent</c>; the span of every outbound <c>HttpClient</c> call, which carries the context
/// on to the receiver; the spans from <see cref="BarakoTracing"/>; and Npgsql's span of each
/// database command made under one of those, cut down by <see cref="SpanScrubber"/> first. The
/// <c>Carom</c> source is listened to as well, and cut to its name and timing.
///
/// Export never waits on a request. A finished span goes on a bounded queue and a background
/// thread sends batches; a full queue drops the span, and a collector that is down or slow costs
/// the batch after <see cref="TracingOptions.TimeoutSeconds"/>, not the request.
/// </remarks>
internal static class TracingSetup
{
    public static TracingOptions Add(IServiceCollection services, IConfiguration configuration)
    {
        var options = TracingOptions.FromConfiguration(configuration);

        // A method of its own, so a host with tracing off never enters code that names the SDK.
        if (options.Enabled) Register(services, options);

        return options;
    }

    private static void Register(IServiceCollection services, TracingOptions options)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: typeof(TracingSetup).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing => Export(
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SampleRatio)))
                    .AddSource(BarakoTracing.SourceName, SpanScrubber.DatabaseSource, SpanScrubber.CaromSource)
                    .AddAspNetCoreInstrumentation(server => server.Filter = context => IsTraced(context.Request.Path))
                    .AddHttpClientInstrumentation(),
                options));
    }

    /// <summary>The scrubber and then the exporter, in that order, so the exporter sees only what is left.</summary>
    /// <remarks>
    /// Apart from <see cref="Register"/> so a test can put the same export behind a source of its
    /// own, without listening to any request the rest of the process is serving.
    /// </remarks>
    internal static TracerProviderBuilder Export(TracerProviderBuilder tracing, TracingOptions options)
    {
        var endpoint = options.Endpoint!;
        var timeoutMilliseconds = options.TimeoutSeconds * 1000;

        return tracing
            .AddProcessor(new SpanScrubber())
            .AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = endpoint;
                otlp.Protocol = options.Protocol == TracingOptions.HttpProtobuf
                    ? OtlpExportProtocol.HttpProtobuf
                    : OtlpExportProtocol.Grpc;
                otlp.TimeoutMilliseconds = timeoutMilliseconds;
                if (options.Headers is not null) otlp.Headers = options.Headers;

                otlp.ExportProcessorType = ExportProcessorType.Batch;
                otlp.BatchExportProcessorOptions.MaxQueueSize = options.MaxQueueSize;
                otlp.BatchExportProcessorOptions.MaxExportBatchSize =
                    Math.Min(TracingOptions.MaxExportBatchSize, options.MaxQueueSize);
                otlp.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = timeoutMilliseconds;
            });
    }

    /// <summary>
    /// False for the health probes and the metrics scrape, which arrive every few seconds from the
    /// platform and would be most of what a collector receives.
    /// </summary>
    internal static bool IsTraced(PathString path) =>
        !HealthProbePaths.IsHealthPath(path.Value)
        && !path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}
