using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The uniqueness rules a content type declares: checked once when the type is saved, and read on
/// every entry write.
/// </summary>
/// <remarks>
/// A stored rule the API would refuse to save today is skipped on a write instead of applied or
/// failing it. That is a rule naming a field the type no longer has, a field since made Sensitive
/// or Hidden, or a state the lifecycle does not declare. The endpoints refuse to save one, but a
/// type stored some other way can hold one, and a rule nobody can meet would refuse every write of
/// the type. <see cref="Resolve"/> reports what it skipped so the caller can log it.
///
/// Only Public fields. A refusal tells the caller that some entry holds the value they sent, which
/// for a field they may not read is a way to test what it holds.
///
/// Field and state names match ignoring case, which is how the entry validator and the transition
/// service read them.
/// </remarks>
internal static class UniquenessRules
{
    /// <summary>The most rules one type may declare. Each costs a lock and a query on a write.</summary>
    public const int MaxRules = 5;

    public const int MaxFields = 5;

    public const int MaxNameLength = 64;

    private const int MaxEchoLength = 50;

    /// <summary>
    /// The field types a rule may name: the ones whose value is one text, number or boolean, and
    /// whose spellings of one value the comparison can tell apart.
    /// </summary>
    /// <remarks>
    /// An allow list, so a field type added later is refused until it is placed here on purpose.
    /// Not <c>date</c>, <c>datetime</c> or <c>time</c>: the validator accepts one instant or one time
    /// of day in many spellings (<c>2026-10-02T00:00:00Z</c> and <c>2026-10-02T08:00:00+08:00</c>,
    /// <c>9:00</c> and <c>09:00</c>) and stores each as written, and a collection sync writes a date
    /// in the serializer's form. Compared as text, the same value written two ways would pass, and
    /// the database cannot read every spelling .NET accepts, so the two sides could not normalise
    /// them alike.
    /// </remarks>
    public static IReadOnlyList<string> FieldTypes { get; } =
    [
        "string", "text", "int", "decimal", "money", "bool",
        "email", "url", "slug", "uuid", "reference", "choice",
    ];

    /// <summary>How one field of a rule is read off an entry.</summary>
    public const string PlainKind = "plain";

    /// <summary>
    /// An id held as text. Compared without regard to case, braces, parentheses or dashes, since
    /// the entry validator accepts an id in any of those spellings and stores it as written.
    /// </summary>
    public const string IdKind = "id";

    /// <summary>The entry's creator, not a data field.</summary>
    public const string CreatorKind = "creator";

    /// <summary>
    /// An email address, compared with A to Z lowered and nothing else, on both sides, so
    /// <c>A@x.com</c> and <c>a@x.com</c> are one address. Letters outside A to Z are compared as
    /// written, because the database's lower case and .NET's need not agree on them.
    /// </summary>
    public const string EmailKind = "email";

    /// <param name="Name">The field as the type declares it, or <see cref="UniquenessRule.CreatedByField"/>.</param>
    /// <param name="Kind">One of <see cref="PlainKind"/>, <see cref="IdKind"/> and <see cref="CreatorKind"/>.</param>
    public sealed record Part(string Name, string Kind);

    /// <summary>A stored rule a write applies.</summary>
    /// <param name="InitialState">The state an entry with none yet is read as being in.</param>
    public sealed record Usable(string Name, IReadOnlyList<Part> Parts, string? WhenState, string? InitialState);

    /// <param name="Usable">The rules a write applies.</param>
    /// <param name="Skipped">The names of the stored rules a save would refuse today.</param>
    public sealed record Resolved(IReadOnlyList<Usable> Usable, IReadOnlyList<string> Skipped);

    /// <summary>What is wrong with the rules a type declares, for the type validator.</summary>
    public static List<string> DefinitionErrors(
        IReadOnlyList<UniquenessRule>? rules,
        IReadOnlyCollection<FieldDefinition>? fields,
        LifecycleDefinition? lifecycle)
    {
        var errors = new List<string>();
        if (rules is null || rules.Count == 0)
            return errors;

        // One error for an oversized list, so it cannot turn into an equally oversized response.
        if (rules.Count > MaxRules)
        {
            errors.Add($"A type declares at most {MaxRules} uniqueness rules, and {rules.Count} were sent.");
            return errors;
        }

        foreach (var rule in rules)
            errors.AddRange(Problems(rule, fields ?? [], lifecycle));

        var repeated = rules
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var name in repeated)
            errors.Add($"Uniqueness rule '{Shorten(name)}' is declared more than once, ignoring case.");

        return errors;
    }

    /// <summary>The stored rules against the type as it is now.</summary>
    public static Resolved Resolve(ContentTypeDefinition definition)
    {
        var usable = new List<Usable>();
        var skipped = new List<string>();
        var fields = definition.Fields ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in (definition.Uniqueness ?? []).Take(MaxRules))
        {
            if (rule is null)
                continue;

            if (Problems(rule, fields, definition.Lifecycle).Count > 0 || !seen.Add(rule.Name))
            {
                skipped.Add(Shorten(rule.Name ?? string.Empty));
                continue;
            }

            var parts = rule.Fields
                .Select(name => IsCreator(name)
                    ? new Part(UniquenessRule.CreatedByField, CreatorKind)
                    : PartFor(fields.First(f => f is not null && Matches(f.Name, name))))
                .ToList();

            usable.Add(new Usable(
                rule.Name,
                parts,
                string.IsNullOrWhiteSpace(rule.WhenState) ? null : rule.WhenState,
                definition.Lifecycle?.InitialState));
        }

        if ((definition.Uniqueness?.Count ?? 0) > MaxRules)
            skipped.AddRange(definition.Uniqueness!.Skip(MaxRules).Select(r => Shorten(r?.Name ?? string.Empty)));

        return new Resolved(usable, skipped);
    }

    /// <summary>The rules of <paramref name="definition"/> that name <paramref name="fieldName"/>.</summary>
    public static List<string> NamedBy(ContentTypeDefinition definition, string fieldName) =>
        (definition.Uniqueness ?? [])
            .Where(r => r?.Fields is not null && r.Fields.Any(f => Matches(f, fieldName)))
            .Select(r => r.Name)
            .ToList();

    /// <summary>Whether two rules count the same entries and compare the same fields in the same order.</summary>
    public static bool Same(UniquenessRule a, UniquenessRule b) =>
        Matches(a.Name, b.Name)
        && Matches(Blank(a.WhenState), Blank(b.WhenState))
        && (a.Fields ?? []).SequenceEqual(b.Fields ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether two rule lists hold the same rules, in any order.</summary>
    public static bool Same(IReadOnlyList<UniquenessRule>? a, IReadOnlyList<UniquenessRule>? b)
    {
        var left = (a ?? []).Where(r => r is not null).ToList();
        var right = (b ?? []).Where(r => r is not null).ToList();

        return left.Count == right.Count
            && left.All(l => right.Count(r => Same(l, r)) == 1);
    }

    public static bool IsCreator(string? name) =>
        string.Equals(name, UniquenessRule.CreatedByField, StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Part PartFor(FieldDefinition field) =>
        new(field.Name,
            IsType(field.Type, "reference") || IsType(field.Type, "uuid") ? IdKind
            : IsType(field.Type, "email") ? EmailKind
            : PlainKind);

    private static List<string> Problems(
        UniquenessRule? rule, IReadOnlyCollection<FieldDefinition> fields, LifecycleDefinition? lifecycle)
    {
        var errors = new List<string>();

        if (rule is null)
        {
            errors.Add("A uniqueness rule is empty.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("Every uniqueness rule needs a name, because the refusal names it.");
            return errors;
        }

        var label = Shorten(rule.Name);

        if (rule.Name.Length > MaxNameLength || !IsPascalCase(rule.Name))
        {
            errors.Add($"Uniqueness rule '{label}' must be named in PascalCase, letters and digits only, "
                + $"in at most {MaxNameLength} characters.");
        }

        var names = rule.Fields ?? [];

        if (names.Count == 0)
        {
            errors.Add($"Uniqueness rule '{label}' names no field.");
        }
        else if (names.Count > MaxFields)
        {
            errors.Add($"Uniqueness rule '{label}' names {names.Count} fields, and a rule takes at most {MaxFields}.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Uniqueness rule '{label}' has a blank name in fields.");
                    continue;
                }

                if (!seen.Add(name))
                {
                    errors.Add($"Uniqueness rule '{label}' names the field '{Shorten(name)}' more than once, ignoring case.");
                    continue;
                }

                if (IsCreator(name))
                    continue;

                var field = fields.FirstOrDefault(f => f is not null && Matches(f.Name, name));

                if (field is null)
                {
                    errors.Add($"Uniqueness rule '{label}' names the field '{Shorten(name)}', which the type does not declare.");
                }
                else if (!IsComparable(field.Type) || field.Multiple)
                {
                    errors.Add($"Uniqueness rule '{label}' names the field '{field.Name}', and a rule compares a field "
                        + $"holding one text, number or boolean: {string.Join(", ", FieldTypes)}, and a choice that takes one option.");
                }
                else if (field.Sensitivity != SensitivityLevel.Public)
                {
                    errors.Add($"Uniqueness rule '{label}' names the field '{field.Name}', which is {field.Sensitivity}. "
                        + "A refusal would tell a caller who may not read the field that an entry holds the value they sent.");
                }
            }
        }

        if (rule.WhenState is not null)
        {
            if (string.IsNullOrWhiteSpace(rule.WhenState))
            {
                errors.Add($"Uniqueness rule '{label}' has a blank whenState. Leave it out to count every entry.");
            }
            else if (lifecycle is null)
            {
                errors.Add($"Uniqueness rule '{label}' names the state '{Shorten(rule.WhenState)}', and the type declares no lifecycle.");
            }
            else if (!(lifecycle.States ?? []).Contains(rule.WhenState, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Uniqueness rule '{label}' names the state '{Shorten(rule.WhenState)}', which is not a declared state.");
            }
        }

        return errors;
    }

    private static bool IsComparable(string? type) =>
        !string.IsNullOrWhiteSpace(type) && FieldTypes.Any(known => IsType(type, known));

    /// <summary>Whether a field's type is <paramref name="canonical"/> or one of its aliases.</summary>
    private static bool IsType(string? type, string canonical) =>
        Matches(type, canonical)
        || FieldTypeRegistry.AliasesOf(canonical).Contains(type ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    private static bool IsPascalCase(string name) =>
        char.IsUpper(name[0]) && name.All(char.IsLetterOrDigit);

    private static string Shorten(string value) =>
        value.Length > MaxEchoLength ? value[..MaxEchoLength] : value;
}
