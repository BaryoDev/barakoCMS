using barakoCMS.Features.WorkflowsV2.Actions;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Registry for workflow action plugins.
/// </summary>
public class ActionRegistry : IActionRegistry
{
    private readonly Dictionary<string, IWorkflowActionV2> _actions;

    public ActionRegistry(IEnumerable<IWorkflowActionV2> actions)
    {
        _actions = actions.ToDictionary(
            a => a.Type,
            a => a,
            StringComparer.OrdinalIgnoreCase);
    }

    public IWorkflowActionV2? GetAction(string type)
    {
        return _actions.TryGetValue(type, out var action) ? action : null;
    }

    public IEnumerable<IWorkflowActionV2> GetAllActions()
    {
        return _actions.Values;
    }

    public IEnumerable<IWorkflowActionV2> GetActionsByCategory(string category)
    {
        return _actions.Values.Where(a =>
            a.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<ActionMetadata> GetActionMetadata()
    {
        return _actions.Values.Select(a => new ActionMetadata
        {
            Type = a.Type,
            Name = a.Name,
            Description = a.Description,
            Category = a.Category,
            SupportsPreHook = a.SupportsPreHook,
            SupportsPostHook = a.SupportsPostHook,
            CanModifyData = a.CanModifyData,
            CanBlockOperation = a.CanBlockOperation,
            Schema = a.GetConfigSchema()
        });
    }
}
