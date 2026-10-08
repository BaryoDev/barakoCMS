using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Security;

/// <summary>One fixed window: how many requests, over how long, and how many may wait for the next window.</summary>
internal sealed record RateLimitWindow(int PermitLimit, int WindowSeconds, int QueueLimit);

/// <summary>What a named policy counts a request against.</summary>
internal enum RateLimitPartitionBy
{
    /// <summary>The client IP.</summary>
    Ip,

    /// <summary>The signed-in user, whether by token or by API key.</summary>
    User,

    /// <summary>The API key the request authenticated with.</summary>
    ApiKey,
}

/// <summary>A policy defined under <c>RateLimiting:Policies:{name}</c>, which a route names.</summary>
internal sealed record NamedRateLimit(string Name, RateLimitWindow Window, RateLimitPartitionBy PartitionBy);

/// <summary>The rate limits read from the <c>RateLimiting</c> section, validated.</summary>
/// <param name="RendererKey">Null when no renderer key is configured, which means no renderer partition.</param>
/// <param name="Delivery">Null when not configured, which leaves public delivery on the global limit alone.</param>
/// <param name="ApiKey">Null when not configured, which means an API key has no quota of its own.</param>
internal sealed record RateLimitSettings(
    RateLimitWindow Global,
    RateLimitWindow Auth,
    RateLimitWindow Batch,
    RateLimitWindow Registration,
    string? RendererKey,
    RateLimitWindow Renderer,
    RateLimitWindow SiteShare,
    RateLimitWindow? Delivery,
    RateLimitWindow? ApiKey,
    IReadOnlyList<NamedRateLimit> Policies)
{
    public override string ToString() =>
        $"RateLimitSettings {{ Global = {Global}, Auth = {Auth}, Batch = {Batch}, "
      + $"Registration = {Registration}, RendererKey = {(RendererKey is null ? "unset" : "set")}, Renderer = {Renderer}, SiteShare = {SiteShare}, "
      + $"Delivery = {Delivery?.ToString() ?? "unset"}, ApiKey = {ApiKey?.ToString() ?? "unset"}, Policies = [{string.Join(", ", Policies)}] }}";
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
/// key in <see cref="RendererHeader"/> is counted in its own bucket instead. The global limiter
/// honours it, and the delivery limit leaves such a request to that bucket. The auth, telemetry and
/// registration policies stay per IP, since a leaked renderer key must not buy extra password guesses.
/// </para>
/// <para>
/// The site share policy is per tenant and per visitor. barakoPress redeems share links server side,
/// so every visitor of every site it renders arrives from its one IP. With the renderer key it may
/// name the visitor in <see cref="VisitorIpHeader"/>, and then that address is the visitor. Without a
/// matching key the header is ignored and the socket IP is the visitor, so nobody else can pick an
/// address to escape the limit.
/// </para>
/// <para>
/// <c>Delivery</c>, <c>ApiKey</c> and <c>Policies</c> have no default: unset, a route is limited as
/// it was before they existed. A policy under <c>Policies:{name}</c> is one a route names with
/// <c>RequireRateLimiting</c>. One partitioned by IP is counted here. One partitioned by user or by
/// API key, and the API key quota, need a verified caller, and this limiter runs before
/// authentication, so <see cref="RateLimitAfterAuthentication"/> counts those.
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
    public const string LogoutPolicy = "logout";
    public const string DeliveryPolicy = "delivery";
    public const string TlsAskPolicy = "tls-ask";
    public const string EmailPreviewPolicy = "email-preview";

    public const int MaxPolicyNameLength = 64;

    internal const string RendererPartition = "renderer";

    private const string NotCountedHere = "not-counted-here";

    /// <summary>
    /// The policy names this class registers, each with the section that holds its numbers. A
    /// configured policy may not take one of them, in any case: two ways to set one limit would
    /// need a rule for which wins, and the existing sections already are that setting.
    /// </summary>
    private static readonly Dictionary<string, string?> BuiltInPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        [AuthPolicy] = "Auth",
        [BatchPolicy] = "Batch",
        [RegistrationPolicy] = "Registration",
        [SiteSharePolicy] = "SiteShare",
        [DeliveryPolicy] = "Delivery",
        [LogoutPolicy] = null,
        [TlsAskPolicy] = null,
        [EmailPreviewPolicy] = null,
    };

    private static readonly RateLimitWindow OptInDefaults = new(0, 60, 0);

    public static readonly RateLimitWindow DefaultGlobal = new(100, 60, 10);
    public static readonly RateLimitWindow DefaultAuth = new(5, 15 * 60, 0);
    public static readonly RateLimitWindow DefaultBatch = new(20, 60, 0);
    public static readonly RateLimitWindow DefaultRegistration = new(5, 60 * 60, 0);
    public static readonly RateLimitWindow DefaultRenderer = new(1000, 60, 10);
    public static readonly RateLimitWindow DefaultSiteShare = new(10, 60, 0);

    /// <summary>
    /// Fixed rather than configurable: it bounds lookups, it is not a guessing limit, and sharing the
    /// auth bucket meant a logout after a few reloads was refused while the session stayed live.
    /// </summary>
    public static readonly RateLimitWindow Logout = new(30, 60, 0);

    /// <summary>
    /// Fixed, per bucket of asked names (see <see cref="TlsAskPartitionKey"/>). A proxy asks once per
    /// name it has no certificate for, so a real name stays far below this.
    /// </summary>
    public static readonly RateLimitWindow TlsAsk = new(60, 60, 0);

    /// <summary>
    /// Fixed, per IP. A preview renders markdown and reads the entry, the template, its layout and
    /// what the placeholders follow, so it is held under the global limit. It sends nothing.
    /// </summary>
    public static readonly RateLimitWindow EmailPreview = new(30, 60, 0);

    /// <summary>How many buckets the asked names are spread over, which bounds the limiter's partitions.</summary>
    public const int TlsAskBuckets = 4096;

    public const string TlsAskPath = "/api/tenants/tls-ask";

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
            Window(section, "SiteShare", DefaultSiteShare),
            OptInWindow(section, "Delivery"),
            OptInWindow(section, "ApiKey"),
            Policies(section));
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
            // The ask comes from the proxy's one address, so a flood of made-up server names would
            // fill that address's bucket and refuse the real names with it. The tls-ask policy
            // counts it instead, by name.
            if (string.Equals(context.Request.Path.Value, TlsAskPath, StringComparison.OrdinalIgnoreCase))
                return RateLimitPartition.GetNoLimiter(NotCountedHere);

            var partition = GlobalPartitionKey(context, rendererKeyHash);
            var window = partition == RendererPartition ? settings.Renderer : settings.Global;
            return RateLimitPartition.GetFixedWindowLimiter(partition, _ => Options(window));
        });

        options.AddPolicy(AuthPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"auth-{ClientIp(context)}", _ => Options(settings.Auth)));

        options.AddPolicy(LogoutPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"logout-{ClientIp(context)}", _ => Options(Logout)));

        options.AddPolicy(EmailPreviewPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter($"email-preview-{ClientIp(context)}", _ => Options(EmailPreview)));

        options.AddPolicy(DeliveryPolicy, context => DeliveryPartition(context, settings.Delivery, rendererKeyHash));

        options.AddPolicy(TlsAskPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(TlsAskPartitionKey(context), _ => Options(TlsAsk)));

        foreach (var policy in settings.Policies)
        {
            options.AddPolicy(policy.Name, context => policy.PartitionBy == RateLimitPartitionBy.Ip
                ? RateLimitPartition.GetFixedWindowLimiter($"policy|{policy.Name}|{ClientIp(context)}", _ => Options(policy.Window))
                : RateLimitPartition.GetNoLimiter(NotCountedHere));
        }

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
            await WithCorsHeaders(context.HttpContext);
            await Reject(context.HttpContext, context.Lease, cancellationToken);
        };
    }

    internal static async Task Reject(HttpContext context, RateLimitLease lease, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // A fixed window reports its whole window, not the time left in it: never too short, and
        // the limiter does not say when the current window started, so it is passed on as it is.
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                ((long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.Response.WriteAsync("Too many requests. Please try again later.", cancellationToken);
    }

    /// <summary>
    /// Puts the CORS headers the request's origin is allowed on a response this limiter refuses.
    /// </summary>
    /// <remarks>
    /// The limiter runs before the CORS middleware, so a refused request never reaches it, and a
    /// browser on another origin would read the 429 as a network error. Moving CORS ahead of the
    /// limiter would let a preflight be answered without being counted, so the same policy is
    /// applied here instead, through the same provider: a tenant domain origin still gets no
    /// credentials, and an origin nobody allowed still gets no allow header.
    /// <see cref="RateLimitAfterAuthentication"/> runs after the CORS middleware and needs none of this.
    /// </remarks>
    internal static async Task WithCorsHeaders(HttpContext context)
    {
        var services = context.RequestServices;
        if (services.GetService<ICorsPolicyProvider>() is not { } provider || services.GetService<ICorsService>() is not { } cors)
            return;

        TenantDomainCorsPolicyProvider.VaryByOrigin(context);

        if (!context.Request.Headers.ContainsKey(HeaderNames.Origin))
            return;

        var policy = await provider.GetPolicyAsync(context, TenantDomainCorsPolicyProvider.PolicyName);
        if (policy is null)
            return;

        cors.ApplyResult(cors.EvaluatePolicy(context, policy), context.Response);
    }

    /// <summary>
    /// Stops the host when a route names a rate limit policy nobody registered, naming both.
    /// </summary>
    /// <remarks>
    /// The framework only finds this out on the first request to the route, and answers every
    /// request to it with an error from then on. Every route mapped so far is checked, whichever
    /// assembly it came from. Routes the host maps after <c>UseBarakoCMS</c> returns are not seen here.
    /// </remarks>
    public static void RequireRegisteredPolicies(IApplicationBuilder app)
    {
        // The same cast UseFastEndpoints makes earlier in UseBarakoCMS, so it holds by now.
        var routes = (IEndpointRouteBuilder)app;

        // The options are built here for the first time, which is where two registrations of one
        // policy name surface.
        RateLimiterOptions options;
        try
        {
            options = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimiterOptions>>().Value;
        }
        catch (ArgumentException duplicate) when (duplicate is not ArgumentNullException && duplicate.ParamName == "policyName")
        {
            throw DuplicatePolicy(duplicate, Read(app.ApplicationServices.GetRequiredService<IConfiguration>()));
        }

        RequireRegisteredPolicies(routes.DataSources.SelectMany(source => source.Endpoints), options);
    }

    /// <summary>
    /// Says which setting or registration to rename when two of them add a policy of one name.
    /// </summary>
    /// <remarks>
    /// The core, a module and the host all add policies to the same options, in registration order,
    /// and the framework refuses the second with a message that names no setting. Whichever came
    /// second, the name is in that message, so it is matched here against the names the core adds.
    /// <c>delivery</c> gets its own wording because a host that registered that name itself started
    /// before the core took it.
    /// </remarks>
    internal static InvalidOperationException DuplicatePolicy(ArgumentException duplicate, RateLimitSettings settings)
    {
        bool Names(string name) => duplicate.Message.Contains($"the name {name}. (", StringComparison.Ordinal);

        if (settings.Policies.FirstOrDefault(policy => Names(policy.Name)) is { } configured)
        {
            return new InvalidOperationException(
                $"{Section}:Policies:{configured.Name} has the name of a rate limit policy that a module or the host "
              + "registers in code. Give the setting another name.", duplicate);
        }

        if (Names(DeliveryPolicy))
        {
            return new InvalidOperationException(
                $"A module or the host registers its own rate limit policy named '{DeliveryPolicy}'. The core now "
              + $"registers that name for the public delivery routes ({Section}:Delivery), so it is reserved from this "
              + "release on. Rename the policy registered in code, and the routes that name it.", duplicate);
        }

        return new InvalidOperationException(
            $"Two registrations add a rate limit policy of the same name. {duplicate.Message} The core registers "
          + $"{string.Join(", ", BuiltInPolicies.Keys)} and every name under {Section}:Policies.", duplicate);
    }

    internal static void RequireRegisteredPolicies(IEnumerable<Endpoint> endpoints, RateLimiterOptions options)
    {
        var registered = new Dictionary<string, bool>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var name = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            if (name is null)
            {
                continue;
            }

            if (!registered.TryGetValue(name, out var known))
            {
                known = IsRegistered(options, name);
                registered[name] = known;
            }

            if (!known)
            {
                var route = endpoint is RouteEndpoint routed ? routed.RoutePattern.RawText : endpoint.DisplayName;
                missing.Add($"'{route ?? "a route"}' names '{name}'");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "A route names a rate limit policy that does not exist: " + string.Join("; ", missing.Distinct()) + ". "
              + $"Define it under {Section}:Policies with the same spelling, case included, or register it in code.");
        }
    }

    /// <summary>
    /// The options keep their policies private, and a module registers its own on the same options.
    /// Adding a name that is taken throws, so that is the question asked. A name that was free is
    /// now a policy with no limit, which is why the caller stops the host for it.
    /// </summary>
    private static bool IsRegistered(RateLimiterOptions options, string name)
    {
        try
        {
            options.AddPolicy(name, _ => RateLimitPartition.GetNoLimiter(NotCountedHere));
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// No limit when delivery is not configured, and none for the renderer: its reads for every
    /// site come from one IP and are already counted in the renderer bucket. Otherwise the client IP.
    /// </summary>
    internal static RateLimitPartition<string> DeliveryPartition(HttpContext context, RateLimitWindow? delivery, byte[]? rendererKeyHash) =>
        delivery is { } window && !HasRendererKey(context, rendererKeyHash)
            ? RateLimitPartition.GetFixedWindowLimiter($"delivery|{ClientIp(context)}", _ => Options(window))
            : RateLimitPartition.GetNoLimiter(NotCountedHere);

    /// <summary>
    /// The bucket of the name a TLS ask is about, not of the caller: a flood of made-up names then
    /// spreads over <see cref="TlsAskBuckets"/> buckets and leaves a real name's bucket nearly empty.
    /// </summary>
    /// <remarks>
    /// The string hash is seeded per process, so which names share a bucket cannot be worked out in
    /// advance, only probed for. A value that is not a host goes to one bucket of its own, since every
    /// such ask is answered no.
    /// </remarks>
    internal static string TlsAskPartitionKey(HttpContext context)
    {
        var host = barakoCMS.Infrastructure.Multitenancy.TenantDomainLookup.BareHost(context.Request.Query["domain"].ToString());
        var name = barakoCMS.Infrastructure.Multitenancy.TenantDomainMap.Normalise(host);
        return name is null
            ? "tls-ask|not-a-host"
            : $"tls-ask|{(uint)name.GetHashCode(StringComparison.Ordinal) % TlsAskBuckets}";
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

    internal static FixedWindowRateLimiterOptions Options(RateLimitWindow window) => new()
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

    /// <summary>
    /// A limit with no default. Null when nothing under it is set. A window or a queue with no
    /// <c>PermitLimit</c> is refused, since it would read as a limit that is on while it is off.
    /// </summary>
    private static RateLimitWindow? OptInWindow(IConfigurationSection section, string name)
    {
        string[] keys = [nameof(RateLimitWindow.PermitLimit), nameof(RateLimitWindow.WindowSeconds), nameof(RateLimitWindow.QueueLimit)];
        if (keys.All(key => string.IsNullOrWhiteSpace(section[$"{name}:{key}"])))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(section[$"{name}:{nameof(RateLimitWindow.PermitLimit)}"]))
        {
            throw new InvalidOperationException(
                $"{Section}:{name}:{nameof(RateLimitWindow.PermitLimit)} is not set, but another value under "
              + $"{Section}:{name} is. The limit is off until {nameof(RateLimitWindow.PermitLimit)} is set, so set it or remove the rest.");
        }

        return Window(section, name, OptInDefaults);
    }

    private static IReadOnlyList<NamedRateLimit> Policies(IConfigurationSection section)
    {
        var policies = new List<NamedRateLimit>();

        foreach (var child in section.GetSection("Policies").GetChildren())
        {
            var name = child.Key;
            if (name.Length > MaxPolicyNameLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                throw new InvalidOperationException(
                    $"{Section}:Policies has a policy whose name cannot be used. A name is up to {MaxPolicyNameLength} "
                  + "letters, digits, dashes, underscores and dots.");
            }

            if (BuiltInPolicies.TryGetValue(name, out var owner))
            {
                throw new InvalidOperationException(
                    $"{Section}:Policies:{name} has the name of a built-in policy. "
                  + (owner is null ? "That one is fixed and has no setting." : $"Set its numbers under {Section}:{owner} instead."));
            }

            var path = $"Policies:{name}";
            var window = OptInWindow(section, path) ?? throw new InvalidOperationException(
                $"{Section}:{path}:{nameof(RateLimitWindow.PermitLimit)} is not set. A policy needs a limit.");

            policies.Add(new NamedRateLimit(name, window, PartitionBy(section, path)));
        }

        return policies;
    }

    private static RateLimitPartitionBy PartitionBy(IConfigurationSection section, string path)
    {
        var raw = section[$"{path}:Partition"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RateLimitPartitionBy.Ip;
        }

        // TryParse also reads "7" as a value with no name, so the name has to be one of the three.
        if (!Enum.TryParse<RateLimitPartitionBy>(raw.Trim(), ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new InvalidOperationException(
                $"{Section}:{path}:Partition is '{raw}'. Use {nameof(RateLimitPartitionBy.Ip)}, "
              + $"{nameof(RateLimitPartitionBy.User)} or {nameof(RateLimitPartitionBy.ApiKey)}.");
        }

        return value;
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
