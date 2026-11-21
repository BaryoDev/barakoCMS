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
    public static string Resolve(IConfiguration config)
    {
        var key = config["JWT:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable("JWT__Key");
        }

        return JwtKeyGuard.Validate(
            key,
            Extensions.ServiceCollectionExtensions.IsDevelopment(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")));
    }
}
