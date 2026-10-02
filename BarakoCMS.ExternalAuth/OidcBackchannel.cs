using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace BarakoCMS.ExternalAuth;

/// <summary>What a provider's discovery document says, after it has been checked.</summary>
/// <param name="SecretInBody">
/// True when the token endpoint takes the client secret as a form field (<c>client_secret_post</c>),
/// false when it takes it as HTTP Basic, which is what a document that lists nothing means.
/// </param>
internal sealed record OidcEndpoints(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string JwksUri,
    bool SecretInBody);

/// <summary>
/// Every outbound call the OpenID Connect flow makes: the discovery document, the signing keys and
/// the code exchange.
/// </summary>
/// <remarks>
/// <para>
/// All three go through the core's <c>ExternalApi</c> client. Its handler resolves a name once,
/// refuses loopback, private, link-local and metadata addresses, dials the address it checked, and
/// follows no redirect. The authority is configuration and the other URLs come from a document the
/// authority served, so without that a provider entry, or a provider's own document, could point
/// the server at an internal address.
/// </para>
/// <para>
/// Discovery and keys are cached for <see cref="Lifetime"/>. A token signed with a key id the cache
/// does not hold refetches the keys, at most once per <see cref="KeyRefreshInterval"/>, which is how
/// a rotation is picked up without letting a stream of unknown key ids become a stream of fetches. A
/// fetch that fails is not retried for <see cref="FailureBackoff"/>. One fetch runs at a time.
/// </para>
/// </remarks>
internal sealed class OidcBackchannel(IHttpClientFactory httpFactory, ILogger<OidcBackchannel> logger)
{
    public const string HttpClientName = "ExternalApi";

    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan KeyRefreshInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    internal const int MaxMetadataBytes = 256 * 1024;
    internal const int MaxTokenResponseBytes = 64 * 1024;
    internal const int MaxKeys = 50;

    private sealed record Cached<T>(T Value, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Cached<OidcEndpoints>> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Cached<IReadOnlyList<JsonWebKey>>> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long one outbound call may take. Settable so a test need not wait for it.</summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Replaced by tests that need the cache to age without waiting.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The provider's endpoints, or null when its discovery document cannot be used.</summary>
    public async Task<OidcEndpoints?> EndpointsAsync(OidcProvider provider, CancellationToken ct)
    {
        var key = CacheKey(provider);
        if (_endpoints.TryGetValue(key, out var hit) && Now() - hit.At < Lifetime)
        {
            return hit.Value;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_endpoints.TryGetValue(key, out hit) && Now() - hit.At < Lifetime)
            {
                return hit.Value;
            }

            if (BackingOff("discovery|" + key))
            {
                return null;
            }

            var fetched = await FetchEndpointsAsync(provider, ct);
            if (fetched is null)
            {
                _failedUntil["discovery|" + key] = Now() + FailureBackoff;
                return null;
            }

            Bound();
            _endpoints[key] = new Cached<OidcEndpoints>(fetched, Now());
            return fetched;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The provider's signing keys. Empty when they cannot be fetched and none are cached, which
    /// fails every signature check.
    /// </summary>
    /// <param name="keyId">The <c>kid</c> of the token about to be checked, if it has one.</param>
    public async Task<IReadOnlyList<JsonWebKey>> SigningKeysAsync(
        OidcProvider provider, OidcEndpoints endpoints, string? keyId, CancellationToken ct)
    {
        var key = CacheKey(provider);
        if (_keys.TryGetValue(key, out var hit) && !NeedsRefresh(hit, keyId))
        {
            return hit.Value;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_keys.TryGetValue(key, out hit) && !NeedsRefresh(hit, keyId))
            {
                return hit.Value;
            }

            if (BackingOff("keys|" + key))
            {
                return hit?.Value ?? Array.Empty<JsonWebKey>();
            }

            var fetched = await FetchKeysAsync(provider, endpoints, ct);
            if (fetched is null)
            {
                _failedUntil["keys|" + key] = Now() + FailureBackoff;
                return hit?.Value ?? Array.Empty<JsonWebKey>();
            }

            Bound();
            _keys[key] = new Cached<IReadOnlyList<JsonWebKey>>(fetched, Now());
            return fetched;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Exchanges the authorization code for an id token, sending the PKCE verifier this browser was
    /// given at the start. Null when the provider refuses or answers with no id token.
    /// </summary>
    public async Task<string?> ExchangeCodeAsync(
        OidcProvider provider,
        OidcEndpoints endpoints,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };

        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, endpoints.TokenEndpoint);
        if (endpoints.SecretInBody)
        {
            form["client_id"] = provider.ClientId;
            form["client_secret"] = provider.ClientSecret;
        }
        else
        {
            var basic = $"{Uri.EscapeDataString(provider.ClientId)}:{Uri.EscapeDataString(provider.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        }

        request.Content = new FormUrlEncodedContent(form);

        // The body is never logged: on success it holds tokens, and on failure a provider may echo
        // the request back.
        using var document = await SendForJsonAsync(request, MaxTokenResponseBytes, ct);
        if (document is null)
        {
            logger.LogWarning("OIDC provider {Provider}: the code exchange failed", provider.Name);
            return null;
        }

        return Text(document.RootElement, "id_token");
    }

    private bool NeedsRefresh(Cached<IReadOnlyList<JsonWebKey>> hit, string? keyId)
    {
        var age = Now() - hit.At;
        if (age >= Lifetime)
        {
            return true;
        }

        var known = keyId is null || hit.Value.Any(k => string.Equals(k.Kid, keyId, StringComparison.Ordinal));
        return !known && age >= KeyRefreshInterval;
    }

    private bool BackingOff(string key) => _failedUntil.TryGetValue(key, out var until) && Now() < until;

    /// <summary>
    /// A provider whose authority or issuer is edited gets a new cache key, so entries could pile up
    /// under a host that reloads configuration. Past six entries per allowed provider, across the
    /// three caches, everything is dropped and refetched.
    /// </summary>
    private void Bound()
    {
        if (_endpoints.Count + _keys.Count + _failedUntil.Count > OidcProviders.MaxProviders * 6)
        {
            _endpoints.Clear();
            _keys.Clear();
            _failedUntil.Clear();
        }
    }

    private static string CacheKey(OidcProvider provider) => provider.Authority + "\n" + provider.Issuer;

    private async Task<OidcEndpoints?> FetchEndpointsAsync(OidcProvider provider, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, provider.DiscoveryUrl);
        using var document = await SendForJsonAsync(request, MaxMetadataBytes, ct);
        if (document is null)
        {
            logger.LogWarning("OIDC provider {Provider}: the discovery document could not be fetched", provider.Name);
            return null;
        }

        var root = document.RootElement;

        // Exact, as the specification has it. A document that names another issuer is describing
        // somebody else's keys, however it came to be served from this authority.
        if (!string.Equals(Text(root, "issuer"), provider.Issuer, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "OIDC provider {Provider}: the discovery document's issuer is not the configured one", provider.Name);
            return null;
        }

        var authorization = Text(root, "authorization_endpoint");
        var token = Text(root, "token_endpoint");
        var jwks = Text(root, "jwks_uri");
        if (!OidcProviders.IsHttpsUrl(authorization) || !OidcProviders.IsHttpsUrl(token) || !OidcProviders.IsHttpsUrl(jwks))
        {
            logger.LogWarning(
                "OIDC provider {Provider}: the discovery document names an endpoint that is not an https URL", provider.Name);
            return null;
        }

        var secretInBody = false;
        if (root.TryGetProperty("token_endpoint_auth_methods_supported", out var methods)
            && methods.ValueKind == JsonValueKind.Array)
        {
            var offered = methods.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString())
                .ToList();
            secretInBody = offered.Contains("client_secret_post");
            if (!secretInBody && !offered.Contains("client_secret_basic"))
            {
                logger.LogWarning(
                    "OIDC provider {Provider}: the token endpoint takes neither client_secret_post nor client_secret_basic",
                    provider.Name);
                return null;
            }
        }

        return new OidcEndpoints(authorization!, token!, jwks!, secretInBody);
    }

    private async Task<IReadOnlyList<JsonWebKey>?> FetchKeysAsync(
        OidcProvider provider, OidcEndpoints endpoints, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, endpoints.JwksUri);
        using var document = await SendForJsonAsync(request, MaxMetadataBytes, ct);
        if (document is null)
        {
            logger.LogWarning("OIDC provider {Provider}: the signing keys could not be fetched", provider.Name);
            return null;
        }

        try
        {
            // Asymmetric signing keys only. A symmetric key in the set would let whoever knows it
            // sign an id token, and nothing here is configured to expect one.
            return new JsonWebKeySet(document.RootElement.GetRawText()).Keys
                .Where(k => k.Kty is "RSA" or "EC")
                .Where(k => string.IsNullOrEmpty(k.Use) || k.Use == "sig")
                .Take(MaxKeys)
                .ToList();
        }
        catch (Exception)
        {
            logger.LogWarning("OIDC provider {Provider}: the signing keys document could not be read", provider.Name);
            return null;
        }
    }

    /// <summary>
    /// Sends, with a deadline, and reads at most <paramref name="maxBytes"/> of a JSON object back.
    /// Null for anything else: a refusal by the address guard, a timeout, a status that is not a
    /// success, a body that is too long or is not a JSON object.
    /// </summary>
    private async Task<JsonDocument?> SendForJsonAsync(HttpRequestMessage request, int maxBytes, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), deadline.Token);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read > maxBytes)
            {
                return null;
            }

            var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The resilience handler and the address guard each throw their own types, and none of
            // them is worth telling apart here: the provider could not be reached.
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
