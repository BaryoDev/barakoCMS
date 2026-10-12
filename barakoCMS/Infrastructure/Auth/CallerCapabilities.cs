using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Scoped, because <see cref="IPermissionResolver"/> answers for the current tenant and the
/// tenant is per request.
/// </summary>
internal sealed class CallerCapabilities(IPermissionResolver resolver) : ICallerCapabilities
{
    public async Task<bool> HoldsAsync(ClaimsPrincipal caller, string capability, CancellationToken ct = default)
    {
        if (caller.Identity?.IsAuthenticated != true) return false;

        // A station holds exactly the list on its key. It has no user id and no roles to resolve.
        // Its key is checked against the store on every HTTP request; the hub checks it again at
        // Subscribe, because a hub principal is built once when the connection opens.
        if (caller.HasClaim("auth_method", "station"))
            // Names only. "*" is never a wildcard here, so a hand-edited station document cannot
            // become all-powerful: creation refuses it, and this does not honour it either.
            return caller.FindAll(Stations.StationAuthenticationHandler.CapabilityClaim)
                .Any(c => c.Value != SystemCapabilities.All
                          && string.Equals(c.Value, capability, StringComparison.OrdinalIgnoreCase));

        if (!Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)) return false;

        return await resolver.HasCapabilityAsync(userId, capability, ct);
    }
}
