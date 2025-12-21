# Workflow Automation

BarakoCMS includes an event-driven workflow engine that triggers actions automatically when content changes.

## How It Works

1. **Event Triggered**: Content is Created, Updated, Deleted, or Status Changed.
2. **Async Processing**: A background daemon processes the event (zero API latency).
3. **Workflow Matched**: The system finds active workflows for that Content Type and Event.
4. **Actions Executed**: Configured actions (Email, SMS, Webhook) are executed.

## Creating a Workflow

```bash
POST /api/workflows
Authorization: Bearer {ADMIN_TOKEN}

{
  "name": "Welcome Email",
  "triggerContentType": "UserProfile",
  "triggerEvent": "Created",
  "actions": [
    {
      "type": "SendEmail",
      "config": {
        "to": "{{data.Email}}",
        "subject": "Welcome {{data.FirstName}}!",
        "body": "Thanks for joining us."
      }
    }
  ]
}
```

## Template Variables

You can inject data from the content record into your action configuration:

- `{{data.FieldName}}`: Value of a field in the content data.
- `{{id}}`: The ID of the record.
- `{{status}}`: The status of the record.

## Available Actions

- **SendEmail**: Sends an email via the configured `IEmailService`.
- **SendSms**: Sends an SMS via the configured `ISmsService`.
- **(More coming in Phase 2)**: Custom plugins, Webhooks, Task creation.
