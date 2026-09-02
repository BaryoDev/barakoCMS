using System.Text;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

/// <summary>A compiled read predicate, or the reason there is not one.</summary>
/// <param name="Sql">
/// A SQL fragment for Marten's <c>MatchesSql</c>, with <c>?</c> placeholders, or null when the rules
/// could not be compiled and the caller has to evaluate them per item instead.
/// </param>
public sealed record ReadPredicate(string? Sql, object[] Parameters)
{
    /// <summary>Nothing was compiled. The caller falls back to per-item evaluation.</summary>
    public static readonly ReadPredicate None = new(null, []);

    /// <summary>Every row of the type, which is what a rule with no conditions grants.</summary>
    public static ReadPredicate All { get; } = new("TRUE", []);

    /// <summary>No rows. What an empty set of granting rules means.</summary>
    public static ReadPredicate Nothing { get; } = new("FALSE", []);

    public bool Compiled => Sql is not null;
}

/// <summary>
/// Turns permission conditions into a SQL predicate, or declines to.
/// </summary>
/// <remarks>
/// The whole risk of this class is that it disagrees with <see cref="ConditionEvaluator"/>. Two
/// evaluators for one language drift, and the drift shows up as rows silently appearing or
/// vanishing rather than as an error, so the rule here is: compile only what can be shown to mean
/// the same thing, and return <see cref="ReadPredicate.None"/> for everything else. Declining costs
/// a slow query. Guessing costs somebody seeing a row they may not.
///
/// <c>PermissionPredicateAgreementTests</c> runs generated rules and generated content through both
/// and asserts they agree. If that test is ever deleted this class becomes a liability, because
/// nothing else can tell you the two have diverged.
///
/// What is deliberately not compiled, and why:
///
/// <list type="bullet">
/// <item><c>$status</c>: the evaluator compares the enum NAME while Marten stores the enum as an
/// integer, so an equivalent predicate would have to carry its own copy of the name-to-number map
/// and stay in step with it.</item>
/// <item>Any expected value that is not a string or a list of strings. The evaluator compares
/// <c>ToString()</c> of both sides, and reproducing .NET number and date formatting in SQL is a
/// bigger surface than the feature is worth.</item>
/// <item>Unknown operators and unknown <c>$</c> fields. Both deny in the evaluator, so a predicate
/// COULD be constant false, but a rule containing one is a configuration mistake and falling back
/// keeps one answer rather than two.</item>
/// </list>
/// </remarks>
internal static class PermissionPredicateCompiler
{
    /// <summary>
    /// Compiles the union of the rules that grant, as one predicate.
    /// </summary>
    /// <param name="rules">The enabled rules for this type and action, one per granting role.</param>
    /// <param name="userId">Substituted for <c>$CURRENT_USER</c>.</param>
    public static ReadPredicate Compile(IReadOnlyList<Models.PermissionRule> rules, Guid userId)
    {
        // Additive union: any rule granting is enough, so this is an OR and an empty set is FALSE.
        // Same shape as the loop in PermissionResolver, deliberately.
        if (rules.Count == 0) return ReadPredicate.Nothing;

        var parts = new List<string>(rules.Count);
        var parameters = new List<object>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;

            if (rule.Conditions is null || rule.Conditions.Count == 0)
            {
                // One unconditional grant makes the whole union unconditional, and short-circuiting
                // here also drops every parameter the other rules would have bound.
                return ReadPredicate.All;
            }

            if (!TryCompileRule(rule.Conditions, userId, out var sql, out var ruleParameters))
            {
                return ReadPredicate.None;
            }

            parts.Add($"({sql})");
            parameters.AddRange(ruleParameters);
        }

        return parts.Count == 0
            ? ReadPredicate.Nothing
            : new ReadPredicate(string.Join(" OR ", parts), parameters.ToArray());
    }

    /// <summary>All the conditions of one rule, ANDed, or false when any of them cannot compile.</summary>
    private static bool TryCompileRule(
        Dictionary<string, object> conditions, Guid userId, out string sql, out List<object> parameters)
    {
        sql = string.Empty;
        parameters = [];

        var parts = new List<string>(conditions.Count);

        foreach (var (field, rawOperators) in conditions)
        {
            if (Normalize(rawOperators) is not Dictionary<string, object> operators)
            {
                return false;
            }

            if (!TryValueExpression(field, out var value, out var presence, out var fieldParameters))
            {
                return false;
            }

            foreach (var (op, rawExpected) in operators)
            {
                if (!TryCompileOperator(op, value, Normalize(rawExpected), userId, out var comparison, out var opParameters))
                {
                    return false;
                }

                // The check that stops the whole class of bug this file is exposed to. Parameters
                // bind positionally, so an expression that mentions the value one more time than the
                // caller counted does not fail: it shifts every parameter after it and quietly asks
                // a different question. Refusing here turns that into a fallback rather than a wrong
                // answer, and the test suite into the place it is found.
                var placeholderCount = (presence ?? string.Empty).Count(ch => ch == '?') + comparison.Count(ch => ch == '?');

                if (placeholderCount != fieldParameters.Count + opParameters.Count)
                {
                    return false;
                }

                // The presence check rides with every comparison rather than being emitted once,
                // because a field the document does not have denies the rule in the evaluator
                // whatever the operator was, including the negative ones.
                parts.Add(presence is null ? comparison : $"{presence} AND {comparison}");
                parameters.AddRange(fieldParameters);
                parameters.AddRange(opParameters);
            }
        }

        // A condition value that is an empty operator object constrains nothing, which is what the
        // evaluator's inner loop does with it too.
        sql = parts.Count == 0 ? "TRUE" : string.Join(" AND ", parts);
        return true;
    }

    /// <summary>
    /// The SQL that reads the value a condition names, and the check that it is there at all.
    /// </summary>
    /// <remarks>
    /// The key lookup is <c>-&gt;</c> with a bound key, which is case SENSITIVE, and that is not an
    /// oversight. <c>Content.Data</c> is a plain <c>Dictionary&lt;string, object&gt;</c> with the
    /// default comparer, so <c>TryGetValue</c> is case sensitive too. The public delivery filters
    /// next door match keys case-insensitively because the projection they mirror does; copying that
    /// here would grant on entries the evaluator denies, which is the wrong direction to be wrong in.
    ///
    /// <c>COALESCE(... , '')</c> is the other half of matching the evaluator, and the empty string is
    /// the point. A JSON null reaches the evaluator as a <c>JsonElement</c>, and
    /// <c>JsonElement.ToString()</c> returns <b>String.Empty</b> for the Null kind, not the text
    /// "null" its raw JSON would suggest. Postgres <c>#&gt;&gt;</c> gives SQL NULL for the same value,
    /// which compares as nothing at all, so without the coalesce a null-valued field would match an
    /// <c>_in</c> containing "" in the evaluator and match nothing here.
    ///
    /// This was written as 'null' first, from reading the JSON rather than the method, and the
    /// agreement test found it. An absent key also produces SQL NULL here, which is why the presence
    /// check is a separate term rather than folded into the coalesce.
    /// </remarks>
    private static bool TryValueExpression(
        string field, out string value, out string? presence, out List<object> parameters)
    {
        value = string.Empty;
        presence = null;
        parameters = [];

        if (!field.StartsWith('$'))
        {
            value = Text("d.data -> 'Data' -> ?");
            presence = "(d.data -> 'Data' -> ?) IS NOT NULL";

            // Bound twice, in the order the fragment reads: the presence check first, then the
            // value. MatchesSql binds positionally and does not care that it is the same string.
            parameters = [field, field];
            return true;
        }

        switch (field[1..].ToLowerInvariant())
        {
            case "createdby":
                // Marten writes a Guid as its "D" string, which is what Guid.ToString() gives the
                // evaluator, so no formatting has to be reproduced.
                value = "(d.data ->> 'CreatedBy')";
                return true;

            case "lastmodifiedby":
                value = "(d.data ->> 'LastModifiedBy')";
                return true;

            default:
                // Includes $status, which is the one that looks compilable and is not.
                return false;
        }
    }

    /// <summary>
    /// The text <c>JsonElement.ToString()</c> would give for a jsonb value.
    /// </summary>
    /// <remarks>
    /// Every comparison in <see cref="ConditionEvaluator"/> is
    /// <c>actualValue?.ToString() == expectedValue?.ToString()</c>, and after a Marten round trip
    /// <c>actualValue</c> is a <c>JsonElement</c>. So this has to reproduce that method, not what the
    /// JSON looks like:
    ///
    /// <list type="bullet">
    /// <item>A JSON null is SQL NULL here, because it reaches the evaluator as a C# null reference
    /// rather than as a <c>JsonElement</c>, and <c>null?.ToString()</c> is null rather than the empty
    /// string. So it equals nothing, is in nothing, and differs from everything, which is exactly
    /// what SQL NULL does under the operators below.</item>
    /// <item>True and False return <c>bool.TrueString</c> and <c>bool.FalseString</c>, which are
    /// "True" and "False" with a capital letter, while Postgres renders them lower case.</item>
    /// <item>String returns the string without its quotes, which is what <c>#&gt;&gt;</c> gives.</item>
    /// <item>Everything else returns the raw JSON text.</item>
    /// </list>
    ///
    /// The booleans are the ones nobody would guess. Every one of these was found by the agreement
    /// test rather than by reading, which is the argument for that test in one paragraph.
    /// </remarks>
    /// <param name="extraction">
    /// A jsonb expression, which may contain exactly one <c>?</c>. It is wrapped in a subquery so it
    /// appears once: writing the CASE arms against it directly would repeat the expression four
    /// times, and with it the placeholder, so the caller would have to bind the field name four
    /// times in the right order. Binding by counting occurrences of a string is how a predicate ends
    /// up asking about the wrong column.
    /// </param>
    private static string Text(string extraction) =>
        "(SELECT CASE jsonb_typeof(t.v) "
      + "WHEN 'null' THEN NULL "
      + "WHEN 'boolean' THEN (CASE WHEN t.v::text = 'true' THEN 'True' ELSE 'False' END) "
      + "WHEN 'string' THEN (t.v #>> '{}') "
      + "ELSE t.v::text END "
      + $"FROM (SELECT {extraction} AS v) t)";

    private static bool TryCompileOperator(
        string op, string value, object? expected, Guid userId, out string sql, out List<object> parameters)
    {
        sql = string.Empty;
        parameters = [];

        switch (op)
        {
            case "_eq":
            case "_ne":
            {
                if (!TryScalar(expected, userId, substitute: true, out var text)) return false;

                // IS DISTINCT FROM, not <>. A null-valued field is "different from alpha" in the
                // evaluator (!Equals(null, "alpha") is true) while plain <> gives unknown, which
                // does not pass a WHERE. For non-null values the two are identical.
                sql = op == "_eq" ? $"{value} = ?" : $"{value} IS DISTINCT FROM ?";
                parameters = [text];
                return true;
            }

            case "_in":
            case "_nin":
            {
                if (expected is not List<object> items) return false;

                var texts = new List<object>(items.Count);
                foreach (var item in items)
                {
                    // No substitution inside a list, which looks like an oversight and is not.
                    // ConditionEvaluator tests `expectedValue is string` on the WHOLE expected value
                    // before substituting, and for _in that value is the list, so the check fails and
                    // the items keep the literal text "$CURRENT_USER". Substituting here would make
                    // the predicate match rows the evaluator does not, which is the direction that
                    // leaks. The agreement test found this, having generated documents whose field
                    // literally held that string.
                    if (!TryScalar(item, userId, substitute: false, out var text)) return false;
                    texts.Add(text);
                }

                // An empty list matches nothing in the evaluator, so _in is false and _nin is true.
                // Written out rather than left to an empty IN (), which is a syntax error.
                if (texts.Count == 0)
                {
                    sql = op == "_in" ? "FALSE" : "TRUE";
                    return true;
                }

                var placeholders = string.Join(", ", Enumerable.Repeat("?", texts.Count));

                // Same reason as _ne above: a null-valued field is in nothing, so _in denies, and it
                // is not in the list either, so _nin grants. Plain NOT IN gives unknown for a null
                // and would deny where the evaluator grants.
                //
                // Written as a coalesced IN rather than the more obvious
                // "value IS NULL OR value NOT IN (...)", because that form names the value twice and
                // the value carries a bound placeholder. Every extra mention is another parameter
                // the caller has to bind in the right position, which is a counting exercise nobody
                // wins twice. This mentions it once.
                sql = op == "_in"
                    ? $"{value} IN ({placeholders})"
                    : $"COALESCE({value} IN ({placeholders}), FALSE) = FALSE";
                parameters = texts;
                return true;
            }

            default:
                // Unknown operators deny in the evaluator. Falling back rather than emitting FALSE
                // keeps a misconfigured rule producing one answer instead of two.
                return false;
        }
    }

    /// <summary>The text the evaluator would compare against, for the values worth compiling.</summary>
    /// <param name="substitute">
    /// Whether <c>$CURRENT_USER</c> means the caller here. True for a scalar expected value, false
    /// for one inside a list, because that is where the evaluator draws the line.
    /// </param>
    private static bool TryScalar(object? expected, Guid userId, bool substitute, out string text)
    {
        text = string.Empty;

        if (expected is not string s) return false;

        text = substitute && s == "$CURRENT_USER" ? userId.ToString() : s;
        return true;
    }

    /// <summary>
    /// The same normalisation <see cref="ConditionEvaluator"/> applies, for the same reason.
    /// </summary>
    /// <remarks>
    /// A Role read back from Marten holds its condition values as <c>JsonElement</c>, so without this
    /// every rule would look uncompilable and the endpoint would silently keep its slow path.
    /// </remarks>
    private static object? Normalize(object? value)
    {
        if (value is not JsonElement element) return value;

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Normalize(p.Value)!),
            JsonValueKind.Array => element.EnumerateArray().Select(e => Normalize(e)).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value,
        };
    }
}
