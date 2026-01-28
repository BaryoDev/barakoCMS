namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Group of conditions with logical operator (AND/OR).
/// </summary>
public class WorkflowConditionGroup
{
    /// <summary>
    /// Logical operator: "AND" or "OR".
    /// </summary>
    public string Operator { get; set; } = "AND";

    /// <summary>
    /// List of conditions or nested condition groups.
    /// </summary>
    public List<WorkflowConditionRule> Rules { get; set; } = new();
}

/// <summary>
/// Single condition rule.
/// </summary>
public class WorkflowConditionRule
{
    /// <summary>
    /// Field path to evaluate (e.g., "data.Amount", "status", "user.roles").
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// Comparison operator.
    /// </summary>
    public string? Operator { get; set; }

    /// <summary>
    /// Value to compare against. Can be a template variable.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Nested condition group for complex logic.
    /// If set, Field/Operator/Value are ignored.
    /// </summary>
    public WorkflowConditionGroup? Group { get; set; }
}

/// <summary>
/// Supported condition operators.
/// </summary>
public static class ConditionOperators
{
    // Equality
    public const string Equal = "eq";
    public const string NotEqual = "ne";

    // Comparison
    public const string GreaterThan = "gt";
    public const string GreaterThanOrEqual = "gte";
    public const string LessThan = "lt";
    public const string LessThanOrEqual = "lte";

    // Array
    public const string In = "in";
    public const string NotIn = "nin";

    // String
    public const string Contains = "contains";
    public const string NotContains = "notContains";
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string Matches = "matches";

    // Existence
    public const string Exists = "exists";
    public const string IsNull = "isNull";
    public const string IsEmpty = "isEmpty";

    // Type
    public const string IsType = "isType";

    /// <summary>
    /// All valid operators.
    /// </summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Equal, NotEqual,
        GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual,
        In, NotIn,
        Contains, NotContains, StartsWith, EndsWith, Matches,
        Exists, IsNull, IsEmpty,
        IsType
    };
}
