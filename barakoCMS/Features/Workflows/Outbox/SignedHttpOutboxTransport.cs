using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Outbox;

/// <summary>
/// Posts an outbox message as JSON, signed the way a webhook is (see <see cref="WebhookSigning"/> and
/// <c>docs/webhooks.md</c>), through the same guarded client.
/// </summary>
/// <remarks>
/// The message id goes out as both <c>X-Barako-Delivery</c> and <c>Idempotency-Key</c>, and it is the
/// same on every attempt, so a receiver that already took the message can tell a retry from a new one.
/// </remarks>
internal sealed class SignedHttpOutboxTransport(
    IHttpClientFactory httpClientFactory,
    ISecretProtector protector,
    OutboundAddressGuard addressGuard,
    IConfiguration configuration,
    ILogger<SignedHttpOutboxTransport> logger) : IOutboxTransport
{
    public const string TopicHeader = "X-Barako-Topic";
    public const string MessageTypeHeader = "X-Barako-Message-Type";

    public async Task<OutboxSendResult> SendAsync(OutboxMessage message, CancellationToken ct)
    {
        var redacted = WebhookAction.Redact(message.Url);

        if (!Uri.TryCreate(message.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !await addressGuard.IsHostAllowedAsync(uri.Host, uri.Port, ct))
        {
            return OutboxSendResult.Refused($"The URL {redacted} is not allowed: it must be http or https to a non-internal host.");
        }

        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(message.Secret)) parameters[WebhookSigning.SecretParameter] = message.Secret;

        if (WebhookSigning.IsInsecureSignedUrl(message.Url, parameters, WebhookSigning.AllowsInsecureSignedUrls(configuration)))
        {
            return OutboxSendResult.Refused(WebhookSigning.InsecureSignedUrlReason);
        }

        string? secret = null;
        if (parameters.Count > 0)
        {
            secret = protector.Unprotect(message.Secret!);
            if (secret is null)
            {
                return OutboxSendResult.Refused("The signing secret could not be decrypted. Enter it again on the workflow.");
            }
        }

        var timer = Stopwatch.StartNew();

        try
        {
            var body = Encoding.UTF8.GetBytes(message.Body);
            using var request = new HttpRequestMessage(HttpMethod.Post, message.Url)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
                },
            };

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation(WebhookSigning.DeliveryHeader, message.Id.ToString());
            request.Headers.TryAddWithoutValidation("Idempotency-Key", message.Id.ToString());
            request.Headers.TryAddWithoutValidation(WebhookSigning.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));

            if (message.Tenant.All(c => c is > ' ' and <= '~'))
                request.Headers.TryAddWithoutValidation(WebhookSigning.TenantHeader, message.Tenant);
            if (message.Topic.All(c => c is > ' ' and <= '~'))
                request.Headers.TryAddWithoutValidation(TopicHeader, message.Topic);
            if (message.MessageType.All(c => c is > ' ' and <= '~'))
                request.Headers.TryAddWithoutValidation(MessageTypeHeader, message.MessageType);

            if (secret is not null)
                request.Headers.TryAddWithoutValidation(WebhookSigning.SignatureHeader, WebhookSigning.Sign(secret, timestamp, body));

            request.Options.Set(OutboundResilienceHandler.Replayable, true);
            OutboundResilienceHandler.SetTenant(request, message.Tenant);

            using var response = await httpClientFactory.CreateClient("ExternalApi")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            timer.Stop();
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode) return OutboxSendResult.Ok(status, timer.ElapsedMilliseconds);

            // A 4xx other than a timeout or a throttle is the receiver saying the message itself is
            // wrong, which sending it again does not fix.
            var permanent = status is >= 400 and < 500 && status is not (408 or 425 or 429);
            return permanent
                ? new OutboxSendResult(false, true, status, $"{redacted} refused the message with status {status}.", timer.ElapsedMilliseconds)
                : OutboxSendResult.Retry($"{redacted} answered with status {status}.", status, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            logger.LogWarning("Outbox message {MessageId} to {Url} was not delivered ({Exception})", message.Id, redacted, ex.GetType().Name);
            return OutboxSendResult.Retry($"The request to {redacted} could not be delivered ({ex.GetType().Name}).", null, timer.ElapsedMilliseconds);
        }
    }
}
