using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Http;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>What one cached access token was granted for.</summary>
/// <remarks>
/// The version is the connector's <c>UpdatedAt</c>, which every save through the API moves. A token
/// granted before an edit was granted for the old token URL, client id or secret, so it is not the
/// edited connector's token.
/// </remarks>
internal readonly record struct ConnectorTokenKey(string Tenant, Guid ConnectorId, long Version);

/// <summary>
/// Access tokens from the client credentials grant, held in this process and nowhere else.
/// </summary>
/// <remarks>
/// Per instance on purpose. A shared cache would put a live token in a store that outlives the
/// process, and the cost of not sharing is one grant per instance per token lifetime.
/// </remarks>
internal sealed class ConnectorTokenCache(TimeProvider clock)
{
    internal const int MaxEntries = 256;

    /// <summary>
    /// What one tenant may hold. Without it the cache is one pool, and a tenant with many
    /// long-lived tokens pushes every other tenant's out.
    /// </summary>
    internal const int MaxEntriesPerTenant = 32;

    /// <summary>
    /// How old a cached token has to be before a 401 to it is worth a new grant. A provider that
    /// answers 401 for some other reason would otherwise cost a grant and a second send on every
    /// call.
    /// </summary>
    internal static readonly TimeSpan RegrantAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);
    private const long MaxLifetimeSeconds = 3600;
    private const long RefreshMarginSeconds = 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<ConnectorTokenKey, Entry> _entries = new();

    private readonly record struct Entry(string Token, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt);

    internal int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>How long a token is reused, given what the token endpoint said about its life.</summary>
    /// <remarks>
    /// An endpoint that says nothing usable gets a minute. One that says more than an hour gets an
    /// hour, so a wrong number cannot pin a token here for good.
    /// </remarks>
    internal static TimeSpan LifetimeFor(long? expiresIn)
    {
        if (expiresIn is not > 0) return DefaultLifetime;

        var seconds = Math.Min(expiresIn.Value, MaxLifetimeSeconds);
        return TimeSpan.FromSeconds(seconds - Math.Min(RefreshMarginSeconds, seconds / 2));
    }

    public string? Get(ConnectorTokenKey key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.ExpiresAt > clock.GetUtcNow()) return entry.Token;

            _entries.Remove(key);
            return null;
        }
    }

    public void Set(ConnectorTokenKey key, string token, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            foreach (var stale in _entries
                         .Where(e => e.Value.ExpiresAt <= now
                             || (e.Key.Tenant == key.Tenant && e.Key.ConnectorId == key.ConnectorId))
                         .Select(e => e.Key)
                         .ToList())
            {
                _entries.Remove(stale);
            }

            while (_entries.Count(e => e.Key.Tenant == key.Tenant) >= MaxEntriesPerTenant)
            {
                _entries.Remove(_entries.Where(e => e.Key.Tenant == key.Tenant).MinBy(e => e.Value.ExpiresAt).Key);
            }

            while (_entries.Count >= MaxEntries)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.ExpiresAt).Key);
            }

            _entries[key] = new Entry(token, now, now + lifetime);
        }
    }

    /// <summary>
    /// Whether a 401 to <paramref name="token"/> is worth one new grant, dropping the token when it is.
    /// </summary>
    /// <remarks>
    /// False only when that token is still the cached one and was granted less than
    /// <see cref="RegrantAfter"/> ago. A token another call has already replaced is left alone, and
    /// the repeat picks the newer one up.
    /// </remarks>
    public bool TryRetire(ConnectorTokenKey key, string? token)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Token != token) return true;
            if (clock.GetUtcNow() - entry.GrantedAt < RegrantAfter) return false;

            _entries.Remove(key);
            return true;
        }
    }
}

/// <param name="Tenant">The tenant asking, which keys the outbound breaker for the token host.</param>
internal sealed record ClientCredentials(
    Uri TokenUrl, string ClientId, string ClientSecret, string? Scope, string? Audience, bool InBody, string? Tenant = null);

/// <summary>A token, or the sentence that says why there is none. Never the response body.</summary>
internal sealed record TokenGrant(string? AccessToken, long? ExpiresIn, string? Error);

/// <summary>The OAuth 2.0 client credentials grant, RFC 6749 section 4.4.</summary>
internal static class ClientCredentialsGrant
{
    internal const int MaxResponseBytes = 64 * 1024;

    // An error body is written by the other side and can echo what was sent, so only a code from
    // this list is ever repeated. RFC 6749 section 5.2, plus the ones providers add.
    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client",
        "unsupported_grant_type", "invalid_scope", "invalid_target", "access_denied",
        "server_error", "temporarily_unavailable",
    };

    /// <summary>
    /// Asks the token endpoint for a token. Throws what the HTTP client throws when nothing answers.
    /// </summary>
    public static async Task<TokenGrant> RequestAsync(
        HttpClient client, ClientCredentials credentials, CancellationToken ct)
    {
        var host = credentials.TokenUrl.IdnHost;

        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (!string.IsNullOrWhiteSpace(credentials.Scope)) form.Add(new("scope", credentials.Scope.Trim()));
        if (!string.IsNullOrWhiteSpace(credentials.Audience)) form.Add(new("audience", credentials.Audience.Trim()));

        using var request = new HttpRequestMessage(HttpMethod.Post, credentials.TokenUrl);
        if (credentials.Tenant is not null) OutboundResilienceHandler.SetTenant(request, credentials.Tenant);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (credentials.InBody)
        {
            form.Add(new("client_id", credentials.ClientId));
            form.Add(new("client_secret", credentials.ClientSecret));
        }
        else
        {
            // Section 2.3.1: each half is form encoded before the pair is base64 encoded.
            var pair = $"{WebUtility.UrlEncode(credentials.ClientId)}:{WebUtility.UrlEncode(credentials.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }

        request.Content = new FormUrlEncodedContent(form);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;

        var body = await ConnectorSender.ReadCappedAsync(response, MaxResponseBytes, ct);
        if (body is null)
        {
            return Failed($"The token endpoint at {host} answered {status} with more than {MaxResponseBytes} bytes.");
        }

        using var json = Parse(body);
        var root = json?.RootElement;

        if (!response.IsSuccessStatusCode || root is null || !root.Value.TryGetProperty("access_token", out var token))
        {
            if (root is not null && root.Value.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                return Failed(code is not null && KnownErrors.Contains(code)
                    ? $"The token endpoint at {host} answered {status} with the error '{code}'."
                    : $"The token endpoint at {host} answered {status} with an error code that is not a standard one.");
            }

            if (!response.IsSuccessStatusCode) return Failed($"The token endpoint at {host} answered {status}.");

            return Failed(root is null
                ? $"The token endpoint at {host} answered {status} with a body that is not a JSON object."
                : $"The token endpoint at {host} answered {status} without an access_token.");
        }

        var value = token.ValueKind == JsonValueKind.String ? token.GetString() : null;

        // Printable ASCII with no space is every token RFC 6750 allows, and anything else would
        // either be refused as a header value or split one.
        if (string.IsNullOrEmpty(value) || value.Any(c => c is < '!' or > '~'))
        {
            return Failed($"The token endpoint at {host} answered {status} with an access_token that cannot be sent as a Bearer header.");
        }

        if (root.Value.TryGetProperty("token_type", out var type)
            && (type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
        {
            return Failed($"The token endpoint at {host} granted a token type other than Bearer.");
        }

        return new TokenGrant(value, ExpiresIn(root.Value), null);
    }

    private static TokenGrant Failed(string error) => new(null, null, error);

    private static JsonDocument? Parse(string body)
    {
        try
        {
            var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;

            json.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A number by the RFC, a string of digits from some providers. A fraction is cut off, and the
    // clamp only keeps the cast in range: the lifetime has its own cap.
    private static long? ExpiresIn(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => (long)Math.Clamp(number, -1d, 1e12),
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) => seconds,
            _ => null,
        };
    }
}
