using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending HTTP POST requests to webhooks.
/// </summary>
[WorkflowActionMetadata(
    Description = "Send HTTP POST requests to external webhooks",
    RequiredParameters = new[] { "Url" },
    ExampleJson = @"{""Type"":""Webhook"",""Parameters"":{""Url"":""https://example.com/webhook""}}"
)]
internal class WebhookAction : IWorkflowAction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IQuerySession _session;
    private readonly OutboundAddressGuard _addressGuard;
    private readonly ILogger<WebhookAction> _logger;

    /// <summary>
    /// Creates a new WebhookAction.
    /// </summary>
    public WebhookAction(
        IHttpClientFactory httpClientFactory,
        IQuerySession session,
        OutboundAddressGuard addressGuard,
        ILogger<WebhookAction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _session = session;
        _addressGuard = addressGuard;
        _logger = logger;
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

        // Early, logged refusal for a URL that is obviously out of bounds. It is not the guard: the
        // address that gets dialled is checked again when the socket is opened, inside the client's
        // connect callback, which is the only check a changing DNS answer cannot get around.
        if (!await IsUrlSafeAsync(url, ct))
        {
            _logger.LogWarning("Webhook URL {Url} is not allowed (must be http/https to a non-internal host). Skipping webhook action.", Redact(url));
            // Permanent. A URL that is not http or https, or that resolves somewhere the guard
            // refuses, is the same on the fifth attempt as the first: it is a typo in the workflow,
            // not a provider having a bad afternoon.
            return WorkflowActionResult.PermanentFailure(
                $"Webhook URL {Redact(url)} is not allowed: it must be http or https to a non-internal host.");
        }

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

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
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

            using var response = await client.SendAsync(request, ct);

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
            // The exception is not attached, and the stack is passed separately. Logging the
            // exception object writes its Message, and a transport failure raised against a URL that
            // carries a token in its query can carry that URL, and with it the token, into whatever
            // aggregates the logs. The stack trace is the half worth keeping and contains no URL.
            _logger.LogError(
                "Failed to send webhook to {Url} ({Exception}). {Stack}",
                Redact(url), ex.GetType().Name, ex.StackTrace);

            // The exception type, not its message. A transport failure names the host it could not
            // reach, and for a URL carrying a token in its query that message is the token.
            return WorkflowActionResult.Failure(
                $"Webhook to {Redact(url)} could not be delivered ({ex.GetType().Name}).");
        }
        catch (Exception ex)
        {
            // Same reason as above. This catch is broader, so the exception is likelier to be one
            // whose message names the request it was made against.
            _logger.LogError(
                "Unexpected error while sending webhook to {Url} ({Exception}). {Stack}",
                Redact(url), ex.GetType().Name, ex.StackTrace);
            return WorkflowActionResult.Failure(
                $"Webhook to {Redact(url)} failed unexpectedly ({ex.GetType().Name}).");
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
