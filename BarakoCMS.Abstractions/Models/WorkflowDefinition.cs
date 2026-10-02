namespace barakoCMS.Models;

public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty; // e.g., "PurchaseOrder"

    /// <summary>
    /// More content types this workflow fires for, alongside <see cref="TriggerContentType"/>.
    /// </summary>
    /// <remarks>
    /// The workflow fires for <see cref="TriggerContentType"/> and for every entry here, so neither
    /// field overrides the other. A workflow saved before this existed has no list and fires for its
    /// single type, as it always did. Saving through the API stores both: the list holds every type
    /// and <see cref="TriggerContentType"/> holds the first, so a reader that only knows the single
    /// field still sees a type the workflow fires for.
    /// </remarks>
    public List<string> TriggerContentTypes { get; set; } = new();

    public string TriggerEvent { get; set; } = string.Empty; // e.g., "Created", "Updated"

    /// <summary>
    /// More events this workflow fires on, alongside <see cref="TriggerEvent"/>.
    /// </summary>
    /// <remarks>
    /// Read the same way as <see cref="TriggerContentTypes"/>: the workflow fires on
    /// <see cref="TriggerEvent"/> and on every entry here, a workflow saved before this existed has
    /// no list and fires on its single event, and saving through the API stores every event in the
    /// list and the first in <see cref="TriggerEvent"/>.
    /// </remarks>
    public List<string> TriggerEvents { get; set; } = new();
    public Dictionary<string, string> Conditions { get; set; } = new(); // e.g., "Status" == "Approved"
    public List<WorkflowAction> Actions { get; set; } = new();
}

public class WorkflowAction
{
    public string Type { get; set; } = string.Empty; // "Email", "SMS", "Webhook"
    public Dictionary<string, string> Parameters { get; set; } = new(); // e.g., "To": "admin@example.com"
}
