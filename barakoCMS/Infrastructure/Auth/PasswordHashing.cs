namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// The one place a password hash is made, so the work factor is one number.
/// </summary>
/// <remarks>
/// BCrypt records the factor inside the hash, so an account keeps the factor it was hashed with until
/// the hash is rewritten. Raising <see cref="WorkFactor"/> reaches an existing account at its next
/// successful sign-in, which is the only moment the plaintext is available (#639).
/// </remarks>
internal static class PasswordHashing
{
    /// <summary>
    /// 11 is BCrypt.Net's default, which is what every hash was made with before this constant
    /// existed. Raise it here; the sign-in path upgrades existing hashes to match.
    /// </summary>
    public const int WorkFactor = 11;

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    /// <summary>
    /// True when the stored hash was made below <see cref="WorkFactor"/>. An empty hash, an account
    /// with no password, is never due one: rehashing it would give it a usable password.
    /// </summary>
    public static bool NeedsRehash(string? hash) =>
        !string.IsNullOrEmpty(hash) && BCrypt.Net.BCrypt.PasswordNeedsRehash(hash, WorkFactor);
}
