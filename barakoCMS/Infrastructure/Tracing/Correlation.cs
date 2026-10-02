using System.Diagnostics;
using Serilog.Context;

namespace barakoCMS.Infrastructure.Tracing;

/// <summary>
/// The correlation id of the work in hand and the span that caused it, for whatever is written
/// while that work runs.
/// </summary>
/// <remarks>
/// Ambient, because the things that need it run far from where it is known: events are stamped
/// by <see cref="EventOriginListener"/> when a session saves, and a workflow run is queued inside
/// the projection daemon, and neither is handed a request.
///
/// Three places begin one. A request, in <see cref="Middleware.CorrelationIdMiddleware"/>. The
/// workflow projection, from the metadata of the event it is handling. The workflow runner, from
/// the run it claimed. Anything else (a scheduled publish, a collection sync, a module's own
/// background work) runs with none, and what it writes carries none: null means no request caused
/// this, and nothing is invented to fill it.
///
/// Both values are stored and logged, so both are validated on the way in, including when they
/// are read back from the database.
/// </remarks>
internal static class Correlation
{
    /// <summary>The longest correlation id accepted. A trace id is 32 characters and a GUID 36.</summary>
    public const int MaxLength = 64;

    /// <summary>The length of a W3C <c>traceparent</c> of version 00, the only one written here.</summary>
    private const int TraceParentLength = 55;

    private static readonly AsyncLocal<Scope?> Ambient = new();

    /// <summary>The correlation id of the work in hand, or null when no request caused it.</summary>
    public static string? Id => Ambient.Value?.Id;

    /// <summary>
    /// The W3C <c>traceparent</c> of what is doing the work, or null. The current span, unless the
    /// scope was begun with <see cref="BeginFrom"/>, which fixes it.
    /// </summary>
    public static string? Cause =>
        Ambient.Value is { Fixed: true } scope ? scope.Cause : TraceParentOf(Activity.Current);

    /// <summary>
    /// Makes <paramref name="id"/> the correlation id until the result is disposed, and puts it on
    /// every log line written meanwhile. The cause is whichever span is current when it is asked for.
    /// </summary>
    /// <param name="id">The correlation id. A value that is not a valid one counts as none.</param>
    public static IDisposable Begin(string? id) => Enter(new Scope(Normalise(id), null, false, Ambient.Value));

    /// <summary>
    /// As <see cref="Begin"/>, for work picked up from something stored, with the cause fixed to
    /// <paramref name="cause"/> whatever span is current. Null fixes it to none.
    /// </summary>
    /// <remarks>
    /// The projection passes the <c>traceparent</c> on the event, because a span of the daemon is
    /// not what caused a run, and an event with none must not borrow one.
    /// </remarks>
    public static IDisposable BeginFrom(string? id, string? cause) =>
        Enter(new Scope(Normalise(id), NormaliseTraceParent(cause), true, Ambient.Value));

    private static Scope Enter(Scope scope)
    {
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>A fresh id for a request that arrived with none and has no trace to take one from.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// <paramref name="value"/> when it can be stored and logged as it is, otherwise null.
    /// </summary>
    /// <remarks>
    /// Letters, digits, dot, underscore and hyphen, up to <see cref="MaxLength"/>. That admits a
    /// trace id, a GUID in either form and a ULID, and nothing that can break a log line, a header
    /// or a query. The check runs before the value is used for anything, so a refused one is never
    /// echoed.
    /// </remarks>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return null;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) return null;
        }

        return value;
    }

    /// <summary><paramref name="value"/> when it is a W3C <c>traceparent</c>, otherwise null.</summary>
    /// <remarks>
    /// Every character is checked here and not left to <c>ActivityContext.TryParse</c>, which
    /// promises to find the ids and nothing about what sits between them. The value is stored and
    /// sent on as a header, so a line break where a hyphen belongs must not get through. TryParse
    /// is still asked, because it is what refuses an id of all zeros.
    /// </remarks>
    public static string? NormaliseTraceParent(string? value) =>
        value is { Length: TraceParentLength }
        && value.StartsWith("00-", StringComparison.Ordinal)
        && value[35] == '-'
        && value[52] == '-'
        && IsLowerHex(value.AsSpan(3, 32))
        && IsLowerHex(value.AsSpan(36, 16))
        && IsLowerHex(value.AsSpan(53, 2))
        && ActivityContext.TryParse(value, null, out _)
            ? value
            : null;

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        }

        return true;
    }

    /// <summary>The trace id of <paramref name="activity"/>, or null when it has no W3C one.</summary>
    /// <remarks>
    /// Read from the typed id, never from <see cref="Activity.Id"/> or <see cref="Activity.RootId"/>
    /// of a span in the older hierarchical format: those are built from whatever the caller sent in
    /// <c>traceparent</c> or <c>Request-Id</c>, unparsed.
    /// </remarks>
    public static string? TraceIdOf(Activity? activity) =>
        activity is { IdFormat: ActivityIdFormat.W3C } ? activity.TraceId.ToHexString() : null;

    /// <summary>The <c>traceparent</c> that names <paramref name="activity"/> as a parent, or null.</summary>
    public static string? TraceParentOf(Activity? activity) =>
        activity is { IdFormat: ActivityIdFormat.W3C } ? NormaliseTraceParent(activity.Id) : null;

    private sealed class Scope(string? id, string? cause, bool isFixed, Scope? previous) : IDisposable
    {
        private readonly IDisposable? _log = id is null ? null : LogContext.PushProperty("CorrelationId", id);

        public string? Id { get; } = id;

        public string? Cause { get; } = cause;

        public bool Fixed { get; } = isFixed;

        public void Dispose()
        {
            Ambient.Value = previous;
            _log?.Dispose();
        }
    }
}
