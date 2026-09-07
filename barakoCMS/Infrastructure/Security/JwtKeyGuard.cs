namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Validates the JWT signing key at startup, failing fast rather than booting with weak or
/// placeholder auth.
/// </summary>
/// <remarks>
/// A length check alone is not enough. The shipped <c>k8s/02-secret.yaml</c> carries a 45-character
/// placeholder (<c>REPLACE_THIS_WITH_A_REAL_32_CHAR_SECRET_KEY</c>) that passes a length check, so an
/// operator who applies the manifests without editing them boots on a key that is public in the
/// repository, and anyone can forge tokens. The compose file fails closed through a required
/// variable; k8s cannot, so the guard belongs in the code both paths run.
/// </remarks>
internal static class JwtKeyGuard
{
    public const int MinLength = 32;

    /// <summary>The prefix every placeholder secret in the shipped manifests starts with.</summary>
    private const string PlaceholderPrefix = "REPLACE_THIS";

    /// <summary>
    /// Throws <see cref="System.InvalidOperationException"/> when the key is missing, too short, or a
    /// shipped placeholder. Returns the key unchanged when it is acceptable.
    /// </summary>
    public static string Validate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinLength)
        {
            throw new System.InvalidOperationException(
                $"JWT:Key must be configured and at least {MinLength} characters (256 bits) for security.");
        }

        if (key.StartsWith(PlaceholderPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            throw new System.InvalidOperationException(
                "JWT:Key is still the placeholder shipped in k8s/02-secret.yaml. Set a real, secret, "
              + "randomly generated key (at least 32 characters) before starting.");
        }

        return key;
    }
}
