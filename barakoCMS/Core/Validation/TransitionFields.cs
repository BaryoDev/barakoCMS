using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The fields a <see cref="StateTransition"/> requires or takes, checked once when a type is saved
/// and read on every move.
/// </summary>
/// <remarks>
/// A stored transition naming a field the type no longer has is skipped on a move instead of
/// failing it. A Portability import replaces a type's fields and keeps its lifecycle, so the two can
/// drift apart, and a requirement nobody can meet would leave every entry stuck in its state.
/// <see cref="Resolve"/> reports what it skipped so the caller can log it.
///
/// Names match a field ignoring case and the first match wins, which is how the entry validator
/// reads a data bag.
/// </remarks>
internal static class TransitionFields
{
    private const int MaxNameLength = 50;

    /// <summary>What a transition's field lists resolve to against the type's fields.</summary>
    /// <param name="Required">Fields that must hold a value once the move is made.</param>
    /// <param name="Optional">Fields that may be sent with the move.</param>
    /// <param name="Skipped">Names that match no field of the type.</param>
    public sealed record Resolved(
        IReadOnlyList<FieldDefinition> Required,
        IReadOnlyList<FieldDefinition> Optional,
        IReadOnlyList<string> Skipped)
    {
        /// <summary>The field a request key writes to, or null when the transition does not take it.</summary>
        public FieldDefinition? Find(string key) =>
            Required.Concat(Optional).FirstOrDefault(f => Matches(f.Name, key));

        public bool TakesFields => Required.Count > 0 || Optional.Count > 0;

        public IEnumerable<string> Names => Required.Concat(Optional).Select(f => f.Name);
    }

    /// <summary>What is wrong with the fields a transition names, for the type validator.</summary>
    public static List<string> DefinitionErrors(StateTransition transition, IReadOnlyCollection<FieldDefinition> fields)
    {
        var errors = new List<string>();
        var required = transition.RequiredFields ?? [];
        var optional = transition.OptionalFields ?? [];
        if (required.Count == 0 && optional.Count == 0)
            return errors;

        var label = string.IsNullOrWhiteSpace(transition.Name) ? "(unnamed)" : Shorten(transition.Name);

        // One error for an oversized list, so it cannot turn into an equally oversized response.
        if (required.Count + optional.Count > fields.Count)
        {
            errors.Add($"Transition '{label}' names {required.Count + optional.Count} fields, and the "
                + $"type declares {fields.Count}.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (list, names) in new[] { ("requiredFields", required), ("optionalFields", optional) })
        {
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Transition '{label}' has a blank name in {list}.");
                    continue;
                }

                if (!fields.Any(f => f is not null && Matches(f.Name, name)))
                {
                    errors.Add($"Transition '{label}' names the field '{Shorten(name)}' in {list}, "
                        + "which the type does not declare.");
                    continue;
                }

                if (!seen.Add(name))
                    errors.Add($"Transition '{label}' names the field '{Shorten(name)}' more than once "
                        + "across requiredFields and optionalFields, ignoring case.");
            }
        }

        return errors;
    }

    /// <summary>The fields a stored transition requires and takes, against the type as it is now.</summary>
    /// <remarks>
    /// A name in both lists is read as required: the stricter reading, and one a caller can still
    /// meet. A repeated name is read once.
    /// </remarks>
    public static Resolved Resolve(StateTransition transition, ContentTypeDefinition definition)
    {
        var required = new List<FieldDefinition>();
        var optional = new List<FieldDefinition>();
        var skipped = new List<string>();
        var fields = definition.Fields ?? [];

        foreach (var (target, names) in new[] { (required, transition.RequiredFields), (optional, transition.OptionalFields) })
        {
            foreach (var name in names ?? [])
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var field = fields.FirstOrDefault(f => f is not null && Matches(f.Name, name));
                if (field is null)
                {
                    skipped.Add(name);
                    continue;
                }

                if (!required.Contains(field) && !optional.Contains(field))
                    target.Add(field);
            }
        }

        return new Resolved(required, optional, skipped);
    }

    /// <summary>
    /// The required fields the data bag leaves blank, read the way a required field is read on an
    /// entry write.
    /// </summary>
    public static List<FieldDefinition> Blank(Resolved resolved, IReadOnlyDictionary<string, object> data)
    {
        var blank = new List<FieldDefinition>();

        foreach (var field in resolved.Required)
        {
            var pair = data.FirstOrDefault(kv => Matches(kv.Key, field.Name));
            if (pair.Key is null || ContentValidatorService.IsBlank(field, pair.Value))
                blank.Add(field);
        }

        return blank;
    }

    public static bool Matches(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string value) => value.Length > MaxNameLength ? value[..MaxNameLength] : value;
}
