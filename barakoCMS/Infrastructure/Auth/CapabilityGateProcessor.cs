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
    /// <summary>Whether the pre-capability role names still open the gate. Default on.</summary>
    public const string LegacyRoleFallbackKey = "Auth:LegacyRoleFallback";

    private readonly bool _legacyRoleFallback;

    public CapabilityGateProcessor(IConfiguration configuration)
    {
        _legacyRoleFallback = configuration.GetValue(LegacyRoleFallbackKey, true);
    }

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

        if (_legacyRoleFallback && required.LegacyRoles.Any(principal.IsInRole)) return;

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
