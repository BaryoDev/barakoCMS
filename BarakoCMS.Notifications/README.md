<div align="center">
  <h1>BarakoCMS.Notifications</h1>
  <p><em>Tell a user something happened, from any module.</em></p>
</div>

---

A notification has a recipient, a type, a title, an optional body and payload, a created time and a
read state. It is stored per tenant, so one raised in a tenant is invisible from another, and a user
sees only their own.

## Enable it

```sh
dotnet add package BarakoCMS.Notifications
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);
```

The module is found and registered like every first-party module. Its table is created on first
boot, or by `migrations/5.2.0/notifications.sql` ahead of a deploy.

## Raise one from a module

Resolve `INotificationService` and call `RaiseAsync`. This is an accounting module asking its
approver to look at an entry:

```csharp
public sealed class EntryApprovals(INotificationService notifications)
{
    public Task AskForApprovalAsync(Guid approverId, string entryNumber, CancellationToken ct) =>
        notifications.RaiseAsync(
            new RaiseNotification(approverId, "entry.needs-approval", $"Entry {entryNumber} needs approval")
            {
                Payload = new Dictionary<string, object> { ["entryNumber"] = entryNumber },
            }, ct);
}
```

The notification is saved in the current tenant, then handed to every `INotificationSink`. A sink
that throws is logged and skipped, and one that takes longer than `Modules:Notifications:SinkTimeoutSeconds`
(default 10) is cut off, so a down channel never loses the in-app copy or holds the caller.

The recipient must be a member of the current tenant, or `RaiseAsync` throws `RecipientNotInTenantException`
(the HTTP route answers 404). A type or title cannot hold control characters, and a serialized payload is
at most 8 KB; both throw `ArgumentException` (the route answers 400).

Name types after the module that owns them (`entry.needs-approval`, `ticket.assigned`), so a client
can pick an icon or a link from the type and the payload.

## Endpoints

| Route | Who | |
|---|---|---|
| `GET /api/notifications` | any signed-in user | Own notifications, newest first, paged. `?unreadOnly=true` leaves out read ones. |
| `GET /api/notifications/unread-count` | any signed-in user | `{ "count": n }` |
| `POST /api/notifications/{id}/read` | the recipient | 204. Someone else's id is 404. |
| `POST /api/notifications/read-all` | any signed-in user | Marks up to 500 of own, returns `{ "updated": n }`. Call again while it returns 500. |
| `POST /api/notifications` | `manage_notifications` | Raise one for a user of the tenant. |
| `GET /api/notifications/admin` | `manage_notifications` | Every user's in the tenant, paged. `?recipientUserId=` narrows it. |

## Delivery sinks

The in-app store is always on. Add a channel by registering an `INotificationSink`:

```csharp
services.AddScoped<INotificationSink, MyWebhookSink>();
```

Email is built in and off by default. It uses whichever `IEmailService` the host has (Resend or SMTP):

```json
{ "Modules": { "Notifications": { "EmailDelivery": { "Enabled": true, "Types": ["payment.approved"] } } } }
```

`Types` limits which notification types are emailed; leave it empty for all.
