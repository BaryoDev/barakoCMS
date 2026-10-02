using System.Diagnostics;
using OpenTelemetry;

namespace barakoCMS.Infrastructure.Tracing;

/// <summary>
/// Cuts the request and outbound call spans down to a fixed list of attributes before they are
/// exported.
/// </summary>
/// <remarks>
/// The same rule as <see cref="Logging.LogSafe"/>, in the other place a value can leave the
/// process. The framework fills these spans, not this codebase, and what it records by default
/// includes the request path, the query string and the full URL of an outbound call. A path here
/// can hold a share link or a preview token, and a webhook URL is often the credential itself, so
/// the destination is exported as host and port and the path is not exported at all.
///
/// A list of what stays, not of what goes: an attribute a later framework version adds is dropped
/// until someone decides it is safe. Spans from any other source pass through untouched, which is
/// where <see cref="BarakoTracing"/> and a host's own sources land.
///
/// It runs at the end of the span, when every attribute is set, and it is registered ahead of the
/// exporter, so the exporter never sees the original.
/// </remarks>
internal sealed class SpanScrubber : BaseProcessor<Activity>
{
    /// <summary>The source ASP.NET Core starts request spans from.</summary>
    public const string ServerSource = "Microsoft.AspNetCore";

    /// <summary>The source HttpClient starts outbound call spans from.</summary>
    public const string ClientSource = "System.Net.Http";

    internal static readonly IReadOnlySet<string> ServerTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "network.protocol.version",
        "server.address",
        "server.port",
        "error.type",
        BarakoTracing.CorrelationIdTag,
    };

    internal static readonly IReadOnlySet<string> ClientTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.request.resend_count",
        "http.response.status_code",
        "network.protocol.version",
        "server.address",
        "server.port",
        "error.type",
    };

    public override void OnEnd(Activity data)
    {
        if (AllowedFor(data.Source.Name) is { } allowed)
        {
            KeepOnly(data, allowed);
        }
    }

    /// <summary>The attributes a span from <paramref name="sourceName"/> may keep, or null for all of them.</summary>
    internal static IReadOnlySet<string>? AllowedFor(string sourceName) => sourceName switch
    {
        ServerSource => ServerTags,
        ClientSource => ClientTags,
        _ => null,
    };

    /// <summary>Removes every attribute not in <paramref name="allowed"/>, and the status text.</summary>
    /// <remarks>
    /// The status keeps its code. Its description is free text an instrumentation writes from an
    /// exception or a cancellation, which is not on the list either.
    /// </remarks>
    internal static void KeepOnly(Activity span, IReadOnlySet<string> allowed)
    {
        List<string>? dropped = null;

        foreach (var tag in span.TagObjects)
        {
            if (!allowed.Contains(tag.Key))
            {
                (dropped ??= []).Add(tag.Key);
            }
        }

        if (dropped is not null)
        {
            foreach (var key in dropped)
            {
                span.SetTag(key, null);
            }
        }

        if (span.StatusDescription is not null)
        {
            span.SetStatus(span.Status);
        }
    }
}
