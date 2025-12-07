# BarakoCMS v1.2.1 Release Notes

**Date:** December 8, 2025
**Version:** 1.2.1

## Overview

BarakoCMS v1.2.1 introduces enterprise-grade data integrity and automation features, making it a robust platform for critical applications. The key additions are **Idempotency** for safe retry logic, **Content History** for auditability and recovery, and a **Workflow Engine** for automating business processes.

## 🚀 New Features

### 1. Idempotency Support
Prevent duplicate operations when network issues cause retries.

*   **How it works**: Clients send an `Idempotency-Key` header with a unique value (UUID recommended). If a request with the same key is received again, the server returns `409 Conflict` (or the cached response in future iterations) instead of re-processing.
*   **Usage**:
    ```http
    POST /api/contents
    Idempotency-Key: <unique-uuid>
    Content-Type: application/json

    { ... }
    ```
*   **Protection**: Applies to `POST`, `PUT`, and `PATCH` requests.

### 2. Content History & Rollback
Full audit trail of content changes with the ability to revert to any previous state.

*   **View History**:
    `GET /api/contents/{id}/history`
    Returns a list of versions with metadata (timestamp, modifier).
*   **Rollback**:
    `POST /api/contents/{id}/rollback/{versionId}`
    Reverts the content to the data state of the specified version. This creates a *new* version in the history stream (forward-only audit log).

### 3. Workflow Automation
Trigger automated actions based on content events.

*   **Triggers**: Supports `Created` and `Updated` events.
*   **Actions**:
    *   **Email**: Send notifications to defined recipients (e.g., HR, Admin).
*   **Configuration**: Workflows are defined in the database.
    *   *Example*: "When `AttendanceRecord` is `Created`, send email to `hr@company.com`."

### 4. Code Quality & Standards
*   **Strict Security**: Enforced `AnalysisLevel` to `latest` and `EnforceCodeStyleInBuild` to ensure maintainable, secure code.
*   **Dependencies**: Updated to latest FastEndpoints and Marten libraries.

## 🛠 Fixes & Improvements

*   **Fixed**: Workflow triggering on `Create` and `Update` endpoints was missing invocation logic.
*   **Fixed**: Rollback test incorrectly handling content type headers (`415` error).
*   **Fixed**: Duplicate partial class definitions in `Program.cs`.
*   **Docs**: Added comprehensive XML documentation to core models.

## 📦 Installation

```bash
dotnet add package BarakoCMS --version 1.2.0
```

## 📝 Next Steps (Roadmap)
*   **v1.3.0**: Enhanced Workflow conditions (Scripting support).
*   **v1.3.0**: Distributed Cache for Idempotency (Redis).
