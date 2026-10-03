using System.Text.Json;
using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>What makes a condition the one it is: its content type, its rule, its key and its operators.</summary>
internal readonly record struct ConditionIdentity(string Slug, string Slot, string Key, string Operators);

/// <summary>
/// What a role write checks about a condition that follows a reference, such as
/// <c>Class.InstructorUser</c>.
/// </summary>
/// <remarks>
/// A condition that cannot resolve denies when it is evaluated, so none of this decides access. It
/// is here so the mistake is reported to whoever saves the role, not found later as a role that
/// reads nothing.
///
/// Two halves, the way a saved sync is checked: <see cref="ShapeErrors"/> needs no database and is
/// what the create slice's validator runs, and <see cref="CheckAsync"/> reads the tenant's content
/// types and runs in the endpoint.
///
/// A condition the stored role already holds, unchanged, is passed over. An update replaces the
/// whole permission list, and without that a role holding a condition written for another tenant's
/// content types, or one stored before this check, could not be renamed from here.
///
/// A message names the condition only once its key has the shape of two names, and never the
/// content type slug, which is whatever the request sent.
/// </remarks>
internal static class ReferenceConditionRules
{
    private const int MaxErrors = 20;

    /// <summary>
    /// The most content types one write may hold reference conditions on. Their definitions are
    /// read in one query, and this is what keeps that query's size the role's and not the request's.
    /// </summary>
    public const int MaxContentTypes = 50;

    private static readonly string OperatorList = string.Join(", ", ReferenceConditions.Operators);

    /// <summary>One condition that follows a reference, and where in the role it sits.</summary>
    private readonly record struct Held(string Slug, string Slot, string Key, object? Operators)
    {
        public bool IsCreate => Slot == "create";
    }

    private static IEnumerable<(string Slot, PermissionRule? Rule)> RulesOf(ContentTypePermission permission)
    {
        yield return ("create", permission.Create);
        yield return ("read", permission.Read);
        yield return ("update", permission.Update);
        yield return ("delete", permission.Delete);

        if (permission.Transitions is null)
            yield break;

        // Lower case, because a transition rule is matched without regard to case.
        foreach (var (name, rule) in permission.Transitions)
            yield return ("transition:" + (name ?? string.Empty).ToLowerInvariant(), rule);
    }

    private static IEnumerable<Held> PathsOf(List<ContentTypePermission>? permissions)
    {
        foreach (var permission in permissions ?? [])
        {
            if (permission is null)
                continue;

            foreach (var (slot, rule) in RulesOf(permission))
            {
                if (rule?.Conditions is null)
                    continue;

                foreach (var (key, operators) in rule.Conditions)
                {
                    if (ReferenceConditions.IsPath(key))
                        yield return new Held(permission.ContentTypeSlug ?? string.Empty, slot, key, operators);
                }
            }
        }
    }

    /// <summary>
    /// The conditions the request holds that the stored role does not hold exactly so: same content
    /// type, same rule, same key, same operators and values.
    /// </summary>
    private static List<Held> Changed(List<ContentTypePermission>? permissions, List<ContentTypePermission>? stored)
    {
        var kept = PathsOf(stored).Select(Identity).ToHashSet();

        return PathsOf(permissions).Where(held => !kept.Contains(Identity(held))).ToList();
    }

    private static ConditionIdentity Identity(Held held) =>
        new(held.Slug, held.Slot, held.Key, OperatorsText(held.Operators));

    /// <summary>
    /// The operators and their values, in a text that does not depend on the order the operators
    /// were written in. A stored condition comes back in the database's key order and a request in
    /// the client's, and the same condition must read as the same. A list keeps its order.
    /// </summary>
    private static string OperatorsText(object? operators)
    {
        IEnumerable<(string Name, object? Value)>? pairs = operators switch
        {
            Dictionary<string, object> held => held.Select(pair => (pair.Key, (object?)pair.Value)),
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                element.EnumerateObject().Select(property => (property.Name, (object?)property.Value)),
            _ => null,
        };

        if (pairs is null)
            return JsonSerializer.Serialize(operators);

        return string.Join(
            "\n",
            pairs.OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Name) + ":" + JsonSerializer.Serialize(pair.Value)));
    }

    public static List<string> ShapeErrors(List<ContentTypePermission>? permissions) =>
        ShapeErrors(PathsOf(permissions));

    private static List<string> ShapeErrors(IEnumerable<Held> paths)
    {
        var errors = new List<string>();

        foreach (var held in paths)
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
        if (!ReferenceConditions.TrySplit(held.Key, out _, out _))
        {
            return "A condition key holding a dot follows one reference to a field of the entry it points at, "
                 + "written Reference.Field. Each name starts with a letter and holds letters, digits and "
                 + $"underscores, {ReferenceConditions.MaxNameLength} characters at most. A second dot is refused: "
                 + "one reference is followed, not two.";
        }

        if (held.IsCreate)
        {
            return $"The condition '{held.Key}' is on a Create rule. Creating has no stored entry to follow a "
                 + "reference from, and a Create rule's conditions are not evaluated.";
        }

        var names = ReferenceConditions.OperatorNames(held.Operators);
        if (names is not { Count: > 0 } || names.Any(name => !ReferenceConditions.Operators.Contains(name)))
            return $"The condition '{held.Key}' needs at least one operator, and only these: {OperatorList}.";

        if (!ReferenceConditions.ComparesText(held.Operators))
        {
            return $"The condition '{held.Key}' compares text: _eq and _ne take a text value, and _in and _nin "
                 + "take a list of text holding at least one.";
        }

        return null;
    }

    /// <summary>
    /// Checks every changed condition: its shape, then that the rule's content type exists in this
    /// tenant, that the first name is a reference field on it, and that the second is a Public
    /// field of the type the reference points at.
    /// </summary>
    /// <param name="stored">The permissions the role holds now, or null for a new role.</param>
    /// <remarks>
    /// Roles are stored once for every tenant and content types per tenant, so this answers for the
    /// tenant the request was made in. In another tenant the same condition is resolved against
    /// that tenant's types when it is evaluated, and denies where they do not declare it.
    ///
    /// Two queries at most, whatever the request holds: the rules' own types, then the types their
    /// reference fields point at.
    ///
    /// "Not a field" and "not a reference" are one message, and so are "not a field" and "not
    /// Public" on the referenced type. Saving a role does not take the capability that reads a
    /// type's schema, and two answers would say which fields a type has.
    /// </remarks>
    public static async Task<List<string>> CheckAsync(
        IQuerySession session,
        List<ContentTypePermission>? permissions,
        List<ContentTypePermission>? stored,
        CancellationToken cancellationToken)
    {
        var changed = Changed(permissions, stored);
        if (changed.Count == 0)
            return [];

        var errors = ShapeErrors(changed);
        if (errors.Count > 0)
            return errors;

        var slugs = changed.Select(held => held.Slug).Where(slug => slug.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (slugs.Length > MaxContentTypes)
        {
            return
            [
                $"A role write checks conditions that follow a reference on at most {MaxContentTypes} content types. "
              + "Save the role in more than one step.",
            ];
        }

        var definitions = await DefinitionsForAsync(session, changed, cancellationToken);

        foreach (var held in changed)
        {
            if (Error(definitions, held) is { } error && !errors.Contains(error))
                errors.Add(error);

            if (errors.Count >= MaxErrors)
                break;
        }

        return errors;
    }

    /// <summary>Whether any rule in these permissions holds a condition whose key holds a dot.</summary>
    public static bool HoldsPath(List<ContentTypePermission>? permissions) => PathsOf(permissions).Any();

    /// <summary>Every such condition, as what makes it the condition it is.</summary>
    public static IEnumerable<ConditionIdentity> Paths(List<ContentTypePermission>? permissions) =>
        PathsOf(permissions).Select(Identity);

    /// <summary>
    /// The conditions in these permissions that a write would refuse in this session's tenant. For
    /// the start-up notice, which asks every tenant.
    /// </summary>
    /// <remarks>
    /// Each is identified by its content type, its rule, its key and its operators, since any of
    /// the four can be what a write refuses: the same key is refused on a Create rule and accepted
    /// on a Read rule, and refused with a number where it is accepted with text.
    /// </remarks>
    public static async Task<HashSet<ConditionIdentity>> UnresolvedAsync(
        IQuerySession session, List<ContentTypePermission>? permissions, CancellationToken cancellationToken)
    {
        var unresolved = new HashSet<ConditionIdentity>();
        var wellFormed = new List<Held>();

        foreach (var held in PathsOf(permissions))
        {
            if (ShapeError(held) is null)
                wellFormed.Add(held);
            else
                unresolved.Add(Identity(held));
        }

        var definitions = await DefinitionsForAsync(session, wellFormed, cancellationToken);

        foreach (var held in wellFormed)
        {
            if (Error(definitions, held) is not null)
                unresolved.Add(Identity(held));
        }

        return unresolved;
    }

    /// <summary>
    /// The definitions these well-formed conditions name, in two queries: the rules' own types,
    /// then the types their reference fields point at, under each name those may be stored by.
    /// </summary>
    private static async Task<Dictionary<string, ContentTypeDefinition>> DefinitionsForAsync(
        IQuerySession session, List<Held> paths, CancellationToken cancellationToken)
    {
        var slugs = paths.Select(held => held.Slug).Where(slug => slug.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = await DefinitionsAsync(session, slugs, cancellationToken);

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var held in paths)
        {
            ReferenceConditions.TrySplit(held.Key, out var reference, out _);

            var field = ReferenceConditions.ReferenceField(definitions.GetValueOrDefault(held.Slug), reference);
            if (field is null)
                continue;

            foreach (var name in ReferenceConditions.TargetNames(field.ReferenceType!))
            {
                if (!definitions.ContainsKey(name))
                    targets.Add(name);
            }
        }

        foreach (var (name, definition) in await DefinitionsAsync(session, targets.ToArray(), cancellationToken))
            definitions[name] = definition;

        return definitions;
    }

    private static async Task<Dictionary<string, ContentTypeDefinition>> DefinitionsAsync(
        IQuerySession session, string[] names, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ContentTypeDefinition>(StringComparer.Ordinal);
        if (names.Length == 0)
            return found;

        var definitions = await session.Query<ContentTypeDefinition>()
            .Where(d => d.Name.In(names))
            .ToListAsync(cancellationToken);

        foreach (var definition in definitions)
            found[definition.Name] = definition;

        return found;
    }

    private static string? Error(Dictionary<string, ContentTypeDefinition> definitions, Held held)
    {
        ReferenceConditions.TrySplit(held.Key, out var reference, out var field);

        var definition = definitions.GetValueOrDefault(held.Slug);
        if (definition is null)
        {
            return $"The condition '{held.Key}' is on a content type this tenant does not define, so the reference "
                 + "it follows cannot be checked.";
        }

        var referenceField = ReferenceConditions.ReferenceField(definition, reference);
        if (referenceField is null)
        {
            return $"The condition '{held.Key}' cannot be followed: '{reference}' is not a reference field of the "
                 + "content type.";
        }

        var target = ReferenceConditions.TargetNames(referenceField.ReferenceType!)
            .Select(name => definitions.GetValueOrDefault(name))
            .FirstOrDefault(found => found is not null);

        if (!ReferenceConditions.IsComparable(target, field))
        {
            return $"The condition '{held.Key}' cannot be compared: '{field}' is not a Public field of the content "
                 + $"type '{reference}' points at.";
        }

        return null;
    }
}
