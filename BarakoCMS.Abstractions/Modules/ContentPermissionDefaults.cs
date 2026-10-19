using Marten;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>The content actions a default can grant.</summary>
public enum ContentAction
{
    Create,
    Read,
    Update,
    Delete,
}

/// <summary>
/// Which seeded roles start with which rights on the content types a module seeds, said once.
/// </summary>
/// <remarks>
/// Without a rule no role can create or read a content type, and only SuperAdmin bypasses that. A
/// module that seeds a type declares its rights here through <see cref="IBarakoModule.ContentPermissions"/>
/// and the host applies them after the module's seed.
///
/// A role is named the way <see cref="CapabilityDefaults"/> names it: by seeded id, with the seeded
/// name beside it, and a role that does not exist is skipped, never created. Tenant scoping is the
/// same too: the role found is the deployment's, so a tenant's own role of the same name is never
/// picked, and the rule names the type's slug as the role's other permissions do.
///
/// A default is a starting point and never an overwrite. A role that already has a permission entry
/// for the type, whoever wrote it and whatever it allows, is left exactly as it is, so an operator
/// who narrowed or widened the rights keeps them across restarts. Nothing is ever taken away. The
/// rules written carry no conditions and no field limits.
/// </remarks>
public sealed class ContentPermissionDefaults
{
    private readonly List<(string Slug, SeededRole Role, ContentAction[] Actions)> _entries;

    private ContentPermissionDefaults(List<(string, SeededRole, ContentAction[])> entries) => _entries = entries;

    /// <summary>A declaration with nothing granted yet. Add to it with <see cref="Allow"/>.</summary>
    public static ContentPermissionDefaults None() => new([]);

    /// <summary>
    /// The same declaration, also letting <paramref name="role"/> perform <paramref name="actions"/>
    /// on the content type with this slug.
    /// </summary>
    public ContentPermissionDefaults Allow(string contentTypeSlug, SeededRole role, params ContentAction[] actions)
    {
        if (string.IsNullOrWhiteSpace(contentTypeSlug))
        {
            throw new ArgumentException("A content type slug is empty.", nameof(contentTypeSlug));
        }

        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(actions);

        if (role.Id == Guid.Empty || string.IsNullOrWhiteSpace(role.Name))
        {
            throw new ArgumentException("A seeded role needs its id and its seeded name.", nameof(role));
        }

        if (actions.Length == 0)
        {
            throw new ArgumentException("No action is named.", nameof(actions));
        }

        return new ContentPermissionDefaults([.. _entries, (contentTypeSlug, role, actions.Distinct().ToArray())]);
    }

    /// <summary>Every (slug, role, actions) this declaration holds.</summary>
    public IReadOnlyList<(string ContentTypeSlug, SeededRole Role, IReadOnlyList<ContentAction> Actions)> Entries =>
        _entries.Select(e => (e.Slug, e.Role, (IReadOnlyList<ContentAction>)e.Actions)).ToList();

    /// <summary>
    /// Adds the permission entries and reports how many roles were changed. The host calls it after
    /// <see cref="IBarakoModule.SeedAsync"/>; a test may call it directly.
    /// </summary>
    /// <remarks>
    /// Seeds an entry only for a role that has none for the type. Does not commit.
    /// </remarks>
    public async Task<int> GrantAsync(IDocumentSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_entries.Count == 0)
        {
            return 0;
        }

        var staged = session.PendingChanges.AllChangedFor<Role>().ToList();
        var changed = new HashSet<Guid>();

        foreach (var (slug, seeded, actions) in _entries)
        {
            var role = await CapabilityDefaults.FindSeededAsync(session, staged, seeded, ct);
            if (role is null)
            {
                continue;
            }

            // Remembered so a second entry for the same role lands on the object already changed.
            if (staged.All(r => r.Id != role.Id))
            {
                staged.Add(role);
            }

            if (role.Permissions.Any(p => string.Equals(p.ContentTypeSlug, slug, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            role.Permissions.Add(Build(slug, actions));
            session.Store(role);
            changed.Add(role.Id);
        }

        return changed.Count;
    }

    private static ContentTypePermission Build(string slug, ContentAction[] actions)
    {
        PermissionRule Rule(ContentAction action) => new() { Enabled = actions.Contains(action) };

        return new ContentTypePermission
        {
            ContentTypeSlug = slug,
            Create = Rule(ContentAction.Create),
            Read = Rule(ContentAction.Read),
            Update = Rule(ContentAction.Update),
            Delete = Rule(ContentAction.Delete),
        };
    }
}
