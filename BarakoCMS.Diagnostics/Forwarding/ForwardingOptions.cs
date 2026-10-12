namespace BarakoCMS.Diagnostics.Forwarding;

/// <summary>
/// Forwarding captured browser errors to a Sentry-compatible receiver (#627), bound from
/// <c>Modules:Diagnostics:Forwarding</c>. Off unless <see cref="Enabled"/> is true and
/// <see cref="Dsn"/> parses, so an instance without the section behaves exactly as before.
/// </summary>
public sealed class ForwardingOptions
{
    public const string SectionName = "Forwarding";

    public bool Enabled { get; set; }

    /// <summary>
    /// The project DSN the receiver gives you, <c>https://PUBLIC_KEY@host/PROJECT_ID</c>. Sentry,
    /// GlitchTip and other receivers of the same protocol all hand one out.
    /// </summary>
    public string? Dsn { get; set; }

    /// <summary>Sent as the event's environment, for example <c>production</c>. Not sent when unset.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// The most reports waiting to be sent. Past it, new reports are dropped, not queued: a receiver
    /// that is down must not grow this process's memory.
    /// </summary>
    public int QueueCapacity { get; set; } = 500;

    /// <summary>How long one send may take before it is abandoned.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    internal SentryDsn? ParsedDsn => Enabled ? SentryDsn.TryParse(Dsn) : null;
}

/// <summary>A Sentry DSN taken apart: where to send, and the public key that goes in the auth header.</summary>
/// <param name="EnvelopeEndpoint">Where envelopes are posted.</param>
/// <param name="PublicKey">The key the auth header names.</param>
/// <param name="Public">The DSN without a secret key, which is what goes in the envelope header.</param>
internal sealed record SentryDsn(Uri EnvelopeEndpoint, string PublicKey, string Public)
{
    public static SentryDsn? TryParse(string? dsn)
    {
        if (string.IsNullOrWhiteSpace(dsn)
            || !Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var key = uri.UserInfo.Split(':')[0];
        var path = uri.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var project = slash >= 0 ? path[(slash + 1)..] : path;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(project))
        {
            return null;
        }

        var prefix = slash > 0 ? path[..slash] : string.Empty;
        var endpoint = new UriBuilder(uri.Scheme, uri.Host, uri.Port, $"{prefix}/api/{project}/envelope/").Uri;
        var withoutSecret = new UriBuilder(uri) { UserName = key, Password = string.Empty }.Uri.ToString().TrimEnd('/');
        return new SentryDsn(endpoint, key, withoutSecret);
    }
}
