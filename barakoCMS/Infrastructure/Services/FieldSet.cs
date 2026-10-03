namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The fields a caller's permission rules allow for one action on a content type: every field, or
/// the fields named.
/// </summary>
/// <remarks>
/// Names are compared exactly, as the content type declares them. A role write refuses a name the
/// type does not declare in that spelling, so a set naming another spelling was written for another
/// tenant's type and allows nothing here.
/// </remarks>
public sealed class FieldSet
{
    public static readonly FieldSet All = new(null);

    private readonly HashSet<string>? _names;

    private FieldSet(HashSet<string>? names) => _names = names;

    /// <summary>Whether no rule narrows the fields.</summary>
    public bool IsAll => _names is null;

    /// <summary>The fields named, or null when <see cref="IsAll"/>.</summary>
    public IReadOnlyCollection<string>? Names => _names;

    public bool Allows(string name) => _names is null || _names.Contains(name);

    /// <summary>The set a rule holds: every field for null, otherwise the fields it names.</summary>
    public static FieldSet Of(IEnumerable<string>? names) =>
        names is null ? All : new FieldSet(new HashSet<string>(names.Where(n => n is not null), StringComparer.Ordinal));

    /// <summary>What any one of the sets allows. Every field when any of them is every field.</summary>
    public static FieldSet Union(IEnumerable<FieldSet> sets)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            if (set._names is null)
                return All;
            names.UnionWith(set._names);
        }

        return new FieldSet(names);
    }

    /// <summary>What every one of the sets allows. Every field only when each of them is.</summary>
    public static FieldSet Intersection(IEnumerable<FieldSet> sets)
    {
        HashSet<string>? names = null;
        foreach (var set in sets)
        {
            if (set._names is null)
                continue;

            if (names is null)
                names = new HashSet<string>(set._names, StringComparer.Ordinal);
            else
                names.IntersectWith(set._names);
        }

        return names is null ? All : new FieldSet(names);
    }
}
