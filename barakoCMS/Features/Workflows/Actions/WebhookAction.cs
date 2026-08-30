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
    {
        var url = parameters.GetValueOrDefault("Url");
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("Webhook URL not provided. Skipping webhook action.");
            return;
        }

        // Early, logged refusal for a URL that is obviously out of bounds. It is not the guard: the
        // address that gets dialled is checked again when the socket is opened, inside the client's
        // connect callback, which is the only check a changing DNS answer cannot get around.
        if (!await IsUrlSafeAsync(url, ct))
        {
            _logger.LogWarning("Webhook URL {Url} is not allowed (must be http/https to a non-internal host). Skipping webhook action.", url);
            return;
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

            var response = await client.PostAsJsonAsync(url, payload, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhook successfully sent to {Url} for content {ContentId}", url, content.Id);
            }
            else
            {
                _logger.LogWarning("Webhook to {Url} returned status {StatusCode}", url, response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to send webhook to {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while sending webhook to {Url}", url);
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
}
