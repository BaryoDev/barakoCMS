using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending HTTP POST requests to webhooks.
/// </summary>
[WorkflowActionMetadata(
    Description = "Send HTTP POST requests to external webhooks",
    RequiredParameters = new[] { "Url" },
    ExampleJson = @"{""Type"":""Webhook"",""Parameters"":{""Url"":""https://example.com/webhook""}}"
)]
public class WebhookAction : IWorkflowAction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookAction> _logger;

    /// <summary>
    /// Creates a new WebhookAction.
    /// </summary>
    public WebhookAction(IHttpClientFactory httpClientFactory, ILogger<WebhookAction> logger)
    {
        _httpClientFactory = httpClientFactory;
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

        try
        {
            var client = _httpClientFactory.CreateClient("ExternalApi");

            // Build payload with content data
            var payload = new
            {
                contentId = content.Id,
                contentType = content.ContentType,
                status = content.Status.ToString(),
                data = content.Data,
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
}
