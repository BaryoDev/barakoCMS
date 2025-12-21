# Architecture Deep Dive

BarakoCMS is built on a **Vertical Slice** architecture using **Event Sourcing** and **CQRS** (Command Query Responsibility Segregation). This design ensures scalability, maintainability, and loose coupling.

## System Overview

```mermaid
graph TD
    Client[Client App / Postman]
    API[FastEndpoints API]
    Marten[MartenDB / PostgreSQL]
    Daemon[Async Daemon]
    Workflow[Workflow Engine]

    Client -->|Command (POST/PUT)| API
    Client -->|Query (GET)| API
    API -->|Store Event| Marten
    API -->|Read Projection| Marten
    Marten -->|Event Stream| Daemon
    Daemon -->|Trigger| Workflow
```

## Key Patterns

### 1. Vertical Slice Architecture

Instead of layers (Controller, Service, Repository), we organize by **Features**. Each endpoint is a self-contained slice containing everything it needs.

```text
Features/
  ├── Content/
  │   ├── Create/
  │   │   ├── Endpoint.cs
  │   │   ├── Models.cs
  │   │   └── Validator.cs
  │   ├── Update/
  │   └── Delete/
```

### 2. Event Sourcing (The "Write" Side)

We don't just update rows; we store **Events**.
When you update content, we append a `ContentUpdated` event.

| Stream ID | Version | Event Type       | Data                        |
| --------- | ------- | ---------------- | --------------------------- |
| UUID-1    | 1       | ContentCreated   | `{ "Title": "Draft" }`      |
| UUID-1    | 2       | ContentUpdated   | `{ "Title": "Final" }`      |
| UUID-1    | 3       | ContentPublished | `{ "Status": "Published" }` |

**Benefits:**
*   **Audit Log**: Who changed what and when (Free!)
*   **Time Travel**: "Show me the article as it was last Tuesday."
*   **Debuggability**: Replay events to reproduce bugs.

### 3. Projections (The "Read" Side)

MartenDB automatically "projects" these events into a queryable document (a "View").

*   **Live Aggregation**: Calculated on-the-fly for single records.
*   **Inline Projections**: Updated transactionally during writes.
*   **Async Projections**: Updated in the background for heavy reports.

### 4. Role-Based Access Control (RBAC)

Our authorization middleware intercepts requests *before* the handler.

1.  **Extract User**: Parse JWT for UserID.
2.  **Load Permissions**: Fetch User -> Groups -> Roles -> Permissions.
3.  **Evaluate**: Check if `CanUpdate(ContentType="article")` is true.
4.  **Check Conditions**: If constrained (e.g., "Owns Record"), load the record and verify.

### 5. Async Workflow Engine

We use a "Fire-and-Forget" pattern backed by persistence.

1.  **API**: Saves event and returns `200 OK` instantly.
2.  **Marten Async Daemon**: Polling process sees new event.
3.  **Permission**: Daemon checks if any Workflow matches the event.
4.  **Execute**: Runs the `WorkflowAction` (e.g., SendGrid API).
