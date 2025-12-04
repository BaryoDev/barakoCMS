namespace barakoCMS.Models;

public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty; // e.g., "PurchaseOrder"
    public string TriggerEvent { get; set; } = string.Empty; // e.g., "Created", "Updated"
    public Dictionary<string, string> Conditions { get; set; } = new(); // e.g., "Status" == "Approved"
    public List<WorkflowAction> Actions { get; set; } = new();
}

public class WorkflowAction
{
    public string Type { get; set; } = string.Empty; // "Email", "SMS", "Webhook"
    public Dictionary<string, string> Parameters { get; set; } = new(); // e.g., "To": "admin@example.com"
}
