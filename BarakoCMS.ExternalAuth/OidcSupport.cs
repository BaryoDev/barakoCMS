using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.ExternalAuth;

/// <summary>What the start endpoint leaves in the browser for the callback to check.</summary>
/// <param name="State">Sent to the provider and expected back unchanged.</param>
/// <param name="Nonce">Sent to the provider and expected inside the id token.</param>
/// <param name="CodeVerifier">The PKCE secret. Only its SHA-256 goes to the provider at the start.</param>
internal sealed record OidcFlow(string State, string Nonce, string CodeVerifier)
{
    public static OidcFlow New() => new(
        ExternalAuthSupport.NewState(), ExternalAuthSupport.NewState(), ExternalAuthSupport.NewState());

    public string CodeChallenge =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string ToCookie() => $"{State}.{Nonce}.{CodeVerifier}";

    /// <summary>Null unless the cookie is exactly three values of the shape <see cref="New"/> mints.</summary>
    public static OidcFlow? FromCookie(string? value)
    {
        var parts = (value ?? string.Empty).Split('.');
        return parts.Length == 3 && parts.All(part => OidcSupport.TokenPattern().IsMatch(part))
            ? new OidcFlow(parts[0], parts[1], parts[2])
            : null;
    }
}

internal static partial class OidcSupport
{
    public const string RateLimitPolicy = "external-auth-oidc";
    public const string RateLimitSection = "Oidc:RateLimit";
    public const int DefaultPermitLimit = 20;
    public const int DefaultWindowSeconds = 300;

    public const int MaxClubLength = 100;
    public const int MaxCodeLength = 2048;

    /// <summary>32 random bytes as unpadded base64url, which is what state, nonce and verifier all are.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    public static partial Regex TokenPattern();

    /// <summary>
    /// A rate limit setting, or the default when it is unset, not a number or not above zero. The
    /// limit cannot be switched off by a typo.
    /// </summary>
    public static int Positive(IConfiguration config, string name, int fallback) =>
        int.TryParse(config[$"{RateLimitSection}:{name}"], out var value) && value > 0 ? value : fallback;

    public static string CallbackUrl(IConfiguration config, HttpContext context, OidcProvider provider) =>
        ExternalAuthSupport.BaseUrl(config, context) + $"/api/auth/oidc/{provider.Name}/callback";

    /// <summary>
    /// Expires a <c>__Host-</c> cookie. The removal has to carry Secure and Path=/ like the cookie it
    /// removes, or the browser discards the removal and keeps the cookie.
    /// </summary>
    public static void Expire(HttpResponse response, string name) =>
        response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

    public static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// Is this a Postgres unique-constraint violation (SQLSTATE 23505), at any depth? Marten wraps
    /// the Npgsql exception at a depth that varies by command, so the chain is walked.
    /// </summary>
    public static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Remembers which <c>state</c> values a callback has already used, so each works once.</summary>
/// <remarks>
/// <para>
/// The browser half of single use is the cookie, which every callback expires. This is the server
/// half: a callback replayed with the cookie attached by hand is refused here, before the code is
/// sent anywhere.
/// </para>
/// <para>
/// In memory, so it holds for the instance that saw the first use. Behind several instances a
/// replay can reach one that did not, and what refuses it there is the provider, which redeems an
/// authorization code once. Bounded at <see cref="Capacity"/>: expired entries go first, and if
/// every entry is still live the set is dropped rather than refusing every sign-in until it drains.
/// </para>
/// </remarks>
internal sealed class OidcConsumedStates
{
    internal const int Capacity = 10_000;

    /// <summary>As long as the state cookie lives, after which the browser no longer holds it.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>True the first time a state is presented, false while that use is remembered.</summary>
    public bool TryConsume(string state)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        var now = Now();
        lock (_lock)
        {
            if (_seen.TryGetValue(key, out var until) && until > now)
            {
                return false;
            }

            if (_seen.Count >= Capacity)
            {
                foreach (var expired in _seen.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
                {
                    _seen.Remove(expired);
                }

                if (_seen.Count >= Capacity)
                {
                    _seen.Clear();
                }
            }

            _seen[key] = now + Lifetime;
            return true;
        }
    }
}

/// <summary>
/// Says at startup which configured providers are off because their configuration cannot be used.
/// Without it a typo in an authority reads as a button that is simply missing.
/// </summary>
internal sealed class OidcConfigurationReport(IConfiguration config, ILogger<OidcConfigurationReport> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ExternalAuthSupport.ExternalAuthDisabled(config))
        {
            foreach (var problem in OidcProviders.Problems(config))
            {
                logger.LogWarning("An OIDC provider is configured and off: {Problem}", problem);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
