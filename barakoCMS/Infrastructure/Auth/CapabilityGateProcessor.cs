using barakoCMS.Infrastructure.Services;
using FastEndpoints;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Enforces the capability gate an endpoint declares with
/// <see cref="CapabilityGate.RequireCapability"/>. Runs for every request and does nothing unless the
/// endpoint carries <see cref="RequiredCapability"/> metadata.
/// </summary>
/// <remarks>
/// The lookup is per request rather than a capability claim in the token. A claim would be stale for
/// as long as the token lives (15 minutes), so revoking a capability during an incident would not
/// take, and the operator would have no way to tell. <see cref="CachedPermissionResolver"/> absorbs
/// the cost, and it already evicts on the role and membership changes that can alter the answer.
/// See issue #272.
/// </remarks>
public sealed class CapabilityGateProcessor : IGlobalPreProcessor
{
    /// <summary>
    /// Whether the pre-capability role names still open the gate. Default off since 4.0.
    /// </summary>
    /// <remarks>
    /// It was on through 3.x so a deployment kept working across the upgrade while its roles had no
    /// capabilities yet. 4.0 is a major, every core and module endpoint now gates on a capability
    /// (#443), and the seeder adds capabilities a role is missing rather than only filling an empty
    /// list (#488), so a seeded deployment reaches everything it used to without the fallback.
    ///
    /// A deployment whose roles are curated by hand, or one mid-upgrade, sets it back to true and
    /// nothing changes for it. What changed is which way it points when nobody says.
    /// </remarks>
    public const string LegacyRoleFallbackKey = "Auth:LegacyRoleFallback";

    /// <summary>
    /// Read from the request's own services rather than captured in the constructor.
    /// </summary>
    /// <remarks>
    /// FastEndpoints keeps global pre-processors in process-wide configuration, so the first host
    /// built in a process supplies the instance every later host uses. Capturing the flag at
    /// construction therefore froze it to whichever host started first, which is invisible in
    /// production (one host per process) and wrong in a test suite that builds a second host to
    /// exercise the other setting: the derived host's own instance held the right value and a
    /// request through it was still judged by the first host's.
    ///
    /// Per request it is a dictionary lookup on an already-built configuration, and it means a host
    /// that says the fallback is on is a host where it is on.
    /// </remarks>
    private static bool LegacyRoleFallback(HttpContext http) =>
        http.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue(LegacyRoleFallbackKey, false);

    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;

        // An earlier pre-processor may already have refused this request and written its body.
        if (http.Response.HasStarted) return;

        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequiredCapability>();
        if (required is null) return;

        var principal = http.User;

        // Authentication is FastEndpoints' job and it has already run. An unauthenticated caller
        // reaching here means the endpoint allows anonymous, and refusing it would be this
        // pre-processor inventing a gate the endpoint did not declare.
        if (principal.Identity?.IsAuthenticated != true) return;

        if (required.LegacyRoles.Any(principal.IsInRole) && LegacyRoleFallback(http)) return;

        if (!Guid.TryParse(principal.FindFirst("UserId")?.Value, out var userId))
        {
            await Deny(http, required.Capability, ct);
            return;
        }

        var resolver = http.RequestServices.GetRequiredService<IPermissionResolver>();
        if (await resolver.HasCapabilityAsync(userId, required.Capability, ct)) return;

        await Deny(http, required.Capability, ct);
    }

    private static async Task Deny(HttpContext http, string capability, CancellationToken ct)
    {
        http.Response.StatusCode = 403;
        // Writing the body short-circuits the endpoint (setting the status alone would not).
        await http.Response.WriteAsync($"This caller is missing the '{capability}' capability.", ct);
    }
}
