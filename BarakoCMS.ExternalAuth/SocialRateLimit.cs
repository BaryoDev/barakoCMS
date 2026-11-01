using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// The limits on the anonymous routes the social sign-in work added. The Facebook data deletion
/// routes get a tight one, and the code exchange gets its own, higher one: a frontend that
/// exchanges from its own server reaches the exchange from one address for every visitor, so the
/// deletion limit would cap its sign-ins at ten a minute. One bucket per client address and per
/// policy, read from configuration when the partition is created, like <c>Oidc:RateLimit</c>.
/// </summary>
internal static class SocialRateLimit
{
    public const string Policy = "external-auth-social";
    public const string Section = "Social:RateLimit";
    public const int DefaultPermitLimit = 10;
    public const int DefaultWindowSeconds = 60;

    public const string ExchangePolicy = "external-auth-social-exchange";
    public const int DefaultExchangePermitLimit = 300;
    public const int DefaultExchangeWindowSeconds = 60;

    /// <summary>A setting, or the default when it is unset, not a number or not above zero.</summary>
    public static int Positive(IConfiguration config, string name, int fallback) =>
        int.TryParse(config[$"{Section}:{name}"], out var value) && value > 0 ? value : fallback;

    public static int PermitLimit(IConfiguration config) => Positive(config, "PermitLimit", DefaultPermitLimit);

    public static TimeSpan Window(IConfiguration config) =>
        TimeSpan.FromSeconds(Positive(config, "WindowSeconds", DefaultWindowSeconds));

    public static int ExchangePermitLimit(IConfiguration config) =>
        Positive(config, "ExchangePermitLimit", DefaultExchangePermitLimit);

    public static TimeSpan ExchangeWindow(IConfiguration config) =>
        TimeSpan.FromSeconds(Positive(config, "ExchangeWindowSeconds", DefaultExchangeWindowSeconds));

    public static void Register(RateLimiterOptions options)
    {
        options.AddPolicy(Policy, context =>
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"social-{ip}", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit(config), Window = Window(config) });
        });

        options.AddPolicy(ExchangePolicy, context =>
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"social-exchange-{ip}", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = ExchangePermitLimit(config), Window = ExchangeWindow(config) });
        });
    }
}
