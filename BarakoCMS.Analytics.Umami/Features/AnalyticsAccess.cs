using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using Marten;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Analytics.Umami.Features;

/// <summary>
/// The deployment has one Umami account, every website in it is readable by id, and creating one
/// uses the deployment's credentials. None of that is scoped to a tenant, so every analytics route
/// needs its capability from a global role, not from a tenant membership.
/// </summary>
internal static class AnalyticsAccess
{
    public static Task<bool> AllowedAsync(
        IQuerySession session, ClaimsPrincipal user, IConfiguration configuration, string capability, CancellationToken ct) =>
        PlatformScope.HoldsGloballyAsync(session, user, configuration, capability, AnalyticsCapabilities.LegacyRoles, ct);
}
