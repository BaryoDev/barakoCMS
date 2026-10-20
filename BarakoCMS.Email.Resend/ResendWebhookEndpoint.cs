using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
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
public sealed class ResendWebhookEndpoint(IDocumentSession session, IConfiguration config, IInboundWebhookVerifier verifier) : EndpointWithoutRequest
{
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
        var secret = config["Resend:WebhookSecret"] ?? Environment.GetEnvironmentVariable("RESEND_WEBHOOK_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            Logger.LogError(
                "Resend webhook received but no signing secret is configured. Set Resend:WebhookSecret "
                + "or RESEND_WEBHOOK_SECRET. Refusing the request rather than trusting it.");
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var headers = HttpContext.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
        var outcome = await verifier.ReceiveAsync(
            new InboundWebhookRequest("resend", InboundWebhookScheme.Svix, secret, body, headers),
            token => RecordAsync(body, token),
            ct);

        if (outcome is InboundWebhookOutcome.BadSignature or InboundWebhookOutcome.OutsideWindow)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(ct);
    }

    private async Task RecordAsync(string body, CancellationToken ct)
    {
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
                return; // ignore delivered/sent/opened/etc.

            if (root.TryGetProperty("data", out var data))
            {
                var email = FirstRecipient(data);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var emailId = data.TryGetProperty("email_id", out var eid) ? eid.GetString() ?? "" : "";
                    session.Store(new EmailEvent
                    {
                        Email = email!.Trim().ToLowerInvariant(),
                        Type = kind,
                        Reason = BounceReason(data),
                        EmailId = emailId,
                        Tenant = await SendingTenantAsync(session, emailId, ct),
                        At = DateTime.UtcNow,
                    });
                }
            }
        }
        catch (JsonException)
        {
            // A malformed payload shouldn't make Resend retry forever; ack it.
        }
    }

    /// <summary>The tenant that sent this email, from the send record, or null when there is none.</summary>
    internal static async Task<string?> SendingTenantAsync(IQuerySession session, string emailId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(emailId)
            ? null
            : (await session.LoadAsync<SentEmail>(emailId, ct))?.Tenant;

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

    /// <summary>How far from now a webhook timestamp may be, in either direction.</summary>
    internal static readonly TimeSpan Tolerance = InboundWebhookVerifier.DefaultTolerance;

    /// <summary>
    /// The Svix signature and window check, kept as a plain function so it can be tested without a
    /// web host. The check itself lives in core, in <see cref="IInboundWebhookVerifier"/>.
    /// </summary>
    internal static bool VerifySvix(
        string secret, string body, string id, string ts, string sigHeader, DateTimeOffset now) =>
        InboundWebhookVerifier.Check(
            new InboundWebhookRequest("resend", InboundWebhookScheme.Svix, secret, body,
                new Dictionary<string, string>
                {
                    ["svix-id"] = id,
                    ["svix-timestamp"] = ts,
                    ["svix-signature"] = sigHeader,
                }),
            now) == InboundWebhookOutcome.Processed;
}
