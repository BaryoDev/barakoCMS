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

    /// <summary>The key docker-compose.yml and docker-compose.hub.yml fall back to. Public, so only Development may run on it.</summary>
    internal const string ComposeDevKey = "dev-only-key-minimum-32-chars-long";

    /// <summary>The prefix every placeholder secret in the shipped manifests starts with.</summary>
    private const string PlaceholderPrefix = "REPLACE_THIS";

    /// <summary>
    /// Throws <see cref="System.InvalidOperationException"/> when the key is missing, too short, or a
    /// shipped placeholder, or the compose development key outside Development. Returns the key unchanged when it is acceptable.
    /// </summary>
    public static string Validate(string? key, bool isDevelopment = false)
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

        if (!isDevelopment && string.Equals(key, ComposeDevKey, System.StringComparison.Ordinal))
        {
            throw new System.InvalidOperationException(
                "JWT:Key is the development key shipped in the compose files. Set a real, secret, "
              + "randomly generated key (at least 32 characters) before starting outside Development.");
        }

        return key;
    }

    /// <summary><see cref="Validate"/>, then logs the non-ASCII warning once. Startup only, so a mint does not repeat it.</summary>
    public static string ValidateAtStartup(string? key, bool isDevelopment)
    {
        var valid = Validate(key, isDevelopment);
        if (NonAsciiWarning(valid) is { } warning)
        {
            Serilog.Log.Warning("{JwtKeyWarning}", warning);
        }

        return valid;
    }

    /// <summary>
    /// A warning when the key holds a non-ASCII character, otherwise null. Never an error: tokens
    /// are signed and validated with the key read as ASCII, so such a site works, but the key is
    /// weaker than it looks and changing the encoding later would invalidate every token.
    /// </summary>
    public static string? NonAsciiWarning(string key) =>
        key.Any(c => c > 127)
            ? "JWT:Key contains characters outside ASCII. Tokens read the key as ASCII, so each such "
            + "character becomes a question mark. Use an ASCII key, such as a random base64 or hex string."
            : null;
}
