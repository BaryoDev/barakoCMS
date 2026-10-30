using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// The limit on the anonymous routes the social sign-in work added: the code exchange and the
/// Facebook data deletion routes. One bucket per client address, read from configuration when the
/// partition is created, like <c>Oidc:RateLimit</c>.
/// </summary>
internal static class SocialRateLimit
{
    public const string Policy = "external-auth-social";
    public const string Section = "Social:RateLimit";
    public const int DefaultPermitLimit = 10;
    public const int DefaultWindowSeconds = 60;

    /// <summary>A setting, or the default when it is unset, not a number or not above zero.</summary>
    public static int Positive(IConfiguration config, string name, int fallback) =>
        int.TryParse(config[$"{Section}:{name}"], out var value) && value > 0 ? value : fallback;

    public static int PermitLimit(IConfiguration config) => Positive(config, "PermitLimit", DefaultPermitLimit);

    public static TimeSpan Window(IConfiguration config) =>
        TimeSpan.FromSeconds(Positive(config, "WindowSeconds", DefaultWindowSeconds));

    public static void Register(RateLimiterOptions options) =>
        options.AddPolicy(Policy, context =>
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"social-{ip}", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit(config), Window = Window(config) });
        });
}
