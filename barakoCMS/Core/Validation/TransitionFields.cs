using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The fields a <see cref="StateTransition"/> requires or takes, checked once when a type is saved
/// and read on every move.
/// </summary>
/// <remarks>
/// A stored transition naming a field the type no longer has is skipped on a move instead of
/// failing it. The API refuses to save one and an import refuses to drop such a field, but a type
/// stored some other way can still hold one, and a requirement nobody can meet would leave every
/// entry stuck in its state. <see cref="Resolve"/> reports what it skipped so the caller can log it.
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
    /// The required fields this move was not given a value for.
    /// </summary>
    /// <param name="data">The entry as it will be stored, or null when the request sent nothing.</param>
    /// <param name="sent">The value the request sent for each field, as the request held it.</param>
    /// <remarks>
    /// A value already on the entry does not count. An entry rejected, sent back and rejected again
    /// would otherwise pass on the first rejection's reason, and the workflow on the move would send
    /// that one out again. Blank is read the way a required field is read on an entry write.
    ///
    /// <paramref name="data"/> is read after write-path sensitivity has run. That step puts the
    /// stored value back for a field the caller may not see, so a sent value that did not survive it
    /// counts as not sent, and the move is refused instead of going through with the caller's value
    /// thrown away. Dropping the <c>WasSent</c> test is what lets a stored value stand in.
    /// </remarks>
    public static List<FieldDefinition> NotSent(
        Resolved resolved,
        IReadOnlyDictionary<string, object>? data,
        IReadOnlyDictionary<FieldDefinition, object?> sent)
    {
        if (data is null)
            return resolved.Required.ToList();

        var missing = new List<FieldDefinition>();

        foreach (var field in resolved.Required)
        {
            var pair = data.FirstOrDefault(kv => Matches(kv.Key, field.Name));
            if (pair.Key is null
                || ContentValidatorService.IsBlank(field, pair.Value)
                || !WasSent(field, pair.Value, sent))
            {
                missing.Add(field);
            }
        }

        return missing;
    }

    // The same object, not an equal one: a stored value that happens to equal what was sent was
    // still put there by the sensitivity step and not by the caller.
    private static bool WasSent(FieldDefinition field, object? kept, IReadOnlyDictionary<FieldDefinition, object?> sent) =>
        sent.TryGetValue(field, out var value) && ReferenceEquals(value, kept);

    public static bool Matches(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string value) => value.Length > MaxNameLength ? value[..MaxNameLength] : value;
}
