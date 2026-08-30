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
        : "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:;";

    /// <summary>
    /// The dashboard's shipped bundle renders three dozen React <c>style</c> props, so every one of
    /// those elements carries an inline style attribute and the page renders wrong without
    /// 'unsafe-inline'. The allowance is scoped to that path instead of the whole app, and the
    /// middleware only reaches for it when <c>HealthChecksUI:Enabled</c> is on.
    /// </summary>
    public static string HealthDashboardContentSecurityPolicy(string? env) => env == "Development"
        ? ContentSecurityPolicy(env)
        : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;";

    public static bool IsHealthDashboardPath(string? path) =>
        path is not null && path.StartsWith(HealthDashboardPrefix, StringComparison.OrdinalIgnoreCase);
}
