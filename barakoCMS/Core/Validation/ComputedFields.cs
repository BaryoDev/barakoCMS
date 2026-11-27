using System.Globalization;
using System.Text.Json;
using barakoCMS.Core.Expressions;
using barakoCMS.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Computed fields: a field whose value the server works out from other fields of the same entry.
/// </summary>
/// <remarks>
/// A rule is either one of the fixed functions or a formula in the expression language, never both.
/// The function list is fixed, with no code. The definition is checked
/// when the type is saved (inputs exist, their types fit the function, no field reads itself or
/// sits in a cycle). The value is worked out by <c>ContentWriter</c> inside every write, so a
/// route that forgets the rule still cannot skip it, and it is stored with the entry so it filters,
/// sorts and delivers like any field.
///
/// An input that is missing or blank leaves the result out of the entry, except for
/// <c>coalesce</c> and <c>concat</c>, which skip it. A division by zero leaves the result out too.
/// </remarks>
internal static class ComputedFields
{
    public const int MaxRound = MathFunctions.MaxRound;

    private static readonly string[] NumericTypes = ["int", "decimal", "money"];
    private static readonly string[] TextTypes = ["string", "text"];
    private static readonly string[] MomentTypes = ["date", "datetime"];

    /// <summary>The functions, with how many arguments each takes: min, and max (or -1 for no upper limit).</summary>
    private static readonly Dictionary<string, (int Min, int Max)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = (2, -1),
        ["subtract"] = (2, 2),
        ["multiply"] = (2, -1),
        ["divide"] = (2, 2),
        ["round"] = (1, 1),
        ["min"] = (2, -1),
        ["max"] = (2, -1),
        ["hours"] = (2, 2),
        ["days"] = (2, 2),
        ["duration"] = (2, 2),
        ["concat"] = (2, -1),
        ["coalesce"] = (2, -1),
    };

    public static IEnumerable<string> FunctionNames => Functions.Keys;

    public static bool IsComputed(FieldDefinition field) => field.Computed is not null;

    /// <summary>Whether the rule is a formula. A blank <c>Expression</c> is no formula.</summary>
    public static bool HasExpression(ComputedDefinition def) => !string.IsNullOrWhiteSpace(def.Expression);

    /// <summary>
    /// The names a rule reads: its arguments, or the fields its formula names. A formula that does not
    /// parse reads nothing that can be told, so it gives an empty list; the save check refuses it.
    /// </summary>
    public static IReadOnlyList<string> Inputs(ComputedDefinition def) =>
        HasExpression(def)
            ? ExpressionEngine.Names(def.Expression)?.Fields ?? []
            : def.Args ?? [];

    /// <summary>
    /// Whether a rule reads a field of this name, for the places that ask who or what is touched when a
    /// field changes. A formula that does not parse is taken to read every field, the safe answer.
    /// </summary>
    public static bool Reads(ComputedDefinition? def, string field)
    {
        if (def is null)
            return false;

        if (HasExpression(def) && ExpressionEngine.Names(def.Expression) is null)
            return true;

        return Inputs(def).Any(a => Matches(a, field));
    }

    /// <summary>Whether a rule reads any of the named fields. A formula that does not parse counts as reading all of them.</summary>
    public static bool ReadsAny(ComputedDefinition def, IEnumerable<string> names)
    {
        if (HasExpression(def) && ExpressionEngine.Names(def.Expression) is null)
            return names.Any();

        return Inputs(def).Any(a => names.Any(n => Matches(a, n)));
    }

    private static bool IsSpan(string fn) => fn.ToLowerInvariant() is "hours" or "days" or "duration";

    private static bool IsNumeric(string fn) =>
        fn.ToLowerInvariant() is "add" or "subtract" or "multiply" or "divide" or "round" or "min" or "max" or "hours" or "days" or "duration";

    /// <summary>What is wrong with the computed fields of a type, for the type validator.</summary>
    public static List<string> DefinitionErrors(IReadOnlyCollection<FieldDefinition> fields)
        => DefinitionErrors(fields, null);

    /// <summary>
    /// As above, but a computed field in <paramref name="carried"/> is not checked again for role
    /// list and mask, which a type stored before that rule may already break.
    /// </summary>
    public static List<string> DefinitionErrors(IReadOnlyCollection<FieldDefinition> fields, IReadOnlySet<FieldDefinition>? carried)
    {
        var errors = new List<string>();
        var computed = fields.Where(f => f is not null && IsComputed(f) && !string.IsNullOrWhiteSpace(f.Name)).ToList();

        foreach (var field in computed)
            errors.AddRange(FieldErrors(field, fields, carried?.Contains(field) == true));

        if (errors.Count == 0)
            errors.AddRange(CycleErrors(computed));

        return errors;
    }

    private static IEnumerable<string> FieldErrors(FieldDefinition field, IReadOnlyCollection<FieldDefinition> fields, bool carried)
    {
        var def = field.Computed!;
        var name = field.Name;

        if (HasExpression(def))
        {
            foreach (var error in ExpressionErrors(field, fields, carried))
                yield return error;

            yield break;
        }

        if (!Functions.TryGetValue(def.Fn ?? string.Empty, out var arity))
        {
            yield return $"Field '{name}' computes with '{def.Fn}', which is not a function. Use {string.Join(", ", Functions.Keys)}.";
            yield break;
        }

        var fn = def.Fn!.ToLowerInvariant();
        var args = def.Args ?? [];

        if (args.Count < arity.Min || (arity.Max >= 0 && args.Count > arity.Max))
        {
            var takes = arity.Max == arity.Min ? $"{arity.Min}" : arity.Max < 0 ? $"at least {arity.Min}" : $"{arity.Min} to {arity.Max}";
            yield return $"Field '{name}' computes with {fn}, which takes {takes} arguments, and {args.Count} were given.";
            yield break;
        }

        foreach (var error in ShapeErrors(field, fn))
            yield return error;

        var inputs = new List<FieldDefinition>();
        foreach (var arg in args)
        {
            if (IsNumeric(fn) && !IsSpan(fn) && TryLiteral(arg, out _))
                continue;

            var input = fields.FirstOrDefault(f => f is not null && Matches(f.Name, arg));
            if (input is null)
            {
                yield return $"Field '{name}' computes from '{arg}', which the type does not declare.";
                continue;
            }

            inputs.Add(input);

            if (Matches(input.Name, name))
            {
                yield return $"Field '{name}' computes from itself.";
                continue;
            }

            if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(input.Sensitivity))
            {
                yield return $"Field '{name}' is {field.Sensitivity} and computes from '{input.Name}', which is {input.Sensitivity}. "
                    + "A computed value may not be more widely readable than what it is computed from.";
            }

            if (carried)
                continue;

            foreach (var leak in ReadAudienceErrors(field, input))
                yield return leak;
        }

        foreach (var error in InputTypeErrors(field, fn, inputs))
            yield return error;
    }

    /// <summary>The save-time check of a rule that holds a formula.</summary>
    private static IEnumerable<string> ExpressionErrors(FieldDefinition field, IReadOnlyCollection<FieldDefinition> fields, bool carried)
    {
        var def = field.Computed!;
        var name = field.Name;

        if (!string.IsNullOrWhiteSpace(def.Fn) || def.Args is { Count: > 0 })
        {
            yield return $"Field '{name}' has a formula and also fn or args. A computed field takes one or the other.";
            yield break;
        }

        var parsed = ExpressionEngine.ParseAndCheck(def.Expression, FieldTypes(fields), refuseClock: true);
        if (parsed.Expression is not { } expression)
        {
            foreach (var error in parsed.Errors)
                yield return $"Field '{name}' has a formula that is refused ({error.Rule} at position {error.Position}): {error.Message}.";

            yield break;
        }

        foreach (var system in expression.SystemNames)
        {
            yield return $"Field '{name}' has a formula that reads '{system}', which is not part of the entry's data, "
                + "so the stored value would go stale. Read a field instead.";
        }

        if (def.Round is { } digits && (digits < 0 || digits > MaxRound))
            yield return $"Field '{name}' rounds to {digits} places, and rounding is 0 to {MaxRound}.";

        if (def.Round is not null && expression.Type != ExprType.Number)
            yield return $"Field '{name}' has a formula that does not give a number, so it takes no round.";

        if (field.IsRequired)
            yield return $"Field '{name}' is computed by the server, so it cannot be required of a caller.";

        if (field.DefaultValue is not null)
            yield return $"Field '{name}' is computed by the server, so it takes no default value.";

        if (ResultTypeError(field, expression.Type) is { } wrongType)
            yield return wrongType;

        foreach (var read in expression.Fields)
        {
            var input = fields.FirstOrDefault(f => f is not null && Matches(f.Name, read));
            if (input is null)
                continue;

            if (Matches(input.Name, name))
            {
                yield return $"Field '{name}' computes from itself.";
                continue;
            }

            if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(input.Sensitivity))
            {
                yield return $"Field '{name}' is {field.Sensitivity} and computes from '{input.Name}', which is {input.Sensitivity}. "
                    + "A computed value may not be more widely readable than what it is computed from.";
            }

            if (carried)
                continue;

            foreach (var leak in ReadAudienceErrors(field, input))
                yield return leak;
        }
    }

    private static string? ResultTypeError(FieldDefinition field, ExprType result)
    {
        var fieldType = field.Type.ToLowerInvariant();
        var (fits, wants) = result switch
        {
            ExprType.Number => (NumericTypes.Contains(fieldType), "int, decimal or money"),
            ExprType.Text => (TextTypes.Contains(fieldType), "string or text"),
            ExprType.Bool => (fieldType == "bool", "bool"),
            ExprType.Date => (MomentTypes.Contains(fieldType), "date or datetime"),
            _ => (false, string.Empty),
        };

        if (result == ExprType.Null)
            return $"Field '{field.Name}' has a formula that can only give no value. Give it a formula that gives a number, text, a bool or a date.";

        return fits
            ? null
            : $"Field '{field.Name}' has a formula that gives {Values.Word(result)}, so its type has to be {wants}, not {field.Type}.";
    }

    /// <summary>The type a formula sees for a field name: null when the type has no such field, Null when it has one a formula cannot read.</summary>
    private static NameResolver FieldTypes(IReadOnlyCollection<FieldDefinition> fields) => name =>
        fields.FirstOrDefault(f => f is not null && Matches(f.Name, name)) is { } field
            ? ExprTypeOf(field.Type) ?? ExprType.Null
            : null;

    internal static ExprType? ExprTypeOf(string? type) => type?.ToLowerInvariant() switch
    {
        "int" or "decimal" or "money" => ExprType.Number,
        "string" or "text" => ExprType.Text,
        "bool" => ExprType.Bool,
        "date" or "datetime" => ExprType.Date,
        _ => null,
    };

    private static IEnumerable<string> ShapeErrors(FieldDefinition field, string fn)
    {
        var def = field.Computed!;
        var name = field.Name;

        if (def.Round is { } digits && (digits < 0 || digits > MaxRound))
            yield return $"Field '{name}' rounds to {digits} places, and rounding is 0 to {MaxRound}.";

        if (def.Round is not null && !IsNumeric(fn))
            yield return $"Field '{name}' computes with {fn}, which does not give a number, so it takes no round.";

        if (field.IsRequired)
            yield return $"Field '{name}' is computed by the server, so it cannot be required of a caller.";

        if (field.DefaultValue is not null)
            yield return $"Field '{name}' is computed by the server, so it takes no default value.";

        var resultType = field.Type.ToLowerInvariant();
        if (IsNumeric(fn) && !NumericTypes.Contains(resultType))
            yield return $"Field '{name}' computes with {fn}, which gives a number, so its type has to be int, decimal or money, not {field.Type}.";
        if (fn == "concat" && !TextTypes.Contains(resultType))
            yield return $"Field '{name}' computes with concat, which gives text, so its type has to be string or text, not {field.Type}.";
    }

    /// <summary>
    /// Role list and mask, the two ways besides the level that delivery narrows who reads a field.
    /// </summary>
    /// <remarks>
    /// Delivery lets a field's role list replace the capability its level names, so a list is the
    /// audience when present and the level's capability holders are when not. A computed field is
    /// no wider than an input when its list is inside the input's. An input with no list is read by
    /// the holders of its level's capability, so a list on the computed field over it would let
    /// listed roles read it without that capability, and is refused. Roles are compared exactly,
    /// case included, as delivery does. A Public or Members input is read alike by everyone, so a
    /// list on it means nothing and is not checked. A mask only applies to a caller who may not read
    /// the field, and it is stricter in the order Remove, Redact, Last4, so a computed field showing
    /// more of its value than the input does would hand that caller a part of what the input hides.
    /// Public and Members fields are never masked.
    /// </remarks>
    private static IEnumerable<string> ReadAudienceErrors(FieldDefinition field, FieldDefinition input)
    {
        if (RoleListError(field, input) is { } roleError)
            yield return roleError;

        if (MaskStrictness(field) < MaskStrictness(input))
        {
            yield return $"Field '{field.Name}' computes from '{input.Name}', which is masked as {EffectiveMask(input)}, "
                + $"and '{field.Name}' shows {EffectiveMask(field)}. Mask '{field.Name}' at least as strictly (Remove is strictest, then Redact, then Last4).";
        }
    }

    private static string? RoleListError(FieldDefinition field, FieldDefinition input)
    {
        if (SensitivityRank.IsUniform(input.Sensitivity))
            return null;

        var own = field.VisibleToRoles ?? [];
        var who = $"'{field.Name}' ({field.Sensitivity}) computes from '{input.Name}' ({input.Sensitivity})";

        if (input.VisibleToRoles is not { Count: > 0 } allowed)
        {
            return own.Count == 0
                ? null
                : $"Field {who}, which has no role list, so the holders of its level's capability read it. "
                    + $"'{field.Name}' lists {string.Join(", ", own.Select(r => $"'{r}'"))}, which replaces that capability and can let those roles read what they could not. "
                    + $"Remove the list from '{field.Name}', or give '{input.Name}' a list that holds those roles.";
        }

        var wider = own.Count == 0
            ? "has no role list"
            : own.Where(r => !allowed.Contains(r, StringComparer.Ordinal)).ToList() is { Count: > 0 } extra
                ? $"lists {string.Join(", ", extra.Select(r => $"'{r}'"))}, which '{input.Name}' does not"
                : null;

        return wider is null
            ? null
            : $"Field {who}, which only {string.Join(", ", allowed.Select(r => $"'{r}'"))} may read, "
                + $"and '{field.Name}' {wider}. Give '{field.Name}' a visibleToRoles list inside the input's, so it is not readable by more roles than what it is computed from.";
    }

    private static FieldMask EffectiveMask(FieldDefinition f) =>
        f.Mask != FieldMask.Default ? f.Mask : f.Sensitivity == SensitivityLevel.Hidden ? FieldMask.Remove : FieldMask.Redact;

    private static int MaskStrictness(FieldDefinition f)
    {
        if (f.Sensitivity is SensitivityLevel.Public or SensitivityLevel.Members)
            return 0;
        return EffectiveMask(f) switch { FieldMask.Remove => 3, FieldMask.Redact => 2, FieldMask.Last4 => 1, _ => 2 };
    }

    private static IEnumerable<string> InputTypeErrors(FieldDefinition field, string fn, List<FieldDefinition> inputs)
    {
        var name = field.Name;

        if (IsSpan(fn))
        {
            var kinds = inputs.Select(i => KindOf(i.Type)).ToList();
            if (kinds.Count == 2 && (kinds.Contains(null) || kinds[0] != kinds[1]))
            {
                yield return $"Field '{name}' computes with {fn}, which takes two dates, two datetimes or two times of day, "
                    + $"and got {string.Join(" and ", inputs.Select(i => $"'{i.Name}' ({i.Type})"))}.";
            }

            yield break;
        }

        if (IsNumeric(fn))
        {
            foreach (var input in inputs.Where(i => !NumericTypes.Contains(i.Type.ToLowerInvariant())))
                yield return $"Field '{name}' computes with {fn}, which takes numbers, and '{input.Name}' is {input.Type}.";

            yield break;
        }

        if (fn == "coalesce")
        {
            foreach (var input in inputs.Where(i => !string.Equals(i.Type, field.Type, StringComparison.OrdinalIgnoreCase)))
                yield return $"Field '{name}' computes with coalesce, which takes fields of the type of its result, {field.Type}, and '{input.Name}' is {input.Type}.";

            yield break;
        }

        foreach (var input in inputs.Where(i => !IsScalarText(i.Type)))
            yield return $"Field '{name}' computes with concat, which takes single values, and '{input.Name}' is {input.Type}.";
    }

    private static bool IsScalarText(string type) =>
        type.ToLowerInvariant() is not ("array" or "object" or "json" or "geopoint" or "file" or "inlineimage" or "token");

    private static IEnumerable<string> CycleErrors(List<FieldDefinition> computed)
    {
        var edges = computed.ToDictionary(
            f => f.Name,
            f => Inputs(f.Computed!).Where(a => computed.Any(c => Matches(c.Name, a))).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys)
        {
            var path = new List<string>();
            if (Visit(start, edges, state, path))
            {
                yield return $"Computed fields read each other in a circle: {string.Join(", ", path)}.";
                yield break;
            }
        }
    }

    private static bool Visit(string node, Dictionary<string, List<string>> edges, Dictionary<string, int> state, List<string> path)
    {
        if (state.GetValueOrDefault(node) == 2)
            return false;

        if (state.GetValueOrDefault(node) == 1)
        {
            path.Add(node);
            return true;
        }

        state[node] = 1;
        path.Add(node);
        foreach (var next in edges.GetValueOrDefault(node) ?? [])
        {
            var key = edges.Keys.First(k => Matches(k, next));
            if (Visit(key, edges, state, path))
                return true;
        }

        path.RemoveAt(path.Count - 1);
        state[node] = 2;
        return false;
    }

    /// <summary>The computed fields in the order they can be worked out: a field after the computed fields it reads.</summary>
    /// <remarks>Assumes the type passed <see cref="DefinitionErrors"/>. A cycle that got through is cut rather than looped on.</remarks>
    public static List<FieldDefinition> InOrder(IEnumerable<FieldDefinition> fields)
    {
        var computed = fields.Where(f => f is not null && IsComputed(f) && !string.IsNullOrWhiteSpace(f.Name)).ToList();
        return InOrder(computed, Read(computed));
    }

    /// <summary>The formulas of the computed fields, each lexed and parsed once, for the callers that need them more than once.</summary>
    private static Dictionary<FieldDefinition, ExpressionEngine.Syntax> Read(IEnumerable<FieldDefinition> computed)
    {
        var read = new Dictionary<FieldDefinition, ExpressionEngine.Syntax>(ReferenceEqualityComparer.Instance);
        foreach (var field in computed.Where(f => HasExpression(f.Computed!)))
            read[field] = ExpressionEngine.Read(field.Computed!.Expression);

        return read;
    }

    private static IReadOnlyList<string> InputsOf(FieldDefinition field, Dictionary<FieldDefinition, ExpressionEngine.Syntax> read) =>
        read.TryGetValue(field, out var syntax)
            ? ExpressionEngine.Names(syntax)?.Fields ?? []
            : field.Computed!.Args ?? [];

    private static List<FieldDefinition> InOrder(
        List<FieldDefinition> computed, Dictionary<FieldDefinition, ExpressionEngine.Syntax> read)
    {
        // What each field reads among the computed fields, worked out once. Asking again on every pass
        // below parsed every formula once per placed field.
        var dependsOn = computed.ToDictionary(
            f => f,
            f => InputsOf(f, read).Where(a => computed.Any(c => Matches(c.Name, a))).ToList(),
            ReferenceEqualityComparer.Instance);

        var ordered = new List<FieldDefinition>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (ordered.Count < computed.Count)
        {
            var next = computed.Where(f => !placed.Contains(f.Name))
                .FirstOrDefault(f => dependsOn[f].All(placed.Contains));

            next ??= computed.First(f => !placed.Contains(f.Name));
            ordered.Add(next);
            placed.Add(next.Name);
        }

        return ordered;
    }

    /// <summary>
    /// Removes whatever the data holds under each computed field and puts the computed value in its
    /// place, or nothing when the inputs do not give one.
    /// </summary>
    /// <param name="fields">
    /// The type's fields. A formula reads the types of the fields it names from this list, so it is
    /// the whole list and not only the computed fields that is wanted.
    /// </param>
    /// <param name="log">Where a formula that gives no value says why: the field, the rule and the position, never a value.</param>
    public static void Apply(IEnumerable<FieldDefinition> fields, Dictionary<string, object> data, ILogger? log = null)
    {
        var all = fields as IReadOnlyCollection<FieldDefinition> ?? fields.ToList();
        var computed = all.Where(f => f is not null && IsComputed(f) && !string.IsNullOrWhiteSpace(f.Name)).ToList();

        // Each formula is parsed once for the whole write: the order reads its names, and the value
        // reads its tree.
        var read = Read(computed);
        foreach (var field in InOrder(computed, read))
        {
            foreach (var key in data.Keys.Where(k => Matches(k, field.Name)).ToList())
                data.Remove(key);

            if (Compute(field, data, all, log, read.GetValueOrDefault(field)) is { } value)
                data[field.Name] = value;
        }
    }

    /// <summary>The value of one computed field over the data, or null when the inputs give none.</summary>
    /// <param name="fields">The type's fields, for a formula to read the types of what it names. Not needed for a function.</param>
    public static object? Compute(
        FieldDefinition field, IReadOnlyDictionary<string, object> data,
        IReadOnlyCollection<FieldDefinition>? fields = null, ILogger? log = null) =>
        Compute(field, data, fields, log, read: null);

    private static object? Compute(
        FieldDefinition field, IReadOnlyDictionary<string, object> data,
        IReadOnlyCollection<FieldDefinition>? fields, ILogger? log, ExpressionEngine.Syntax? read)
    {
        var def = field.Computed!;
        if (HasExpression(def))
            return ComputeExpression(field, data, fields ?? [field], log, read);

        var fn = def.Fn.ToLowerInvariant();
        var args = def.Args ?? [];

        if (fn == "concat")
        {
            var parts = args.Select(a => TextOf(Find(data, a))).Where(t => !string.IsNullOrEmpty(t)).ToList();
            return parts.Count == 0 ? null : string.Concat(parts);
        }

        if (fn == "coalesce")
        {
            foreach (var arg in args)
            {
                var value = Find(data, arg);
                if (!IsBlank(value))
                    return Unwrap(value!);
            }

            return null;
        }

        if (IsSpan(fn))
        {
            if (!TryMoment(Find(data, args[0]), out var from)
                || !TryMoment(Find(data, args[1]), out var to))
            {
                return null;
            }

            var span = to - from;
            var amount = fn switch
            {
                "hours" => DateMath.Hours(span),
                "days" => DateMath.Days(span),
                _ => DateMath.Minutes(span),
            };
            return Shape(field, amount);
        }

        return ComputeNumber(field, fn, args, data);
    }

    /// <summary>
    /// A formula over the stored data. The formula is checked again here with the default bounds,
    /// because a stored type can predate a bound, and a failure gives no value and a log line that
    /// names the field, the rule and the position. Nothing logged is an entry value or the formula's text.
    /// </summary>
    private static object? ComputeExpression(
        FieldDefinition field, IReadOnlyDictionary<string, object> data, IReadOnlyCollection<FieldDefinition> fields, ILogger? log,
        ExpressionEngine.Syntax? read)
    {
        var parsed = ExpressionEngine.Check(
            read ?? ExpressionEngine.Read(field.Computed!.Expression), FieldTypes(fields), refuseClock: true);
        if (parsed.Expression is not { } expression)
        {
            Say(log, field, parsed.Errors.FirstOrDefault());
            return null;
        }

        if (expression.SystemNames.Count > 0)
        {
            Say(log, field, new ExpressionError("system-name", 0, "a stored formula may not read a system name"));
            return null;
        }

        var result = ExpressionEngine.Evaluate(
            expression, name => name.IsSystem ? null : ValueOf(fields, data, name.Name), TimeProvider.System);

        if (result.Note is { } note)
            Say(log, field, note);

        return Stored(field, result.Value);
    }

    private static void Say(ILogger? log, FieldDefinition field, ExpressionError? error) =>
        log?.LogWarning(
            "Computed field {Field} gave no value: formula rule {Rule} at position {Position}.",
            field.Name, error?.Rule ?? "unknown", error?.Position ?? 0);

    /// <summary>What a formula's result is stored as, in the shape the field's type holds.</summary>
    private static object? Stored(FieldDefinition field, object? value) => value switch
    {
        decimal number => Shape(field, number),
        string text => text,
        bool flag => flag,
        DateTime moment => string.Equals(field.Type, "date", StringComparison.OrdinalIgnoreCase)
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A stored value in the shape the formula's type for that field expects, or null when it is absent or does not fit.</summary>
    internal static object? ValueOf(IReadOnlyCollection<FieldDefinition> fields, IReadOnlyDictionary<string, object> data, string name)
    {
        var kind = fields.FirstOrDefault(f => f is not null && Matches(f.Name, name)) is { } declared
            ? ExprTypeOf(declared.Type)
            : null;

        var raw = Find(data, name);
        return kind switch
        {
            ExprType.Number => NumberOf(raw),
            ExprType.Text => IsNull(raw) ? null : Unwrap(raw!) as string ?? TextOf(raw),
            ExprType.Bool => BoolOf(raw),
            ExprType.Date => MomentOf(raw),
            _ => null,
        };
    }

    private static bool IsNull(object? value) =>
        value is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static bool? BoolOf(object? value) => IsNull(value)
        ? null
        : Unwrap(value!) switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null,
        };

    private static DateTime? MomentOf(object? value)
    {
        if (IsNull(value))
            return null;

        switch (Unwrap(value!))
        {
            case DateTime moment:
                return moment;
            case DateTimeOffset offset:
                return offset.UtcDateTime;
        }

        var text = TextOf(value);
        return text is not null
            && text.Contains('-')
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.UtcDateTime
                : null;
    }

    private static object? ComputeNumber(FieldDefinition field, string fn, List<string> args, IReadOnlyDictionary<string, object> data)
    {
        var numbers = new List<decimal>();
        foreach (var arg in args)
        {
            if (TryLiteral(arg, out var literal))
            {
                numbers.Add(literal);
                continue;
            }

            if (NumberOf(Find(data, arg)) is not { } number)
                return null;

            numbers.Add(number);
        }

        decimal result;
        try
        {
            result = fn switch
            {
                "add" => numbers.Aggregate(0m, (a, b) => a + b),
                "subtract" => numbers[0] - numbers[1],
                "multiply" => numbers.Aggregate(1m, (a, b) => a * b),
                "divide" => numbers[1] == 0 ? throw new DivideByZeroException() : numbers[0] / numbers[1],
                "min" => numbers.Min(),
                "max" => numbers.Max(),
                _ => numbers[0],
            };
        }
        catch (Exception e) when (e is DivideByZeroException or OverflowException)
        {
            return null;
        }

        return Shape(field, result);
    }

    /// <summary>A number rounded as the field asks, and as a whole number for an int field.</summary>
    private static object? Shape(FieldDefinition field, decimal value)
    {
        var digits = string.Equals(field.Type, "int", StringComparison.OrdinalIgnoreCase)
            ? 0
            : field.Computed!.Round ?? (string.Equals(field.Computed.Fn, "round", StringComparison.OrdinalIgnoreCase) ? 0 : (int?)null);

        if (digits is { } places)
            value = Math.Round(value, places, MidpointRounding.AwayFromZero);

        if (!string.Equals(field.Type, "int", StringComparison.OrdinalIgnoreCase))
            return value;

        return value is >= long.MinValue and <= long.MaxValue ? (long)value : null;
    }

    /// <summary>Whether a value sent for a computed field is the one already stored, which a client echoing an entry back sends.</summary>
    public static bool SameAsStored(object? sent, object? stored) =>
        (IsBlank(sent) && IsBlank(stored))
        || (NumberOf(sent) is { } a && NumberOf(stored) is { } b && a == b)
        || (!IsBlank(sent) && !IsBlank(stored) && string.Equals(TextOf(sent), TextOf(stored), StringComparison.Ordinal));

    public static string RefusedMessage(FieldDefinition field) =>
        $"Field '{field.DisplayName}' ({field.Name}) is computed by the server and cannot be set. Leave it out.";

    private static object? Find(IReadOnlyDictionary<string, object> data, string name)
    {
        foreach (var (key, value) in data)
        {
            if (Matches(key, name))
                return value;
        }

        return null;
    }

    private static bool Matches(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryLiteral(string arg, out decimal value) =>
        decimal.TryParse(arg, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string? KindOf(string type) => type.ToLowerInvariant() switch
    {
        "date" or "datetime" => "moment",
        "time" => "time",
        _ => null,
    };

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } je => string.IsNullOrWhiteSpace(je.GetString()),
        _ => false,
    };

    private static object Unwrap(object value) => value is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.String => je.GetString()!,
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.TryGetDecimal(out var m) ? m : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => je.Clone(),
        }
        : value;

    private static string? TextOf(object? value)
    {
        if (IsBlank(value))
            return null;

        return Unwrap(value!) switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };
    }

    private static decimal? NumberOf(object? value)
    {
        if (IsBlank(value))
            return null;

        return Unwrap(value!) switch
        {
            long l => l,
            int i => i,
            decimal d => d,
            double d when Math.Abs(d) < 7.9e28 => (decimal)d,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>A date, a datetime or a time of day as a point on one line, so two of them subtract.</summary>
    private static bool TryMoment(object? value, out TimeSpan point)
    {
        point = default;
        var text = TextOf(value);
        if (text is null)
            return false;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            && text.Contains('-'))
        {
            point = moment.UtcDateTime - DateTime.UnixEpoch;
            return true;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out point);
    }
}
