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

            // Condition value should be a dictionary with operators
            if (conditionValue is not Dictionary<string, object> operators)
                return false;

            foreach (var (op, expectedValue) in operators)
            {
                if (!EvaluateOperator(op, actualValue, expectedValue, user))
                    return false;
            }
        }

        return true;
    }

    private bool EvaluateOperator(string op, object? actualValue, object expectedValue, Models.User user)
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
