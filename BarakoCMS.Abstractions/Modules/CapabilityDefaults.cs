using Marten;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>
/// Which seeded roles start with which capabilities, said once.
/// </summary>
/// <remarks>
/// A module holds one of these beside its capability names. Its seed grants from it and its
/// endpoints take their legacy role list from it, so who gets a default is changed in one place.
///
/// A role is named by the id it is seeded under, with the seeded name beside it. The id is what
/// finds the role, so a seeded role that was renamed keeps its defaults. The name finds it in a
/// database where no role holds the id.
///
/// SuperAdmin is never named here: it holds <see cref="SystemCapabilities.All"/>, which satisfies
/// a capability from a module core has never heard of.
/// </remarks>
public sealed class CapabilityDefaults
{
    private CapabilityDefaults(IReadOnlyList<string> capabilities, IReadOnlyList<SeededRole> roles)
    {
        Capabilities = capabilities;
        Roles = roles;
        LegacyRoles = SystemRoles.LegacyNames(roles.ToArray());
    }

    /// <summary>Starts a declaration for these capabilities. Nothing is granted until a role is named.</summary>
    public static CapabilityDefaults For(params string[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (capabilities.Any(c => string.IsNullOrWhiteSpace(c)))
        {
            throw new ArgumentException("A capability name is empty.", nameof(capabilities));
        }

        return new CapabilityDefaults(capabilities.ToArray(), Array.Empty<SeededRole>());
    }

    /// <summary>
    /// The same capabilities, granted to these roles as well. Pass <see cref="SystemRoles.Admin"/>
    /// for the seeded Admin role, or a <see cref="SeededRole"/> of your own for a role the module
    /// seeds.
    /// </summary>
    public CapabilityDefaults GrantedTo(params SeededRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Name)))
        {
            throw new ArgumentException("A seeded role needs its id and its seeded name.", nameof(roles));
        }

        return new CapabilityDefaults(Capabilities, Roles.Concat(roles).ToArray());
    }

    /// <summary>The capabilities each role in <see cref="Roles"/> starts with.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>The seeded roles that start with them.</summary>
    public IReadOnlyList<SeededRole> Roles { get; }

    /// <summary>
    /// The role names a gate on these capabilities honours while <c>Auth:LegacyRoleFallback</c> is
    /// on: the seeded name of each role, then SuperAdmin.
    /// </summary>
    public IReadOnlyList<string> LegacyRoles { get; }

    /// <summary>
    /// Grants the capabilities to each declared role that exists, and reports how many roles were
    /// changed. Call it from <c>SeedAsync</c>.
    /// </summary>
    /// <remarks>
    /// The role is the one holding the seeded id, whatever it is called now. Where no role holds
    /// the id, it is the role carrying the seeded name, so a database that holds the role under
    /// another id is granted as it was when grants went by name. Where neither exists nothing is
    /// created: a module does not know whether the host seeded the system roles at all, and
    /// inventing an "Admin" on a deployment that deliberately has none would be a module granting
    /// itself access to a role nobody made.
    ///
    /// A role stored on this session and not saved yet counts, found among the session's pending
    /// changes, so a module can create its own role and grant to it in the same seed.
    ///
    /// Additive and idempotent. Nothing is ever taken off a role, a role the declaration does not
    /// name is not touched, and a role that already holds the capabilities is not rewritten, so a
    /// restart is not a write. It runs on every start, so a default taken off a seeded role comes
    /// back on the next one, as core's own defaults do.
    ///
    /// Does not commit: the host calls <c>SaveChangesAsync</c> once the seed returns, which is what
    /// keeps a module's seed all-or-nothing.
    /// </remarks>
    public async Task<int> GrantAsync(IDocumentSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Capabilities.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        var seen = new HashSet<Guid>();

        // A role the same seed stored a moment ago is not in the database yet, and a lightweight
        // session reads the database. Without this, a module that creates a role and grants to it
        // in one seed left the role empty until the next start.
        var staged = session.PendingChanges.AllChangedFor<Role>().ToList();

        foreach (var seeded in Roles)
        {
            var name = seeded.Name;
            var role = staged.FirstOrDefault(r => r.Id == seeded.Id)
                       ?? await session.LoadAsync<Role>(seeded.Id, ct)
                       ?? staged.FirstOrDefault(r => r.Name == name)
                       ?? await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, ct);

            if (role is null || !seen.Add(role.Id))
            {
                continue;
            }

            var held = role.SystemCapabilities ?? [];
            var missing = Capabilities
                .Where(c => !held.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count == 0)
            {
                continue;
            }

            role.SystemCapabilities = [.. held, .. missing];
            session.Store(role);
            changed++;
        }

        return changed;
    }
}
