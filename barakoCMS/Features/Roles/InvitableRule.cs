using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>
/// A role an invite may grant holds nothing beyond membership: no system capability, not a platform
/// role, not a seeded one. Checked when the flag is set, when a capability is added to a flagged
/// role, and again when an invite names the role.
/// </summary>
internal static class InvitableRule
{
    public const string Message =
        "A role that can be granted by an invite cannot be a platform or built-in role or hold a system capability.";

    public static bool Allows(Guid id, bool isPlatform, IEnumerable<string>? capabilities) =>
        !isPlatform && !SystemRoles.Contains(id) && !(capabilities ?? []).Any();

    public static bool Allows(Role role) => Allows(role.Id, role.IsPlatform, role.SystemCapabilities);
}
