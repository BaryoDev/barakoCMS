using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// POST /api/webhooks/resend — receives Resend delivery events and records bounces/complaints as
/// <see cref="EmailEvent"/> documents, so apps can tell a user an address is bad. Verifies the Svix
/// signature when <c>Resend:WebhookSecret</c> (or RESEND_WEBHOOK_SECRET) is set.
/// </summary>
public sealed class ResendWebhookEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;

    public ResendWebhookEndpoint(IDocumentSession session, IConfiguration config)
    {
        _session = session;
        _config = config;
    }

    public override void Configure()
    {
        Post("/api/webhooks/resend");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        // Fail closed. This used to read `if (secret is set && signature is bad) reject`, which
        // short-circuits to "accept" when no secret is configured, so an unconfigured deployment
        // took forged webhooks from anyone. The payload writes EmailEvent documents that mark an
        // address bounced or complained, so a forged post can get a real recipient suppressed.
        //
        // An unconfigured receiver is refused rather than trusted: useless is a better failure
        // than forgeable, and the 401 says which setting is missing.
        var secret = _config["Resend:WebhookSecret"] ?? Environment.GetEnvironmentVariable("RESEND_WEBHOOK_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            Logger.LogError(
                "Resend webhook received but no signing secret is configured. Set Resend:WebhookSecret "
                + "or RESEND_WEBHOOK_SECRET. Refusing the request rather than trusting it.");
            await SendUnauthorizedAsync(ct);
            return;
        }

        if (!VerifySvix(secret, body))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var kind = type switch
            {
                "email.bounced" => "bounced",
                "email.complained" => "complained",
                "email.delivery_delayed" => "delivery_delayed",
                _ => null,
            };
            if (kind is null)
            {
                await SendOkAsync(ct); // ignore delivered/sent/opened/etc.
                return;
            }

            if (root.TryGetProperty("data", out var data))
            {
                var email = FirstRecipient(data);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    _session.Store(new EmailEvent
                    {
                        Email = email!.Trim().ToLowerInvariant(),
                        Type = kind,
                        Reason = BounceReason(data),
                        EmailId = data.TryGetProperty("email_id", out var eid) ? eid.GetString() ?? "" : "",
                        At = DateTime.UtcNow,
                    });
                    await _session.SaveChangesAsync(ct);
                }
            }
        }
        catch
        {
            // A malformed payload shouldn't make Resend retry forever; ack it.
        }

        await SendOkAsync(ct);
    }

    private static string? FirstRecipient(JsonElement data)
    {
        if (data.TryGetProperty("to", out var to))
        {
            if (to.ValueKind == JsonValueKind.Array && to.GetArrayLength() > 0)
                return to[0].GetString();
            if (to.ValueKind == JsonValueKind.String)
                return to.GetString();
        }
        return null;
    }

    private static string BounceReason(JsonElement data)
    {
        if (data.TryGetProperty("bounce", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            var type = b.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            var sub = b.TryGetProperty("subType", out var bs) ? bs.GetString() : null;
            var msg = b.TryGetProperty("message", out var bm) ? bm.GetString() : null;
            return string.Join(" — ", new[] { type, sub, msg }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return "";
    }

    /// <summary>Svix signature check: HMAC-SHA256 of "{id}.{timestamp}.{body}" with the whsec key.</summary>
    private bool VerifySvix(string secret, string body) => VerifySvix(
        secret,
        body,
        HttpContext.Request.Headers["svix-id"].ToString(),
        HttpContext.Request.Headers["svix-timestamp"].ToString(),
        HttpContext.Request.Headers["svix-signature"].ToString());

    /// <summary>
    /// The signature check, separated from HttpContext so it can be tested directly. This is the
    /// only thing standing between a stranger and a write on an anonymous endpoint, so it wants a
    /// test that does not depend on standing up a web host.
    /// </summary>
    internal static bool VerifySvix(string secret, string body, string id, string ts, string sigHeader)
    {
        try
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(sigHeader))
                return false;

            var key = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
            var keyBytes = Convert.FromBase64String(key);
            using var hmac = new HMACSHA256(keyBytes);
            var signed = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{body}"));
            var expected = Convert.ToBase64String(signed);

            foreach (var part in sigHeader.Split(' '))
            {
                var comma = part.IndexOf(',');
                var candidate = comma >= 0 ? part[(comma + 1)..] : part;
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected)))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
