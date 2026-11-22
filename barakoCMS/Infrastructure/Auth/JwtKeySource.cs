using barakoCMS.Infrastructure.Security;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// The one place the JWT signing key is read for minting and for validating a token, so both use
/// the same key and both refuse a weak one.
/// </summary>
/// <remarks>
/// The key is <c>JWT:Key</c> from the configuration given, else the <c>JWT__Key</c> environment
/// variable, with a blank value counting as unset, then <see cref="JwtKeyGuard.Validate"/>. It is
/// read from the host's own <see cref="IConfiguration"/> each time it is needed, so a key added to
/// that configuration after <c>AddBarakoCMS</c> ran is checked as well.
/// </remarks>
internal static class JwtKeySource
{
    /// <summary>
    /// The key as the bytes every token is signed and validated with. ASCII, because that is what
    /// bearer validation and every token shipped so far used. A non-ASCII character becomes
    /// <c>?</c>, so it must be read the same way everywhere or the two sides hold different keys.
    /// </summary>
    public static byte[] Bytes(string key) => System.Text.Encoding.ASCII.GetBytes(key);

    /// <summary>The configured key before any check, or null when none is set.</summary>
    public static string? Read(IConfiguration config)
    {
        var key = config["JWT:Key"];
        return string.IsNullOrWhiteSpace(key) ? Environment.GetEnvironmentVariable("JWT__Key") : key;
    }

    public static string Resolve(IConfiguration config, bool isDevelopment)
    {
        return JwtKeyGuard.Validate(Read(config), isDevelopment);
    }
}
