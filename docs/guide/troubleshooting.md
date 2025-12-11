# Troubleshooting Guide

Common issues and how to resolve them.

## 🔴 Database Issues

### `Npgsql.NpgsqlException: Failed to connect`
*   **Cause**: PostgreSQL is not running or port 5432 is blocked.
*   **Fix**: 
    1. Run `docker ps` to ensure container is up.
    2. Check `appsettings.json` connection string.

### `Marten.Exceptions.MartenCommandException: Relation "mt_events" does not exist`
*   **Cause**: Database schema not initialized.
*   **Fix**: Restart the API. Marten automatically applies migrations on startup in Development mode.

## 🟠 API Issues

### `412 Precondition Failed`
*   **Context**: Updating Content.
*   **Cause**: **Optimistic Concurrency**. Another user modified the record since you loaded it.
*   **Fix**: fetch the latest version (`GET /api/contents/{id}`) and retry with the new `version` number.

### `409 Conflict` (Idempotency)
*   **Context**: Creating Content.
*   **Cause**: You sent the same `Idempotency-Key` header twice for different payloads.
*   **Fix**: Generate a new UUID for the new request.

## 🟡 Workflow Issues

### Email not sending
*   **Cause**: `MockEmailService` is active by default.
*   **Fix**: Implement `IEmailService` with a real provider (SendGrid/SMTP) and register it in `Program.cs`.

### Workflow not triggering
*   **Checklist**:
    1. Is `Features:UseAsyncWorkflows` enabled?
    2. Does the workflow `TriggerContentType` match exactly?
    3. Do the `Conditions` match the content data?
