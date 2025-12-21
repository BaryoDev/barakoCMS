# Plugin Development Guide

**Build Custom Workflow Actions for BarakoCMS**

This guide shows you how to create custom workflow action plugins without modifying core code.

---

## Table of Contents

1. [Introduction](#introduction)
2. [Quick Start](#quick-start)
3. [Plugin Architecture](#plugin-architecture)
4. [Creating Your First Plugin](#creating-your-first-plugin)
5. [Built-in Plugin Examples](#built-in-plugin-examples)
6. [Advanced Topics](#advanced-topics)
7. [Testing](#testing)
8. [Best Practices](#best-practices)
9. [Troubleshooting](#troubleshooting)

---

## Introduction

### What are Workflow Action Plugins?

Workflow action plugins are reusable components that execute specific tasks when workflow conditions are met. They enable you to:

- **Extend functionality** without touching core code
- **Encapsulate business logic** into reusable actions
- **Leverage dependency injection** for services
- **Maintain clean separation** between workflow logic and actions

### Why Use Plugins?

**Before (Hardcoded)**:
```csharp
// Tightly coupled to WorkflowEngine
if (action.Type == "SendEmail")
{
    // Hardcoded email logic
    _emailService.Send(...);
}
```

**After (Plugin-Based)**:
```csharp
// Clean plugin interface
public class EmailAction : IWorkflowAction
{
    public async Task ExecuteAsync(...) { }
}
```

**Benefits**:
- ✅ Add new actions by creating a class
- ✅ No core code modifications
- ✅ Testable in isolation
- ✅ Auto-discovered by DI

---

## Quick Start

### 3-Minute Plugin

Create a simple notification plugin:

```csharp
// 1. Add metadata attribute
[WorkflowActionMetadata(
    Description = "Log a message to console",
    RequiredParameters = new[] { "message" },
    ExampleJson = @"{""type"": ""LogMessage"", ""parameters"": {""message"": ""Hello World""}}"
)]

// 2. Implement IWorkflowAction
public class LogMessageAction : IWorkflowAction
{
    private readonly ILogger<LogMessageAction> _logger;

    public string Type => "LogMessage";

    public LogMessageAction(ILogger<LogMessageAction> logger)
    {
        _logger = logger;
    }

    // 3. Implement ExecuteAsync
    public async Task ExecuteAsync(
        Dictionary<string, string> parameters,
        Content triggeringContent,
        CancellationToken cancellationToken = default)
    {
        var message = parameters["message"];
        _logger.LogInformation("Workflow: {Message}", message);
        await Task.CompletedTask;
    }
}
```

**That's it!** The plugin is auto-discovered and ready to use.

### Use in Workflow

```json
{
  "name": "Log on Publish",
  "triggerContentType": "Article",
  "triggerEvent": "Published",
  "actions": [{
    "type": "LogMessage",
    "parameters": {
      "message": "Article {{data.Title}} was published!"
    }
  }]
}
```

---

## Plugin Architecture

### The `IWorkflowAction` Interface

```csharp
public interface IWorkflowAction
{
    /// <summary>
    /// Unique identifier for this action type
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Execute the workflow action
    /// </summary>
    Task ExecuteAsync(
        Dictionary<string, string> parameters,  // From workflow definition
        Content triggeringContent,              // Content that triggered workflow
        CancellationToken cancellationToken = default);
}
```

### Metadata Attribute

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class WorkflowActionMetadataAttribute : Attribute
{
    public string Description { get; set; }
    public string[] RequiredParameters { get; set; }
    public string ExampleJson { get; set; }
}
```

### Auto-Discovery

Plugins are automatically discovered via Dependency Injection:

```csharp
// ServiceCollectionExtensions.cs
services.AddScoped<IWorkflowAction, EmailAction>();
services.AddScoped<IWorkflowAction, SmsAction>();
services.AddScoped<IWorkflowAction, YourCustomAction>(); // Auto-discovered!
```

---

## Creating Your First Plugin

### Step 1: Create the Class

```bash
# Create file
touch barakoCMS/Features/Workflows/Actions/SlackNotificationAction.cs
```

### Step 2: Implement Interface

```csharp
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Actions;

[WorkflowActionMetadata(
    Description = "Send notification to Slack channel",
    RequiredParameters = new[] { "webhookUrl", "message" },
    ExampleJson = @"{
      ""type"": ""SlackNotification"",
      ""parameters"": {
        ""webhookUrl"": ""https://hooks.slack.com/services/....."",
        ""message"": ""New content: {{data.Title}}""
      }
    }"
)]
public class SlackNotificationAction : IWorkflowAction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlackNotificationAction> _logger;

    public string Type => "SlackNotification";

    public SlackNotificationAction(
        IHttpClientFactory httpClientFactory,
        ILogger<SlackNotificationAction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        Dictionary<string, string> parameters,
        Content triggeringContent,
        CancellationToken cancellationToken = default)
    {
        var webhookUrl = parameters["webhookUrl"];
        var message = parameters["message"];

        var payload = new { text = message };

        using var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(
            webhookUrl, payload, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Slack notification sent for content {ContentId}", 
                triggeringContent.Id);
        }
        else
        {
            _logger.LogError(
                "Failed to send Slack notification: {StatusCode}", 
                response.StatusCode);
        }
    }
}
```

### Step 3: Register in DI

```csharp
// Extensions/ServiceCollectionExtensions.cs
services.AddScoped<IWorkflowAction, SlackNotificationAction>();
```

### Step 4: Test with Dry-Run

```bash
POST /api/workflows/dry-run
{
  "workflow": {
    "name": "Test Slack",
    "triggerContentType": "Article",
    "triggerEvent": "Published",
    "actions": [{
      "type": "SlackNotification",
      "parameters": {
        "webhookUrl": "https://hooks.slack.com/...",
        "message": "New article published!"
      }
    }]
  },
  "sampleContent": {
    "id": "...",
    "contentType": "Article",
    "data": { "Title": "Hello World" }
  }
}
```

---

## Built-in Plugin Examples

### 1. EmailAction (Simple)

```csharp
[WorkflowActionMetadata(
    Description = "Send email notification",
    RequiredParameters = new[] { "to", "subject", "body" },
    ExampleJson = "..."
)]
public class EmailAction : IWorkflowAction
{
    private readonly IEmailService _emailService;
    public string Type => "Email";

    public async Task ExecuteAsync(
        Dictionary<string, string> parameters,
        Content content,
        CancellationToken ct = default)
    {
        await _emailService.SendAsync(
            parameters["to"],
            parameters["subject"],
            parameters["body"],
            ct);
    }
}
```

**Usage**:
```json
{
  "type": "Email",
  "parameters": {
    "to": "{{data.Email}}",
    "subject": "Welcome {{data.FirstName}}!",
    "body": "Thank you for signing up."
  }
}
```

### 2. ConditionalAction (Advanced)

```csharp
[WorkflowActionMetadata(
    Description = "Execute actions based on condition",
    RequiredParameters = new[] { "condition", "ifTrue", "ifFalse" },
    ExampleJson = "..."
)]
public class ConditionalAction : IWorkflowAction
{
    private readonly IEnumerable<IWorkflowAction> _actions;
    public string Type => "Conditional";

    public async Task ExecuteAsync(
        Dictionary<string, string> parameters,
        Content content,
        CancellationToken ct = default)
    {
        var condition = parameters["condition"];
        var result = EvaluateCondition(condition, content);
        
        var actionType = result ? parameters["ifTrue"] : parameters["ifFalse"];
        var action = _actions.FirstOrDefault(a => a.Type == actionType);
        
        if (action != null)
        {
            await action.ExecuteAsync(parameters, content, ct);
        }
    }
}
```

**Usage**:
```json
{
  "type": "Conditional",
  "parameters": {
    "condition": "{{data.Amount}} > 1000",
    "ifTrue": "Email",
    "ifFalse": "SMS"
  }
}
```

### All 6 Built-in Actions

| Action          | Purpose            | Required Parameters              |
| --------------- | ------------------ | -------------------------------- |
| **Email**       | Send email         | `to`, `subject`, `body`          |
| **SMS**         | Send SMS           | `to`, `message`                  |
| **Webhook**     | HTTP POST          | `url`, `payload`                 |
| **CreateTask**  | Create task        | `title`, `assignee`, `dueDate`   |
| **UpdateField** | Update content     | `fieldName`, `fieldValue`        |
| **Conditional** | If/then/else logic | `condition`, `ifTrue`, `ifFalse` |

---

## Advanced Topics

### Template Variables

Use dynamic values from content:

```csharp
// Available system variables
{{id}}              // Content ID
{{contentType}}     // Content Type
{{status}}          // Status (0=Draft, 1=Published, 2=Archived)
{{createdAt}}       // Creation timestamp
{{updatedAt}}       // Last update timestamp

// Data field variables
{{data.FieldName}}  // Any field from content.Data
{{data.Email}}
{{data.Amount}}
{{data.Title}}
```

**Resolution happens automatically** via `ITemplateVariableExtractor`:

```csharp
// In your action
var resolvedMessage = _variableExtractor.ResolveVariables(
    parameters["message"], 
    triggeringContent);
```

### Dependency Injection

Inject any registered service:

```csharp
public class MyAction : IWorkflowAction
{
    private readonly IDocumentSession _session;      // Database
    private readonly IHttpClientFactory _httpFactory; // HTTP
    private readonly IMemoryCache _cache;            // Cache
    private readonly ILogger<MyAction> _logger;      // Logging

    public MyAction(
        IDocumentSession session,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<MyAction> logger)
    {
        _session = session;
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }
}
```

### Accessing Database

```csharp
public async Task ExecuteAsync(
    Dictionary<string, string> parameters,
    Content content,
    CancellationToken ct)
{
    // Query related content
    var relatedContent = await _session
        .Query<Content>()
        .Where(c => c.ContentType == "RelatedType")
        .ToListAsync(ct);

    // Update content
    content.Data["ProcessedAt"] = DateTime.UtcNow.ToString("o");
    _session.Update(content);
    await _session.SaveChangesAsync(ct);
}
```

### Error Handling

```csharp
public async Task ExecuteAsync(
    Dictionary<string, string> parameters,
    Content content,
    CancellationToken ct)
{
    try
    {
        // Your action logic
        await DoWorkAsync(parameters, ct);
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, 
            "Network error in workflow action {ActionType} for content {ContentId}",
            Type, content.Id);
        // Don't rethrow - let workflow continue
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, 
            "Unexpected error in workflow action {ActionType}",
            Type);
        throw; // Rethrow critical errors
    }
}
```

### Cancellation Tokens

Always respect cancellation tokens:

```csharp
public async Task ExecuteAsync(
    Dictionary<string, string> parameters,
    Content content,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested(); // Check at start
    
    await LongRunningOperationAsync(ct); // Pass to async calls
    
    ct.ThrowIfCancellationRequested(); // Check periodically
}
```

---

## Testing

### Unit Test Example

```csharp
public class SlackNotificationActionTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPostToWebhook()
    {
        // Arrange
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<ILogger<SlackNotificationAction>>();
        
        var action = new SlackNotificationAction(
            mockFactory.Object, 
            mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            ["webhookUrl"] = "https://hooks.slack.com/test",
            ["message"] = "Test message"
        };

        var content = new Content { Id = Guid.NewGuid() };

        // Act
        await action.ExecuteAsync(parameters, content);

        // Assert
        // Verify HTTP POST was made
        mockFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Once);
    }
}
```

### Integration Test with Dry-Run

```csharp
[Fact]
public async Task DryRun_SlackAction_ShouldSimulate()
{
    // Arrange
    var client = _factory.CreateClient();

    var request = new
    {
        workflow = new
        {
            name = "Test Slack",
            triggerContentType = "Article",
            triggerEvent = "Published",
            actions = new[]
            {
                new
                {
                    type = "SlackNotification",
                    parameters = new
                    {
                        webhookUrl = "https://hooks.slack.com/...",
                        message = "Test: {{data.Title}}"
                    }
                }
            }
        },
        sampleContent = new
        {
            contentType = "Article",
            data = new { Title = "Test Article" }
        }
    };

    // Act
    var response = await client.PostAsJsonAsync("/api/workflows/dry-run", request);

    // Assert
    response.IsSuccessStatusCode.Should().BeTrue();
    var result = await response.Content.ReadFromJsonAsync<DryRunResult>();
    result.Success.Should().BeTrue();
}
```

---

## Best Practices

### ✅ DO

1. **Keep plugins stateless**
   ```csharp
   // Good: Stateless
   public class EmailAction : IWorkflowAction
   {
       private readonly IEmailService _emailService; // Injected dependency only
   }
   ```

2. **Use async/await properly**
   ```csharp
   // Good: Truly async
   public async Task ExecuteAsync(...)
   {
       await _httpClient.PostAsync(...);
   }
   ```

3. **Validate parameters early**
   ```csharp
   public async Task ExecuteAsync(
       Dictionary<string, string> parameters, ...)
   {
       if (!parameters.ContainsKey("required"))
           throw new ArgumentException("Missing required parameter");
   }
   ```

4. **Log execution details**
   ```csharp
   _logger.LogInformation(
       "Executing {ActionType} for content {ContentId} with params {Params}",
       Type, content.Id, JsonSerializer.Serialize(parameters));
   ```

### ❌ DON'T

1. **Don't store state in fields**
   ```csharp
   // Bad: Stateful (breaks in concurrent scenarios)
   public class BadAction : IWorkflowAction
   {
       private string _lastMessage; // Don't do this!
   }
   ```

2. **Don't block async code**
   ```csharp
   // Bad: Blocking
   public async Task ExecuteAsync(...)
   {
       await Task.Run(() => SyncMethod()).Wait(); // Don't!
   }
   ```

3. **Don't ignore cancellation tokens**
   ```csharp
   // Bad: Ignores cancellation
   public async Task ExecuteAsync(..., CancellationToken ct)
   {
       await LongOperation(); // Should pass ct!
   }
   ```

---

## Troubleshooting

### Plugin Not Discovered

**Problem**: Your plugin doesn't appear in `GET /api/workflows/actions`

**Solutions**:
1. Verify it implements `IWorkflowAction`
2. Check it's registered in DI:
   ```csharp
   services.AddScoped<IWorkflowAction, YourAction>();
   ```
3. Ensure metadata attribute is present
4. Rebuild and restart application

### "Unknown action type" Error

**Problem**: Validation fails with unknown action type

**Solutions**:
1. Check `Type` property matches workflow JSON
2. Verify plugin is registered in DI
3. Use `GET /api/workflows/actions` to see available types

### Template Variables Not Resolving

**Problem**: `\{\{data.Field\}\}` shows literally in output

**Solutions**:
1. Inject `ITemplateVariableExtractor`
2. Call `ResolveVariables()` before using parameter values
3. Check field exists in content.Data

### Parameters Missing

**Problem**: `KeyNotFoundException` when accessing parameters

**Solutions**:
1. Check `RequiredParameters` in metadata attribute
2. Validate parameters exist before accessing:
   ```csharp
   if (!parameters.TryGetValue("key", out var value))
       throw new ArgumentException("Missing parameter: key");
   ```

### Action Not Executing

**Problem**: Workflow created but action never runs

**Solutions**:
1. Check async daemon is running (visible in logs)
2. Verify workflow conditions are met
3. Check event is being published
4. Review execution logs: `GET /api/workflows/{id}/debug`

---

## Next Steps

- ✅ Follow [Migration Guide](workflow-migration-guide.md) to upgrade existing workflows
- ✅ See `AttendancePOC` project for real-world examples
- ✅ Join community discussions on GitHub
- ✅ Share your custom plugins!

---

**Happy Plugin Building!** 🚀
