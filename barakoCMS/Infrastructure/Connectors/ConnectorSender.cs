using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>The outcome of one call through a connector. Never the response body.</summary>
/// <remarks>
/// The body is deliberately absent. A 401 from an OAuth provider frequently echoes the credential
/// that was sent, so a "helpful" error that quotes the response is how a token reaches a log
/// aggregator, an error tracker and a support ticket in one step.
/// </remarks>
public sealed record ConnectorCallResult(bool Succeeded, int? StatusCode, long ElapsedMs, string? Error)
{
    public string Describe() => StatusCode is { } code
        ? $"HTTP {code} in {ElapsedMs} ms"
        : $"{Error ?? "failed"} after {ElapsedMs} ms";
}

public interface IConnectorSender
{
    /// <summary>Performs one harmless authenticated request and reports how it went.</summary>
    Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct);

    /// <summary>Sends a composed request through a connector and reports the outcome.</summary>
    /// <remarks>
    /// The request arrives already composed. Credentials are attached here, to the finished message,
    /// which is what keeps a template from being able to resolve one: nothing that builds a body
    /// ever holds a secret.
    /// </remarks>
    Task<ConnectorCallResult> SendAsync(
        Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct);
}

internal sealed class ConnectorSender : IConnectorSender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IQuerySession _session;
    private readonly IConnectorSecretProtector _protector;
    private readonly ILogger<ConnectorSender> _logger;

    public ConnectorSender(
        IHttpClientFactory httpClientFactory,
        IQuerySession session,
        IConnectorSecretProtector protector,
        ILogger<ConnectorSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _session = session;
        _protector = protector;
        _logger = logger;
    }

    public async Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct)
    {
        if (!Uri.TryCreate(connector.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new ConnectorCallResult(false, null, 0, "The base URL is not an absolute http or https URL.");
        }

        if (!Uri.TryCreate(baseUri, connector.ProbePath, out var target))
        {
            return new ConnectorCallResult(false, null, 0, "The probe path does not combine with the base URL.");
        }

        // The address check is not here. It lives in the connect callback of the ExternalApi client,
        // which resolves the name once and opens the socket to an address that answer survived, with
        // redirects off. Checking here as well would only re-resolve, and a name whose answer changes
        // between the check and the connection is the whole of #258. Send time means socket time.
        var client = _httpClientFactory.CreateClient("ExternalApi");

        using var request = new HttpRequestMessage(HttpMethod.Get, target);

        // Credentials are attached to the finished request, after everything else about it is
        // decided. Nothing that composes a request ever holds a secret, so no template, condition or
        // payload can resolve one: without that rule, a template plus a connector an attacker can
        // point somewhere is an exfiltration primitive.
        var attached = await TryAttachAuthAsync(request, connector, ct);
        if (attached is not null)
        {
            return new ConnectorCallResult(false, null, 0, attached);
        }

        var timer = Stopwatch.StartNew();

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            timer.Stop();

            return new ConnectorCallResult(
                response.IsSuccessStatusCode, (int)response.StatusCode, timer.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();

            // The exception type and message, not the exception. A connector's own base URL can
            // carry a credential in the userinfo part, and ToString() on an HttpRequestException
            // chain has printed the request URI before.
            _logger.LogWarning("Connector {Slug} probe failed: {Reason}", connector.Slug, ex.GetType().Name);
            return new ConnectorCallResult(false, null, timer.ElapsedMilliseconds, Describe(ex));
        }
    }

    public async Task<ConnectorCallResult> SendAsync(
        Connector connector, ComposedRequest composed, SuccessRule rule, string? successJsonPath, CancellationToken ct)
    {
        if (!composed.Ok)
        {
            return new ConnectorCallResult(false, null, 0, composed.Refusal);
        }

        if (!Uri.TryCreate(composed.Url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return new ConnectorCallResult(false, null, 0, "The composed URL is not an absolute http or https URL.");
        }

        // The address check is not here. It is in the connect callback of the ExternalApi client,
        // which resolves once and opens the socket to an address that answer survived, with
        // redirects off. A name that resolves publicly when the request is composed and privately
        // when it is sent is the case a check here could not see.
        var client = _httpClientFactory.CreateClient("ExternalApi");

        using var request = new HttpRequestMessage(new HttpMethod(composed.Method), target);

        foreach (var (name, value) in composed.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (composed.Body is not null)
        {
            request.Content = new StringContent(
                composed.Body, Encoding.UTF8, composed.BodyContentType ?? "application/json");
        }

        var attached = await TryAttachAuthAsync(request, connector, ct);
        if (attached is not null)
        {
            return new ConnectorCallResult(false, null, 0, attached);
        }

        var timer = Stopwatch.StartNew();

        try
        {
            using var response = await client.SendAsync(request, ct);
            timer.Stop();

            // The body is read only when a rule needs it, and it is never returned or logged. A 401
            // from an OAuth provider frequently contains the credential that was sent.
            string? body = null;
            if (rule == SuccessRule.TwoHundredAndJsonPathAbsent && !string.IsNullOrWhiteSpace(successJsonPath))
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }

            var status = (int)response.StatusCode;
            var ok = SuccessEvaluator.Succeeded(rule, status, body, successJsonPath);

            return new ConnectorCallResult(
                ok, status, timer.ElapsedMilliseconds,
                ok ? null : $"The provider answered {status} and the success rule was not met.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            _logger.LogWarning("Connector {Slug} send failed: {Reason}", connector.Slug, ex.GetType().Name);
            return new ConnectorCallResult(false, null, timer.ElapsedMilliseconds, Describe(ex));
        }
    }

    /// <summary>Returns null when the credentials went on, or the reason they did not.</summary>
    private async Task<string?> TryAttachAuthAsync(HttpRequestMessage request, Connector connector, CancellationToken ct)
    {
        if (connector.Auth == ConnectorAuth.None) return null;

        if (!_protector.IsConfigured)
        {
            return "Connectors:Key is not configured, so the stored credential cannot be decrypted.";
        }

        switch (connector.Auth)
        {
            case ConnectorAuth.BearerToken:
            {
                var token = await SecretAsync(connector.Id, ConnectorSecretKeys.Token, ct);
                if (token is null) return Missing(ConnectorSecretKeys.Token);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return null;
            }

            case ConnectorAuth.Basic:
            {
                var username = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.Username) ?? string.Empty;
                var password = await SecretAsync(connector.Id, ConnectorSecretKeys.Password, ct);
                if (password is null) return Missing(ConnectorSecretKeys.Password);

                var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", pair);
                return null;
            }

            case ConnectorAuth.ApiKeyHeader:
            {
                var header = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.HeaderName);
                if (string.IsNullOrWhiteSpace(header))
                {
                    return $"Auth is ApiKeyHeader, so Settings needs '{ConnectorSettingKeys.HeaderName}'.";
                }

                var key = await SecretAsync(connector.Id, ConnectorSecretKeys.ApiKey, ct);
                if (key is null) return Missing(ConnectorSecretKeys.ApiKey);

                request.Headers.TryAddWithoutValidation(header, key);
                return null;
            }

            case ConnectorAuth.OAuth2ClientCredentials:
                // Refused rather than accepted and inert. An operator who picks this has decided
                // they need a token exchange, and starting the call without one would report a 401
                // as a credential problem when it is a missing feature.
                return "OAuth2ClientCredentials is not implemented yet. Use BearerToken with a token you obtained, or ApiKeyHeader.";

            default:
                return $"Unknown auth mode '{connector.Auth}'.";
        }
    }

    private async Task<string?> SecretAsync(Guid connectorId, string key, CancellationToken ct)
    {
        var stored = await _session.Query<ConnectorSecret>()
            .Where(s => s.ConnectorId == connectorId && s.Key == key)
            .FirstOrDefaultAsync(ct);

        return stored is null ? null : _protector.Unprotect(stored.ProtectedValue);
    }

    private static string Missing(string key) =>
        $"No '{key}' secret is stored for this connector, or it will not decrypt under the current Connectors:Key.";

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "The request could not be completed. The host may be unreachable, or its address is blocked.",
        TaskCanceledException => "The request timed out.",
        _ => "The request failed.",
    };
}

/// <summary>The secret names each auth mode looks for.</summary>
public static class ConnectorSecretKeys
{
    public const string Token = "Token";
    public const string Password = "Password";
    public const string ApiKey = "ApiKey";
    public const string ClientSecret = "ClientSecret";

    public static readonly string[] All = [Token, Password, ApiKey, ClientSecret];
}

/// <summary>Non-secret settings the auth modes read.</summary>
public static class ConnectorSettingKeys
{
    public const string Username = "Username";
    public const string HeaderName = "HeaderName";
}
