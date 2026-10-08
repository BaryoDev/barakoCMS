using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// The key a provider's client secret is signed with, read from
/// <c>Oidc:Providers:{name}:SignedClientSecret</c>. Apple takes no fixed secret: it takes an ES256
/// JWT that the app signs with a key made in its developer account.
/// </summary>
/// <param name="KeyId">The key's id, sent as the JWT's <c>kid</c>.</param>
/// <param name="TeamId">The developer team, sent as <c>iss</c>.</param>
/// <param name="PrivateKeyPem">A P-256 private key in PEM, the <c>.p8</c> file as downloaded.</param>
/// <param name="Audience">The JWT's <c>aud</c>. The provider's issuer when not set.</param>
internal sealed partial record OidcSignedSecret(string KeyId, string TeamId, string PrivateKeyPem, string? Audience)
{
    public const string Section = "SignedClientSecret";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Null with no problem when the section is not there. Null with a problem when it is there and
    /// cannot be used, so a provider that meant to sign its secret is off rather than sending none.
    /// </summary>
    public static OidcSignedSecret? Read(IConfigurationSection section, out string? problem)
    {
        problem = null;
        var keyId = section["KeyId"]?.Trim() ?? string.Empty;
        var teamId = section["TeamId"]?.Trim() ?? string.Empty;

        // An environment variable cannot hold a line break, so a key written with \n is accepted.
        var pem = (section["PrivateKey"] ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal).Trim();
        var audience = section["Audience"]?.Trim();
        if (keyId.Length == 0 && teamId.Length == 0 && pem.Length == 0)
        {
            return null;
        }

        if (!IdPattern().IsMatch(keyId) || !IdPattern().IsMatch(teamId))
        {
            problem = "needs KeyId and TeamId, each 1 to 64 characters of letters, digits, dot, underscore and hyphen";
            return null;
        }

        if (!string.IsNullOrEmpty(audience) && !OidcProviders.IsIssuerUrl(audience))
        {
            problem = "Audience must be an absolute https URL with no query or fragment";
            return null;
        }

        if (!IsP256PrivateKey(pem))
        {
            problem = "PrivateKey must be a P-256 private key in PEM";
            return null;
        }

        return new OidcSignedSecret(keyId, teamId, pem, string.IsNullOrEmpty(audience) ? null : audience);
    }

    private static bool IsP256PrivateKey(string pem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.ExportParameters(includePrivateParameters: true) is { D: not null } parameters
                && parameters.Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    public override string ToString() => $"OidcSignedSecret {{ KeyId = {KeyId}, TeamId = {TeamId} }}";
}

/// <summary>The client secret a provider is sent with the code exchange.</summary>
/// <remarks>
/// The configured string, or for a provider with <see cref="OidcProvider.SignedSecret"/> an ES256 JWT:
/// <c>iss</c> the team, <c>sub</c> the client id, <c>aud</c> the provider, and an expiry
/// <see cref="Lifetime"/> after it was made. Apple refuses one that lives longer than six months. One
/// is made per key and kept until <see cref="RenewBefore"/> is left of it, so the private key is
/// used about once a month rather than on every sign-in.
/// </remarks>
internal sealed class OidcClientSecrets
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromDays(1);

    private sealed record Signed(string Jwt, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Signed> _signed = new(StringComparer.Ordinal);

    /// <summary>Replaced by tests that need the secret to age without waiting.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public string For(OidcProvider provider)
    {
        if (provider.SignedSecret is not { } key)
        {
            return provider.ClientSecret;
        }

        var audience = key.Audience ?? provider.Issuer;
        var cacheKey = string.Join('\n', provider.Name, provider.ClientId, key.KeyId, key.TeamId, audience,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.PrivateKeyPem))));
        var now = Now();
        if (_signed.TryGetValue(cacheKey, out var hit) && now < hit.ExpiresAt - RenewBefore)
        {
            return hit.Jwt;
        }

        // An edited key gets a new cache key, so old entries could pile up under a host that reloads
        // configuration.
        if (_signed.Count >= OidcProviders.MaxProviders * 2)
        {
            _signed.Clear();
        }

        var made = Sign(key, provider.ClientId, audience, now);
        _signed[cacheKey] = made;
        return made.Jwt;
    }

    private static Signed Sign(OidcSignedSecret key, string clientId, string audience, DateTimeOffset now)
    {
        var expiresAt = now + Lifetime;
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "ES256",
            ["kid"] = key.KeyId,
            ["typ"] = "JWT",
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = key.TeamId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["aud"] = audience,
            ["sub"] = clientId,
        });

        var signingInput = Base64Url(header) + "." + Base64Url(payload);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(key.PrivateKeyPem);

        // JWS wants r and s side by side, which is what .NET produces by default.
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return new Signed($"{signingInput}.{Base64Url(signature)}", expiresAt);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
