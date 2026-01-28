using barakoCMS.Features.WorkflowsV2.Models;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Interface for evaluating complex workflow conditions.
/// </summary>
public interface IAdvancedConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition group against a workflow context.
    /// </summary>
    bool Evaluate(WorkflowConditionGroup? conditions, WorkflowContext context);

    /// <summary>
    /// Evaluate a single condition rule.
    /// </summary>
    bool EvaluateRule(WorkflowConditionRule rule, WorkflowContext context);

    /// <summary>
    /// Get the value of a field from the context.
    /// </summary>
    object? GetFieldValue(string fieldPath, WorkflowContext context);

    /// <summary>
    /// Validate that a condition group is well-formed.
    /// </summary>
    List<string> ValidateConditions(WorkflowConditionGroup? conditions);
}
