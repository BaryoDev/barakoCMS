using Marten;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>
/// How a module gives its own capabilities to the roles that already reached its endpoints.
/// </summary>
/// <remarks>
/// A module's endpoints gate on capabilities the module declares, and core cannot name them: core
/// does not reference a module, and a third-party one is not in this repository at all. So
/// <see cref="SystemCapabilities.DefaultsFor"/> cannot include them, and without something here a
/// migrated module would be reachable only through the legacy role-name fallback. Turning that
/// fallback off, which is the point of issue #443, would take every module away from every Admin.
///
/// Call this from <c>SeedAsync</c>. The module names the roles that could already reach it, which
/// are the same names its old <c>Roles(...)</c> gate listed, so a migration grants exactly what was
/// already reachable and widens nothing. SuperAdmin is deliberately not among them: it holds
/// <see cref="SystemCapabilities.All"/>, which satisfies a capability added after the role was
/// written, including one from a module core has never heard of.
///
/// Additive and idempotent. A role that already holds the capability is not rewritten, so a restart
/// is not a write, and a capability the module adds is not removed by core's own backfill, which
/// unions its defaults rather than replacing the list.
/// </remarks>
public static class ModuleCapabilities
{
    /// <summary>
    /// Grants <paramref name="capabilities"/> to each named role that exists, and reports how many
    /// roles were changed.
    /// </summary>
    /// <remarks>
    /// A role that does not exist is skipped rather than created. A module does not know whether the
    /// host seeded the system roles at all, and inventing an "Admin" on a deployment that
    /// deliberately has none would be a module granting itself access to a role nobody made.
    ///
    /// Does not commit: the host calls <c>SaveChangesAsync</c> once the seed returns, which is what
    /// keeps a module's seed all-or-nothing.
    /// </remarks>
    public static async Task<int> GrantAsync(
        IDocumentSession session,
        IReadOnlyCollection<string> roleNames,
        IReadOnlyCollection<string> capabilities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(roleNames);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (roleNames.Count == 0 || capabilities.Count == 0)
        {
            return 0;
        }

        var changed = 0;

        foreach (var roleName in roleNames)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == roleName, ct);
            if (role is null)
            {
                continue;
            }

            var missing = capabilities
                .Where(c => !role.SystemCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count == 0)
            {
                continue;
            }

            role.SystemCapabilities = [.. role.SystemCapabilities, .. missing];
            session.Store(role);
            changed++;
        }

        return changed;
    }
}
