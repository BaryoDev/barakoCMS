using barakoCMS.Infrastructure.Security;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>
/// Encrypts a connector credential at rest.
/// </summary>
/// <remarks>
/// A third protector rather than a reuse of either existing one, and the reason is written down in
/// SECURITY.md: <c>Mfa:Key</c> falling back to <c>JWT:Key</c> couples two controls to one secret, and
/// that is recorded there as a lesson rather than a feature.
///
/// Rotating an encryption key makes everything encrypted under it undecryptable. One key per class of
/// secret means that is one decision at a time: rotating the connector key breaks integrations and
/// leaves second factors alone, instead of locking every enrolled user out on the same afternoon.
///
/// **No fallback.** <c>Connectors:Key</c> or nothing. Falling back to the JWT key would quietly
/// recouple exactly what this exists to separate, and an operator would have no way to tell which
/// key their credentials were under.
/// </remarks>
public interface IConnectorSecretProtector
{
    /// <summary>Whether a key is configured at all. False means the feature is unavailable.</summary>
    bool IsConfigured { get; }

    string Protect(string plaintext);

    /// <summary>Decrypts, or null when the value will not decrypt under the current key.</summary>
    string? Unprotect(string protectedValue);
}

public sealed class ConnectorSecretProtector : IConnectorSecretProtector
{
    /// <summary>Shortest key material accepted, matching what startup already demands of JWT:Key.</summary>
    internal const int MinimumKeyLength = 32;

    private readonly byte[]? _key;

    public ConnectorSecretProtector(IConfiguration config)
    {
        var material = config["Connectors:Key"];
        _key = string.IsNullOrEmpty(material) ? null : AesGcmEnvelope.DeriveKey(material);
    }

    public bool IsConfigured => _key is not null;

    public string Protect(string plaintext)
    {
        if (_key is null)
        {
            throw new InvalidOperationException(
                "Connectors:Key is not configured, so a connector credential cannot be encrypted.");
        }

        return AesGcmEnvelope.Protect(_key, plaintext);
    }

    public string? Unprotect(string protectedValue)
    {
        if (_key is null || string.IsNullOrEmpty(protectedValue)) return null;

        try
        {
            return AesGcmEnvelope.Unprotect(_key, protectedValue);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Reachable rather than a bug: the key was rotated. The caller answers as it would for a
            // credential that was never set, which is to refuse the call and say it needs entering
            // again, instead of surfacing a stack trace from inside an HTTP send.
            return null;
        }
    }
}
