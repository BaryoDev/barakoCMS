using barakoCMS.Features.Workflows;

namespace barakoCMS.Modules;

/// <summary>Lets a module add workflow action types. Names must start with the module name and a dot.</summary>
public interface IModuleWorkflowActions
{
    void Add<TAction>(string type) where TAction : class, IWorkflowAction;
}
