using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BarakoCMS.ExternalAuth;

/// <summary>Who an id token says signed in, once every check on it has passed.</summary>
/// <param name="Issuer">The token's <c>iss</c>. With a template issuer this is the directory's own value.</param>
/// <param name="Email">Lowercased, or null when the token carries none that looks like an address.</param>
internal sealed record OidcIdentity(
    string Issuer,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? Picture);

/// <summary>Why an id token was refused. Logged by name; never shown to the caller.</summary>
internal enum OidcRefusal
{
    None,
    Malformed,
    Algorithm,
    Issuer,
    Signature,
    Audience,
    Lifetime,
    Nonce,
    Subject,
    SigningKeyIssuer,
}

/// <summary>Checks an id token against the provider it is supposed to have come from.</summary>
/// <remarks>
/// In order: the algorithm is one of the asymmetric ones in <see cref="Algorithms"/>, so <c>none</c>
/// and the HMAC family (where the "key" would be something the client also knows) never reach a
/// signature check; <c>iss</c> is exactly the configured issuer; one published key is chosen by the
/// token's <c>kid</c> and the signature verifies against that key and no other; <c>aud</c> contains
/// an accepted audience (the client id, or for the id token grant one of the configured ones), and
/// with more than one audience <c>azp</c> is an accepted one too; <c>exp</c> and
/// <c>nbf</c> hold within <see cref="ClockSkew"/>; <c>nonce</c> is the one this browser was given;
/// <c>sub</c> is present.
/// </remarks>
internal static class OidcIdToken
{
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    internal static readonly string[] Algorithms =
        ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    internal const int MaxLength = 16 * 1024;
    private const int MaxSubjectLength = 255;
    private const int MaxEmailLength = 254;
    private const int MaxNameLength = 200;
    private const int MaxPictureLength = 2000;

    public static async Task<(OidcIdentity? Identity, OidcRefusal Refusal)> ValidateAsync(
        string? idToken,
        OidcProvider provider,
        IReadOnlyList<JsonWebKey> keys,
        string nonce,
        IReadOnlyList<string>? grantAudiences = null)
    {
        IReadOnlyList<string> audiences = grantAudiences ?? [provider.ClientId];
        if (audiences.Count == 0)
        {
            return (null, OidcRefusal.Audience);
        }

        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaxLength)
        {
            return (null, OidcRefusal.Malformed);
        }

        var handler = new JsonWebTokenHandler();
        JsonWebToken token;
        JsonElement payload;
        try
        {
            token = handler.ReadJsonWebToken(idToken);
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            payload = document.RootElement.Clone();
        }
        catch (Exception)
        {
            return (null, OidcRefusal.Malformed);
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return (null, OidcRefusal.Malformed);
        }

        if (!Algorithms.Contains(token.Alg, StringComparer.Ordinal))
        {
            return (null, OidcRefusal.Algorithm);
        }

        var issuer = Text(payload, "iss");
        var expectedIssuer = ExpectedIssuer(provider.Issuer, payload);
        if (issuer is null || expectedIssuer is null || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
        {
            return (null, OidcRefusal.Issuer);
        }

        // The key is chosen here and is the only one the signature is checked against, so the rule
        // applied to the key afterwards is applied to the key that verified. Letting the library try
        // every published key would verify a token under a key its header never named.
        var signingKey = SelectKey(keys, token.Kid, provider.IssuerIsTemplate);
        if (signingKey is null)
        {
            return (null, provider.IssuerIsTemplate ? OidcRefusal.SigningKeyIssuer : OidcRefusal.Signature);
        }

        SecurityKey[] verifyingKeys = [signingKey];

        var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = expectedIssuer,
            ValidateAudience = true,
            ValidAudiences = audiences,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, _, _) => verifyingKeys,
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = Algorithms,
        });

        if (!result.IsValid)
        {
            return (null, result.Exception switch
            {
                SecurityTokenInvalidAudienceException => OidcRefusal.Audience,
                SecurityTokenExpiredException or SecurityTokenNotYetValidException
                    or SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException => OidcRefusal.Lifetime,
                SecurityTokenInvalidIssuerException => OidcRefusal.Issuer,
                SecurityTokenInvalidAlgorithmException => OidcRefusal.Algorithm,
                _ => OidcRefusal.Signature,
            });
        }

        // With several audiences the token has to say which one it was issued to, and it has to be
        // us. An azp that is there at all has to be us too, whatever the audience count. A native
        // Google token names the server's client as aud and the app's as azp, so the grant lists both.
        var severalAudiences = payload.TryGetProperty("aud", out var audience)
            && audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1;
        if ((severalAudiences || payload.TryGetProperty("azp", out _))
            && !(Text(payload, "azp") is { } party && audiences.Contains(party, StringComparer.Ordinal)))
        {
            return (null, OidcRefusal.Audience);
        }

        // The grant may list the web client, because a native Google token names it as aud. A token
        // the browser flow was issued names it as aud too, with azp the web client or absent. So for
        // the grant, a token addressed to the web client has to name a listed native app as azp.
        if (grantAudiences is not null
            && Audiences(payload).Contains(provider.ClientId, StringComparer.Ordinal)
            && !(Text(payload, "azp") is { } app && app != provider.ClientId && grantAudiences.Contains(app, StringComparer.Ordinal)))
        {
            return (null, OidcRefusal.Audience);
        }

        if (provider.IssuerIsTemplate && !SigningKeyMayIssue(signingKey, issuer, payload))
        {
            return (null, OidcRefusal.SigningKeyIssuer);
        }

        var presented = Text(payload, "nonce");
        if (presented is null || !FixedTimeEquals(presented, nonce))
        {
            return (null, OidcRefusal.Nonce);
        }

        var subject = Text(payload, "sub");
        if (string.IsNullOrEmpty(subject) || subject.Length > MaxSubjectLength)
        {
            return (null, OidcRefusal.Subject);
        }

        var email = Text(payload, "email")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength || !email.Contains('@'))
        {
            email = null;
        }

        // Absent is false: a provider that says nothing has not vouched for the address. Only the
        // JSON boolean counts. The text "true" is what a free-form profile attribute mapped into a
        // token looks like, and this flag decides whose account a first sign-in lands on. A provider
        // that documents the text form for its own claim (Apple) is configured to have it taken.
        var verified = payload.TryGetProperty(provider.EmailVerifiedClaim, out var flag)
            && (flag.ValueKind == JsonValueKind.True
                || (provider.EmailVerifiedMayBeText && flag.ValueKind == JsonValueKind.String && flag.GetString() == "true"));

        return (new OidcIdentity(
            issuer,
            subject,
            email,
            email is not null && verified,
            Bounded(Text(payload, "name"), MaxNameLength),
            Picture(Text(payload, "picture"))), OidcRefusal.None);
    }

    /// <summary>
    /// The configured issuer, or for a template the issuer of the directory the token names.
    /// </summary>
    /// <remarks>
    /// Microsoft's multi-directory endpoints publish <c>https://login.microsoftonline.com/{tenantid}/v2.0</c>
    /// and sign tokens whose <c>iss</c> carries the directory's id in that place. The documented
    /// check is to put the token's <c>tid</c> into the template and compare exactly, with <c>tid</c>
    /// required to be a GUID so nothing else can be substituted into the URL.
    /// </remarks>
    internal static string? ExpectedIssuer(string configured, JsonElement payload)
    {
        if (!configured.Contains(OidcProvider.TenantPlaceholder, StringComparison.Ordinal))
        {
            return configured;
        }

        var tenant = Text(payload, "tid");
        return Guid.TryParseExact(tenant, "D", out _)
            ? configured.Replace(OidcProvider.TenantPlaceholder, tenant, StringComparison.Ordinal)
            : null;
    }

    /// <summary>
    /// The one published key a token may be verified with, or null when its header does not settle
    /// that.
    /// </summary>
    /// <remarks>
    /// A <c>kid</c> has to match exactly one key. A token with no <c>kid</c> is accepted only from a
    /// provider with a fixed issuer that publishes a single key, where there is nothing to choose
    /// between. With a template issuer a <c>kid</c> is always required, because each key says which
    /// directory it signs for and the header is what ties the token to one of them.
    /// </remarks>
    internal static JsonWebKey? SelectKey(IReadOnlyList<JsonWebKey> keys, string? keyId, bool issuerIsTemplate)
    {
        if (string.IsNullOrEmpty(keyId))
        {
            return !issuerIsTemplate && keys.Count == 1 ? keys[0] : null;
        }

        var named = keys.Where(k => string.Equals(k.Kid, keyId, StringComparison.Ordinal)).Take(2).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>
    /// The second half of the template check: a key in a shared key set says which issuer it signs
    /// for, and a key published for one directory must not vouch for another.
    /// </summary>
    private static bool SigningKeyMayIssue(JsonWebKey key, string issuer, JsonElement payload)
    {
        if (!key.AdditionalData.TryGetValue("issuer", out var raw))
        {
            return true;
        }

        var keyIssuer = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        return !string.IsNullOrEmpty(keyIssuer)
            && string.Equals(ExpectedIssuer(keyIssuer, payload), issuer, StringComparison.Ordinal);
    }

    /// <summary>The token's <c>aud</c>, which may be one string or an array of them.</summary>
    private static List<string> Audiences(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return [];
        }

        if (aud.ValueKind == JsonValueKind.String)
        {
            return [aud.GetString()!];
        }

        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList()
            : [];
    }

    /// <summary>The <c>kid</c> in a token's header, read without checking anything, to pick the key set.</summary>
    internal static string? KeyIdOf(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaxLength)
        {
            return null;
        }

        try
        {
            var keyId = new JsonWebTokenHandler().ReadJsonWebToken(idToken).Kid;
            return string.IsNullOrEmpty(keyId) ? null : keyId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// Kept only as an absolute https URL. The claim can be a profile field its owner typed, and it
    /// ends up in an image tag, so anything else is dropped.
    /// </summary>
    private static string? Picture(string? value) =>
        Bounded(value, MaxPictureLength) is { } url && OidcProviders.IsHttpsUrl(url) ? url : null;

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) || value.Length > max ? null : value;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
