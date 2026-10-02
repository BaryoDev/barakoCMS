using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core;

/// <summary>
/// The stored form of <see cref="FieldDefinition.VisibleToRoles"/>: role ids, written as text.
/// </summary>
/// <remarks>
/// The list used to hold role names, so renaming a role changed who could read the field. Anything
/// that writes a list a caller or a file supplied (create, add field, the sensitivity endpoint, a
/// blueprint, an import) goes through <see cref="ToIdsAsync"/>, which swaps each name for the id
/// of the role that carries it. A writer that stores a definition back with its lists as it loaded
/// them leaves them as they are. A name no role carries is kept as it is and still matches a role of
/// that name on read, which is how a definition stored before this, or imported from elsewhere,
/// keeps working.
///
/// Anything that sends a definition out goes through <see cref="ToNamesAsync"/>. The API answered
/// with names before and a client sends back what it was given, and an id means nothing to the
/// instance a bundle is imported into.
///
/// Names are matched exactly, case included, the way the role claim was.
/// </remarks>
public static class RoleReferences
{
    private const int NamesPerQuery = 500;

    /// <summary>Whether an entry is a role id rather than a role name.</summary>
    public static bool IsId(string? entry, out Guid id) => Guid.TryParse(entry, out id);

    /// <summary>Swaps every role name in the fields' lists for that role's id.</summary>
    public static async Task ToIdsAsync(
        IQuerySession session, IEnumerable<FieldDefinition> fields, CancellationToken ct = default)
    {
        var listed = fields.Where(f => f is { VisibleToRoles.Count: > 0 }).ToList();
        if (listed.Count == 0)
            return;

        var names = listed
            .SelectMany(f => f.VisibleToRoles)
            .Where(entry => !string.IsNullOrWhiteSpace(entry) && !IsId(entry, out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var chunk in names.Chunk(NamesPerQuery))
        {
            var roles = await session.Query<Role>().Where(r => r.Name.In(chunk)).ToListAsync(ct);
            foreach (var role in roles)
                ids.TryAdd(role.Name, role.Id);
        }

        foreach (var field in listed)
        {
            field.VisibleToRoles = Rewrite(
                field.VisibleToRoles,
                entry => IsId(entry, out var id) ? id.ToString()
                    : ids.TryGetValue(entry, out var roleId) ? roleId.ToString()
                    : entry);
        }
    }

    /// <summary>
    /// Swaps every role id in the fields' lists for that role's current name, for a copy that is
    /// answered or exported. An id that names no role is kept.
    /// </summary>
    public static async Task ToNamesAsync(
        IQuerySession session, IEnumerable<FieldDefinition> fields, CancellationToken ct = default)
    {
        var listed = fields.Where(f => f is { VisibleToRoles.Count: > 0 }).ToList();
        if (listed.Count == 0)
            return;

        var wanted = new HashSet<Guid>();
        foreach (var entry in listed.SelectMany(f => f.VisibleToRoles))
        {
            if (IsId(entry, out var id))
                wanted.Add(id);
        }

        var names = new Dictionary<Guid, string>();
        foreach (var chunk in wanted.Chunk(NamesPerQuery))
        {
            var roles = await session.Query<Role>().Where(r => r.Id.In(chunk)).ToListAsync(ct);
            foreach (var role in roles)
                names[role.Id] = role.Name;
        }

        foreach (var field in listed)
        {
            field.VisibleToRoles = Rewrite(
                field.VisibleToRoles,
                entry => IsId(entry, out var id) && names.TryGetValue(id, out var name) ? name : entry);
        }
    }

    /// <remarks>
    /// A blank entry is kept. It matches nobody, and a list holding only blanks still means
    /// "nobody but SuperAdmin"; dropping it would empty the list and hand the field to the default.
    /// </remarks>
    private static List<string> Rewrite(IEnumerable<string> entries, Func<string, string> map)
    {
        var rewritten = new List<string>();
        foreach (var entry in entries)
        {
            var stored = string.IsNullOrWhiteSpace(entry) ? entry : map(entry);
            if (!rewritten.Contains(stored, StringComparer.Ordinal))
                rewritten.Add(stored);
        }

        return rewritten;
    }
}
