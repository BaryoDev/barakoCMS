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
/// signature check; <c>iss</c> is exactly the configured issuer; the signature verifies against the
/// provider's published keys; <c>aud</c> contains the client id; <c>exp</c> and <c>nbf</c> hold
/// within <see cref="ClockSkew"/>; <c>nonce</c> is the one this browser was given; <c>sub</c> is
/// present.
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
        string nonce)
    {
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

        var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = expectedIssuer,
            ValidateAudience = true,
            ValidAudience = provider.ClientId,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
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

        // With several audiences the token has to say which one it was issued to, and it has to be us.
        var authorizedParty = Text(payload, "azp");
        if (authorizedParty is not null && !string.Equals(authorizedParty, provider.ClientId, StringComparison.Ordinal))
        {
            return (null, OidcRefusal.Audience);
        }

        if (provider.IssuerIsTemplate && !SigningKeyMayIssue(keys, token.Kid, issuer, payload))
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

        // Absent is false: a provider that says nothing has not vouched for the address.
        var verified = payload.TryGetProperty(provider.EmailVerifiedClaim, out var flag)
            && (flag.ValueKind == JsonValueKind.True
                || (flag.ValueKind == JsonValueKind.String && flag.GetString() == "true"));

        return (new OidcIdentity(
            issuer,
            subject,
            email,
            email is not null && verified,
            Bounded(Text(payload, "name"), MaxNameLength),
            Bounded(Text(payload, "picture"), MaxPictureLength)), OidcRefusal.None);
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
    /// The second half of the template check: a key in a shared key set says which issuer it signs
    /// for, and a key published for one directory must not vouch for another.
    /// </summary>
    private static bool SigningKeyMayIssue(IReadOnlyList<JsonWebKey> keys, string? keyId, string issuer, JsonElement payload)
    {
        var key = keys.FirstOrDefault(k => string.Equals(k.Kid, keyId, StringComparison.Ordinal));
        if (key is null || !key.AdditionalData.TryGetValue("issuer", out var raw))
        {
            return true;
        }

        var keyIssuer = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        return !string.IsNullOrEmpty(keyIssuer)
            && string.Equals(ExpectedIssuer(keyIssuer, payload), issuer, StringComparison.Ordinal);
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) || value.Length > max ? null : value;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
