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
    RateLimitWindow Renderer)
{
    public override string ToString() =>
        $"RateLimitSettings {{ Global = {Global}, Auth = {Auth}, Batch = {Batch}, "
      + $"Registration = {Registration}, RendererKey = {(RendererKey is null ? "unset" : "set")}, Renderer = {Renderer} }}";
}

/// <summary>
/// Reads the <c>RateLimiting</c> section and builds the global limiter and the auth, telemetry and
/// registration policies from it.
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
/// </remarks>
internal static class RateLimitSetup
{
    public const string Section = "RateLimiting";
    public const string RendererHeader = "X-Barako-Renderer-Key";
    public const int RendererKeyMinLength = 32;

    public const string AuthPolicy = "auth";
    public const string BatchPolicy = "telemetry";
    public const string RegistrationPolicy = "registration";

    internal const string RendererPartition = "renderer";

    public static readonly RateLimitWindow DefaultGlobal = new(100, 60, 10);
    public static readonly RateLimitWindow DefaultAuth = new(5, 15 * 60, 0);
    public static readonly RateLimitWindow DefaultBatch = new(20, 60, 0);
    public static readonly RateLimitWindow DefaultRegistration = new(5, 60 * 60, 0);
    public static readonly RateLimitWindow DefaultRenderer = new(1000, 60, 10);

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
            Window(section, "Renderer", DefaultRenderer));
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
        if (rendererKeyHash is not null
            && context.Request.Headers.TryGetValue(RendererHeader, out var presented)
            && presented.Count == 1
            && !string.IsNullOrEmpty(presented[0])
            && CryptographicOperations.FixedTimeEquals(Hash(presented[0]!), rendererKeyHash))
        {
            return RendererPartition;
        }

        return ClientIp(context);
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
