using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
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

        // SSRF guard: only allow http/https to a public host. Blocks loopback, link-local
        // (incl. cloud metadata 169.254.169.254), and private/internal ranges so a workflow
        // cannot be used to reach internal services or exfiltrate content data to them.
        if (!await IsUrlSafeAsync(url, ct))
        {
            _logger.LogWarning("Webhook URL {Url} is not allowed (must be http/https to a non-internal host). Skipping webhook action.", url);
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

    /// <summary>
    /// Validates that the URL is an absolute http/https URL whose host does not resolve to any
    /// loopback, link-local, or private/internal address. All resolved addresses must be public.
    /// Note: this does not fully close DNS-rebinding (the HttpClient re-resolves at send time);
    /// pin the connection to a validated IP if that threat is in scope.
    /// </summary>
    private async Task<bool> IsUrlSafeAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve webhook host {Host}", uri.Host);
                return false;
            }
        }

        return addresses.Length > 0 && addresses.All(a => !IsBlockedAddress(a));
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8
            if (b[0] == 10) return true;                                 // 10.0.0.0/8
            if (b[0] == 127) return true;                                // 127.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local (cloud metadata)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 CGNAT
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 multicast + reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 unique-local
        }

        return false;
    }
}
