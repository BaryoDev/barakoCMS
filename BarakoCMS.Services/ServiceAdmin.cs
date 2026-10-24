using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using Marten;

namespace BarakoCMS.Services;

/// <summary>
/// The registry is one deployment-wide table and a service's URLs are not any tenant's, so reading
/// and editing it is for a caller holding manage_services through a global role, not through a
/// tenant membership.
/// </summary>
internal static class ServiceAdmin
{
    public static Task<bool> AllowedAsync(
        IQuerySession session, ClaimsPrincipal user, IConfiguration configuration, CancellationToken ct) =>
        PlatformScope.HoldsGloballyAsync(session, user, configuration,
            ServicesCapabilities.ManageServices, ServicesCapabilities.Defaults.LegacyRoles, ct);

    /// <summary>A name is a route segment, so it stays to characters that need no escaping.</summary>
    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();
}
