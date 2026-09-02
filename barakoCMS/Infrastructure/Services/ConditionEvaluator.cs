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
        => Evaluate(conditions, contentData, null, user);

    /// <inheritdoc />
    public bool Evaluate(
        Dictionary<string, object> conditions,
        Models.Content content,
        Models.User user)
        => Evaluate(conditions, content.Data, content, user);

    private bool Evaluate(
        Dictionary<string, object> conditions,
        Dictionary<string, object> contentData,
        Models.Content? content,
        Models.User user)
    {
        foreach (var (field, conditionValue) in conditions)
        {
            if (!TryResolve(field, contentData, content, out var actualValue))
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

    /// <summary>
    /// Finds the value a condition names, from the document for a <c>$</c> prefixed key and from the
    /// data bag otherwise.
    /// </summary>
    /// <remarks>
    /// A document property is refused rather than treated as absent when no document was supplied.
    /// Both answers deny, so the difference is invisible to a caller, and it is worth keeping anyway:
    /// the overload without a document cannot answer an ownership question, and silently returning
    /// "no match" would make a permission rule that is never satisfiable look like one that simply
    /// did not apply.
    /// </remarks>
    private static bool TryResolve(
        string field,
        Dictionary<string, object> contentData,
        Models.Content? content,
        out object? value)
    {
        value = null;

        if (!field.StartsWith('$'))
            return contentData.TryGetValue(field, out value);

        if (content is null)
            return false;

        switch (field[1..].ToLowerInvariant())
        {
            case "createdby":
                value = content.CreatedBy.ToString();
                return true;
            case "lastmodifiedby":
                value = content.LastModifiedBy.ToString();
                return true;
            case "status":
                value = content.Status.ToString();
                return true;
            default:
                // An unknown document property denies rather than throwing. A rule naming one is a
                // configuration mistake, and refusing is the direction that cannot leak.
                return false;
        }
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
