using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>Where else a type's definition names one of its fields.</summary>
/// <remarks>
/// A uniqueness rule, a transition, a numbering rule and a lookup name fields by their names. A
/// rename has to carry them along, or the rule stops comparing the field, the transition demands a
/// field the type no longer has, the number is written under a name the type does not declare, and
/// the lookup matches or returns nothing.
/// </remarks>
internal static class FieldNames
{
    /// <summary>The uniqueness rules, transitions, numbering rule and lookup that name a field, as text for an error.</summary>
    public static List<string> NamedBy(ContentTypeDefinition definition, string field)
    {
        var named = new List<string>();
        named.AddRange((definition.Uniqueness ?? new List<UniquenessRule>())
            .Where(r => r.Fields.Any(f => Same(f, field)))
            .Select(r => $"uniqueness rule '{r.Name}'"));
        named.AddRange((definition.Lifecycle?.Transitions ?? new List<StateTransition>())
            .Where(t => t.RequiredFields.Concat(t.OptionalFields).Any(f => Same(f, field)))
            .Select(t => $"transition '{t.Name}'"));
        if (definition.Numbering is { } numbering && Same(numbering.Field, field))
        {
            named.Add("the numbering rule");
        }

        if (definition.Lookup is { } lookup && LookupNames(lookup).Any(f => Same(f, field)))
        {
            named.Add("the lookup");
        }

        return named;
    }

    /// <summary>Renames every mention of a field in the type's rules, transitions, numbering rule and lookup.</summary>
    public static void Rename(ContentTypeDefinition definition, string from, string to)
    {
        foreach (var rule in definition.Uniqueness ?? new List<UniquenessRule>())
        {
            Replace(rule.Fields, from, to);
        }

        foreach (var transition in definition.Lifecycle?.Transitions ?? new List<StateTransition>())
        {
            Replace(transition.RequiredFields, from, to);
            Replace(transition.OptionalFields, from, to);
        }

        if (definition.Numbering is { } numbering && Same(numbering.Field, from))
        {
            numbering.Field = to;
        }

        if (definition.Lookup is { } lookup)
        {
            if (Same(lookup.MatchField, from))
            {
                lookup.MatchField = to;
            }

            Replace(lookup.VerifyFields, from, to);
            Replace(lookup.ReturnFields, from, to);
            if (lookup.Reveal is { } reveal)
            {
                Replace(reveal.Fields, from, to);
            }
        }
    }

    private static IEnumerable<string> LookupNames(LookupDefinition lookup) =>
        new[] { lookup.MatchField }.Concat(lookup.VerifyFields).Concat(lookup.ReturnFields).Concat(lookup.Reveal?.Fields ?? []);

    private static void Replace(List<string> names, string from, string to)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (Same(names[i], from))
            {
                names[i] = to;
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
