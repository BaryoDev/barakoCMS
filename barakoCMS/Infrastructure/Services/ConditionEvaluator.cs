using System.Text.Json;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for evaluating permission conditions (Directus/Strapi style)
/// </summary>
public class ConditionEvaluator : IConditionEvaluator
{
    /// <summary>
    /// Evaluate if conditions match the content and user context
    /// </summary>
    public bool Evaluate(
        Dictionary<string, object> conditions,
        Dictionary<string, object> contentData,
        Models.User user)
    {
        foreach (var (field, conditionValue) in conditions)
        {
            if (!contentData.TryGetValue(field, out var actualValue))
                return false; // Field doesn't exist in content

            // A Role loaded fresh from Marten deserializes any object-typed property as
            // JsonElement, not the concrete CLR shape it was stored with, because there is no
            // static type to reconstruct against below a Dictionary<string, object>. Normalize
            // one level down (and everything nested inside it) so the checks below — and the
            // $CURRENT_USER substitution in EvaluateOperator — see the same plain CLR types
            // whether conditions came from memory or from a round trip through the database.
            if (Normalize(conditionValue) is not Dictionary<string, object> operators)
                return false;

            foreach (var (op, rawExpectedValue) in operators)
            {
                if (!EvaluateOperator(op, actualValue, Normalize(rawExpectedValue), user))
                    return false;
            }
        }

        return true;
    }

    // Recursively converts a System.Text.Json.JsonElement (of any kind) into the equivalent plain
    // CLR value (Dictionary<string, object>, List<object>, string, double, bool, or null), leaving
    // anything that is already a plain CLR value untouched. Applied once at each level a condition
    // value is read, so the rest of this class never needs to know or care whether a value came
    // from memory or from a database round trip.
    private static object? Normalize(object? value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => Normalize(p.Value)!),
            JsonValueKind.Array => element.EnumerateArray().Select(e => Normalize(e)).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value,
        };
    }

    private bool EvaluateOperator(string op, object? actualValue, object? expectedValue, Models.User user)
    {
        // Replace $CURRENT_USER placeholder
        if (expectedValue is string strValue && strValue == "$CURRENT_USER")
        {
            expectedValue = user.Id.ToString();
        }

        return op switch
        {
            "_eq" => Equals(actualValue?.ToString(), expectedValue?.ToString()),
            "_ne" => !Equals(actualValue?.ToString(), expectedValue?.ToString()),
            "_in" => EvaluateIn(actualValue, expectedValue),
            "_nin" => !EvaluateIn(actualValue, expectedValue),
            _ => false // Unknown operator
        };
    }

    private bool EvaluateIn(object? actualValue, object expectedValue)
    {
        if (expectedValue is not System.Collections.IEnumerable enumerable)
            return false;

        var actualStr = actualValue?.ToString();
        foreach (var item in enumerable)
        {
            if (item?.ToString() == actualStr)
                return true;
        }

        return false;
    }
}
