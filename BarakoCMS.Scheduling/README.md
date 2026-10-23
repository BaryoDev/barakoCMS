# BarakoCMS.Scheduling

Schedule a named action for a due time. When the time comes, one API instance hands the entry to the
handler registered for that action name, and the entry never fires twice.

```sh
dotnet add package BarakoCMS.Scheduling
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Scheduling`). To name it by hand, put
`modules.Add(new BarakoCMS.Scheduling.SchedulingModule())` in the `AddBarakoCMS` callback. See `MODULES.md`
in the repository.

```csharp
public sealed class SendReminder : IScheduledActionHandler
{
    public string Action => "send-reminder";

    public Task HandleAsync(ScheduledActionContext context, CancellationToken ct) { /* ... */ }
}

services.AddSingleton<IScheduledActionHandler, SendReminder>();

var id = await scheduler.ScheduleAsync(tenantId, "send-reminder", dueAt, payloadJson);
await scheduler.CancelAsync(id);
```

An entry goes Pending, Firing, then Fired or Failed. A handler that throws, or has no handler for the
action, leaves it Failed. A handler that dies with its instance leaves it Firing. Neither is retried.

Not in this version: recurrence, a workflow schedule trigger, and moving the scheduled content
sweep onto this module.
