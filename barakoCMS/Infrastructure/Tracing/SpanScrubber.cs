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
/// until someone decides it is safe.
///
/// Which list applies is decided by the span's kind as well as its source. ASP.NET Core takes the
/// source of its request spans from the container, so a host that registers its own would rename
/// them, and a rule keyed on the name alone would then let the path through. So every Server span
/// is cut to the request list and every Client span to the outbound list, whatever started it.
/// Internal, Consumer and Producer spans pass untouched, which is where
/// <see cref="BarakoTracing"/> and a host's own work land.
///
/// Npgsql's spans are Client spans with a list of their own, decided by source before kind. They
/// keep the statement, which holds placeholders and not parameter values, cut to
/// <see cref="MaxStatementLength"/>; they lose the connection string, the database user and the
/// connection id. Two kinds are not exported at all: a database span with no parent, which is the
/// projection daemon and the other background polls and would be most of what a collector gets,
/// and any cut-down span that carries an event with attributes. Npgsql records a failed command
/// as an event holding the server's message, which can quote row values, and an event cannot be
/// edited after it is added, so the span is dropped. Its parent still shows the failure.
///
/// Carom's source is cut to nothing but its name and timing. Carom 2.0.1 starts no spans itself;
/// one started through its telemetry package carries whatever its caller put on it, and a breaker
/// key can hold a URL.
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

    /// <summary>The source Npgsql starts a span from for every command.</summary>
    public const string DatabaseSource = "Npgsql";

    /// <summary>The source Carom's telemetry package starts spans from.</summary>
    public const string CaromSource = "Carom";

    /// <summary>The longest statement text a database span keeps.</summary>
    public const int MaxStatementLength = 2000;

    private const string StatementTag = "db.statement";

    /// <remarks>
    /// No <c>db.connection_string</c>, <c>db.user</c> or <c>db.connection_id</c>. The statement is
    /// the command text as written, with <c>$1</c> or <c>@name</c> where a value goes; the values
    /// are sent apart from it and Npgsql never puts them on a span.
    /// </remarks>
    internal static readonly IReadOnlySet<string> DatabaseTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "db.system",
        "db.name",
        "db.operation",
        StatementTag,
        "net.transport",
        "net.peer.name",
        "net.peer.port",
    };

    internal static readonly IReadOnlySet<string> CaromTags = new HashSet<string>(StringComparer.Ordinal);

    /// <remarks>
    /// No <c>server.address</c>. On a request span it is the caller's Host header, so it is text
    /// the caller chose, with as many values as callers care to send.
    /// </remarks>
    internal static readonly IReadOnlySet<string> ServerTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "network.protocol.version",
        "error.type",
        BarakoTracing.CorrelationIdTag,
    };

    /// <remarks>Here <c>server.address</c> is the host this server chose to call, and it stays.</remarks>
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
        if (AllowedFor(data.Source.Name, data.Kind) is not { } allowed) return;

        if (ShouldDrop(data))
        {
            // The exporters skip a span that is not recorded, and they run after this.
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        KeepOnly(data, allowed);

        if (data.GetTagItem(StatementTag) is string { Length: > MaxStatementLength } statement)
        {
            data.SetTag(StatementTag, statement[..MaxStatementLength]);
        }
    }

    /// <summary>A database span with no parent, or a cut-down span holding an event with attributes.</summary>
    internal static bool ShouldDrop(Activity span)
    {
        if (span.Source.Name == DatabaseSource && span.ParentSpanId == default) return true;

        foreach (var @event in span.Events)
        {
            if (@event.Tags.Any()) return true;
        }

        return false;
    }

    /// <summary>The attributes a span of that source and kind may keep, or null for all of them.</summary>
    internal static IReadOnlySet<string>? AllowedFor(string sourceName, ActivityKind kind) =>
        sourceName == ServerSource || kind == ActivityKind.Server ? ServerTags
        : sourceName == DatabaseSource ? DatabaseTags
        : sourceName == CaromSource ? CaromTags
        : sourceName == ClientSource || kind == ActivityKind.Client ? ClientTags
        : null;

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
