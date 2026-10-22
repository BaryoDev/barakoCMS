using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>The core <see cref="IInboundWebhookVerifier"/>.</summary>
public sealed class InboundWebhookVerifier(IDocumentSession session) : IInboundWebhookVerifier
{
    /// <summary>
    /// Five minutes either way, which is what the Svix spec suggests. Symmetric because the clocks
    /// on both machines can be wrong.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public TimeSpan Tolerance => DefaultTolerance;

    public InboundWebhookOutcome Verify(InboundWebhookRequest request, DateTimeOffset now) =>
        Check(request, now);

    /// <summary>The same check without a session, for callers that only need the signature.</summary>
    public static InboundWebhookOutcome Check(InboundWebhookRequest request, DateTimeOffset now)
    {
        var tolerance = DefaultTolerance;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Secret))
                return InboundWebhookOutcome.BadSignature;

            var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
            string Header(string? name) => name is not null && headers.TryGetValue(name, out var v) ? v : "";

            var isSvix = request.Scheme == InboundWebhookScheme.Svix;
            var timestampHeader = isSvix ? "svix-timestamp" : request.TimestampHeader;
            var signatureHeader = isSvix ? "svix-signature" : request.SignatureHeader;
            var ts = Header(timestampHeader);
            var signature = Header(signatureHeader);
            var id = isSvix ? Header("svix-id") : "";

            if (string.IsNullOrEmpty(signature) || (isSvix && (id.Length == 0 || ts.Length == 0)))
                return InboundWebhookOutcome.BadSignature;

            if (timestampHeader is not null)
            {
                // An unreadable timestamp is refused, not read as zero.
                if (!long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
                    return InboundWebhookOutcome.OutsideWindow;
                var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                if (age > tolerance || age < -tolerance)
                    return InboundWebhookOutcome.OutsideWindow;
            }

            return isSvix
                ? VerifySvix(request, id, ts, signature)
                : VerifyHmac(request, ts, signature);
        }
        catch
        {
            return InboundWebhookOutcome.BadSignature;
        }
    }

    private static InboundWebhookOutcome VerifySvix(InboundWebhookRequest request, string id, string ts, string header)
    {
        var key = request.Secret.StartsWith("whsec_", StringComparison.Ordinal)
            ? request.Secret["whsec_".Length..]
            : request.Secret;
        using var hmac = new HMACSHA256(Convert.FromBase64String(key));
        var expected = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{request.Body}")));

        foreach (var part in header.Split(' '))
        {
            var comma = part.IndexOf(',');
            var candidate = comma >= 0 ? part[(comma + 1)..] : part;
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected)))
                return InboundWebhookOutcome.Processed;
        }
        return InboundWebhookOutcome.BadSignature;
    }

    private static InboundWebhookOutcome VerifyHmac(InboundWebhookRequest request, string ts, string header)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(request.Secret));
        var signed = request.TimestampHeader is null ? request.Body : $"{ts}.{request.Body}";
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        var candidate = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..]
            : header;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate.ToLowerInvariant()), Encoding.UTF8.GetBytes(expected))
            ? InboundWebhookOutcome.Processed
            : InboundWebhookOutcome.BadSignature;
    }

    public async Task<InboundWebhookOutcome> ReceiveAsync(
        InboundWebhookRequest request, Func<CancellationToken, Task> handler, CancellationToken ct)
    {
        var verdict = Verify(request, DateTimeOffset.UtcNow);
        if (verdict != InboundWebhookOutcome.Processed)
            return verdict;

        var eventId = EventIdOf(request);
        string? key = null;
        if (!string.IsNullOrEmpty(eventId))
        {
            key = InboundWebhookEvent.IdFor(request.Provider, eventId);
            if (await session.LoadAsync<InboundWebhookEvent>(key, ct) is not null)
                return InboundWebhookOutcome.Duplicate;
            session.Insert(new InboundWebhookEvent { Id = key, ReceivedAt = DateTime.UtcNow });
        }

        try
        {
            await handler(ct);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception e) when (key is not null && e.GetType().Name == "DocumentAlreadyExistsException")
        {
            // Two deliveries of the same event raced past the check above; the other one won.
            return InboundWebhookOutcome.Duplicate;
        }
        return InboundWebhookOutcome.Processed;
    }

    private static string? EventIdOf(InboundWebhookRequest request)
    {
        var name = request.Scheme == InboundWebhookScheme.Svix ? "svix-id" : request.EventIdHeader;
        if (name is null)
            return null;
        foreach (var (k, v) in request.Headers)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }
}
