namespace barakoCMS.Infrastructure.Tracing;

/// <summary>Where spans are sent, and how hard the host tries to send them.</summary>
/// <remarks>
/// Tracing is on only when <see cref="EndpointKey"/> is set. Unset, nothing is registered, nothing
/// listens and no connection is opened, so a deployment that never asked for telemetry sends none.
///
/// A value out of range stops the host instead of being clamped, for the reason the other limits
/// here give: a typing mistake should not quietly become some other number. No message quotes a
/// value, because an endpoint can carry credentials and the headers are credentials.
/// </remarks>
internal sealed record TracingOptions
{
    public const string EndpointKey = "Tracing:Otlp:Endpoint";
    public const string ProtocolKey = "Tracing:Otlp:Protocol";
    public const string HeadersKey = "Tracing:Otlp:Headers";
    public const string TimeoutKey = "Tracing:Otlp:TimeoutSeconds";
    public const string ServiceNameKey = "Tracing:ServiceName";
    public const string SampleRatioKey = "Tracing:SampleRatio";
    public const string MaxQueueSizeKey = "Tracing:MaxQueueSize";

    public const string Grpc = "grpc";
    public const string HttpProtobuf = "http/protobuf";

    public const string DefaultServiceName = "barakocms";
    public const int DefaultTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 60;
    public const int DefaultMaxQueueSize = 2048;
    public const int MaxMaxQueueSize = 65536;

    /// <summary>The most spans one export call carries.</summary>
    public const int MaxExportBatchSize = 512;

    /// <summary>The collector's OTLP endpoint. Null when tracing is off.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary><see cref="Grpc"/> or <see cref="HttpProtobuf"/>.</summary>
    public string Protocol { get; init; } = Grpc;

    /// <summary>Headers sent with every export, as <c>name=value,name=value</c>. A secret.</summary>
    public string? Headers { get; init; }

    /// <summary>How long one export call may take before it is given up and its spans dropped.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    public string ServiceName { get; init; } = DefaultServiceName;

    /// <summary>
    /// The share of traces started here that are recorded, 0 to 1. A request that arrives with a
    /// sampled <c>traceparent</c> is recorded whatever this says, and one that arrives unsampled
    /// is not.
    /// </summary>
    public double SampleRatio { get; init; } = 1.0;

    /// <summary>How many finished spans may wait for export. A span that finds it full is dropped.</summary>
    public int MaxQueueSize { get; init; } = DefaultMaxQueueSize;

    public bool Enabled => Endpoint is not null;

    public static TracingOptions FromConfiguration(IConfiguration configuration)
    {
        var endpoint = configuration[EndpointKey];
        if (string.IsNullOrWhiteSpace(endpoint)) return new TracingOptions();

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{EndpointKey} must be an absolute http or https URL, the OTLP endpoint of a collector. "
              + "Leave it unset to keep tracing off.");
        }

        var protocol = configuration[ProtocolKey];
        protocol = string.IsNullOrWhiteSpace(protocol) ? Grpc : protocol.Trim().ToLowerInvariant();
        if (protocol is not (Grpc or HttpProtobuf))
        {
            throw new InvalidOperationException($"{ProtocolKey} must be {Grpc} or {HttpProtobuf}.");
        }

        var timeout = configuration.GetValue(TimeoutKey, DefaultTimeoutSeconds);
        if (timeout is < 1 or > MaxTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"{TimeoutKey} must be between 1 and {MaxTimeoutSeconds}, and is {timeout}. "
              + "It is how many seconds one export to the collector may take.");
        }

        var ratio = configuration.GetValue(SampleRatioKey, 1.0);
        if (double.IsNaN(ratio) || ratio is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException(
                $"{SampleRatioKey} must be between 0 and 1. It is the share of traces started here that are recorded.");
        }

        var queue = configuration.GetValue(MaxQueueSizeKey, DefaultMaxQueueSize);
        if (queue is < 1 or > MaxMaxQueueSize)
        {
            throw new InvalidOperationException(
                $"{MaxQueueSizeKey} must be between 1 and {MaxMaxQueueSize}, and is {queue}. "
              + "It is how many finished spans may wait for export before new ones are dropped.");
        }

        var serviceName = configuration[ServiceNameKey];
        var headers = configuration[HeadersKey];

        return new TracingOptions
        {
            Endpoint = uri,
            Protocol = protocol,
            Headers = string.IsNullOrWhiteSpace(headers) ? null : headers,
            TimeoutSeconds = timeout,
            ServiceName = string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName.Trim(),
            SampleRatio = ratio,
            MaxQueueSize = queue,
        };
    }
}
