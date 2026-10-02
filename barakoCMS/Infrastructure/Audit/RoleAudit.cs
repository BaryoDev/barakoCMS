using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>
/// What an audit row says about a role: capability names and, per content type, which actions the
/// role may take.
/// </summary>
/// <remarks>
/// A permission rule's conditions are left out on purpose. They hold the literal values an entry is
/// compared against, and an audit row carries names and ids, never a value. A rule with conditions
/// is recorded as conditional, which is enough to see that it narrowed or widened.
///
/// Everything here tolerates a null list or rule. A role document is stored as the request sent it,
/// so a stored null is possible, and a row that cannot be built must not be what refuses the edit.
/// </remarks>
internal static class RoleAudit
{
    public static List<string> Capabilities(Role role) =>
        role.SystemCapabilities is null ? new List<string>() : role.SystemCapabilities.ToList();

    public static List<string> Permissions(IEnumerable<ContentTypePermission>? permissions) =>
        permissions is null
            ? new List<string>()
            : permissions
                .Where(p => p is not null)
                .Select(Describe)
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

    /// <summary>The names in <paramref name="after"/> that are not in <paramref name="before"/>.</summary>
    public static List<string> Added(IEnumerable<string> before, IEnumerable<string> after) =>
        after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Describe(ContentTypePermission permission)
    {
        var granted = new List<string>();
        Add(granted, "create", permission.Create);
        Add(granted, "read", permission.Read);
        Add(granted, "update", permission.Update);
        Add(granted, "delete", permission.Delete);

        if (permission.Transitions is not null)
        {
            foreach (var transition in permission.Transitions.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
                Add(granted, $"transition:{transition.Key}", transition.Value);
        }

        return $"{permission.ContentTypeSlug}: {(granted.Count == 0 ? "none" : string.Join(", ", granted))}";
    }

    private static void Add(List<string> granted, string action, PermissionRule? rule)
    {
        if (rule is not { Enabled: true })
            return;

        granted.Add(rule.Conditions is { Count: > 0 } ? $"{action} (conditional)" : action);
    }
}
