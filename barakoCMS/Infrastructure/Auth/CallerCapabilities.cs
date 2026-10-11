using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;

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

        if (!Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)) return false;

        return await resolver.HasCapabilityAsync(userId, capability, ct);
    }
}
