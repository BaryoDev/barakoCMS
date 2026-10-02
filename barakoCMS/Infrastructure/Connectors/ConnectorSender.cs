using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>
/// The outcome of one call made in order to read what came back, with the body only on success.
/// </summary>
/// <remarks>
/// <see cref="ConnectorCallResult"/> deliberately has no body, because a workflow action sends and
/// records and never needs one. A collection sync is the other case: reading the answer is the whole
/// point of the call.
///
/// The body is null on every failure, and that is what keeps the two consistent. A 401 from an OAuth
/// provider frequently echoes the credential that was sent, so the failure path here carries exactly
/// what the send path carries, a status code and a sentence. Only a successful response reaches a
/// caller, where the operator's own field mapping decides what is kept.
/// </remarks>
public sealed record ConnectorFetchResult(
    bool Succeeded, int? StatusCode, long ElapsedMs, string? Error, string? Body)
{
    /// <summary>
    /// The provider answered with a <c>Link</c> header naming a next page, so the body is not the
    /// whole of what it holds.
    /// </summary>
    public bool HasNextPage { get; init; }
}

/// <summary>Sends a composed request and hands back what the provider answered.</summary>
/// <remarks>
/// A separate interface rather than another member on <see cref="IConnectorSender"/>. That one is
/// public and a host may already implement it, so adding a member would break it. The same class
/// implements both, so there is still one outbound path, one address guard and one place where
/// credentials are attached.
/// </remarks>
public interface IConnectorFetcher
{
    /// <summary>
    /// Sends <paramref name="request"/> and reads up to <paramref name="maxBytes"/> of the response.
    /// </summary>
    /// <param name="connector">
    /// The connector supplying credentials, or null for a request that carries none, such as a
    /// public feed.
    /// </param>
    /// <param name="request">The request as already composed, credentials not yet attached.</param>
    /// <param name="maxBytes">The most of the response that will be read. A longer one fails.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<ConnectorFetchResult> FetchAsync(
        Connector? connector, ComposedRequest request, int maxBytes, CancellationToken ct);
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

internal sealed class ConnectorSender(
    IHttpClientFactory httpClientFactory,
    IQuerySession session,
    IConnectorSecretProtector protector,
    ConnectorTokenCache tokens,
    ILogger<ConnectorSender> logger) : IConnectorSender, IConnectorFetcher
{
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

        // Checked here as well as on save, because a row saved before that check can hold an
        // absolute probe path, and the credentials below would go wherever it points.
        if (ConnectorOrigin.Of(target) != ConnectorOrigin.Of(baseUri))
        {
            return new ConnectorCallResult(false, null, 0,
                "The probe path addresses a different scheme, host or port from the base URL.");
        }

        // The address check is not here. It lives in the connect callback of the ExternalApi client,
        // which resolves the name once and opens the socket to an address that answer survived, with
        // redirects off. Checking here as well would only re-resolve, and a name whose answer changes
        // between the check and the connection is the whole of #258. Send time means socket time.
        var client = httpClientFactory.CreateClient("ExternalApi");

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
            using var first = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            using var again = await RetryWithNewTokenAsync(client, connector, request, first,
                () => new HttpRequestMessage(HttpMethod.Get, target), HttpCompletionOption.ResponseHeadersRead, ct);
            var response = again ?? first;
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
            logger.LogWarning("Connector {Slug} probe failed: {Reason}", connector.Slug, ex.GetType().Name);
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
        var client = httpClientFactory.CreateClient("ExternalApi");

        using var request = BuildRequest(composed, target);

        var attached = await TryAttachAuthAsync(request, connector, ct);
        if (attached is not null)
        {
            return new ConnectorCallResult(false, null, 0, attached);
        }

        var timer = Stopwatch.StartNew();

        try
        {
            using var first = await client.SendAsync(request, ct);
            using var again = await RetryWithNewTokenAsync(client, connector, request, first,
                () => BuildRequest(composed, target), HttpCompletionOption.ResponseContentRead, ct);
            var response = again ?? first;
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
            logger.LogWarning("Connector {Slug} send failed: {Reason}", connector.Slug, ex.GetType().Name);
            return new ConnectorCallResult(false, null, timer.ElapsedMilliseconds, Describe(ex));
        }
    }

    public async Task<ConnectorFetchResult> FetchAsync(
        Connector? connector, ComposedRequest composed, int maxBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        if (!composed.Ok)
        {
            return new ConnectorFetchResult(false, null, 0, composed.Refusal, null);
        }

        if (!Uri.TryCreate(composed.Url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return new ConnectorFetchResult(false, null, 0, "The composed URL is not an absolute http or https URL.", null);
        }

        // The same client the send path uses, so the address guard, the redirect policy and the
        // proxy decision are the ones already reviewed rather than a second set.
        var client = httpClientFactory.CreateClient("ExternalApi");

        using var request = BuildRequest(composed, target);

        if (connector is not null)
        {
            var attached = await TryAttachAuthAsync(request, connector, ct);
            if (attached is not null)
            {
                // Fixed text, not the attachment path's own message, which is what SendAsync
                // returns. A fetch result is read back by a collection sync, which keeps its last
                // error for the life of the sync and shows it in an admin screen, and those
                // messages name the secret keys a connector holds. Testing the connector says which
                // one is missing, to a caller asking about that connector rather than about a
                // collection, and answers it without storing anything.
                return new ConnectorFetchResult(
                    false, null, 0,
                    $"The credentials for connector '{connector.Slug}' could not be attached, so nothing "
                  + "was sent. Test the connector to see why.",
                    null);
            }
        }

        var timer = Stopwatch.StartNew();

        try
        {
            using var first = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            using var again = await RetryWithNewTokenAsync(client, connector, request, first,
                () => BuildRequest(composed, target), HttpCompletionOption.ResponseHeadersRead, ct);
            var response = again ?? first;

            if (!response.IsSuccessStatusCode)
            {
                timer.Stop();

                // The body is not read at all on a failure, so there is no path by which one can be
                // logged, stored or returned. See the remarks on ConnectorFetchResult.
                return new ConnectorFetchResult(
                    false, (int)response.StatusCode, timer.ElapsedMilliseconds,
                    $"The provider answered {(int)response.StatusCode}.", null);
            }

            var body = await ReadCappedAsync(response, maxBytes, ct);
            timer.Stop();

            if (body is null)
            {
                // Refused rather than truncated. A JSON document cut in half does not parse, and a
                // feed cut in half parses into however many entries happened to fit, which is a
                // wrong answer that looks like a right one.
                return new ConnectorFetchResult(
                    false, (int)response.StatusCode, timer.ElapsedMilliseconds,
                    $"The response is larger than the {maxBytes} byte limit.", null);
            }

            return new ConnectorFetchResult(true, (int)response.StatusCode, timer.ElapsedMilliseconds, null, body)
            {
                HasNextPage = response.Headers.TryGetValues("Link", out var links) && links.Any(NamesNextPage),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            timer.Stop();
            logger.LogWarning(
                "Connector {Slug} fetch failed: {Reason}", connector?.Slug ?? "(none)", ex.GetType().Name);
            return new ConnectorFetchResult(false, null, timer.ElapsedMilliseconds, Describe(ex), null);
        }
    }

    /// <summary>The body, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    /// <remarks>
    /// Read off the stream rather than through <c>ReadAsStringAsync</c>, because the cap has to hold
    /// against a provider that sends no Content-Length or an untrue one. Nothing about a response
    /// from a third party is a reason to allocate what it says to allocate.
    /// </remarks>
    internal static async Task<string?> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[8192];
        using var collected = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            if (collected.Length + read > maxBytes) return null;

            collected.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }

    /// <summary>Returns null when the credentials went on, or the reason they did not.</summary>
    private async Task<string?> TryAttachAuthAsync(HttpRequestMessage request, Connector connector, CancellationToken ct)
    {
        if (connector.Auth == ConnectorAuth.None) return null;

        if (!protector.IsConfigured)
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
                return await TryAttachGrantedTokenAsync(request, connector, ct);

            default:
                return $"Unknown auth mode '{connector.Auth}'.";
        }
    }

    private async Task<string?> SecretAsync(Guid connectorId, string key, CancellationToken ct)
    {
        var stored = await session.Query<ConnectorSecret>()
            .Where(s => s.ConnectorId == connectorId && s.Key == key)
            .FirstOrDefaultAsync(ct);

        return stored is null ? null : protector.Unprotect(stored.ProtectedValue);
    }

    private static string Missing(string key) =>
        $"No '{key}' secret is stored for this connector, or it will not decrypt under the current Connectors:Key.";

    private static readonly HttpRequestOptionsKey<ConnectorTokenKey> SentCachedToken = new("barakocms.connector.cached-token");

    /// <summary>
    /// Attaches a token from the client credentials grant: the cached one, or a new one from the
    /// token endpoint. Returns null when it went on, or the reason it did not.
    /// </summary>
    /// <remarks>
    /// The token request goes through the same ExternalApi client as the call itself, so the token
    /// URL meets the same address guard, redirect policy and timeouts. Neither the secret nor the
    /// token is logged, stored or put in a message.
    /// </remarks>
    private async Task<string?> TryAttachGrantedTokenAsync(
        HttpRequestMessage request, Connector connector, CancellationToken ct)
    {
        var tokenUrl = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.TokenUrl);
        if (!Uri.TryCreate(tokenUrl?.Trim(), UriKind.Absolute, out var tokenUri)
            || (tokenUri.Scheme != Uri.UriSchemeHttp && tokenUri.Scheme != Uri.UriSchemeHttps))
        {
            return $"Auth is OAuth2ClientCredentials, so Settings needs '{ConnectorSettingKeys.TokenUrl}' as an absolute http or https URL.";
        }

        var clientId = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.ClientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return $"Auth is OAuth2ClientCredentials, so Settings needs '{ConnectorSettingKeys.ClientId}'.";
        }

        var clientAuth = connector.Settings.GetValueOrDefault(ConnectorSettingKeys.ClientAuth);
        var inBody = string.Equals(clientAuth, ConnectorSettingKeys.ClientAuthBody, StringComparison.OrdinalIgnoreCase);
        if (!inBody
            && !string.IsNullOrWhiteSpace(clientAuth)
            && !string.Equals(clientAuth, ConnectorSettingKeys.ClientAuthBasic, StringComparison.OrdinalIgnoreCase))
        {
            return $"Settings '{ConnectorSettingKeys.ClientAuth}' must be {ConnectorSettingKeys.ClientAuthBasic} or {ConnectorSettingKeys.ClientAuthBody}.";
        }

        var key = new ConnectorTokenKey(session.TenantId, connector.Id, connector.UpdatedAt.Ticks);

        if (tokens.Get(key) is { } cached)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cached);
            request.Options.Set(SentCachedToken, key);
            return null;
        }

        var secret = await SecretAsync(connector.Id, ConnectorSecretKeys.ClientSecret, ct);
        if (secret is null) return Missing(ConnectorSecretKeys.ClientSecret);

        TokenGrant grant;

        try
        {
            grant = await ClientCredentialsGrant.RequestAsync(
                httpClientFactory.CreateClient("ExternalApi"),
                new ClientCredentials(
                    tokenUri, clientId.Trim(), secret,
                    connector.Settings.GetValueOrDefault(ConnectorSettingKeys.Scope),
                    connector.Settings.GetValueOrDefault(ConnectorSettingKeys.Audience),
                    inBody),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Connector {Slug} token request failed: {Reason}", connector.Slug, ex.GetType().Name);

            return ex switch
            {
                HttpRequestException => $"The token endpoint at {tokenUri.IdnHost} could not be reached. The host may be unreachable, or its address is blocked.",
                TaskCanceledException => $"The token endpoint at {tokenUri.IdnHost} timed out.",
                _ => $"The token request to {tokenUri.IdnHost} failed.",
            };
        }

        if (grant.AccessToken is null)
        {
            logger.LogWarning("Connector {Slug} was granted no token: {Reason}", connector.Slug, grant.Error);
            return grant.Error;
        }

        tokens.Set(key, grant.AccessToken, ConnectorTokenCache.LifetimeFor(grant.ExpiresIn));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        return null;
    }

    /// <summary>
    /// Sends once more with a new token when the provider answered 401 to a cached one. Null when
    /// that is not what happened, and the first answer stands.
    /// </summary>
    /// <remarks>
    /// A provider can revoke a token before the lifetime it gave, and the cache cannot know. One
    /// new token and one more send, never a loop: a 401 to a token granted for this call is the
    /// provider's answer.
    /// </remarks>
    private async Task<HttpResponseMessage?> RetryWithNewTokenAsync(
        HttpClient client, Connector? connector, HttpRequestMessage refused, HttpResponseMessage answer,
        Func<HttpRequestMessage> rebuild, HttpCompletionOption completion, CancellationToken ct)
    {
        if (connector is null || answer.StatusCode != HttpStatusCode.Unauthorized) return null;
        if (!refused.Options.TryGetValue(SentCachedToken, out var key)) return null;

        tokens.Evict(key, refused.Headers.Authorization?.Parameter);

        using var again = rebuild();
        if (await TryAttachAuthAsync(again, connector, ct) is not null) return null;

        return await client.SendAsync(again, completion, ct);
    }

    private static HttpRequestMessage BuildRequest(ComposedRequest composed, Uri target)
    {
        var request = new HttpRequestMessage(new HttpMethod(composed.Method), target);

        foreach (var (name, value) in composed.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (composed.Body is not null)
        {
            request.Content = new StringContent(
                composed.Body, Encoding.UTF8, composed.BodyContentType ?? "application/json");
        }

        return request;
    }

    private static readonly Regex RelParameter = new(
        @"rel\s*=\s*(?:""(?<v>[^""]*)""|(?<v>[^\s;,]+))",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    /// <summary>Whether a <c>Link</c> header value names a next page.</summary>
    /// <remarks>
    /// A rel may carry several space separated types (RFC 8288), so <c>rel="next prefetch"</c> names
    /// one as surely as <c>rel=next</c> does.
    /// </remarks>
    internal static bool NamesNextPage(string link) =>
        RelParameter.Matches(link).Any(m => m.Groups["v"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("next", StringComparer.OrdinalIgnoreCase));

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

    /// <summary>Where OAuth2ClientCredentials asks for a token.</summary>
    public const string TokenUrl = "TokenUrl";

    public const string ClientId = "ClientId";

    /// <summary>Optional. Sent as <c>scope</c> on the token request.</summary>
    public const string Scope = "Scope";

    /// <summary>Optional. Sent as <c>audience</c> on the token request, for providers that ask for one.</summary>
    public const string Audience = "Audience";

    /// <summary>
    /// How the client id and secret reach the token endpoint: <see cref="ClientAuthBasic"/> when
    /// absent, or <see cref="ClientAuthBody"/>.
    /// </summary>
    public const string ClientAuth = "ClientAuth";

    public const string ClientAuthBasic = "Basic";
    public const string ClientAuthBody = "Body";

    /// <summary>
    /// The header name a <see cref="barakoCMS.Models.RequestDefinition"/> uses to carry the
    /// workflow run's idempotency key. See <c>RequestComposer.IdempotencyHeaderName</c>.
    /// </summary>
    public const string IdempotencyHeader = "IdempotencyHeader";
}
