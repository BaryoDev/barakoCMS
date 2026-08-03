namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Builds the Content-Security-Policy value for the global security-headers middleware. Extracted
/// from <c>ServiceCollectionExtensions.UseBarakoCMS</c> so the Development-vs-other-environment
/// choice is unit-testable without standing up the app.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// script-src drops 'unsafe-inline' outside Development — that's the directive that actually
    /// defeats XSS mitigation (an attacker-injected &lt;script&gt; tag just won't execute). style-src
    /// keeps 'unsafe-inline' everywhere for now: a much lower-severity gap (CSS can exfiltrate via
    /// selectors but can't run arbitrary JS), and this pass couldn't verify whether
    /// AspNetCore.HealthChecks.UI's dashboard needs it without a live Postgres to test against — a
    /// partial fix, not the full nonce-based one the roadmap eventually wants. Development keeps the
    /// fully permissive policy because Swagger UI only ever mounts there
    /// (<c>env == "Development"</c> in <c>UseBarakoCMS</c>), never in a deployed environment.
    /// </summary>
    public static string ContentSecurityPolicy(string? env) => env == "Development"
        ? "default-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;"
        : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;";
}
