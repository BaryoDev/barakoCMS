using System.Security.Claims;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Whether the signed-in caller holds a capability right now. One answer for endpoints, the change
/// feed and the realtime hub, whatever kind of credential the caller signed in with.
/// </summary>
/// <remarks>Looked up per call, not read from a token claim, so a revoked capability stops working on the next request.</remarks>
public interface ICallerCapabilities
{
    Task<bool> HoldsAsync(ClaimsPrincipal caller, string capability, CancellationToken ct = default);
}
