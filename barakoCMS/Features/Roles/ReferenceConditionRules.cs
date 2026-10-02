using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>
/// What a role write checks about a condition that follows a reference, such as
/// <c>Class.InstructorUser</c>.
/// </summary>
/// <remarks>
/// A condition that cannot resolve denies when it is evaluated, so none of this decides access. It
/// is here so the mistake is reported to whoever saves the role, not found later as a role that
/// reads nothing.
///
/// Two halves, the way a saved sync is checked: <see cref="ShapeErrors"/> needs no database and
/// runs in each slice's validator, and <see cref="CheckAsync"/> reads the tenant's content types
/// and runs in the endpoint.
///
/// A message names the condition only once its key has the shape of two names, and never the
/// content type slug, which is whatever the request sent.
/// </remarks>
internal static class ReferenceConditionRules
{
    private const int MaxErrors = 20;

    private static readonly string OperatorList = string.Join(", ", ReferenceConditions.Operators);

    /// <summary>The rules of one permission, each with whether it is the Create rule.</summary>
    private static IEnumerable<(PermissionRule? Rule, bool IsCreate)> RulesOf(ContentTypePermission permission)
    {
        yield return (permission.Create, true);
        yield return (permission.Read, false);
        yield return (permission.Update, false);
        yield return (permission.Delete, false);

        if (permission.Transitions is null)
            yield break;

        foreach (var transition in permission.Transitions.Values)
            yield return (transition, false);
    }

    public static List<string> ShapeErrors(List<ContentTypePermission>? permissions)
    {
        var errors = new List<string>();

        foreach (var permission in permissions ?? [])
        {
            if (permission is null)
                continue;

            foreach (var (rule, isCreate) in RulesOf(permission))
            {
                if (rule?.Conditions is null)
                    continue;

                foreach (var (key, operators) in rule.Conditions)
                {
                    if (!ReferenceConditions.IsPath(key))
                        continue;

                    if (ShapeError(key, operators, isCreate) is { } error && !errors.Contains(error))
                        errors.Add(error);

                    if (errors.Count >= MaxErrors)
                        return errors;
                }
            }
        }

        return errors;
    }

    private static string? ShapeError(string key, object? operators, bool isCreate)
    {
        if (!ReferenceConditions.TrySplit(key, out _, out _))
        {
            return "A condition key holding a dot follows one reference to a field of the entry it points at, "
                 + "written Reference.Field. Each name starts with a letter and holds letters, digits and "
                 + $"underscores, {ReferenceConditions.MaxNameLength} characters at most. A second dot is refused: "
                 + "one reference is followed, not two.";
        }

        if (isCreate)
        {
            return $"The condition '{key}' is on a Create rule. Creating has no stored entry to follow a "
                 + "reference from, and a Create rule's conditions are not evaluated.";
        }

        var names = ReferenceConditions.OperatorNames(operators);
        if (names is not { Count: > 0 } || names.Any(name => !ReferenceConditions.Operators.Contains(name)))
            return $"The condition '{key}' needs at least one operator, and only these: {OperatorList}.";

        return null;
    }

    /// <summary>
    /// Checks every well-formed path against this tenant's content types: the rule's type exists,
    /// the first name is a reference field on it, and the second is a Public field of the type the
    /// reference points at.
    /// </summary>
    /// <remarks>
    /// Roles are stored once for every tenant and content types per tenant, so this answers for the
    /// tenant the request was made in. In another tenant the same condition is resolved against
    /// that tenant's types when it is evaluated, and denies where they do not declare it.
    ///
    /// "Not a field" and "not a reference" are one message, and so are "not a field" and "not
    /// Public" on the referenced type. Saving a role does not take the capability that reads a
    /// type's schema, and two answers would say which fields a type has.
    /// </remarks>
    public static async Task<List<string>> CheckAsync(
        IQuerySession session, List<ContentTypePermission>? permissions, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var definitions = new Dictionary<string, ContentTypeDefinition?>(StringComparer.Ordinal);

        async Task<ContentTypeDefinition?> DefinitionAsync(string name)
        {
            if (definitions.TryGetValue(name, out var known))
                return known;

            var definition = await ReferenceConditions.DefinitionAsync(session, name, cancellationToken);
            definitions[name] = definition;
            return definition;
        }

        foreach (var permission in permissions ?? [])
        {
            if (permission is null)
                continue;

            foreach (var (rule, _) in RulesOf(permission))
            {
                if (rule?.Conditions is null)
                    continue;

                foreach (var key in rule.Conditions.Keys)
                {
                    // A malformed key is the validator's to report, and has no names to look up.
                    if (!ReferenceConditions.TrySplit(key, out var reference, out var field))
                        continue;

                    var error = await ErrorAsync(permission.ContentTypeSlug, key, reference, field, DefinitionAsync);
                    if (error is not null && !errors.Contains(error))
                        errors.Add(error);

                    if (errors.Count >= MaxErrors)
                        return errors;
                }
            }
        }

        return errors;
    }

    private static async Task<string?> ErrorAsync(
        string? contentTypeSlug,
        string key,
        string reference,
        string field,
        Func<string, Task<ContentTypeDefinition?>> definitionAsync)
    {
        var definition = string.IsNullOrEmpty(contentTypeSlug) ? null : await definitionAsync(contentTypeSlug);
        if (definition is null)
        {
            return $"The condition '{key}' is on a content type this tenant does not define, so the reference "
                 + "it follows cannot be checked.";
        }

        var referenceField = ReferenceConditions.ReferenceField(definition, reference);
        if (referenceField is null)
            return $"The condition '{key}' cannot be followed: '{reference}' is not a reference field of the content type.";

        var target = await ReferenceConditions.TargetDefinitionAsync(definitionAsync, referenceField.ReferenceType!);
        if (!ReferenceConditions.IsComparable(target, field))
        {
            return $"The condition '{key}' cannot be compared: '{field}' is not a Public field of the content type "
                 + $"'{reference}' points at.";
        }

        return null;
    }
}
