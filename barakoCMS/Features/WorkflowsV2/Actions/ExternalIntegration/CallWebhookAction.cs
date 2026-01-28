using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.ExternalIntegration;

/// <summary>
/// Send HTTP request to an external webhook.
/// </summary>
public class CallWebhookAction : BaseWorkflowAction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialService _credentialService;

    public CallWebhookAction(
        IHttpClientFactory httpClientFactory,
        ICredentialService credentialService,
        ILogger<CallWebhookAction> logger) : base(logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialService = credentialService;
    }

    public override string Type => "CallWebhook";
    public override string Name => "Call Webhook";
    public override string Description => "Send an HTTP request to an external URL.";
    public override string Category => ActionCategories.ExternalIntegration;

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            var url = GetRequiredString(action.Config, "url", context);
            var method = GetString(action.Config, "method", context, "POST").ToUpperInvariant();
            var contentType = GetString(action.Config, "contentType", context, "application/json");
            var timeout = GetInt(action.Config, "timeout", 30);
            var credentialName = GetString(action.Config, "credential", context);

            // Build request body
            string? body = null;
            if (method != "GET" && method != "DELETE")
            {
                body = BuildRequestBody(action.Config, context);
            }

            // Build headers
            var headers = GetDictionary(action.Config, "headers");
            var resolvedHeaders = new Dictionary<string, string>();
            foreach (var kv in headers)
            {
                resolvedHeaders[kv.Key] = ResolveTemplateVariables(kv.Value?.ToString() ?? "", context);
            }

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Would call {Method} {Url}", method, url);
                return Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["method"] = method,
                    ["url"] = url,
                    ["body"] = body ?? ""
                });
            }

            using var client = _httpClientFactory.CreateClient("ExternalApi");
            client.Timeout = TimeSpan.FromSeconds(timeout);

            using var request = new HttpRequestMessage(new HttpMethod(method), url);

            // Add body
            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            // Add custom headers
            foreach (var header in resolvedHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Add authentication from credential
            if (!string.IsNullOrEmpty(credentialName))
            {
                await ApplyCredentialAsync(request, credentialName, context.CancellationToken);
            }

            // Add correlation ID for tracing
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", context.CorrelationId);
            request.Headers.TryAddWithoutValidation("X-Workflow-ID", context.Workflow.Id.ToString());

            var response = await client.SendAsync(request, context.CancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(context.CancellationToken);

            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                Logger.LogInformation("Webhook {Method} {Url} returned {StatusCode}",
                    method, url, statusCode);

                // Try to parse response as JSON
                object? responseData = responseBody;
                try
                {
                    if (!string.IsNullOrEmpty(responseBody) &&
                        response.Content.Headers.ContentType?.MediaType == "application/json")
                    {
                        responseData = JsonSerializer.Deserialize<Dictionary<string, object>>(responseBody);
                    }
                }
                catch
                {
                    // Keep as string
                }

                return Success(new Dictionary<string, object>
                {
                    ["statusCode"] = statusCode,
                    ["body"] = responseData ?? "",
                    ["headers"] = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))
                });
            }
            else
            {
                Logger.LogWarning("Webhook {Method} {Url} failed with {StatusCode}: {Body}",
                    method, url, statusCode, responseBody);

                return Failure($"Webhook returned {statusCode}: {responseBody}");
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP request failed");
            return Failure($"HTTP request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Logger.LogError(ex, "Webhook request timed out");
            return Failure("Webhook request timed out");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to call webhook");
            return Failure($"Failed to call webhook: {ex.Message}");
        }
    }

    private string BuildRequestBody(Dictionary<string, object> config, WorkflowContext context)
    {
        // Check for custom body template
        if (config.TryGetValue("body", out var bodyTemplate))
        {
            var bodyStr = bodyTemplate?.ToString() ?? "";
            return ResolveTemplateVariables(bodyStr, context);
        }

        // Check for body object
        if (config.TryGetValue("bodyObject", out var bodyObj))
        {
            var resolved = ResolveBodyObject(bodyObj, context);
            return JsonSerializer.Serialize(resolved);
        }

        // Default payload
        var payload = new
        {
            contentId = context.Content.Id,
            contentType = context.Content.ContentType,
            status = context.Content.Status.ToString(),
            data = context.Content.Data,
            triggerEvent = context.TriggerEvent,
            triggeredBy = context.User?.Id,
            timestamp = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(payload);
    }

    private object? ResolveBodyObject(object? obj, WorkflowContext context)
    {
        if (obj == null)
            return null;

        if (obj is string str)
            return ResolveTemplateVariables(str, context);

        if (obj is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => ResolveTemplateVariables(je.GetString() ?? "", context),
                JsonValueKind.Object => je.EnumerateObject()
                    .ToDictionary(p => p.Name, p => ResolveBodyObject(p.Value, context)),
                JsonValueKind.Array => je.EnumerateArray()
                    .Select(e => ResolveBodyObject(e, context))
                    .ToList(),
                _ => JsonSerializer.Deserialize<object>(je.GetRawText())
            };
        }

        if (obj is Dictionary<string, object> dict)
        {
            return dict.ToDictionary(kv => kv.Key, kv => ResolveBodyObject(kv.Value, context));
        }

        return obj;
    }

    private async Task ApplyCredentialAsync(HttpRequestMessage request, string credentialName, CancellationToken ct)
    {
        // Use GetAccessTokenAsync which handles all credential types including OAuth2 refresh
        var token = await _credentialService.GetAccessTokenAsync(credentialName, ct);

        if (string.IsNullOrEmpty(token))
        {
            Logger.LogWarning("Credential '{Name}' not found or has no valid token", credentialName);
            return;
        }

        var credential = await _credentialService.GetCredentialAsync(credentialName, ct);
        if (credential == null)
            return;

        switch (credential.Type)
        {
            case CredentialType.ApiKey:
                // For API keys, GetAccessTokenAsync returns the key itself
                // Try to get the header from decrypted data
                var credService = _credentialService as CredentialService;
                var data = credService?.GetDecryptedData(credential);
                var header = data?.ApiKeyHeader ?? "X-API-Key";
                request.Headers.TryAddWithoutValidation(header, token);
                break;

            case CredentialType.Basic:
                // GetAccessTokenAsync returns "Basic base64encoded" for basic auth
                if (token.StartsWith("Basic "))
                {
                    request.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                }
                break;

            case CredentialType.Bearer:
            case CredentialType.OAuth2ClientCredentials:
            case CredentialType.OAuth2AuthorizationCode:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
        }
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("url"))
            errors.Add("'url' is required.");

        if (config.TryGetValue("method", out var method))
        {
            var validMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" };
            if (!validMethods.Contains(method?.ToString()?.ToUpperInvariant()))
            {
                errors.Add($"Invalid HTTP method: {method}");
            }
        }

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "url", Type = "string", Description = "URL to call", Required = true },
                new() { Name = "method", Type = "string", Description = "HTTP method", Enum = new List<string> { "GET", "POST", "PUT", "PATCH", "DELETE" } },
                new() { Name = "headers", Type = "object", Description = "Custom HTTP headers" },
                new() { Name = "body", Type = "string", Description = "Request body template" },
                new() { Name = "bodyObject", Type = "object", Description = "Request body as object" },
                new() { Name = "contentType", Type = "string", Description = "Content-Type header" },
                new() { Name = "timeout", Type = "integer", Description = "Timeout in seconds" },
                new() { Name = "credential", Type = "string", Description = "Name of stored credential to use" }
            },
            Required = new List<string> { "url" },
            Example = @"{""url"": ""https://api.example.com/webhook"", ""method"": ""POST"", ""credential"": ""api-key-prod""}"
        };
    }
}
