namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Encrypts a secret that barakoCMS holds on an operator's behalf, so a database dump or a backup
/// does not hand over a working credential.
/// </summary>
/// <remarks>
/// This is for credentials the deployment stores because somebody typed them into the admin, which
/// is a different job from <c>IMfaSecretProtector</c>: that one protects a per user second factor
/// and derives its key from <c>Mfa:Key</c>. They are kept apart so rotating one does not make the
/// other undecryptable, which is the failure mode SECURITY.md warns about.
///
/// The key is derived from <c>Secrets:Key</c>, falling back to <c>JWT:Key</c>. Changing whichever
/// one is in use makes every value already stored undecryptable, unless the old material stays
/// readable in a key ring (<c>Secrets:Keys</c>, see <see cref="KeyRing"/>), which is how a key is
/// rotated without anybody retyping a credential.
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Decrypts, or returns null when the value cannot be decrypted with the current key.</summary>
    /// <remarks>
    /// Null rather than an exception, because the reachable cause is a rotated key rather than a
    /// bug, and the caller's answer is the same as for a secret that was never set: fall back, and
    /// tell the operator it needs entering again. A send that throws deep inside a provider gives
    /// them a stack trace instead.
    /// </remarks>
    string? Unprotect(string protectedValue);
}

public sealed class SecretProtector : ISecretProtector, IKeyRingProtector
{
    private readonly KeyRing _ring;

    public SecretProtector(IConfiguration config)
    {
        var material = config["Secrets:Key"];
        if (string.IsNullOrEmpty(material)) material = config["JWT:Key"];

        _ring = KeyRing.FromConfiguration(config, "Secrets", material, prefixUnkeyedWrites: true);
        if (!_ring.CanProtect)
            throw new InvalidOperationException("Secrets:Keys, Secrets:Key or JWT:Key must be configured to protect stored secrets.");
    }

    KeyRing IKeyRingProtector.Ring => _ring;

    public string Protect(string plaintext) => _ring.Protect(plaintext);

    public string? Unprotect(string protectedValue) => _ring.Unprotect(protectedValue);
}
