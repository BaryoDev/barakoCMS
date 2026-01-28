using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Advanced condition evaluator with support for complex operators and nested conditions.
/// </summary>
public class AdvancedConditionEvaluator : IAdvancedConditionEvaluator
{
    private readonly ILogger<AdvancedConditionEvaluator> _logger;

    public AdvancedConditionEvaluator(ILogger<AdvancedConditionEvaluator> logger)
    {
        _logger = logger;
    }

    public bool Evaluate(WorkflowConditionGroup? conditions, WorkflowContext context)
    {
        if (conditions == null || conditions.Rules.Count == 0)
        {
            return true; // No conditions = always match
        }

        var isAnd = conditions.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase);

        foreach (var rule in conditions.Rules)
        {
            bool ruleResult;

            if (rule.Group != null)
            {
                // Nested condition group
                ruleResult = Evaluate(rule.Group, context);
            }
            else
            {
                // Single rule
                ruleResult = EvaluateRule(rule, context);
            }

            if (isAnd && !ruleResult)
            {
                return false; // AND: short-circuit on first false
            }

            if (!isAnd && ruleResult)
            {
                return true; // OR: short-circuit on first true
            }
        }

        return isAnd; // AND: all true, OR: all false
    }

    public bool EvaluateRule(WorkflowConditionRule rule, WorkflowContext context)
    {
        if (string.IsNullOrEmpty(rule.Field) || string.IsNullOrEmpty(rule.Operator))
        {
            _logger.LogWarning("Invalid condition rule: missing field or operator");
            return false;
        }

        try
        {
            var actualValue = GetFieldValue(rule.Field, context);
            var expectedValue = ResolveValue(rule.Value, context);

            return EvaluateOperator(rule.Operator, actualValue, expectedValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating condition: {Field} {Operator} {Value}",
                rule.Field, rule.Operator, rule.Value);
            return false;
        }
    }

    public object? GetFieldValue(string fieldPath, WorkflowContext context)
    {
        if (string.IsNullOrEmpty(fieldPath))
            return null;

        var parts = fieldPath.Split('.');
        var root = parts[0].ToLowerInvariant();

        return root switch
        {
            "data" => GetNestedValue(context.Content.Data, parts.Skip(1).ToArray()),
            "previous" => GetPreviousValue(context, parts.Skip(1).ToArray()),
            "status" => context.Content.Status.ToString(),
            "contenttype" => context.Content.ContentType,
            "id" => context.Content.Id,
            "createdat" => context.Content.CreatedAt,
            "updatedat" => context.Content.UpdatedAt,
            "sensitivity" => context.Content.Sensitivity.ToString(),
            "user" => GetUserValue(context, parts.Skip(1).ToArray()),
            "variable" or "var" => GetVariableValue(context, parts.Skip(1).ToArray()),
            "action" => GetActionResultValue(context, parts.Skip(1).ToArray()),
            _ => GetNestedValue(context.Content.Data, parts) // Default to data field
        };
    }

    private object? GetNestedValue(Dictionary<string, object>? data, string[] path)
    {
        if (data == null || path.Length == 0)
            return data;

        var current = data;
        for (int i = 0; i < path.Length; i++)
        {
            var key = path[i];

            if (!current.TryGetValue(key, out var value))
            {
                // Try case-insensitive lookup
                var matchingKey = current.Keys.FirstOrDefault(k =>
                    k.Equals(key, StringComparison.OrdinalIgnoreCase));

                if (matchingKey == null)
                    return null;

                value = current[matchingKey];
            }

            if (i == path.Length - 1)
                return value;

            if (value is Dictionary<string, object> nested)
            {
                current = nested;
            }
            else if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                current = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText()) ?? new();
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private object? GetPreviousValue(WorkflowContext context, string[] path)
    {
        if (context.PreviousContent == null)
            return null;

        if (path.Length == 0)
            return null;

        var root = path[0].ToLowerInvariant();
        return root switch
        {
            "data" => GetNestedValue(context.PreviousContent.Data, path.Skip(1).ToArray()),
            "status" => context.PreviousContent.Status.ToString(),
            _ => GetNestedValue(context.PreviousContent.Data, path)
        };
    }

    private object? GetUserValue(WorkflowContext context, string[] path)
    {
        if (context.User == null || path.Length == 0)
            return null;

        var field = path[0].ToLowerInvariant();
        return field switch
        {
            "id" => context.User.Id,
            "username" => context.User.Username,
            "email" => context.User.Email,
            "roles" => context.User.RoleIds,
            "groups" => context.User.GroupIds,
            _ => null
        };
    }

    private object? GetVariableValue(WorkflowContext context, string[] path)
    {
        if (path.Length == 0)
            return null;

        var key = string.Join(".", path);
        return context.Variables.TryGetValue(key, out var value) ? value : null;
    }

    private object? GetActionResultValue(WorkflowContext context, string[] path)
    {
        if (path.Length < 2)
            return null;

        var actionId = path[0];
        if (!context.ActionResults.TryGetValue(actionId, out var result))
            return null;

        var field = path[1].ToLowerInvariant();
        return field switch
        {
            "success" => result.Success,
            "error" => result.ErrorMessage,
            _ => result.Output.TryGetValue(field, out var value) ? value : null
        };
    }

    private object? ResolveValue(object? value, WorkflowContext context)
    {
        if (value == null)
            return null;

        if (value is string strValue)
        {
            // Check for template variables
            if (strValue.StartsWith("{{") && strValue.EndsWith("}}"))
            {
                var fieldPath = strValue.Substring(2, strValue.Length - 4).Trim();
                return GetFieldValue(fieldPath, context);
            }

            // Check for special values
            if (strValue.Equals("$CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                return context.User?.Id.ToString();

            if (strValue.Equals("$NOW", StringComparison.OrdinalIgnoreCase))
                return DateTime.UtcNow;

            if (strValue.Equals("$TODAY", StringComparison.OrdinalIgnoreCase))
                return DateTime.UtcNow.Date;
        }

        return value;
    }

    private bool EvaluateOperator(string op, object? actual, object? expected)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" or "equals" or "=" or "==" => AreEqual(actual, expected),
            "ne" or "notequals" or "!=" or "<>" => !AreEqual(actual, expected),
            "gt" or ">" => Compare(actual, expected) > 0,
            "gte" or ">=" => Compare(actual, expected) >= 0,
            "lt" or "<" => Compare(actual, expected) < 0,
            "lte" or "<=" => Compare(actual, expected) <= 0,
            "in" => IsIn(actual, expected),
            "nin" or "notin" => !IsIn(actual, expected),
            "contains" => Contains(actual, expected),
            "notcontains" => !Contains(actual, expected),
            "startswith" => StartsWith(actual, expected),
            "endswith" => EndsWith(actual, expected),
            "matches" or "regex" => MatchesRegex(actual, expected),
            "exists" => Exists(actual, expected),
            "isnull" => IsNull(actual, expected),
            "isempty" => IsEmpty(actual, expected),
            "istype" => IsType(actual, expected),
            _ => throw new ArgumentException($"Unknown operator: {op}")
        };
    }

    private bool AreEqual(object? actual, object? expected)
    {
        if (actual == null && expected == null)
            return true;

        if (actual == null || expected == null)
            return false;

        // Handle JsonElement
        actual = UnwrapJsonElement(actual);
        expected = UnwrapJsonElement(expected);

        // String comparison (case-insensitive)
        if (actual is string actualStr && expected is string expectedStr)
            return actualStr.Equals(expectedStr, StringComparison.OrdinalIgnoreCase);

        // Numeric comparison
        if (IsNumeric(actual) && IsNumeric(expected))
            return Convert.ToDouble(actual) == Convert.ToDouble(expected);

        // Boolean comparison
        if (actual is bool actualBool)
            return actualBool == ConvertToBool(expected);

        // Guid comparison
        if (actual is Guid actualGuid)
            return actualGuid.ToString().Equals(expected.ToString(), StringComparison.OrdinalIgnoreCase);

        return actual.Equals(expected);
    }

    private int Compare(object? actual, object? expected)
    {
        if (actual == null && expected == null)
            return 0;

        if (actual == null)
            return -1;

        if (expected == null)
            return 1;

        actual = UnwrapJsonElement(actual);
        expected = UnwrapJsonElement(expected);

        // Numeric comparison
        if (IsNumeric(actual) && IsNumeric(expected))
            return Convert.ToDouble(actual).CompareTo(Convert.ToDouble(expected));

        // DateTime comparison
        if (actual is DateTime actualDt && TryParseDateTime(expected, out var expectedDt))
            return actualDt.CompareTo(expectedDt);

        // String comparison
        return string.Compare(actual.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsIn(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        actual = UnwrapJsonElement(actual);
        expected = UnwrapJsonElement(expected);

        var actualStr = actual.ToString();

        if (expected is IEnumerable<object> enumerable)
        {
            return enumerable.Any(item =>
                item?.ToString()?.Equals(actualStr, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (expected is JsonElement jsonArray && jsonArray.ValueKind == JsonValueKind.Array)
        {
            return jsonArray.EnumerateArray().Any(item =>
                item.ToString().Equals(actualStr, StringComparison.OrdinalIgnoreCase));
        }

        // Comma-separated string
        if (expected is string expectedStr)
        {
            return expectedStr.Split(',')
                .Select(s => s.Trim())
                .Any(s => s.Equals(actualStr, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private bool Contains(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        var actualStr = UnwrapJsonElement(actual)?.ToString() ?? "";
        var expectedStr = UnwrapJsonElement(expected)?.ToString() ?? "";

        return actualStr.Contains(expectedStr, StringComparison.OrdinalIgnoreCase);
    }

    private bool StartsWith(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        var actualStr = UnwrapJsonElement(actual)?.ToString() ?? "";
        var expectedStr = UnwrapJsonElement(expected)?.ToString() ?? "";

        return actualStr.StartsWith(expectedStr, StringComparison.OrdinalIgnoreCase);
    }

    private bool EndsWith(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        var actualStr = UnwrapJsonElement(actual)?.ToString() ?? "";
        var expectedStr = UnwrapJsonElement(expected)?.ToString() ?? "";

        return actualStr.EndsWith(expectedStr, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesRegex(object? actual, object? expected)
    {
        if (actual == null || expected == null)
            return false;

        var actualStr = UnwrapJsonElement(actual)?.ToString() ?? "";
        var pattern = UnwrapJsonElement(expected)?.ToString() ?? "";

        try
        {
            return Regex.IsMatch(actualStr, pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", pattern);
            return false;
        }
    }

    private bool Exists(object? actual, object? expected)
    {
        var shouldExist = ConvertToBool(expected);
        var exists = actual != null;
        return exists == shouldExist;
    }

    private bool IsNull(object? actual, object? expected)
    {
        var shouldBeNull = ConvertToBool(expected);
        var isNull = actual == null;
        return isNull == shouldBeNull;
    }

    private bool IsEmpty(object? actual, object? expected)
    {
        var shouldBeEmpty = ConvertToBool(expected);

        actual = UnwrapJsonElement(actual);

        bool isEmpty = actual switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            IEnumerable<object> e => !e.Any(),
            JsonElement je when je.ValueKind == JsonValueKind.Array => je.GetArrayLength() == 0,
            JsonElement je when je.ValueKind == JsonValueKind.String => string.IsNullOrWhiteSpace(je.GetString()),
            _ => false
        };

        return isEmpty == shouldBeEmpty;
    }

    private bool IsType(object? actual, object? expected)
    {
        var expectedType = UnwrapJsonElement(expected)?.ToString()?.ToLowerInvariant();

        actual = UnwrapJsonElement(actual);

        var actualType = actual switch
        {
            null => "null",
            string => "string",
            int or long or short or byte => "integer",
            float or double or decimal => "number",
            bool => "boolean",
            DateTime => "datetime",
            Guid => "guid",
            IEnumerable<object> => "array",
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Array => "array",
                JsonValueKind.Object => "object",
                JsonValueKind.Null => "null",
                _ => "unknown"
            },
            _ => "object"
        };

        return actualType == expectedType;
    }

    private object? UnwrapJsonElement(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value
            };
        }

        return value;
    }

    private bool IsNumeric(object? value)
    {
        if (value == null)
            return false;

        value = UnwrapJsonElement(value);

        return value is int or long or short or byte or float or double or decimal
            || (value is string s && double.TryParse(s, out _));
    }

    private bool ConvertToBool(object? value)
    {
        if (value == null)
            return false;

        value = UnwrapJsonElement(value);

        if (value is bool b)
            return b;

        if (value is string s)
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("yes", StringComparison.OrdinalIgnoreCase);

        if (IsNumeric(value))
            return Convert.ToDouble(value) != 0;

        return false;
    }

    private bool TryParseDateTime(object? value, out DateTime result)
    {
        result = default;

        if (value == null)
            return false;

        if (value is DateTime dt)
        {
            result = dt;
            return true;
        }

        if (value is string s)
            return DateTime.TryParse(s, out result);

        return false;
    }

    public List<string> ValidateConditions(WorkflowConditionGroup? conditions)
    {
        var errors = new List<string>();

        if (conditions == null)
            return errors;

        if (!conditions.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
            !conditions.Operator.Equals("OR", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Invalid logical operator: {conditions.Operator}. Must be 'AND' or 'OR'.");
        }

        foreach (var rule in conditions.Rules)
        {
            if (rule.Group != null)
            {
                errors.AddRange(ValidateConditions(rule.Group));
            }
            else
            {
                if (string.IsNullOrEmpty(rule.Field))
                    errors.Add("Condition rule missing 'field' property.");

                if (string.IsNullOrEmpty(rule.Operator))
                    errors.Add("Condition rule missing 'operator' property.");
                else if (!ConditionOperators.All.Contains(rule.Operator))
                    errors.Add($"Invalid operator: {rule.Operator}");
            }
        }

        return errors;
    }
}
