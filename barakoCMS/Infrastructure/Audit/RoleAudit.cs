using System.Globalization;
using System.Text.Json;
using barakoCMS.Core;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>
/// What an audit row says about a role: its capability names and, per content type, the actions
/// it may take and the shape of each rule's conditions.
/// </summary>
/// <remarks>
/// A condition is recorded as the field it tests and the operators it uses, never the value it
/// compares against: an audit row carries names and ids. A value can change with the field and
/// operators staying the same, so <see cref="Changed"/> also compares the stored conditions in
/// full, in memory, and records only whether they differ and for which content types.
///
/// Every string a request sent sits in a key of its own. Nothing is joined into a sentence, so a
/// content type named like a grant cannot read as one.
///
/// A role document is stored as the request sent it, with no limit on how many capabilities or
/// permissions it holds or how long a name is. A row keeps the first <see cref="MaxItems"/> of each
/// list with the full count, and the first <see cref="MaxLength"/> characters of each name.
///
/// Everything here tolerates a null list or rule, since a stored null is possible and a row that
/// cannot be built must not be what refuses the edit.
/// </remarks>
internal static class RoleAudit
{
    public const int MaxItems = 50;
    public const int MaxLength = 200;
    private const int MaxOperators = 10;

    /// <summary>A role as a row describes it, taken before the role is changed and again after.</summary>
    internal sealed record Snapshot(
        string Name,
        List<string> Capabilities,
        List<Dictionary<string, object>> Permissions,
        Dictionary<string, string> ConditionPrints);

    public static Snapshot Of(Role role)
    {
        var permissions = (role.Permissions ?? new List<ContentTypePermission>())
            .Where(p => p is not null)
            .ToList();

        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var permission in permissions)
        {
            var slug = permission.ContentTypeSlug ?? string.Empty;
            var print = Print(permission);
            prints[slug] = prints.TryGetValue(slug, out var earlier) ? earlier + "|" + print : print;
        }

        return new Snapshot(
            Clip(role.Name),
            (role.SystemCapabilities ?? new List<string>()).Where(c => c is not null).Select(Clip).ToList(),
            permissions.Select(p => DescribePermission(p)).ToList(),
            prints);
    }

    /// <summary>The metadata for a row about one state of a role: created, or deleted.</summary>
    public static Dictionary<string, object> Describe(Snapshot role) => new()
    {
        ["name"] = role.Name,
        ["capabilities"] = Capped(role.Capabilities),
        ["permissions"] = Capped(role.Permissions),
    };

    /// <summary>The metadata for a row about a role going from one state to another.</summary>
    public static Dictionary<string, object> Changed(Snapshot before, Snapshot after)
    {
        var metadata = new Dictionary<string, object>
        {
            ["name"] = after.Name,
            ["nameBefore"] = before.Name,
            ["capabilitiesBefore"] = Capped(before.Capabilities),
            ["capabilitiesAfter"] = Capped(after.Capabilities),
            ["permissionsBefore"] = Capped(before.Permissions),
            ["permissionsAfter"] = Capped(after.Permissions),
        };

        var added = after.Capabilities.Except(before.Capabilities, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Capabilities.Except(after.Capabilities, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            metadata["capabilitiesAdded"] = Capped(added);
            metadata["capabilitiesRemoved"] = Capped(removed);
        }

        var changedIn = before.ConditionPrints.Keys
            .Union(after.ConditionPrints.Keys, StringComparer.Ordinal)
            .Where(slug => !string.Equals(
                before.ConditionPrints.GetValueOrDefault(slug, string.Empty),
                after.ConditionPrints.GetValueOrDefault(slug, string.Empty),
                StringComparison.Ordinal))
            .Select(Clip)
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToList();

        metadata["conditionsChanged"] = changedIn.Count > 0;
        if (changedIn.Count > 0)
            metadata["conditionsChangedIn"] = Capped(changedIn);

        return metadata;
    }

    /// <summary>One role list of a field as an entry holds it: ids, and names beside them in the same order.</summary>
    internal sealed record RoleList(Dictionary<string, object> Ids, Dictionary<string, object> Names);

    /// <summary>
    /// The role lists of a field, for an entry. A field stores role ids, and an entry it could not
    /// tie to a role as the text that was sent.
    /// </summary>
    /// <remarks>
    /// The id is the reference, since it survives a rename, and the name is what the role was
    /// called when the change was made. A stored id whose role is gone has an empty name, and
    /// stored text that is not an id has an empty id and the text as its name. Capped and clipped
    /// like every other list here, because nothing limits what a request puts in the list.
    ///
    /// One lookup serves every list passed and <paramref name="alsoResolve"/>, which is how the
    /// endpoint names the roles in its response without asking again.
    /// </remarks>
    public static async Task<List<RoleList>> RoleListsAsync(
        IQuerySession session, List<string>?[] lists, IEnumerable<FieldDefinition> alsoResolve, CancellationToken ct)
    {
        var kept = lists
            .Select(list => (list ?? new List<string>())
                .Take(MaxItems)
                .Select(entry => entry ?? string.Empty)
                .ToList())
            .ToList();

        // One definition per entry, so each comes back in its own place: resolving a whole list
        // drops an entry that resolves to a name already in it.
        var single = kept
            .Select(list => list.Select(entry => new FieldDefinition { VisibleToRoles = [entry] }).ToList())
            .ToList();

        await RoleReferences.ToNamesAsync(session, single.SelectMany(list => list).Concat(alsoResolve), ct);

        var described = new List<RoleList>();
        for (var i = 0; i < kept.Count; i++)
        {
            var ids = new List<string>();
            var names = new List<string>();

            for (var j = 0; j < kept[i].Count; j++)
            {
                var stored = kept[i][j];
                var resolved = single[i][j].VisibleToRoles.FirstOrDefault() ?? string.Empty;
                var isId = RoleReferences.IsId(stored, out _);

                ids.Add(isId ? stored : string.Empty);
                names.Add(Clip(isId && string.Equals(resolved, stored, StringComparison.Ordinal) ? string.Empty : resolved));
            }

            var count = lists[i]?.Count ?? 0;
            described.Add(new RoleList(Capped(ids, count), Capped(names, count)));
        }

        return described;
    }

    private static Dictionary<string, object> DescribePermission(ContentTypePermission permission)
    {
        var actions = new List<string>();
        var conditions = new List<Dictionary<string, object>>();
        var fieldSets = new List<Dictionary<string, object>>();

        Rule("create", permission.Create);
        Rule("read", permission.Read);
        Rule("update", permission.Update);
        Rule("delete", permission.Delete);

        var transitions = new List<string>();
        if (permission.Transitions is not null)
        {
            foreach (var (name, rule) in permission.Transitions.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (rule is { Enabled: true })
                    transitions.Add(Clip(name));
                AddConditions(conditions, "transition", name, rule);
            }
        }

        var described = new Dictionary<string, object>
        {
            ["contentType"] = Clip(permission.ContentTypeSlug),
            ["actions"] = actions,
            ["transitions"] = Capped(transitions),
            ["conditions"] = Capped(conditions),
        };

        // Only when a rule holds one, so a row about a role with no field set reads as it always did.
        if (fieldSets.Count > 0)
            described["fieldSets"] = Capped(fieldSets);

        return described;

        void Rule(string action, PermissionRule? rule)
        {
            if (rule is { Enabled: true })
                actions.Add(action);
            AddConditions(conditions, action, null, rule);
            AddFieldSet(action, "readable", rule?.ReadableFields);
            AddFieldSet(action, "writable", rule?.WritableFields);
        }

        void AddFieldSet(string action, string access, List<string>? names)
        {
            if (names is null)
                return;

            var kept = names.Where(n => n is not null).Select(Clip).ToList();
            fieldSets.Add(new Dictionary<string, object>
            {
                ["rule"] = action,
                ["access"] = access,
                ["fields"] = Capped(kept),
            });
        }
    }

    private static void AddConditions(
        List<Dictionary<string, object>> into, string rule, string? transition, PermissionRule? source)
    {
        if (source?.Conditions is not { Count: > 0 } conditions)
            return;

        foreach (var (field, tested) in conditions.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var entry = new Dictionary<string, object> { ["rule"] = rule };
            if (transition is not null)
                entry["transition"] = Clip(transition);
            entry["field"] = Clip(field);
            entry["operators"] = Operators(tested);
            into.Add(entry);
        }
    }

    /// <summary>
    /// The operator names under one tested field. A condition is field, then operator, then value,
    /// so the keys one level down are the operators and nothing below them is read.
    /// </summary>
    private static List<string> Operators(object? tested) => tested switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } element =>
            element.EnumerateObject().Select(p => Clip(p.Name)).Take(MaxOperators).ToList(),
        IDictionary<string, object> operators =>
            operators.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(Clip).Take(MaxOperators).ToList(),
        _ => new List<string>(),
    };

    private static Dictionary<string, object> Capped<T>(IReadOnlyCollection<T> items) => Capped(items, items.Count);

    private static Dictionary<string, object> Capped<T>(IReadOnlyCollection<T> items, int count) => new()
    {
        ["items"] = items.Take(MaxItems).ToList(),
        ["count"] = count,
        ["truncated"] = count > MaxItems,
    };

    private static string Clip(string? value) =>
        value is null ? string.Empty : value.Length <= MaxLength ? value : value[..MaxLength];

    /// <summary>
    /// One permission's conditions, values included, in a form two snapshots can be compared by.
    /// Empty when no rule has a condition. Compared in memory and never stored.
    /// </summary>
    private static string Print(ContentTypePermission permission)
    {
        var rules = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        Add("create", permission.Create);
        Add("read", permission.Read);
        Add("update", permission.Update);
        Add("delete", permission.Delete);

        if (permission.Transitions is not null)
        {
            foreach (var (name, rule) in permission.Transitions)
                Add("transition:" + name, rule);
        }

        return rules.Count == 0 ? string.Empty : JsonSerializer.Serialize(rules);

        void Add(string key, PermissionRule? rule)
        {
            if (rule?.Conditions is { Count: > 0 } conditions)
                rules[key] = Comparable(conditions);
        }
    }

    /// <summary>
    /// A condition value with object keys in one order and scalars as text, so the same condition
    /// read from the request and read from the store compares equal.
    /// </summary>
    private static object? Comparable(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Object => (object?)Sorted(element.EnumerateObject().Select(p => (p.Name, (object?)p.Value))),
                    JsonValueKind.Array => element.EnumerateArray().Select(e => Comparable(e)).ToList(),
                    JsonValueKind.String => "s:" + element.GetString(),
                    JsonValueKind.Number => "n:" + element.GetRawText(),
                    JsonValueKind.True => "b:true",
                    JsonValueKind.False => "b:false",
                    _ => null,
                };
            case IDictionary<string, object> map:
                return Sorted(map.Select(kv => (kv.Key, (object?)kv.Value)));
            case string text:
                return "s:" + text;
            case bool flag:
                return flag ? "b:true" : "b:false";
            case System.Collections.IEnumerable sequence:
                return sequence.Cast<object?>().Select(Comparable).ToList();
            case IFormattable number:
                return "n:" + number.ToString(null, CultureInfo.InvariantCulture);
            default:
                return "o:" + value;
        }
    }

    private static SortedDictionary<string, object?> Sorted(IEnumerable<(string Key, object? Value)> pairs)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            sorted[key] = Comparable(value);
        return sorted;
    }
}
