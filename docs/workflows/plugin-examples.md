# Workflow Plugin Examples

Practical examples demonstrating how to use BarakoCMS workflow plugins to automate common business processes.

## Content Approval Workflow

Auto-create approval tasks when content is submitted for review.

::: v-pre
```json
{
  "name": "Content Approval Workflow",
  "triggerContentType": "Article",
  "triggerEvent": "Updated",
  "conditions": {
    "status": "PendingReview"
  },
  "actions": [
    {
      "type": "CreateTask",
      "parameters": {
        "ContentType": "ApprovalTask",
        "Status": "Draft",
        "Title": "Review: {{data.Title}}",
        "Data.ArticleId": "{{id}}",
        "Data.SubmittedBy": "{{data.Author}}",
        "Data.Priority": "Normal"
      }
    },
    {
      "type": "Email",
      "parameters": {
        "To": "reviewers@example.com",
        "Subject": "New Article Pending Review",
        "Body": "Article '{{data.Title}}' submitted by {{data.Author}} requires review."
      }
    }
  ]
}
```
:::

## Priority-Based Escalation

Send different notifications based on content priority.

::: v-pre
```json
{
  "name": "Priority Escalation",
  "triggerContentType": "SupportTicket",
  "triggerEvent": "Created",
  "conditions": {},
  "actions": [
    {
      "type": "Conditional",
      "parameters": {
        "Condition": "{{data.Priority}} == \"Critical\"",
        "ThenActions": "[{\"Type\":\"SMS\",\"Parameters\":{\"To\":\"+1234567890\",\"Message\":\"CRITICAL: {{data.Subject}}\"}},{\"Type\":\"Email\",\"Parameters\":{\"To\":\"oncall@example.com\",\"Subject\":\"CRITICAL TICKET\",\"Body\":\"Immediate attention required\"}}]",
        "ElseActions": "[{\"Type\":\"Email\",\"Parameters\":{\"To\":\"support@example.com\",\"Subject\":\"New Ticket\",\"Body\":\"Ticket created: {{data.Subject}}\"}}]"
      }
    }
  ]
}
```
:::

## Auto-Assignment Workflow

Automatically assign content to team members based on category.

::: v-pre
```json
{
  "name": "Auto-Assign by Category",
  "triggerContentType": "Task",
  "triggerEvent": "Created",
  "conditions": {},
  "actions": [
    {
      "type": "Conditional",
      "parameters": {
        "Condition": "{{data.Category}} == \"Development\"",
        "ThenActions": "[{\"Type\":\"UpdateField\",\"Parameters\":{\"Field\":\"data.AssignedTo\",\"Value\":\"dev-team@example.com\"}}]",
        "ElseActions": "[{\"Type\":\"Conditional\",\"Parameters\":{\"Condition\":\"{{data.Category}} == \\\"Design\\\"\",\"ThenActions\":\"[{\\\"Type\\\":\\\"UpdateField\\\",\\\"Parameters\\\":{\\\"Field\\\":\\\"data.AssignedTo\\\",\\\"Value\\\":\\\"design-team@example.com\\\"}}]\"}}]"
      }
    },
    {
      "type": "Email",
      "parameters": {
        "To": "{{data.AssignedTo}}",
        "Subject": "New Task Assigned",
        "Body": "You've been assigned: {{data.Title}}"
      }
    }
  ]
}
```
:::

## Status Change Notifications

Notify stakeholders when content status changes.

::: v-pre
```json
{
  "name": "Status Change Alerts",
  "triggerContentType": "PurchaseOrder",
  "triggerEvent": "Updated",
  "conditions": {},
  "actions": [
    {
      "type": "Conditional",
      "parameters": {
        "Condition": "{{status}} == \"Approved\"",
        "ThenActions": "[{\"Type\":\"Email\",\"Parameters\":{\"To\":\"{{data.SubmitterEmail}}\",\"Subject\":\"PO Approved\",\"Body\":\"Your purchase order has been approved.\"}},{\"Type\":\"Webhook\",\"Parameters\":{\"Url\":\"https://api.example.com/po-approved\"}}]"
      }
    }
  ]
}
```
:::

## Multi-Step Approval Chain

Create subsequent approval tasks as previous ones complete.

::: v-pre
```json
{
  "name": "Multi-Level Approval",
  "triggerContentType": "ApprovalTask",
  "triggerEvent": "Updated",
  "conditions": {
    "status": "Approved"
  },
  "actions": [
    {
      "type": "Conditional",
      "parameters": {
        "Condition": "{{data.Level}} == \"1\"",
        "ThenActions": "[{\"Type\":\"CreateTask\",\"Parameters\":{\"ContentType\":\"ApprovalTask\",\"Status\":\"Draft\",\"Title\":\"Level 2 Approval: {{data.Title}}\",\"Data.Level\":\"2\",\"Data.OriginalId\":\"{{data.OriginalId}}\"}}]",
        "ElseActions": "[{\"Type\":\"UpdateField\",\"Parameters\":{\"TargetId\":\"{{data.OriginalId}}\",\"Field\":\"Status\",\"Value\":\"Published\"}},{\"Type\":\"Email\",\"Parameters\":{\"To\":\"{{data.SubmitterEmail}}\",\"Subject\":\"Fully Approved\",\"Body\":\"Your content is now published.\"}}]"
      }
    }
  ]
}
```
:::

## Webhook Integration Pattern

Trigger external services when content events occur.

::: v-pre
```json
{
  "name": "External System Integration",
  "triggerContentType": "Order",
  "triggerEvent": "Created",
  "conditions": {
    "status": "Confirmed"
  },
  "actions": [
    {
      "type": "Webhook",
      "parameters": {
        "Url": "https://external-api.example.com/webhooks/orders"
      }
    },
    {
      "type": "CreateTask",
      "parameters": {
        "ContentType": "Fulfillment",
        "Status": "Draft",
        "Title": "Fulfill Order {{id}}",
        "Data.OrderId": "{{id}}",
        "Data.ExternalSyncStatus": "Pending"
      }
    }
  ]
}
```
:::

## Best Practices

### Conditional Action Formatting

When using `ConditionalAction`, the `ThenActions` and `ElseActions` parameters must be **valid JSON strings**:

::: v-pre
```json
{
  "type": "Conditional",
  "parameters": {
    "Condition": "{{data.Status}} == \"Urgent\"",
    "ThenActions": "[{\"Type\":\"Email\",\"Parameters\":{\"To\":\"urgent@example.com\"}}]"
  }
}
```
:::

### Template Variables

Use template variables to make workflows dynamic:

- `{ {id} }` - Content ID
- `{ {contentType} }` - Content type name
- `{ {status} }` - Content status  
- `{ {data.FieldName} }` - Any field from content data

### Error Handling

Always provide fallback actions for conditional workflows:

::: v-pre
```json
{
  "type": "Conditional",
  "parameters": {
    "Condition": "{{data.Priority}} == \"High\"",
    "ThenActions": "...",
    "ElseActions": "[{\"Type\":\"Email\",\"Parameters\":{\"To\":\"default@example.com\"}}]"
  }
}
```
:::

### Testing Workflows

1. Start with simple, single-action workflows
2. Test each action type independently
3. Add conditional logic incrementally
4. Verify template variable substitution
5. Monitor logs for execution errors
