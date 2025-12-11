# Event Sourcing & Audit Trails

BarakoCMS uses **Event Sourcing** as its primary persistence mechanism. This means we store the *history* of changes, not just the current state.

## Why Event Sourcing?

1.  **Zero Information Loss**: We never overwrite data. Every `UPDATE` is actually an `APPEND`.
2.  **Audit Trail**: Who changed what, when, and why? It's built-in.
3.  **Time Travel**: You can reconstruct the state of any record at any point in time.
4.  **Performance**: Appending an event is faster than locking rows for updates.

## The Event Stream

Every piece of content has a "Stream". A stream is an ordered list of events.

**Example Stream: Article "Hello World"**

| Version | Event Type         | Description                   | Timestamp | User     |
| :------ | :----------------- | :---------------------------- | :-------- | :------- |
| **1**   | `ContentCreated`   | Initial draft created         | 10:00 AM  | `admin`  |
| **2**   | `ContentUpdated`   | Title changed to "Hello .NET" | 10:05 AM  | `editor` |
| **3**   | `ContentPublished` | Status changed to Published   | 11:00 AM  | `admin`  |

## Viewing History via API

You can fetch the full history of any record:

```bash
GET /api/contents/{id}/history
Authorization: Bearer <TOKEN>
```

**Response:**
```json
[
  {
    "version": 1,
    "eventType": "ContentCreated",
    "timestamp": "2024-03-20T10:00:00Z",
    "userId": "user_123",
    "data": { "Title": "Draft" }
  },
  {
    "version": 2,
    "eventType": "ContentUpdated",
    "timestamp": "2024-03-20T10:05:00Z",
    "userId": "user_456",
    "data": { "Title": "Hello .NET" }
  }
]
```

## Rollback (Time Travel)

Mistakes happen. You can revert a record to any previous version. This doesn't delete later events; it creates a *new* event (`ContentRolledBack`) that restores the old state.

```bash
POST /api/contents/{id}/rollback
{
  "targetVersion": 1
}
```

*Result: Stream Version 4 is created, applying the data from Version 1.*
