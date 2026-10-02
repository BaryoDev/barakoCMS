using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>A workflow as a caller writes it, rather than as it is stored.</summary>
/// <remarks>
/// Every field of <see cref="WorkflowDefinition"/> a caller chooses, under the same name, with the
/// same type and the same default, so a request reads as it did when the endpoint bound the stored
/// type. <c>Id</c> is the one field left out. The server picks it, and a request that sends one is
/// read as if it had not.
///
/// A field added to the stored type is not accepted until it is added here, which is the point:
/// whether a caller may set it is decided, not inherited.
/// </remarks>
internal class CreateWorkflowRequest
{
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty;
    public List<string> TriggerContentTypes { get; set; } = new();
    public string TriggerEvent { get; set; } = string.Empty;
    public List<string> TriggerEvents { get; set; } = new();
    public Dictionary<string, string> Conditions { get; set; } = new();
    public List<WorkflowAction> Actions { get; set; } = new();
    public bool Enabled { get; set; } = true;

    /// <summary>The definition this request describes, with no id.</summary>
    /// <remarks>
    /// Values are handed over as they were read, a null included, so the schema validator sees what
    /// it saw before and answers the same.
    /// </remarks>
    public WorkflowDefinition ToDefinition() => new()
    {
        Name = Name,
        TriggerContentType = TriggerContentType,
        TriggerContentTypes = TriggerContentTypes,
        TriggerEvent = TriggerEvent,
        TriggerEvents = TriggerEvents,
        Conditions = Conditions,
        Actions = Actions,
        Enabled = Enabled,
    };
}
