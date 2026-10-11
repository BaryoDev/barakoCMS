using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// An OpenID Connect provider that lives in the test process: a discovery document, a key set and a
/// token endpoint, answered from memory. Nothing here opens a socket.
/// </summary>
/// <remarks>
/// It serves two issuers from one host, plus an Apple-shaped one on a host of its own. The plain one has a fixed issuer. The shared one publishes
/// a <c>{tenantid}</c> template the way Microsoft's multi-directory endpoints do, takes the client
/// secret in the form, and marks its key with the issuer it signs for.
///
/// Tokens are signed by hand, with nothing from the library the production code validates with, so
/// a test cannot pass because both sides share a mistake.
/// </remarks>
internal sealed class OidcStubProvider : HttpMessageHandler
{
    public const string Authority = "https://idp.test.example";
    public const string SharedAuthority = "https://idp.test.example/common/v2.0";
    public const string SharedIssuerTemplate = "https://idp.test.example/{tenantid}/v2.0";
    /// <summary>Shaped like Apple: a fixed issuer, and the client secret taken in the form only.</summary>
    public const string AppleAuthority = "https://apple.test.example";
    public const string ClientId = "barako-test-client";
    public const string ClientSecret = "stub-client-secret-do-not-leak";
    public const string KeyId = "stub-key-1";

    public RSA Key { get; } = RSA.Create(2048);

    /// <summary>What the plain discovery document says its issuer is. A test can make it lie.</summary>
    public string DiscoveryIssuer { get; set; } = Authority;

    /// <summary>Replaces the plain discovery document outright.</summary>
    public string? DiscoveryBody { get; set; }

    /// <summary>Replaces the key set outright.</summary>
    public string? KeysBody { get; set; }

    /// <summary>When set, every request waits on it, which is how a provider that never answers looks.</summary>
    public TaskCompletionSource? Hang { get; set; }

    /// <summary>Narrows <see cref="Hang"/> to addresses that start with this, so one provider can hang alone.</summary>
    public string? HangPrefix { get; set; }

    public int DiscoveryCalls;
    public int KeysCalls;

    /// <summary>Authorization code to the id token it redeems for, and the PKCE challenge it was issued under.</summary>
    public ConcurrentDictionary<string, (string IdToken, string? Challenge)> Codes { get; } = new();

    public ConcurrentDictionary<string, TokenRequest> TokenRequests { get; } = new();

    public ConcurrentQueue<string> RequestedUrls { get; } = new();

    public sealed record TokenRequest(IReadOnlyDictionary<string, string> Form, string? Authorization, int Times);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        RequestedUrls.Enqueue(url);

        if (Hang is not null && (HangPrefix is null || url.StartsWith(HangPrefix, StringComparison.Ordinal)))
        {
            await Hang.Task.WaitAsync(ct);
        }

        if (url == Authority + "/.well-known/openid-configuration")
        {
            Interlocked.Increment(ref DiscoveryCalls);
            return Json(DiscoveryBody ?? Discovery(DiscoveryIssuer, Authority, postSecret: false));
        }

        if (url == SharedAuthority + "/.well-known/openid-configuration")
        {
            Interlocked.Increment(ref DiscoveryCalls);
            return Json(Discovery(SharedIssuerTemplate, SharedAuthority, postSecret: true));
        }

        if (url == AppleAuthority + "/.well-known/openid-configuration")
        {
            Interlocked.Increment(ref DiscoveryCalls);
            return Json(Discovery(AppleAuthority, AppleAuthority, postSecret: true));
        }

        if (url == AppleAuthority + "/keys")
        {
            Interlocked.Increment(ref KeysCalls);
            return Json(Jwks(Key, KeyId));
        }

        if (url == AppleAuthority + "/token")
        {
            return await RedeemAsync(request, ct);
        }

        if (url == Authority + "/keys" || url == SharedAuthority + "/keys")
        {
            Interlocked.Increment(ref KeysCalls);
            return Json(KeysBody ?? Jwks(Key, KeyId, url.StartsWith(SharedAuthority, StringComparison.Ordinal) ? SharedIssuerTemplate : null));
        }

        if (url == Authority + "/token" || url == SharedAuthority + "/token")
        {
            return await RedeemAsync(request, ct);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> RedeemAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var parsed = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
        var form = parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k] ?? "");
        var code = form.GetValueOrDefault("code") ?? "";

        TokenRequests.AddOrUpdate(
            code,
            _ => new TokenRequest(form, request.Headers.Authorization?.ToString(), 1),
            (_, earlier) => earlier with { Times = earlier.Times + 1 });

        if (!Codes.TryGetValue(code, out var issued))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        }

        if (issued.Challenge is not null
            && OidcTestTokens.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? ""))) != issued.Challenge)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        }

        return Json(JsonSerializer.Serialize(new { access_token = "stub-access-token", token_type = "Bearer", id_token = issued.IdToken }));
    }

    public static string Discovery(string issuer, string authority, bool postSecret) => JsonSerializer.Serialize(
        postSecret
            ? new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = authority + "/authorize",
                ["token_endpoint"] = authority + "/token",
                ["jwks_uri"] = authority + "/keys",
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "private_key_jwt" },
            }
            : new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = authority + "/authorize",
                ["token_endpoint"] = authority + "/token",
                ["jwks_uri"] = authority + "/keys",
            });

    public static string Jwks(RSA key, string keyId, string? keyIssuer = null)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = keyId,
            ["n"] = OidcTestTokens.Base64Url(parameters.Modulus!),
            ["e"] = OidcTestTokens.Base64Url(parameters.Exponent!),
        };
        if (keyIssuer is not null)
        {
            jwk["issuer"] = keyIssuer;
        }

        return JsonSerializer.Serialize(new { keys = new[] { jwk } });
    }

    private static HttpResponseMessage Json(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }
}

/// <summary>
/// Hands every caller a client over one handler, and remembers which named client was asked for and
/// which named client sent each request.
/// </summary>
/// <remarks>
/// On a whole host the factory is shared with every other service, and some of them create a
/// client of their own while a test runs. So what a test about one feature can assert is which
/// client sent that feature's requests, not which clients were ever created.
/// </remarks>
internal sealed class RecordingClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public ConcurrentQueue<string> Names { get; } = new();

    public ConcurrentQueue<(string Client, string Url)> Requests { get; } = new();

    public HttpClient CreateClient(string name)
    {
        Names.Enqueue(name);
        return new HttpClient(new Tagging(name, Requests, handler), disposeHandler: false);
    }

    private sealed class Tagging(string client, ConcurrentQueue<(string Client, string Url)> requests, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Enqueue((client, request.RequestUri?.ToString() ?? string.Empty));
            return base.SendAsync(request, ct);
        }
    }
}

/// <summary>Builds id tokens by hand: a header, a payload and a signature over the two.</summary>
internal static class OidcTestTokens
{
    /// <summary>The claims of a token the plain stub issuer would really issue, for the caller to edit.</summary>
    public static Dictionary<string, object?> Claims(string nonce, string subject, string email) => new()
    {
        ["iss"] = OidcStubProvider.Authority,
        ["sub"] = subject,
        ["aud"] = OidcStubProvider.ClientId,
        ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["nonce"] = nonce,
        ["email"] = email,
        ["email_verified"] = true,
        ["name"] = "Stub Person",
        ["picture"] = "https://idp.test.example/p.png",
    };

    public static string Rs256(RSA key, Dictionary<string, object?> claims, string? keyId = OidcStubProvider.KeyId)
    {
        var signingInput = SigningInput("RS256", keyId, claims);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>An HMAC token, keyed with whatever the attacker hopes the validator will use.</summary>
    public static string Hs256(byte[] key, Dictionary<string, object?> claims, string? keyId = OidcStubProvider.KeyId)
    {
        var signingInput = SigningInput("HS256", keyId, claims);
        return $"{signingInput}.{Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)))}";
    }

    /// <summary>The unsigned form: <c>alg</c> is <c>none</c> and the signature is empty.</summary>
    public static string Unsigned(Dictionary<string, object?> claims) => SigningInput("none", null, claims) + ".";

    private static string SigningInput(string algorithm, string? keyId, Dictionary<string, object?> claims)
    {
        var header = new Dictionary<string, object?> { ["alg"] = algorithm, ["typ"] = "JWT" };
        if (keyId is not null)
        {
            header["kid"] = keyId;
        }

        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
