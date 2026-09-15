using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace barakoCMS.Infrastructure.Security;

/// <summary>One fixed window: how many requests, over how long, and how many may wait for the next window.</summary>
internal sealed record RateLimitWindow(int PermitLimit, int WindowSeconds, int QueueLimit);

/// <summary>The rate limits read from the <c>RateLimiting</c> section, validated.</summary>
/// <param name="RendererKey">Null when no renderer key is configured, which means no renderer partition.</param>
internal sealed record RateLimitSettings(
    RateLimitWindow Global,
    RateLimitWindow Auth,
    RateLimitWindow Batch,
    RateLimitWindow Registration,
    string? RendererKey,
    RateLimitWindow Renderer,
    RateLimitWindow SiteShare)
{
    public override string ToString() =>
        $"RateLimitSettings {{ Global = {Global}, Auth = {Auth}, Batch = {Batch}, "
      + $"Registration = {Registration}, RendererKey = {(RendererKey is null ? "unset" : "set")}, Renderer = {Renderer}, SiteShare = {SiteShare} }}";
}

/// <summary>
/// Reads the <c>RateLimiting</c> section and builds the global limiter and the auth, telemetry,
/// registration and site share policies from it.
/// </summary>
/// <remarks>
/// <para>
/// Every default is the value that was hard coded before the limits became configuration, so a
/// deployment that sets nothing behaves as it did. A zero or negative limit is refused at startup
/// rather than read as "off": a typo should not quietly remove the limit on login.
/// </para>
/// <para>
/// The renderer partition exists because one barakoPress container renders every site it serves
/// from one IP, so all of those sites shared one global bucket. A request carrying the configured
/// key in <see cref="RendererHeader"/> is counted in its own bucket instead. Only the global limiter
/// honours it. The auth, telemetry and registration policies stay per IP, since a leaked renderer
/// key must not buy extra password guesses.
/// </para>
/// <para>
/// The site share policy is per tenant and per visitor. barakoPress redeems share links server side,
/// so every visitor of every site it renders arrives from its one IP. With the renderer key it may
/// name the visitor in <see cref="VisitorIpHeader"/>, and then that address is the visitor. Without a
/// matching key the header is ignored and the socket IP is the visitor, so nobody else can pick an
/// address to escape the limit.
/// </para>
/// </remarks>
internal static class RateLimitSetup
{
    public const string Section = "RateLimiting";
    public const string RendererHeader = "X-Barako-Renderer-Key";
    public const string VisitorIpHeader = "X-Barako-Visitor-IP";
    public const int RendererKeyMinLength = 32;

    public const string AuthPolicy = "auth";
    public const string BatchPolicy = "telemetry";
    public const string RegistrationPolicy = "registration";
    public const string SiteSharePolicy = "site-share";

    internal const string RendererPartition = "renderer";

    public static readonly RateLimitWindow DefaultGlobal = new(100, 60, 10);
    public static readonly RateLimitWindow DefaultAuth = new(5, 15 * 60, 0);
    public static readonly RateLimitWindow DefaultBatch = new(20, 60, 0);
    public static readonly RateLimitWindow DefaultRegistration = new(5, 60 * 60, 0);
    public static readonly RateLimitWindow DefaultRenderer = new(1000, 60, 10);
    public static readonly RateLimitWindow DefaultSiteShare = new(10, 60, 0);

    /// <summary>Reads and validates the section. Throws with the offending setting named.</summary>
    public static RateLimitSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);

        var rendererKey = section["Renderer:Key"];
        if (string.IsNullOrWhiteSpace(rendererKey))
        {
            rendererKey = null;
        }
        else if (rendererKey.Length < RendererKeyMinLength)
        {
            // The length only, never the value.
            throw new InvalidOperationException(
                $"{Section}:Renderer:Key is set but shorter than {RendererKeyMinLength} characters. It lets a "
              + "caller skip the per-IP global limit, so it has to be a real secret. Use a longer random value, "
              + "or remove it to turn the renderer partition off.");
        }

        return new RateLimitSettings(
            Window(section, "Global", DefaultGlobal),
            Window(section, "Auth", DefaultAuth),
            Window(section, "Batch", DefaultBatch),
            Window(section, "Registration", DefaultRegistration),
            rendererKey,
            Window(section, "Renderer", DefaultRenderer),
            Window(section, "SiteShare", DefaultSiteShare));
    }

    /// <summary>
    /// Auth and Registration set above their defaults. Allowed, but those two exist to slow down
    /// guessing and account farming, so loosening them should be visible in the startup log.
    /// </summary>
    public static IReadOnlyList<string> Warnings(RateLimitSettings settings)
    {
        var warnings = new List<string>();
        Loosened(warnings, "Auth", settings.Auth, DefaultAuth);
        Loosened(warnings, "Registration", settings.Registration, DefaultRegistration);
        return warnings;
    }

    public static void Configure(RateLimiterOptions options, RateLimitSettings settings)
    {
        var rendererKeyHash = settings.RendererKey is null ? null : Hash(settings.RendererKey);

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var partition = GlobalPartitionKey(context, rendererKeyHash);
            var window = partition == RendererPartition ? settings.Renderer : settings.Global;
            return RateLimitPartition.GetFixedWindowLimiter(partition, _ => Options(window));
        });

        options.AddPolicy(AuthPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"auth-{ClientIp(context)}", _ => Options(settings.Auth)));

        // Anonymous telemetry ingestion (browser error reports). Tighter than the global limit: the
        // endpoint is unauthenticated and each request fans out to one lookup per item in the batch.
        options.AddPolicy(BatchPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"telemetry-{ClientIp(context)}", _ => Options(settings.Batch)));

        options.AddPolicy(RegistrationPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"registration-{ClientIp(context)}", _ => Options(settings.Registration)));

        // Anonymous share link redemption. A guess costs a query, so it is held well under the global
        // limit. The key is 32 random bytes, so this is about load, not about making a guess feasible.
        options.AddPolicy(SiteSharePolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(SiteSharePartitionKey(context, rendererKeyHash), _ => Options(settings.SiteShare)));

        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsync(
                "Too many requests. Please try again later.", cancellationToken);
        };
    }

    /// <summary>
    /// The renderer partition when the header matches the configured key, otherwise the client IP.
    /// A wrong key is not an error and not a separate bucket: it is an ordinary request from its IP.
    /// </summary>
    internal static string GlobalPartitionKey(HttpContext context, byte[]? rendererKeyHash)
    {
        return HasRendererKey(context, rendererKeyHash) ? RendererPartition : ClientIp(context);
    }

    /// <summary>
    /// The tenant the request names, and the visitor: <see cref="VisitorIpHeader"/> when the renderer
    /// key matches and the header is one IP literal, otherwise the client IP.
    /// </summary>
    /// <remarks>
    /// The limiter runs before tenant resolution, so the tenant is what the request selects it by: the
    /// X-Tenant header, else the host. A tenant reached by two hosts gets two buckets, which only
    /// splits a caller's own budget; the global limit per IP still caps the total.
    /// </remarks>
    internal static string SiteSharePartitionKey(HttpContext context, byte[]? rendererKeyHash)
    {
        var visitor = HasRendererKey(context, rendererKeyHash) && VisitorIp(context) is { } named
            ? named
            : ClientIp(context);
        return $"site-share|{TenantSelector(context)}|{visitor}";
    }

    private static bool HasRendererKey(HttpContext context, byte[]? rendererKeyHash) =>
        rendererKeyHash is not null
        && context.Request.Headers.TryGetValue(RendererHeader, out var presented)
        && presented.Count == 1
        && !string.IsNullOrEmpty(presented[0])
        && CryptographicOperations.FixedTimeEquals(Hash(presented[0]!), rendererKeyHash);

    private const int MaxTenantSelectorLength = 253;

    private static string TenantSelector(HttpContext context)
    {
        var header = context.Request.Headers["X-Tenant"].ToString().Trim();
        var selector = header.Length > 0 ? header : context.Request.Host.Host;
        selector = selector.ToLowerInvariant();
        return selector.Length > MaxTenantSelectorLength ? selector[..MaxTenantSelectorLength] : selector;
    }

    /// <summary>The visitor header as a normalised address, or null unless it is exactly one IP literal.</summary>
    internal static string? VisitorIp(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(VisitorIpHeader, out var values) || values.Count != 1)
        {
            return null;
        }

        var raw = values[0];
        if (string.IsNullOrEmpty(raw) || raw.Length > 45 || raw.Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':')))
        {
            return null;
        }

        if (!System.Net.IPAddress.TryParse(raw, out var address))
        {
            return null;
        }

        // IPAddress.TryParse also reads "1" or "10.1" as IPv4 shorthand. A proxy never sends those,
        // so an IPv4 address has to be written as its own dotted quad.
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.ToString() != raw)
        {
            return null;
        }

        return address.ToString();
    }

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    // Kept as an IP string: an IP cannot spell "renderer", so no caller can land in that partition
    // by address.
    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions Options(RateLimitWindow window) => new()
    {
        PermitLimit = window.PermitLimit,
        Window = TimeSpan.FromSeconds(window.WindowSeconds),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = window.QueueLimit,
    };

    private static RateLimitWindow Window(IConfigurationSection section, string name, RateLimitWindow defaults)
    {
        var limit = new RateLimitWindow(
            Integer(section, name, nameof(RateLimitWindow.PermitLimit), defaults.PermitLimit),
            Integer(section, name, nameof(RateLimitWindow.WindowSeconds), defaults.WindowSeconds),
            Integer(section, name, nameof(RateLimitWindow.QueueLimit), defaults.QueueLimit));

        if (limit.PermitLimit <= 0)
            throw Invalid(name, nameof(RateLimitWindow.PermitLimit), limit.PermitLimit, "must be greater than zero. A limit cannot be turned off by setting it to zero");
        if (limit.WindowSeconds <= 0)
            throw Invalid(name, nameof(RateLimitWindow.WindowSeconds), limit.WindowSeconds, "must be greater than zero");
        if (limit.QueueLimit < 0)
            throw Invalid(name, nameof(RateLimitWindow.QueueLimit), limit.QueueLimit, "cannot be negative");

        return limit;
    }

    private static int Integer(IConfigurationSection section, string name, string key, int fallback)
    {
        var raw = section[$"{name}:{key}"];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"{Section}:{name}:{key} is '{raw}', which is not a whole number.");
        }

        return value;
    }

    private static InvalidOperationException Invalid(string name, string key, int value, string rule) =>
        new($"{Section}:{name}:{key} is {value}, and it {rule}.");

    private static void Loosened(List<string> warnings, string name, RateLimitWindow configured, RateLimitWindow defaults)
    {
        // More permits, or the same permits over a shorter window. Either lets more attempts through.
        if (configured.PermitLimit > defaults.PermitLimit || configured.WindowSeconds < defaults.WindowSeconds)
        {
            warnings.Add(
                $"{Section}:{name} allows {configured.PermitLimit} requests in {configured.WindowSeconds} seconds, "
              + $"looser than the default of {defaults.PermitLimit} in {defaults.WindowSeconds} "
              + $"seconds. This limit slows down guessing, so raise it only on purpose.");
        }
    }
}
