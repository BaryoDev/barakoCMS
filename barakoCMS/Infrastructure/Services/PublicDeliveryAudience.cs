using barakoCMS.Features.Roles;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The one rule that keeps a type a role reads only in part off the anonymous route.
/// </summary>
/// <remarks>
/// Anonymous delivery serves every published entry of an opted-in type. A role whose read rule tests
/// a field of the entry (an audience list, an owner id) says some entries are not for everyone, and
/// the public route has no role to test that against. So the two do not meet: a type is not made
/// deliverable while such a role reads it, and a role is not given such a read on a deliverable
/// type. A rule that tests only the status is not a row limit, since the public route serves
/// published entries only.
///
/// Every writer of the flag and of a role's rules calls this. Both sides read the session's own
/// tenant, so a platform role is checked against the types of the tenant that saves it.
/// </remarks>
internal static class PublicDeliveryAudience
{
    public static bool LimitsRows(PermissionRule? read) =>
        read is { Enabled: true, Conditions: { Count: > 0 } conditions }
        && conditions.Keys.Any(k => !string.Equals(k, "$status", StringComparison.Ordinal));

    private static string Normal(string? name) => barakoCMS.Core.ContentTypeName.Normalize(name);

    private static async Task<IReadOnlyList<Role>> RolesAsync(IQuerySession session, CancellationToken ct) =>
        await RoleScope.In(session.Query<Role>(), TenantScopes.SlugFor(session.TenantId)).ToListAsync(ct);

    private static IEnumerable<string> LimitedTypes(Role role) =>
        (role.Permissions ?? []).Where(p => p is not null && LimitsRows(p.Read)).Select(p => Normal(p.ContentTypeSlug));

    /// <summary>Every role and type of the tenant where the role reads only part of a deliverable type.</summary>
    public static async Task<HashSet<(string Role, string Type)>> PairsAsync(IQuerySession session, CancellationToken ct)
    {
        var deliverable = await DeliverableAsync(session, ct);
        var pairs = new HashSet<(string, string)>();
        foreach (var role in await RolesAsync(session, ct))
        {
            pairs.UnionWith(LimitedTypes(role).Where(deliverable.Contains).Select(t => (role.Name, t)));
        }

        return pairs;
    }

    private static async Task<HashSet<string>> DeliverableAsync(IQuerySession session, CancellationToken ct) =>
        (await session.Query<ContentTypeDefinition>().Where(t => t.IsPubliclyDeliverable).Select(t => t.Name).ToListAsync(ct))
        .Select(Normal).ToHashSet(StringComparer.Ordinal);

    /// <summary>The refusal for making <paramref name="typeName"/> deliverable, or null when no role reads it in part.</summary>
    public static async Task<string?> TypeRefusalAsync(IQuerySession session, string typeName, CancellationToken ct)
    {
        var name = Normal(typeName);
        var roles = (await RolesAsync(session, ct)).Where(r => LimitedTypes(r).Contains(name)).Select(r => r.Name).Order(StringComparer.Ordinal).ToList();
        return roles.Count == 0
            ? null
            : $"'{name}' cannot be delivered publicly while a role reads only some of its entries "
              + $"({string.Join(", ", roles)}). Public delivery serves every published entry to anonymous callers, "
              + "so it would show the ones those roles are kept from. Remove the row limit from the role first.";
    }

    /// <summary>
    /// The errors for saving <paramref name="next"/> as a role's rules over <paramref name="stored"/>:
    /// each deliverable type the role would newly read in part. A limit the role already held stays
    /// allowed, or the role could not be edited at all.
    /// </summary>
    public static async Task<List<string>> RoleErrorsAsync(
        IQuerySession session, List<ContentTypePermission>? next, List<ContentTypePermission>? stored, CancellationToken ct)
    {
        var held = (stored ?? []).Where(p => p is not null && LimitsRows(p.Read)).Select(p => Normal(p.ContentTypeSlug)).ToHashSet(StringComparer.Ordinal);
        var added = (next ?? []).Where(p => p is not null && LimitsRows(p.Read)).Select(p => Normal(p.ContentTypeSlug))
            .Where(t => !held.Contains(t)).Distinct().ToList();
        if (added.Count == 0)
        {
            return [];
        }

        var deliverable = await DeliverableAsync(session, ct);
        return added.Where(deliverable.Contains).Order(StringComparer.Ordinal).Select(t =>
            $"This role would read only some entries of '{t}', which is publicly deliverable. Public delivery serves every "
            + "published entry to anonymous callers, so it would show the ones this role is kept from. Turn public "
            + $"delivery off for '{t}' first, or drop the row limit.").ToList();
    }

    /// <summary>The errors for the pairs in <paramref name="after"/> that were not in <paramref name="before"/>.</summary>
    public static List<string> NewPairErrors(HashSet<(string Role, string Type)> before, HashSet<(string Role, string Type)> after) =>
        after.Where(p => !before.Contains(p)).OrderBy(p => p.Type, StringComparer.Ordinal).ThenBy(p => p.Role, StringComparer.Ordinal)
            .Select(p => $"Role '{p.Role}' would read only some entries of '{p.Type}', which would be publicly deliverable. "
                         + "Public delivery serves every published entry to anonymous callers. Drop the row limit "
                         + "from the role, or leave the type off public delivery.").ToList();
}
