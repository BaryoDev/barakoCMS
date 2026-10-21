using barakoCMS.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Features.Workflows;

internal sealed class ModuleWorkflowActionRegistry
{
    private static readonly HashSet<string> CoreTypes = new(StringComparer.Ordinal)
    {
        "Email", "SMS", "Webhook", "CreateTask", "UpdateField", "Request", "Conditional", "CreateContent", "Transition",
    };

    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    public IModuleWorkflowActions For(IServiceCollection services, IBarakoModule module) =>
        new Collector(this, services, module.Name);

    private void Claim(string module, string type)
    {
        if (string.IsNullOrWhiteSpace(type) || !type.StartsWith(module + ".", StringComparison.Ordinal) || type.Length == module.Length + 1)
            throw new InvalidOperationException(
                $"Module '{module}' tried to add the workflow action '{type}'. A module action type starts with '{module}.' and has a name after the dot.");
        if (CoreTypes.Contains(type))
            throw new InvalidOperationException($"Module '{module}' tried to add the workflow action '{type}', which core already owns.");
        if (!_owners.TryAdd(type, module))
            throw new InvalidOperationException($"Module '{module}' tried to add the workflow action '{type}', which module '{_owners[type]}' already added.");
    }

    private sealed class Collector(ModuleWorkflowActionRegistry owner, IServiceCollection services, string module) : IModuleWorkflowActions
    {
        public void Add<TAction>(string type) where TAction : class, IWorkflowAction
        {
            owner.Claim(module, type);
            services.AddScoped<IWorkflowAction>(sp => new ModuleWorkflowAction(ActivatorUtilities.CreateInstance<TAction>(sp), type));
        }
    }
}
