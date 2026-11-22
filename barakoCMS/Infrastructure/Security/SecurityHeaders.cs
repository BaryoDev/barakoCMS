namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Builds the Content-Security-Policy value for the global security-headers middleware. Extracted
/// from <c>ServiceCollectionExtensions.UseBarakoCMS</c> so the Development-vs-other-environment
/// choice is unit-testable without standing up the app.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The health-checks dashboard, which is the one thing this host serves that needs inline
    /// styles. Its <c>ApiPath</c> is <c>/health-ui-api</c>, a sibling rather than a child, so the
    /// match is a prefix rather than a path-segment match.
    /// </summary>
    private const string HealthDashboardPrefix = "/health-ui";

    /// <summary>
    /// script-src drops 'unsafe-inline' outside Development, which is the directive that actually
    /// defeats XSS mitigation (an attacker-injected &lt;script&gt; tag just won't execute). style-src
    /// drops it too: nothing this host serves outside Development emits an inline style except the
    /// health dashboard, which gets its own policy below. Development keeps the fully permissive
    /// policy because Swagger UI only ever mounts there (<c>env == "Development"</c> in
    /// <c>UseBarakoCMS</c>), never in a deployed environment.
    ///
    /// The Next.js admin is a separate application with its own host and its own headers, so this
    /// value never reaches it.
    /// </summary>
    public static string ContentSecurityPolicy(string? env) => env == "Development"
        ? "default-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;"
        : "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:;" + LockedDownDirectives;

    /// <summary>
    /// Directives that do not fall back to default-src and that this API never needs relaxed: no
    /// page may frame it (the same answer as X-Frame-Options DENY), a relative URL cannot be
    /// re-based, a form posts only to this origin, and no plugin content loads. Added outside
    /// Development only, so Swagger UI keeps the policy it has today.
    /// </summary>
    public const string LockedDownDirectives = " frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none';";

    /// <summary>
    /// Features this API never uses, switched off for it and for anything it frames or is framed by.
    /// </summary>
    public const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), "
      + "payment=(), usb=()";

    /// <summary>Opener isolation: a page that opens this API in a window gets no reference back to it.</summary>
    public const string CrossOriginOpenerPolicy = "same-origin";

    /// <summary>
    /// The default for a response. Stored files are the exception: a site on another origin loads
    /// them with <c>&lt;img&gt;</c> or <c>&lt;video&gt;</c>, which a same-origin policy would block, so
    /// the file routes set <see cref="CrossOriginResourcePolicyForFiles"/> themselves and the
    /// middleware leaves a value already set alone.
    /// </summary>
    public const string CrossOriginResourcePolicy = "same-origin";

    /// <summary>What the file download routes send, so media loads from a front end on another origin.</summary>
    public const string CrossOriginResourcePolicyForFiles = "cross-origin";

    /// <summary>
    /// The dashboard's shipped bundle renders three dozen React <c>style</c> props, so every one of
    /// those elements carries an inline style attribute and the page renders wrong without
    /// 'unsafe-inline'. The allowance is scoped to that path instead of the whole app, and the
    /// middleware only reaches for it when <c>HealthChecksUI:Enabled</c> is on.
    /// </summary>
    public static string HealthDashboardContentSecurityPolicy(string? env) => env == "Development"
        ? ContentSecurityPolicy(env)
        : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;" + LockedDownDirectives;

    public static bool IsHealthDashboardPath(string? path) =>
        path is not null && path.StartsWith(HealthDashboardPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Routes whose responses carry a token, a key or the caller's own details: sign-in, refresh,
    /// MFA and OTP, social sign-in, <c>/api/me</c>, API key creation, preview tokens and the two
    /// anonymous share link routes, whose answers depend on a key in the body and can hold an
    /// unpublished entry. These are sent <c>Cache-Control: no-store</c> so neither a browser nor a
    /// proxy keeps them (#654), whatever the status, a 429 from the rate limiter included.
    /// </summary>
    /// <remarks>
    /// <c>/api/meta/describe</c> is here for the last of those reasons: its body differs by what the
    /// caller holds, and a grant can be taken away. <c>/api/meta</c> itself is not matched by it.
    /// </remarks>
    private static readonly string[] NoStorePrefixes =
    [
        "/api/auth", "/api/me", "/api/api-keys", "/api/preview", "/api/meta/describe",
        "/api/public/site/share-links", "/api/viewer-tokens",
    ];

    public static bool IsNoStorePath(string? path) =>
        path is not null && NoStorePrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == prefix.Length || path[prefix.Length] == '/'));
}
