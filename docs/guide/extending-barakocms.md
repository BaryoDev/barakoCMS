# Extending BarakoCMS

BarakoCMS is designed to be infinitely extensible through its plugin architecture. This guide covers how to add new capabilities without modifying the core codebase.

## 1. Adding a Custom Workflow Action

Workflow Actions are plugins that execute logic when an event triggers (e.g., "Send Slack Notification when Content Published").

### Step 1: Implement `IWorkflowAction`

Create a new class in your feature folder (or a separate plugin project) that implements `IWorkflowAction`.

```csharp
using barakoCMS.Features.Workflows;

public class SlackAction : IWorkflowAction
{
    // The unique identifier used in the JSON workflow config
    public string Type => "SlackNotification";

    private readonly IHttpClientFactory _httpClientFactory;

    // Supports Dependency Injection
    public SlackAction(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task ExecuteAsync(WorkflowContext context, Dictionary<string, object> parameters)
    {
        // 1. Extract parameters from the workflow config
        if (!parameters.TryGetValue("webhookUrl", out var urlObj) || urlObj is not string webhookUrl)
            throw new ArgumentException("Missing 'webhookUrl' parameter");
            
        if (!parameters.TryGetValue("message", out var msgObj) || msgObj is not string message)
            throw new ArgumentException("Missing 'message' parameter");

        // 2. Perform the logic (e.g., Call Slack API)
        var client = _httpClientFactory.CreateClient();
        await client.PostAsJsonAsync(webhookUrl, new { text = message });
        
        // 3. Log execution (optional, context handles standard logging)
        Console.WriteLine($"[SlackAction] Sent message to {webhookUrl}");
    }
}
```

### Step 2: Register the Service

Register your new action in `Program.cs` or `ServiceCollectionExtensions.cs`.

```csharp
// In ServiceCollectionExtensions.cs
services.AddScoped<IWorkflowAction, SlackAction>();
```

### Step 3: Use it in a Workflow

You can now use your new action type `"SlackNotification"` immediately in any workflow JSON.

```json
{
  "name": "Notify Team on Publish",
  "triggerContentType": "blog-post",
  "triggerEvent": "StatusChanged",
  "conditions": { "status": "Published" },
  "actions": [
    {
      "type": "SlackNotification",
      "parameters": {
        "webhookUrl": "https://hooks.slack.com/services/...",
        "message": "New post published: {{data.Title}} by {{data.Author}}"
      }
    }
  ]
}
```

---

## 2. Adding Custom Metrics

BarakoCMS uses a centralized `IMetricsService`. To track custom business metrics:

### Step 1: Inject `IMetricsService`

```csharp
public class MyService
{
    private readonly IMetricsService _metrics;

    public MyService(IMetricsService metrics)
    {
        _metrics = metrics;
    }

    public void DoWork()
    {
        // ... work ...
        _metrics.TrackRequest(200, 150); // Provide Status Code and Duration
    }
}
```

*Note: For more complex/custom metrics, you can extend `IMetricsService` or add a new interface `IBusinessMetrics`.*
