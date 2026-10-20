namespace barakoCMS.Core.Interfaces;

/// <summary>How a provider signs the requests it sends.</summary>
public enum InboundWebhookScheme
{
    /// <summary>
    /// HMAC-SHA256 of the body (or of <c>{timestamp}.{body}</c> when a timestamp header is named),
    /// hex encoded in <see cref="InboundWebhookRequest.SignatureHeader"/>, optionally prefixed
    /// <c>sha256=</c>. The secret is used as UTF-8 text.
    /// </summary>
    Hmac,

    /// <summary>
    /// Svix: HMAC-SHA256 of <c>{svix-id}.{svix-timestamp}.{body}</c> keyed by the base64 part of a
    /// <c>whsec_</c> secret, sent as space separated <c>v1,{signature}</c> candidates.
    /// </summary>
    Svix,
}

/// <summary>One signed request a provider sent to a receiver.</summary>
/// <param name="Provider">Names the sender, used to key duplicates. For example <c>resend</c>.</param>
/// <param name="Scheme">How the request is signed.</param>
/// <param name="Secret">The shared signing secret.</param>
/// <param name="Body">The raw request body, exactly as received.</param>
/// <param name="Headers">The request headers. Names are matched case-insensitively.</param>
public sealed record InboundWebhookRequest(
    string Provider,
    InboundWebhookScheme Scheme,
    string Secret,
    string Body,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Hmac only: the header holding the signature.</summary>
    public string SignatureHeader { get; init; } = "X-Signature";

    /// <summary>Hmac only: the header holding a unix timestamp, or null when the provider sends none.</summary>
    public string? TimestampHeader { get; init; }

    /// <summary>Hmac only: the header holding the provider's event id, or null for no deduplication.</summary>
    public string? EventIdHeader { get; init; }
}

/// <summary>What the receiver did with a request.</summary>
public enum InboundWebhookOutcome
{
    /// <summary>Verified, new, and the handler ran.</summary>
    Processed,

    /// <summary>Verified, but this event id was handled before. The handler did not run. Answer 200.</summary>
    Duplicate,

    /// <summary>The signature is missing or wrong. Answer 401.</summary>
    BadSignature,

    /// <summary>The timestamp is outside the replay window or unreadable. Answer 401.</summary>
    OutsideWindow,
}

/// <summary>
/// Verifies and de-duplicates requests a third party sends to an anonymous receiver, so each
/// receiver does not carry its own copy of the signature check.
/// </summary>
/// <remarks>
/// Core exposes it and modules consume it. Verification fails closed: an empty secret never
/// verifies.
/// </remarks>
public interface IInboundWebhookVerifier
{
    /// <summary>How far a request timestamp may be from now, in either direction.</summary>
    TimeSpan Tolerance { get; }

    /// <summary>
    /// Checks the signature and the timestamp window only. Returns <see cref="InboundWebhookOutcome.Processed"/>
    /// when both pass. Does not touch the database.
    /// </summary>
    InboundWebhookOutcome Verify(InboundWebhookRequest request, DateTimeOffset now);

    /// <summary>
    /// Verifies the request, skips an event id already handled, and otherwise runs
    /// <paramref name="handler"/> and records the id in the same commit as whatever the handler
    /// stored. If the handler throws, nothing is recorded, so the provider's retry is handled.
    /// </summary>
    Task<InboundWebhookOutcome> ReceiveAsync(
        InboundWebhookRequest request, Func<CancellationToken, Task> handler, CancellationToken ct);
}
