using System.Text.Json;
using barakoCMS.Core;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>One type lock a save asks for: the normalized name, and whether it is the exclusive one.</summary>
internal readonly record struct TypeLock(string Name, bool Exclusive);

/// <summary>
/// The type locks a save of several types takes in one pass: exclusive on each type it writes,
/// shared on each type its rollups read that it does not write, lowest normalized name first.
/// </summary>
/// <remarks>
/// Two saves whose types roll up over each other (A over X in one, X over A in the other) each hold
/// their own type and then ask for the other's shared lock. Asked for one at a time they wait on
/// each other and Postgres aborts one with 40P01. Asked for together, in one order, the second save
/// queues on the first lock it needs and holds nothing the first is waiting for. The plan is built
/// from the names alone and never throws: a document that is malformed is refused by the real
/// validation, after the locks.
/// </remarks>
internal static class TypeLockPlan
{
    /// <summary>
    /// A name longer than this is left out of the plan. No type name is near it, and it keeps a
    /// document that puts a huge string where a name goes from being normalized and hashed.
    /// </summary>
    private const int MaxNameLength = 256;

    /// <summary>The plan for the named types and the names their rollups read.</summary>
    /// <param name="written">The names of the types the save writes.</param>
    /// <param name="sources">The names the rollups of those types read, whether or not the save writes them.</param>
    public static IReadOnlyList<TypeLock> For(IEnumerable<string?> written, IEnumerable<string?> sources)
    {
        var exclusive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in written.Select(Clean).OfType<string>())
        {
            exclusive.Add(name);
        }

        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in sources.Select(Clean).OfType<string>().Where(n => !exclusive.Contains(n)))
        {
            shared.Add(name);
        }

        return exclusive.Select(n => new TypeLock(n, true))
            .Concat(shared.Select(n => new TypeLock(n, false)))
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The plan for type definitions the save writes.</summary>
    public static IReadOnlyList<TypeLock> ForTypes(IEnumerable<ContentTypeDefinition> types)
    {
        var all = types.Where(t => t is not null).ToList();
        return For(all.Select(t => t.Name), all.SelectMany(SourcesOf));
    }

    /// <summary>The plan for config documents, keyed by the name of the type each one declares.</summary>
    /// <remarks>
    /// The names are read the way the config reader binds them: property names ignore case, as
    /// <c>JsonSerializerDefaults.Web</c> does. A document that repeats a property under another case
    /// adds every value it gives, which can only take a lock the real read would not.
    /// </remarks>
    public static IReadOnlyList<TypeLock> ForDocuments(IReadOnlyDictionary<string, JsonElement> documents) =>
        For(documents.Keys, documents.Values.SelectMany(SourcesOf));

    /// <summary>The names the rollups of one config document read. Empty for anything that is not shaped like a type.</summary>
    public static IEnumerable<string?> SourcesOf(JsonElement document)
    {
        foreach (var field in Properties(document, "fields").Where(f => f.ValueKind == JsonValueKind.Array).SelectMany(f => f.EnumerateArray()))
        {
            foreach (var rollup in Properties(field, "rollup"))
            {
                foreach (var from in Properties(rollup, "from").Where(f => f.ValueKind == JsonValueKind.String))
                {
                    yield return from.GetString();
                }
            }
        }
    }

    /// <summary>The names the rollups of a type read.</summary>
    public static IEnumerable<string?> SourcesOf(ContentTypeDefinition type) =>
        (type.Fields ?? []).Where(f => f?.Rollup is not null).Select(f => f.Rollup!.From);

    private static IEnumerable<JsonElement> Properties(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return property.Value;
            }
        }
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return null;
        }

        var normalized = ContentTypeName.Normalize(name);
        return normalized.Length == 0 ? null : normalized;
    }
}
