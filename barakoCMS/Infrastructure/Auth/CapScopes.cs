using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// The one rule for which <c>cap:</c> scopes exist. A <c>cap:&lt;name&gt;</c> scope lets a key reach
/// an endpoint outside the content API that is gated on that capability.
/// </summary>
internal static class CapScopes
{
    /// <summary>
    /// Capabilities a <c>cap:</c> scope can never name. A leaked key must not escalate into
    /// administration, so the platform capabilities, managing API keys and service principals, and
    /// managing users, roles, memberships and tenants stay behind a person's login. This list is
    /// checked when a key is created and again on every request, so a key stored before the list
    /// grew, or edited by hand, reaches nothing new.
    /// </summary>
    private static readonly IReadOnlySet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SystemCapabilities.All,
        SystemCapabilities.ManageApiKeys,
        SystemCapabilities.ManageRoles,
        SystemCapabilities.ManagePlatformRoles,
        SystemCapabilities.ManageTenants,
        SystemCapabilities.ManageTenantMembers,
        SystemCapabilities.InviteTenantMembers,
        SystemCapabilities.ManageUsers,
        SystemCapabilities.ManageUserMembership,
        SystemCapabilities.ManageUserGroups,
        SystemCapabilities.ManageEmailSettings,
        SystemCapabilities.ViewHidden,
    };

    public static bool IsDenied(string capability) =>
        Denied.Contains(capability) || Features.Users.PlatformRoles.PlatformCapabilities.Contains(capability);

    /// <summary>Why a <c>cap:</c> scope is refused at creation, or null when it is allowed.</summary>
    public static string? Refusal(string scope, CapabilityVocabulary vocabulary)
    {
        var capability = ApiKeyScopes.CapabilityOf(scope);
        if (string.IsNullOrEmpty(capability))
            return $"'{scope}' names no capability.";
        if (IsDenied(capability))
            return $"'{scope}' is not allowed on an API key. Administration stays behind a person's login.";
        return vocabulary.IsKnown(capability) ? null : $"Unknown capability in scope '{scope}'.";
    }

    /// <summary>Whether the key's scopes include <c>cap:&lt;capability&gt;</c> and the capability may be named.</summary>
    public static bool Grants(IEnumerable<string> scopes, string capability) =>
        !IsDenied(capability)
        && scopes.Any(s => ApiKeyScopes.CapabilityOf(s) is { } named
                           && string.Equals(named, capability, StringComparison.OrdinalIgnoreCase));
}
