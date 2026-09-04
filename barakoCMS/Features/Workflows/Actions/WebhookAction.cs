using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending HTTP POST requests to webhooks.
/// </summary>
/// <remarks>
/// With a <c>Secret</c> parameter every delivery is signed; see <see cref="WebhookSigning"/> and
/// <c>docs/webhooks.md</c>. With or without one, every delivery leaves a <see cref="WebhookDelivery"/>
/// behind saying what was sent and what came back.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Send HTTP POST requests to external webhooks, signed when a Secret is set",
    RequiredParameters = new[] { "Url" },
    ExampleJson = @"{""Type"":""Webhook"",""Parameters"":{""Url"":""https://example.com/webhook"",""Secret"":""a shared secret, optional""}}"
)]
internal class WebhookAction : IWorkflowAction
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDocumentSession _session;
    private readonly ISecretProtector _protector;
    private readonly OutboundAddressGuard _addressGuard;
    private readonly ILogger<WebhookAction> _logger;
    private readonly bool _allowInsecureSignedUrls;

    /// <summary>
    /// Creates a new WebhookAction.
    /// </summary>
    public WebhookAction(
        IHttpClientFactory httpClientFactory,
        IDocumentSession session,
        ISecretProtector protector,
        OutboundAddressGuard addressGuard,
        ILogger<WebhookAction> logger,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _session = session;
        _protector = protector;
        _addressGuard = addressGuard;
        _logger = logger;
        _allowInsecureSignedUrls = WebhookSigning.AllowsInsecureSignedUrls(configuration);
    }

    /// <inheritdoc />
    public string Type => "Webhook";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Every way a delivery can fail here is an expected outcome of a configured target rather than
    /// a defect, so each one is a reason on the result. They used to be log lines and nothing else,
    /// which is why a webhook could answer 500 for a week without the workflow looking unhealthy.
    /// </remarks>
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var url = parameters.GetValueOrDefault("Url");
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("Webhook URL not provided. Skipping webhook action.");
            return WorkflowActionResult.PermanentFailure("No Url parameter was configured for this webhook action.");
        }

        var delivery = NewDelivery(parameters, redactedUrl: Redact(url));

        // Early, logged refusal for a URL that is obviously out of bounds. It is not the guard: the
        // address that gets dialled is checked again when the socket is opened, inside the client's
        // connect callback, which is the only check a changing DNS answer cannot get around.
        if (!await IsUrlSafeAsync(url, ct))
        {
            _logger.LogWarning("Webhook URL {Url} is not allowed (must be http/https to a non-internal host). Skipping webhook action.", Redact(url));
            // Permanent. A URL that is not http or https, or that resolves somewhere the guard
            // refuses, is the same on the fifth attempt as the first: it is a typo in the workflow,
            // not a provider having a bad afternoon.
            delivery.Error = "The URL is not allowed: it must be http or https to a non-internal host.";
            await RecordAsync(delivery, ct);
            return WorkflowActionResult.PermanentFailure(
                $"Webhook URL {Redact(url)} is not allowed: it must be http or https to a non-internal host.");
        }

        // Permanent, like a refused address: the scheme is in the workflow and a retry sends the same
        // one. The create endpoint refuses this first, so reaching it here means a definition saved
        // before the rule, or with the opt-in since turned off.
        if (WebhookSigning.IsInsecureSignedUrl(url, parameters, _allowInsecureSignedUrls))
        {
            _logger.LogWarning("Webhook URL {Url} is http and the action holds a secret. Skipping webhook action.", Redact(url));
            delivery.Error = WebhookSigning.InsecureSignedUrlReason;
            await RecordAsync(delivery, ct);
            return WorkflowActionResult.PermanentFailure(
                $"Webhook to {Redact(url)} was not sent. {WebhookSigning.InsecureSignedUrlReason}");
        }

        string? secret = null;
        if (WebhookSigning.HasSecret(parameters))
        {
            secret = _protector.Unprotect(parameters[WebhookSigning.SecretParameter]);
            if (secret is null)
            {
                // Refused rather than sent unsigned. A receiver that checks signatures would reject
                // it anyway, and one that does not would never learn that signing had silently
                // stopped. The reachable cause is a rotated Secrets:Key, and the fix is to enter the
                // secret again.
                _logger.LogWarning("The webhook secret for {Url} could not be decrypted. Skipping webhook action.", Redact(url));
                delivery.Error = "The webhook secret could not be decrypted. Enter it again on the workflow.";
                await RecordAsync(delivery, ct);
                return WorkflowActionResult.PermanentFailure(
                    $"The webhook secret for {Redact(url)} could not be decrypted (Secrets:Key changed?). Enter it again on the workflow.");
            }
        }

        var timer = Stopwatch.StartNew();

        try
        {
            var client = _httpClientFactory.CreateClient("ExternalApi");

            var payload = new
            {
                contentId = content.Id,
                contentType = content.ContentType,
                status = content.Status.ToString(),
                data = await DeliverableDataAsync(content, ct),
                createdAt = content.CreatedAt,
                updatedAt = content.UpdatedAt
            };

            // Serialised here rather than by JsonContent, because the signature is over the exact
            // bytes on the wire and the receiver recomputes it over the exact bytes it read. A body
            // serialised twice, once to sign and once to send, is only the same body by luck.
            var body = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
                },
            };

            // The key the runner computed for this attempt, sent so the receiver can recognise a
            // duplicate. It is stable across retries of the same action, which is what makes a
            // retry safe to make: without the header the receiver has no way to tell a retry from a
            // second publish, and the retry policy's whole premise is that it can.
            //
            // The runner has always put this in the parameters and nothing read it. That made the
            // key dead: the retries happened, the header did not, and every comment claiming a
            // duplicate would be absorbed downstream was describing something that was not sent.
            if (parameters.TryGetValue("IdempotencyKey", out var idempotencyKey)
                && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation(WebhookSigning.DeliveryHeader, delivery.Id.ToString());
            request.Headers.TryAddWithoutValidation(WebhookSigning.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));

            if (secret is not null)
            {
                request.Headers.TryAddWithoutValidation(WebhookSigning.SignatureHeader, WebhookSigning.Sign(secret, timestamp, body));
            }

            delivery.RequestHeaders = RecordableHeaders(request);

            // Headers only. The default completion option holds the whole body in memory before
            // returning, so the 4 KB cut below would apply after a receiver's 100 MB answer had
            // already been buffered.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            delivery.ResponseStatus = (int)response.StatusCode;
            delivery.ResponseBody = await ReadBoundedAsync(response.Content, ct);
            timer.Stop();
            delivery.DurationMs = timer.ElapsedMilliseconds;
            await RecordAsync(delivery, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhook successfully sent to {Url} for content {ContentId}", Redact(url), content.Id);
                return WorkflowActionResult.Success();
            }

            _logger.LogWarning("Webhook to {Url} returned status {StatusCode}", Redact(url), response.StatusCode);
            return WorkflowActionResult.Failure($"Webhook to {Redact(url)} returned status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a webhook failure. The broad catch below would have logged it as
            // an unexpected error and returned normally, so a cancelled run looked like a completed
            // one to everything upstream.
            throw;
        }
        catch (HttpRequestException ex)
        {
            timer.Stop();

            // The exception is not attached, and the stack is passed separately. Logging the
            // exception object writes its Message, and a transport failure raised against a URL that
            // carries a token in its query can carry that URL, and with it the token, into whatever
            // aggregates the logs. The stack trace is the half worth keeping and contains no URL.
            _logger.LogError(
                "Failed to send webhook to {Url} ({Exception}). {Stack}",
                Redact(url), ex.GetType().Name, ex.StackTrace);

            delivery.DurationMs = timer.ElapsedMilliseconds;
            delivery.Error = $"The request could not be delivered ({ex.GetType().Name}).";
            await RecordAsync(delivery, ct);

            // The exception type, not its message. A transport failure names the host it could not
            // reach, and for a URL carrying a token in its query that message is the token.
            return WorkflowActionResult.Failure(
                $"Webhook to {Redact(url)} could not be delivered ({ex.GetType().Name}).");
        }
        catch (Exception ex)
        {
            timer.Stop();

            // Same reason as above. This catch is broader, so the exception is likelier to be one
            // whose message names the request it was made against.
            _logger.LogError(
                "Unexpected error while sending webhook to {Url} ({Exception}). {Stack}",
                Redact(url), ex.GetType().Name, ex.StackTrace);

            delivery.DurationMs = timer.ElapsedMilliseconds;
            delivery.Error = $"The request failed ({ex.GetType().Name}).";
            await RecordAsync(delivery, ct);

            return WorkflowActionResult.Failure(
                $"Webhook to {Redact(url)} failed unexpectedly ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The row this delivery will leave behind, filled from what the runner put in the parameters.
    /// </summary>
    /// <remarks>
    /// The runner supplies <c>RunId</c>, <c>WorkflowId</c>, <c>TriggerEvent</c> and <c>Attempt</c>
    /// the same way it supplies <c>IdempotencyKey</c>. An action invoked some other way, the legacy
    /// engine or a test, leaves them out and the row still gets written with what is known.
    /// </remarks>
    private static WebhookDelivery NewDelivery(IReadOnlyDictionary<string, string> parameters, string redactedUrl)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            Url = redactedUrl,
            Event = parameters.GetValueOrDefault("TriggerEvent") ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var runId)) delivery.RunId = runId;
        if (Guid.TryParse(parameters.GetValueOrDefault("WorkflowId"), out var workflowId)) delivery.WorkflowId = workflowId;
        if (int.TryParse(parameters.GetValueOrDefault("Attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt) && attempt > 0)
        {
            delivery.Attempt = attempt;
        }

        return delivery;
    }

    /// <summary>Every header on the request except the signature.</summary>
    private static Dictionary<string, string> RecordableHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in request.Headers)
        {
            if (string.Equals(name, WebhookSigning.SignatureHeader, StringComparison.OrdinalIgnoreCase)) continue;
            headers[name] = string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        return headers;
    }

    /// <summary>The first <see cref="WebhookDelivery.ResponseBodyLimit"/> bytes of the body, or null when there were none.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);

        var buffer = new byte[WebhookDelivery.ResponseBodyLimit];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }

        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Writes the row. A failure to record is logged and does not change the outcome: the delivery
    /// happened or did not, and the row is the record of that rather than part of it.
    /// </summary>
    private async Task RecordAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        try
        {
            _session.Store(delivery);
            await _session.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not record webhook delivery {DeliveryId} ({Exception})",
                delivery.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The content data a webhook may carry: the fields the content type marks Public, and nothing
    /// at all from a document that is itself Sensitive or Hidden.
    /// </summary>
    /// <remarks>
    /// A webhook target is an arbitrary URL and there is no user behind a workflow, so there are no
    /// roles to mask against and the anonymous delivery projection is the right one. The field
    /// allowlist comes from <see cref="PublicDelivery.PublicData"/>, the same function the public
    /// read path uses, because a second copy of the rules is a second thing to keep in step.
    ///
    /// A missing content type definition yields nothing rather than everything: with no schema to
    /// say which fields are Public, there is no basis for sending any of them.
    ///
    /// Note that <c>IsPubliclyDeliverable</c> is deliberately not consulted. That flag governs the
    /// anonymous HTTP surface, and a webhook is configured by an operator rather than requested by
    /// the public; the sensitivity rules are what a webhook has to respect.
    /// </remarks>
    private async Task<Dictionary<string, object>> DeliverableDataAsync(barakoCMS.Models.Content content, CancellationToken ct)
    {
        if (content.Sensitivity != SensitivityLevel.Public)
            return new Dictionary<string, object>();

        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);

        return PublicDelivery.PublicData(content, definition);
    }

    private async Task<bool> IsUrlSafeAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return await _addressGuard.IsHostAllowedAsync(uri.Host, ct);
    }
    /// <summary>
    /// The part of a webhook URL that is safe to write down.
    /// </summary>
    /// <remarks>
    /// Scheme, host, port and path. The userinfo and the query string are dropped, because both are
    /// places a webhook URL routinely carries a secret: <c>https://user:token@host/hook</c> and
    /// <c>https://host/hook?key=...</c> are how most providers authenticate one.
    ///
    /// This matters more than a log line usually would. The error text is persisted on the run and
    /// returned by the workflow-run API, so an unredacted URL puts a live credential in the database,
    /// in the API response, and in whatever aggregates the logs, readable by everybody who can view
    /// runs rather than only by the person who configured it.
    ///
    /// The host and path are kept deliberately. "A webhook failed" with nothing else is not
    /// diagnosable, and the host is what an operator needs to tell one integration from another.
    /// </remarks>
    internal static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            // Unparseable, so nothing can be said about which part is the secret. The configured
            // value is refused earlier for exactly this, so reaching here means something else.
            return "(the configured URL could not be parsed)";
        }

        // UserName and Password have to be cleared here. The query does not: GetLeftPart(Path) stops
        // before it, which is why this returns scheme, host, port and path and nothing after. Setting
        // Query as well was in the first version of this and was dead, and a mutation that put the
        // query back changed no test, which is how it was found.
        var builder = new UriBuilder(parsed) { UserName = string.Empty, Password = string.Empty };

        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
