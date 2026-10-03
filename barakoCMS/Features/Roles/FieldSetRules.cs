using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>
/// What a role write checks about the field sets on its rules: <see cref="PermissionRule.ReadableFields"/>
/// on a Read rule and <see cref="PermissionRule.WritableFields"/> on a Create or Update rule.
/// </summary>
/// <remarks>
/// Two halves, as <see cref="ReferenceConditionRules"/> has: <see cref="ShapeErrors"/> needs no
/// database and runs in the validators, and <see cref="CheckAsync"/> reads the tenant's content
/// types and runs in the endpoints.
///
/// A set the stored role already holds, naming the same fields, is passed over. Roles are stored
/// once for every tenant and content types per tenant, so a set written for another tenant's type
/// must not stop the role being renamed from here.
///
/// A request that leaves a set out, or sends it as null, keeps the set the stored role holds on that
/// rule (<see cref="CarryOver"/>). A console that does not know the members sends the role back
/// without them, and replacing the permissions as sent would drop every set on every save. An empty
/// list removes the set.
/// </remarks>
internal static class FieldSetRules
{
    private const int MaxErrors = 20;

    /// <summary>The most names one set holds.</summary>
    public const int MaxFieldsPerSet = 200;

    private readonly record struct Held(string Slug, string Slot, bool Readable, List<string> Names);

    private static IEnumerable<(string Slot, PermissionRule? Rule)> RulesOf(ContentTypePermission permission)
    {
        yield return ("create", permission.Create);
        yield return ("read", permission.Read);
        yield return ("update", permission.Update);
        yield return ("delete", permission.Delete);

        if (permission.Transitions is null)
            yield break;

        foreach (var (_, rule) in permission.Transitions)
            yield return ("transition", rule);
    }

    private static IEnumerable<Held> SetsOf(List<ContentTypePermission>? permissions)
    {
        foreach (var permission in permissions ?? [])
        {
            if (permission is null)
                continue;

            foreach (var (slot, rule) in RulesOf(permission))
            {
                if (rule?.ReadableFields is { Count: > 0 } readable)
                    yield return new Held(permission.ContentTypeSlug ?? string.Empty, slot, true, readable);
                if (rule?.WritableFields is { Count: > 0 } writable)
                    yield return new Held(permission.ContentTypeSlug ?? string.Empty, slot, false, writable);
            }
        }
    }

    /// <summary>
    /// Fills in, rule by rule, a set the request left null with the one the stored role holds on
    /// the same rule of the same content type.
    /// </summary>
    public static void CarryOver(List<ContentTypePermission>? requested, List<ContentTypePermission>? stored)
    {
        if (requested is null || stored is null)
            return;

        foreach (var permission in requested)
        {
            if (permission is null)
                continue;

            var before = stored.FirstOrDefault(p => p is not null && p.ContentTypeSlug == permission.ContentTypeSlug);
            if (before is null)
                continue;

            if (permission.Read is { ReadableFields: null } read)
                read.ReadableFields = before.Read?.ReadableFields;
            if (permission.Create is { WritableFields: null } create)
                create.WritableFields = before.Create?.WritableFields;
            if (permission.Update is { WritableFields: null } update)
                update.WritableFields = before.Update?.WritableFields;
        }
    }

    /// <summary>An empty set is stored as no set, which is what it asks for.</summary>
    public static void Normalise(List<ContentTypePermission>? permissions)
    {
        foreach (var permission in permissions ?? [])
        {
            if (permission is null)
                continue;

            foreach (var (_, rule) in RulesOf(permission))
            {
                if (rule is null)
                    continue;
                if (rule.ReadableFields is { Count: 0 })
                    rule.ReadableFields = null;
                if (rule.WritableFields is { Count: 0 })
                    rule.WritableFields = null;
            }
        }
    }

    public static List<string> ShapeErrors(List<ContentTypePermission>? permissions)
    {
        var errors = new List<string>();

        foreach (var held in SetsOf(permissions))
        {
            if (ShapeError(held) is { } error && !errors.Contains(error))
                errors.Add(error);

            if (errors.Count >= MaxErrors)
                break;
        }

        return errors;
    }

    private static string? ShapeError(Held held)
    {
        if (held.Readable && held.Slot != "read")
        {
            return "readableFields is accepted on a Read rule only. It says which fields an entry the rule "
                 + "grants shows.";
        }

        if (!held.Readable && held.Slot is not ("create" or "update"))
        {
            return "writableFields is accepted on a Create or Update rule only. A transition writes the "
                 + "fields its content type declares for it.";
        }

        if (held.Names.Count > MaxFieldsPerSet)
            return $"A field set names at most {MaxFieldsPerSet} fields.";

        if (held.Names.Any(name => name is null || !ReferenceConditions.IsName(name)))
        {
            return "A field set holds field names. Each starts with a letter and holds letters, digits and "
                 + $"underscores, {ReferenceConditions.MaxNameLength} characters at most.";
        }

        return null;
    }

    /// <summary>
    /// Checks every set the stored role does not already hold as it is: its shape, then that the
    /// rule's content type exists in this tenant and declares each field it names, spelled as
    /// declared.
    /// </summary>
    /// <param name="stored">The permissions the role holds now, or null for a new role.</param>
    /// <remarks>
    /// One query, for the content types the changed sets are on. A message names a field only after
    /// its shape is checked, and never the content type slug, which is whatever the request sent.
    /// </remarks>
    public static async Task<List<string>> CheckAsync(
        IQuerySession session,
        List<ContentTypePermission>? permissions,
        List<ContentTypePermission>? stored,
        CancellationToken cancellationToken)
    {
        var kept = SetsOf(stored).Select(Identity).ToHashSet(StringComparer.Ordinal);
        var changed = SetsOf(permissions).Where(held => !kept.Contains(Identity(held))).ToList();
        if (changed.Count == 0)
            return [];

        var errors = new List<string>();
        foreach (var held in changed)
        {
            if (ShapeError(held) is { } error && !errors.Contains(error))
                errors.Add(error);
        }

        if (errors.Count > 0)
            return errors.Take(MaxErrors).ToList();

        var slugs = changed.Select(held => held.Slug).Where(slug => slug.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (slugs.Length > ReferenceConditionRules.MaxContentTypes)
        {
            return
            [
                $"A role write checks field sets on at most {ReferenceConditionRules.MaxContentTypes} content types. "
              + "Save the role in more than one step.",
            ];
        }

        var definitions = slugs.Length == 0
            ? new Dictionary<string, ContentTypeDefinition>(StringComparer.Ordinal)
            : (await session.Query<ContentTypeDefinition>()
                    .Where(d => d.Name.In(slugs))
                    .ToListAsync(cancellationToken))
                .GroupBy(d => d.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var held in changed)
        {
            var list = held.Readable ? "readableFields" : "writableFields";

            if (!definitions.TryGetValue(held.Slug, out var definition))
            {
                var error = $"A {list} set is on a content type this tenant does not define, so the fields it "
                          + "names cannot be checked.";
                if (!errors.Contains(error))
                    errors.Add(error);
                continue;
            }

            var declared = definition.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in held.Names.Where(name => !declared.Contains(name)).Distinct(StringComparer.Ordinal))
            {
                var error = $"The {list} set names '{name}', which is not a field of the content type as it is "
                          + "spelled there.";
                if (!errors.Contains(error))
                    errors.Add(error);

                if (errors.Count >= MaxErrors)
                    return errors;
            }
        }

        return errors;
    }

    /// <summary>The rule a set is on and the names it holds, in one order.</summary>
    private static string Identity(Held held) =>
        held.Slug + "\n" + held.Slot + "\n" + (held.Readable ? "r" : "w") + "\n"
        + string.Join("\n", held.Names.Where(n => n is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
