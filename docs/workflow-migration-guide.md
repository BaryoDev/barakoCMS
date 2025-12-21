# Workflow Migration Guide

**Upgrade from Hardcoded Workflows to Plugin-Based System**

This guide helps you migrate existing workflows to the new plugin-based architecture introduced in Phase 2.

---

## Table of Contents

1. [Overview](#overview)
2. [Why Migrate?](#why-migrate)
3. [Migration Checklist](#migration-checklist)
4. [Before & After Examples](#before--after-examples)
5. [Step-by-Step Migration](#step-by-step-migration)
6. [Breaking Changes](#breaking-changes)
7. [FAQ](#faq)

---

## Overview

### What Changed in Phase 2?

**Phase 1** (Hardcoded):
- Actions hardcoded in `WorkflowEngine.cs`
- Adding new actions required core code changes
- Tight coupling between engine and actions

**Phase 2** (Plugin-Based):
- Actions are independent plugins
- Add new actions by creating a class
- Clean separation via `IWorkflowAction` interface

### Migration Timeline

```
Current State → Gradual Migration → Fully Migrated
(Phase 1)         (Compatible)      (Phase 2)
    ↓                  ↓                 ↓
  Works            Both work         Plugins only
```

**Good News**: Migration is **100% optional** and **fully backward compatible**!

---

## Why Migrate?

### Benefits of Plugin System

| Feature         | Before (Phase 1)     | After (Phase 2)                        |
| --------------- | -------------------- | -------------------------------------- |
| **Add Actions** | Modify core code     | Create plugin class                    |
| **Testing**     | Complex mocking      | Test plugins in isolation              |
| **Reusability** | Copy-paste code      | Reuse plugins across projects          |
| **Discovery**   | Manual documentation | Automatic via `/api/workflows/actions` |
| **Versioning**  | Breaks on changes    | Plugins versioned independently        |
| **Ecosystem**   | None                 | Community plugins possible             |

### When to Migrate?

✅ **Migrate if you**:
- Want to add custom workflow actions
- Need better testability
- Plan to share actions across projects
- Want runtime plugin discovery

⏸️ **Don't migrate if you**:
- Only use built-in Email/SMS actions
- Have simple, stable workflows
- Prefer "if it ain't broke, don't fix it"

---

## Migration Checklist

### Pre-Migration

- [ ] Back up your database (event store)
- [ ] Document existing workflows
- [ ] Review current workflow definitions
- [ ] Identify custom business logic to extract

### Migration Steps

- [ ] Create plugin classes for custom actions
- [ ] Add `WorkflowActionMetadata` attributes
- [ ] Register plugins in DI container
- [ ] Test with dry-run mode
- [ ] Update workflow definitions (optional)
- [ ] Deploy and monitor

### Post-Migration

- [ ] Verify all workflows still execute
- [ ] Check execution logs for errors
- [ ] Update documentation
- [ ] Remove old hardcoded logic (optional)

---

## Before & After Examples

### Example 1: Email Notification

#### Before (Phase 1)

**Hardcoded in WorkflowEngine.cs**:
```csharp
public class WorkflowEngine
{
    public async Task ProcessEventAsync(IEvent @event)
    {
        // Find matching workflows...
        
        foreach (var action in workflow.Actions)
        {
            if (action.Type == "SendEmail") // Hardcoded check
            {
                // Hardcoded email logic
                var to = action.Config["to"];
                var subject = action.Config["subject"];
                var body = action.Config["body"];
                
                await _emailService.SendAsync(to, subject, body);
            }
        }
    }
}
```

**Workflow Definition** (unchanged):
```json
{
  "actions": [{
    "type": "SendEmail",
    "config": {
      "to": "{{data.Email}}",
      "subject": "Welcome!",
      "body": "Thank you for signing up."
    }
  }]
}
```

#### After (Phase 2)

**Email Plugin** (new file):
```csharp
// Features/Workflows/Actions/EmailAction.cs
[WorkflowActionMetadata(
    Description = "Send email notification",
    RequiredParameters = new[] { "to", "subject", "body" },
    ExampleJson = @"{...}"
)]
public class EmailAction : IWorkflowAction
{
    private readonly IEmailService _emailService;
    public string Type => "Email";

    public EmailAction(IEmailService emailService)
    {
        _emailService = emailService;
    }

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

**Workflow Definition**:
```json
{
  "actions": [{
    "type": "Email",  // ← Changed from "SendEmail" to "Email"
    "parameters": {   // ← Changed from "config" to "parameters"
      "to": "{{data.Email}}",
      "subject": "Welcome!",
      "body": "Thank you for signing up."
    }
  }]
}
```

**WorkflowEngine** (simplified):
```csharp
public class WorkflowEngine
{
    private readonly IEnumerable<IWorkflowAction> _actions; // Injected!

    public async Task ProcessEventAsync(IEvent @event)
    {
        // Find matching workflows...
        
        foreach (var actionDef in workflow.Actions)
        {
            // Plugin system handles type lookup
            var action = _actions.FirstOrDefault(a => a.Type == actionDef.Type);
            await action?.ExecuteAsync(actionDef.Parameters, content);
        }
    }
}
```

---

### Example 2: Custom Business Logic

#### Before (Phase 1)

**Hardcoded in WorkflowEngine.cs**:
```csharp
if (action.Type == "ApprovalNotification")
{
    // Complex business logic embedded here
    var amount = decimal.Parse(content.Data["Amount"].ToString());
    
    if (amount > 10000)
    {
        // Notify VP
        await _emailService.SendAsync("vp@company.com", ...);
    }
    else if (amount > 1000)
    {
        // Notify Manager
        await _emailService.SendAsync("manager@company.com", ...);
    }
    else
    {
        // Auto-approve
        content.Data["Status"] = "Approved";
        _session.Update(content);
    }
}
```

#### After (Phase 2)

**Approval Plugin** (new, reusable):
```csharp
[WorkflowActionMetadata(
    Description = "Approval logic based on amount",
    RequiredParameters = new[] { "amountField", "managerEmail", "vpEmail" },
    ExampleJson = @"{...}"
)]
public class ApprovalAction : IWorkflowAction
{
    private readonly IEmailService _emailService;
    private readonly IDocumentSession _session;

    public string Type => "Approval";

    public async Task ExecuteAsync(
        Dictionary<string, string> parameters,
        Content content,
        CancellationToken ct)
    {
        var amountField = parameters["amountField"];
        var amount = decimal.Parse(content.Data[amountField].ToString());

        if (amount > 10000)
        {
            await _emailService.SendAsync(parameters["vpEmail"], 
                "VP Approval Required", 
                $"Amount: {amount:C}");
        }
        else if (amount > 1000)
        {
            await _emailService.SendAsync(parameters["managerEmail"],
                "Manager Approval Required",
                $"Amount: {amount:C}");
        }
        else
        {
            content.Data["Status"] = "Approved";
            _session.Update(content);
            await _session.SaveChangesAsync(ct);
        }
    }
}
```

**Workflow Definition**:
```json
{
  "actions": [{
    "type": "Approval",
    "parameters": {
      "amountField": "Amount",
      "managerEmail": "{{data.ManagerEmail}}",
      "vpEmail": "vp@company.com"
    }
  }]
}
```

**Benefits**:
- ✅ Testable in isolation
- ✅ Reusable across workflows
- ✅ Configurable via parameters
- ✅ No core code changes

---

### Example 3: Chained Actions

#### Before (Phase 1)

**Multiple hardcoded steps**:
```csharp
// Step 1: Send email
await _emailService.SendAsync(...);

// Step 2: Create task
await _taskService.CreateAsync(...);

// Step 3: Update field
content.Data["ProcessedAt"] = DateTime.UtcNow;
_session.Update(content);
```

#### After (Phase 2)

**Separate plugins, chained in workflow**:
```json
{
  "actions": [
    {
      "type": "Email",
      "parameters": {
        "to": "{{data.Email}}",
        "subject": "Processing Started"
      }
    },
    {
      "type": "CreateTask",
      "parameters": {
        "title": "Review: {{data.Title}}",
        "assignee": "{{data.Reviewer}}"
      }
    },
    {
      "type": "UpdateField",
      "parameters": {
        "fieldName": "ProcessedAt",
        "fieldValue": "{{updatedAt}}"
      }
    }
  ]
}
```

**Benefits**:
- ✅ Declarative workflow definition
- ✅ Each action independently tested
- ✅ Easy to reorder or add steps
- ✅ No code changes for new combinations

---

## Step-by-Step Migration

### Step 1: Audit Existing Workflows

List all workflows in your database:

```bash
GET /api/workflows

# Review action types used
{
  "name": "Order Confirmation",
  "actions": [
    { "type": "SendEmail" },
    { "type": "CustomOrderProcessing" }
  ]
}
```

### Step 2: Create Plugins for Custom Actions

For each unique action type, create a plugin:

```csharp
// 1. Create file: Features/Workflows/Actions/CustomOrderProcessingAction.cs

[WorkflowActionMetadata(
    Description = "Process order with business logic",
    RequiredParameters = new[] { "orderId" },
    ExampleJson = "..."
)]
public class CustomOrderProcessingAction : IWorkflowAction
{
    public string Type => "CustomOrderProcessing";
    
    public async Task ExecuteAsync(...)
    {
        // Move logic from WorkflowEngine here
    }
}
```

### Step 3: Register Plugins in DI

```csharp
// Extensions/ServiceCollectionExtensions.cs

public static IServiceCollection AddWorkflows(this IServiceCollection services)
{
    // Existing plugins
    services.AddScoped<IWorkflowAction, EmailAction>();
    services.AddScoped<IWorkflowAction, SmsAction>();
    
    // Your new plugins
    services.AddScoped<IWorkflowAction, CustomOrderProcessingAction>();
    services.AddScoped<IWorkflowAction, ApprovalAction>();
    
    return services;
}
```

### Step 4: Test with Dry-Run

Before deploying, test workflows:

```bash
POST /api/workflows/dry-run
{
  "workflow": { /* your workflow */ },
  "sampleContent": { /* sample data */ }
}

# Should return:
{
  "success": true,
  "actions": [
    { "type": "CustomOrderProcessing", "success": true }
  ]
}
```

### Step 5: Update Workflow Definitions (Optional)

You can keep old definitions OR update them:

**Old (still works)**:
```json
{
  "actions": [{
    "type": "SendEmail",
    "config": { ... }
  }]
}
```

**New (recommended)**:
```json
{
  "actions": [{
    "type": "Email",
    "parameters": { ... }
  }]
}
```

To update, use `PUT /api/workflows/{id}` with new definition.

### Step 6: Deploy and Monitor

```bash
# Deploy new code with plugins
docker build -t barakocms:v2 .
kubectl apply -f deployment.yaml

# Monitor logs for errors
kubectl logs -f deployment/barakocms

# Check execution history
GET /api/workflows/{id}/debug
```

---

## Breaking Changes

### None! 🎉

The plugin system is **100% backward compatible**:

- ✅ Old workflow definitions still work
- ✅ Existing actions continue to execute
- ✅ No database migrations required
- ✅ Gradual migration supported

### What's Changed (Optional Upgrades)

| Old             | New                 | Required?                 |
| --------------- | ------------------- | ------------------------- |
| `action.Config` | `action.Parameters` | No (both work)            |
| `"SendEmail"`   | `"Email"`           | No (alias supported)      |
| Hardcoded logic | Plugin classes      | No (only for new actions) |

---

## FAQ

### Q: Do I need to migrate existing workflows?

**A:** No. Existing workflows continue to work without changes.

### Q: Can I mix old and new approaches?

**A:** Yes! You can have:
- Old workflows using hardcoded actions
- New workflows using plugins
- Same workflow with both types of actions

### Q: Will my existing workflows break?

**A:** No. We maintain backward compatibility. Old workflows execute unchanged.

### Q: How do I add a completely new action type?

**A:**
1. Create plugin class (see [Plugin Guide](plugin-development-guide.md))
2. Register in DI
3. Use in workflows immediately

### Q: Can I version my plugins?

**A:** Yes. Use different class names or namespaces:
```csharp
public class EmailActionV1 : IWorkflowAction { }
public class EmailActionV2 : IWorkflowAction { }
```

### Q: What if I want to remove a plugin?

**A:** Just unregister from DI. Workflows using that action will fail gracefully:
```csharp
// Remove this line:
// services.AddScoped<IWorkflowAction, OldAction>();
```

### Q: How do I test migration before deploying?

**Answer**:
1. Use `/api/workflows/dry-run` endpoint
2. Test in staging environment first
3. Monitor execution logs after deployment

### Q: Can I rollback if something goes wrong?

**A:** Yes. Since it's backward compatible:
1. Deploy old code version
2. Workflows revert to hardcoded behavior
3. No data loss (event sourcing)

### Q: Where can I get help?

**A:**
- Read [Plugin Guide](plugin-development-guide.md)
- Check [GitHub Issues](https://github.com/BaryoDev/barakoCMS/issues)
- Join community Discord
- File support ticket

---

## Next Steps

- ✅ [Plugin Development Guide](plugin-development-guide.md) - Create custom actions
- ✅ `AttendancePOC` Examples - Real-world usage
- ✅ API Documentation (via Swagger UI) - Test workflows

**Happy Migrating!** 🚀
