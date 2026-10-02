using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;

namespace barakoCMS.Features.Monitoring.Describe;

/// <summary>
/// Maps the registries the API already checks requests against onto the describe document.
/// </summary>
/// <remarks>
/// Nothing here is a list of its own. Each method is handed a registry and reads every entry of
/// it, so an entry added to a registry is in the document with no edit here.
/// </remarks>
internal static class DescribeDocument
{
    // The gates of GET /api/capabilities, GET /api/workflows/actions and GET /api/modules. A part
    // of the document is shown to exactly the callers the endpoint that already lists it serves.
    public static readonly RequiredCapability CapabilitiesGate =
        new(SystemCapabilities.ManageRoles, ["SuperAdmin"]);

    public static readonly RequiredCapability WorkflowActionsGate =
        new(SystemCapabilities.ManageWorkflows, ["SuperAdmin", "Admin"]);

    public static readonly RequiredCapability ModulesGate =
        new(SystemCapabilities.ViewModules, ["SuperAdmin", "Admin"]);

    public static IReadOnlyList<DescribedFieldType> FieldTypes(
        IEnumerable<FieldTypeRegistry.FieldTypeSpec> types, IReadOnlyList<string> rules) =>
        types
            .Select(type => new DescribedFieldType(
                type.Name,
                FieldTypeRegistry.AliasesOf(type.Name),
                type.EditorHint,
                rules.Where(rule => FieldRules.AppliesTo(rule, type.Name)).ToArray()))
            .ToArray();

    public static IReadOnlyList<DescribedRule> Rules(IEnumerable<string> rules) =>
        rules.Select(rule => new DescribedRule(rule, FieldRules.AliasesOf(rule))).ToArray();

    // Ordered by type so two calls agree. The registry holds them in the order the container
    // registered them, which is the host's order and means nothing to a reader.
    public static IReadOnlyList<WorkflowActionMetadata> WorkflowActions(IEnumerable<WorkflowActionMetadata> actions) =>
        actions.OrderBy(action => action.Type, StringComparer.Ordinal).ToArray();

    // A module the enabled list left off serves no endpoint and registers no action, so listing it
    // here would describe something this deployment cannot do. GET /api/modules still reports it.
    public static IReadOnlyList<DescribedModule> Modules(ModuleCatalogue catalogue) =>
        catalogue.Entries
            .Where(entry => entry.Enabled)
            .Select(entry => new DescribedModule(entry.Name))
            .OrderBy(module => module.Name, StringComparer.Ordinal)
            .ToArray();
}
