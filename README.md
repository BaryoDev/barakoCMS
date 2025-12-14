# BarakoCMS

**The AI-Native, High-Performance Headless CMS for .NET 8.**

[![NuGet](https://img.shields.io/nuget/v/BarakoCMS.svg)](https://www.nuget.org/packages/BarakoCMS)
[![Documentation](https://img.shields.io/badge/docs-visited-blue)](https://baryodev.github.io/barakoCMS/)
[![License](https://img.shields.io/github/license/BaryoDev/barakoCMS)](LICENSE)

<a href='https://ko-fi.com/T6T01CQT4R' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi3.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>


> [!IMPORTANT]
> **Hobby Project Disclaimer**
> BarakoCMS is a passion project built for educational and portfolio purposes. While we strive for backward compatibility, **breaking changes may occur** as we introduce new features or refine the architecture. Use in production with this understanding.

> 📚 **Full Documentation**: [https://baryodev.github.io/barakoCMS/](https://baryodev.github.io/barakoCMS/)
>
> Please visit our documentation site for Getting Started guides, API Reference, and Architecture Deep Dives.

BarakoCMS is engineered for **Speed**, **Extensibility**, and **Robustness**. Built on the bleeding edge with [FastEndpoints](https://fast-endpoints.com/) and [MartenDB](https://martendb.io/), it delivers a developer-first experience that is both human-friendly and agent-ready.

---

## 📦 Quick Start


### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/) (or PostgreSQL 16+)

### 1. Clone & Setup
```bash
git clone https://github.com/yourusername/barakoCMS.git
cd barakoCMS
docker compose up -d  # Start PostgreSQL
```

### 2. Configure
Update `barakoCMS/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=barako_cms;Username=postgres;Password=postgres"
  },
  "JWT": {
    "Key": "your-super-secret-key-that-is-at-least-32-chars-long"
  }
}
```

### 3. Run
```bash
dotnet run --project barakoCMS
```

Open **Swagger UI**: `http://localhost:5000/swagger`

---

## 🖥️ Admin UI (New!)

BarakoCMS includes a **full-featured Admin Dashboard** built with Next.js 16.

### Features
- **Dashboard**: Health status, quick stats
- **Content Management**: Create, Edit, List, Search, Filter
- **Schema Management**: Define and view Content Types
- **Workflows**: Create and manage automation rules
- **Roles & UserGroups**: RBAC administration
- **Ops**: Health Checks, Logs, Backups (Create, Download, Restore, Delete)

### Running the Admin UI
```bash
cd admin
npm install
npm run dev
```

Open **Admin Dashboard**: `http://localhost:3000`

Default Login: `arnex` / `password123` (or see seeded data)

---

## 🚀 What's New in v2.0 (Phase 1: Advanced RBAC)

> **Production-Ready Enterprise Features**: Advanced role-based access control with granular permissions

### ✨ Advanced RBAC System

**Complete Role & Permission Management**:
- **Roles**: Create roles with content-type-specific CRUD permissions
- **UserGroups**: Organize users into groups for easier management
- **User Assignment**: Assign roles and groups to users dynamically
- **Granular Permissions**: Per-content-type Create, Read, Update, Delete controls
- **Conditional Access**: JSON-based conditions (e.g., `"author": { "_eq": "$CURRENT_USER" }`)
- **System Capabilities**: Global permissions beyond content (e.g., `view_analytics`)

**API Endpoints** (18 new endpoints):
```bash
# Role Management
POST   /api/roles                  # Create role
GET    /api/roles                  # List roles
GET    /api/roles/{id}             # Get role
PUT    /api/roles/{id}             # Update role
DELETE /api/roles/{id}             # Delete role

# UserGroup Management  
POST   /api/user-groups            # Create group
GET    /api/user-groups            # List groups
GET    /api/user-groups/{id}       # Get group
PUT    /api/user-groups/{id}       # Update group
DELETE /api/user-groups/{id}       # Delete group
POST   /api/user-groups/{id}/users # Add user to group
DELETE /api/user-groups/{id}/users/{userId} # Remove user

# User Assignment
POST   /api/users/{id}/roles       # Assign role to user
DELETE /api/users/{id}/roles/{roleId} # Remove role
POST   /api/users/{id}/groups      # Add user to group
DELETE /api/users/{id}/groups/{groupId} # Remove user
```

**Example - Create Role**:
```bash
POST /api/roles
Authorization: Bearer {ADMIN_TOKEN}

{
  "name": "Content Editor",
  "description": "Can edit own articles",
  "permissions": [{
    "contentTypeSlug": "article",
    "create": { "enabled": true },
    "read": { "enabled": true },
    "update": {
      "enabled": true,
      "conditions": { "author": { "_eq": "$CURRENT_USER" } }
    },
    "delete": { "enabled": false }
  }],
  "systemCapabilities": ["view_analytics"]
}
```

### 🛡️ v1.2 Features (Still Available)

- ✅ **Optimistic Concurrency Control** - Prevent data conflicts
- ✅ **Async Workflow Processing** - Non-blocking background tasks
- ✅ **Resilience Patterns** - HTTP retries, circuit breakers

###  🧩 v2.1 Workflow System (Phase 2)

BarakoCMS includes a powerful **plugin-based workflow system**:

- **6 Built-in Actions**: Email, SMS, Webhook, CreateTask, UpdateField, Conditional
- **Custom Plugins**: Create your own actions without touching core code  
- **Template Variables**: Dynamic content substitution (`{{data.FieldName}}`)
- **Dry-Run Testing**: Test workflows without side effects
- **JSON Schema Validation**: Catch errors before execution
- **Execution Logging**: Full audit trail of workflow runs
- **Auto-Discovery**: New actions automatically appear in API

**Quick Example**:
```bash
# Create custom plugin
public class SlackAction : IWorkflowAction
{
    public string Type => "Slack";
    public async Task ExecuteAsync(...) { /* send to Slack */ }
}

# Use in workflow
{
  "actions": [{
    "type": "Slack",
    "parameters": {
      "message": "New order: {{data.OrderNumber}}"
    }
  }]
}
```

See [Plugin Development Guide](docs/plugin-development-guide.md) and [Migration Guide](docs/workflow-migration-guide.md) to get started.

---

## 🌟 Core Features

### ⚡ Unmatched Speed
- **FastEndpoints**: Minimal overhead, maximum throughput
- **MartenDB**: PostgreSQL-backed JSON document store with event sourcing
- **Async-First**: Non-blocking I/O throughout

### 🧩 Infinite Extensibility
- **Plugin Architecture**: Swap `IEmailService`, `ISmsService`, `ISensitivityService`
- **Custom Content Types**: No schema migrations needed
- **Workflow Engine**: Event-driven automation
- **Projections**: Transform events into read models

### 🛡️ Enterprise-Grade Robustness
- ✅ **Advanced RBAC**: Granular role-based access control with conditions
- ✅ **Event Sourcing**: Full audit trail, time travel, replay
- ✅ **Idempotency**: Duplicate request protection
- ✅ **Optimistic Concurrency**: Race condition prevention
- ✅ **Sensitive Data**: Field-level masking/hiding
- ✅ **Resilience**: Built-in retries, circuit breakers

---


**Problem Solved**: Prevents data loss when multiple users edit the same content simultaneously.

**How It Works**:
```csharp
// Client sends version with update
PUT /api/contents/{id}
{
  "id": "...",
  "data": { "title": "Updated Title" },
  "version": 1  // ⬅️ Must match current DB version
}

// ✅ If version matches → Update succeeds (version becomes 2)
// ❌ If version mismatches → 412 Precondition Failed
```

**User Experience**:
```
User A: Saves changes (v1 → v2) ✅
User B: Tries to save with v1 ❌ 
        Gets error: "Content modified by another user. Please refresh."
User B: Refreshes, sees latest content (v2)
User B: Makes changes, saves (v2 → v3) ✅
```

**Developer Usage**:
```bash
# Get current content (includes version in event stream)
GET /api/contents/{id}

# Update with version check
PUT /api/contents/{id}
{
  "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "data": { "Name": "Updated Name" },
  "version": 2  # Current version from DB
}
```

### ⚡ Async Workflow Processing
**Problem Solved**: Heavy workflows (emails, notifications) no longer block API responses.

**Architecture**:
```mermaid
sequenceDiagram
    participant Client
    participant API
    participant DB
    participant AsyncDaemon
    participant WorkflowEngine
    
    Client->>API: PUT /api/contents/{id}
    API->>DB: Save ContentUpdated event
    API-->>Client: 200 OK (instant response)
    
    Note over AsyncDaemon: Background Processing
    AsyncDaemon->>DB: Poll for new events
    AsyncDaemon->>WorkflowProjection: Process event
    WorkflowProjection->>WorkflowEngine: Execute workflows
    WorkflowEngine->>WorkflowEngine: Send emails, notifications
```

**Performance Impact**:
- **Before**: 500-2000ms (includes email sending)
- **After**: 50-100ms (instant API response)
- **Workflows**: Process in background within 1-2 seconds

### 🛡️ Resilience & Health Checks
- **HTTP Retries**: Automatic retry with exponential backoff for external services
- **Circuit Breaker**: Prevents cascade failures
- **Health Endpoint**: `/health` monitors database connectivity

---

## 🌟 Core Features

### ⚡ Unmatched Speed
- **FastEndpoints**: Minimal overhead, maximum throughput
- **MartenDB**: PostgreSQL-backed JSON document store with event sourcing
- **Async-First**: Non-blocking I/O throughout

### 🧩 Infinite Extensibility
- **Plugin Architecture**: Swap `IEmailService`, `ISmsService`, `ISensitivityService`
- **Custom Content Types**: No schema migrations needed
- **Workflow Engine**: Event-driven automation
- **Projections**: Transform events into read models

### 🛡️ Enterprise-Grade Robustness
- ✅ **Event Sourcing**: Full audit trail, time travel, replay
- ✅ **Idempotency**: Duplicate request protection
- ✅ **Optimistic Concurrency**: Race condition prevention
- ✅ **RBAC**: Role-based access control
- ✅ **Sensitive Data**: Field-level masking/hiding
- ✅ **Resilience**: Built-in retries, circuit breakers

---

## 📖 Developer Guide

### Content Management Workflow

#### 1. Define Content Type (Schema)
```bash
POST /api/content-types
Authorization: Bearer <TOKEN>

{
  "name": "Blog Post",
  "fields": {
    "title": "string",
    "body": "richtext",
    "author": "string",
    "publishDate": "datetime",
    "tags": "array"
  }
}
```

#### 2. Create Content (Draft)
```bash
POST /api/contents
Authorization: Bearer <TOKEN>
Idempotency-Key: unique-request-id-123

{
  "contentType": "blog-post",  # Auto-generated slug
  "data": {
    "title": "Getting Started with BarakoCMS",
    "body": "<p>Welcome to our CMS...</p>",
    "author": "Jane Doe",
    "publishDate": "2024-01-15T10:00:00Z",
    "tags": ["tutorial", "cms"]
  },
  "status": 0,  # 0=Draft, 1=Published, 2=Archived
  "sensitivity": 0  # 0=Public, 1=Sensitive, 2=Hidden
}

# Response
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "message": "Content created successfully"
}
```

#### 3. Update Content (with Concurrency Check)
```bash
PUT /api/contents/550e8400-e29b-41d4-a716-446655440000
Authorization: Bearer <TOKEN>
Idempotency-Key: unique-request-id-456

{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "data": {
    "title": "Getting Started with BarakoCMS (Updated)",
    "body": "<p>Welcome! This guide has been updated...</p>",
    "author": "Jane Doe",
    "publishDate": "2024-01-15T10:00:00Z",
    "tags": ["tutorial", "cms", "getting-started"]
  },
  "version": 1  # ⬅️ IMPORTANT: Current version
}

# Success: 200 OK
# Conflict: 412 Precondition Failed
```

#### 4. Publish Content
```bash
PUT /api/contents/550e8400-e29b-41d4-a716-446655440000/status
Authorization: Bearer <TOKEN>

{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "newStatus": 1  # Publish
}
```

#### 5. Query Content
```bash
# Get all published blog posts
GET /api/contents?contentType=blog-post&status=1

# Get specific content
GET /api/contents/550e8400-e29b-41d4-a716-446655440000
```

---

### Authentication & Authorization

#### Login
```bash
POST /api/auth/login

{
  "username": "admin",
  "password": "SecurePassword123!"
}

# Response
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiry": "2024-01-16T10:00:00Z"
}
```

#### Use Token
```bash
# Include in every authenticated request
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

---

### Workflow Automation

Create event-driven workflows that trigger automatically when content changes.

#### How Workflows Work

1. **Event Triggered** - Content created/updated/status changed
2. **Async Processing** - Background daemon picks up event (no API delay)
3. **Workflow Matched** - System finds workflows matching content type + event
4. **Actions Executed** - Email, SMS, webhooks, etc. run automatically

#### Create a Workflow

```bash
POST /api/workflows
Authorization: Bearer {ADMIN_TOKEN}

{
  "name": "Attendance Confirmation Email",
  "description": "Send email to attendee after record creation",
  "triggerContentType": "AttendanceRecord",
  "triggerEvent": "Created",
  "conditions": {
    "status": "Published"
  },
  "actions": [
    {
      "type": "SendEmail",
      "config": {
        "to": "{{data.Email}}",
        "subject": "Attendance Record Created - {{data.FirstName}} {{data.LastName}}",
        "body": "Hello {{data.FirstName}},\n\nYour attendance record has been successfully created.\n\nThank you!"
      }
    }
  ]
}
```

#### Template Variables

Use dynamic values from the content in your workflows:

```json
{
  "to": "{{data.Email}}",              // Field from record data
  "subject": "Record {{id}} Created",  // Record ID
  "body": "Status: {{status}}"         // Record status
}
```

#### Example: Email to Record's Email Field

When creating an attendance record:

```bash
POST /api/contents
{
  "contentType": "AttendanceRecord",
  "data": {
    "FirstName": "John",
    "LastName": "Doe",
    "Email": "john.doe@company.com",  # Email recipient
    "BirthDay": "1990-01-01"
  },
  "status": 1  // Published
}
```

The workflow automatically sends email to `john.doe@company.com` using the `{{data.Email}}` template variable.

#### Available Actions (Current)

- **SendEmail** - Email notifications with templates
- **SendSms** - SMS alerts (via ISmsService)

#### Supported Events

- `Created` - When content is first created
- `Updated` - When content is modified
- `StatusChanged` - When content status changes
- `Deleted` - When content is removed

#### Conditional Execution

Add conditions to control when workflows run:

```json
{
  "conditions": {
    "status": "Published",              // Only for published content
    "data.Salary": { "_gt": 100000 }   // Only if salary > 100k
  }
}
```

#### Multiple Actions

Chain multiple actions in one workflow:

```json
{
  "actions": [
    {
      "type": "SendEmail",
      "config": {
        "to": "{{data.Email}}",
        "subject": "Record Created"
      }
    },
    {
      "type": "SendEmail",
      "config": {
        "to": "manager@company.com",
        "subject": "New Submission: {{data.FirstName}}"
      }
    }
  ]
}
```

**Performance**: All workflows run asynchronously in background - zero impact on API response time.

---

### Advanced Features

#### Event Sourcing & Time Travel
```bash
# View all versions
GET /api/contents/{id}/history

# Response
[
  {
    "version": 1,
    "eventType": "ContentCreated",
    "timestamp": "2024-01-15T10:00:00Z",
    "data": { ... }
  },
  {
    "version": 2,
    "eventType": "ContentUpdated",
    "timestamp": "2024-01-15T11:30:00Z",
    "data": { ... }
  }
]

# Rollback to version 1
POST /api/contents/{id}/rollback/1
```

#### Sensitive Data Protection
```bash
# Create content with sensitive fields
POST /api/contents
{
  "contentType": "employee-record",
  "data": {
    "name": "John Doe",
    "ssn": "123-45-6789",
    "salary": 75000
  },
  "sensitivity": 1  # Sensitive
}

# Standard user gets masked data
GET /api/contents/{id}
# Response
{
  "name": "John Doe",
  "ssn": "***-**-6789",  # Masked
  "salary": "****"      # Masked
}

# SuperAdmin gets full data
GET /api/contents/{id}
Authorization: Bearer <SUPERADMIN_TOKEN>
# Response
{
  "name": "John Doe",
  "ssn": "123-45-6789",  # Full
  "salary": 75000        # Full
}
```

#### Idempotency Protection
```bash
# Prevent duplicate submissions
POST /api/contents
Idempotency-Key: unique-client-generated-uuid

# If retried with same key → Same response, no duplicate
POST /api/contents
Idempotency-Key: unique-client-generated-uuid  # Same key
# Returns: 409 Conflict (already processed)
```

---

## 🧪 Testing

### Run Full Test Suite
```bash
dotnet test
```

### Run Specific Tests
```bash
# Stabilization tests (concurrency, async workflows)
dotnet test --filter "FullyQualifiedName~StabilizationVerificationTests"

# Attendance POC tests
dotnet test AttendancePOC.Tests/AttendancePOC.Tests.csproj
```

### Test Coverage (v1.2)
- ✅ **Optimistic Concurrency**: Verified via `StabilizationVerificationTests`
- ✅ **Async Workflows**: Infrastructure verified, daemon operational
- ✅ **Sensitive Data Masking**: Multiple role-based tests
- ✅ **Idempotency**: Duplicate request handling
- **Overall**: 64/74 tests passing (86%)

---

## 📦 Use as NuGet Package

### Installation
```bash
dotnet add package BarakoCMS
```

### Setup
```csharp
// Program.cs
using barakoCMS.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register BarakoCMS services
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Use BarakoCMS middleware
app.UseBarakoCMS();

app.Run();
```

### Configure
```json
// appsettings.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=your_db;Username=postgres;Password=postgres"
  },
  "JWT": {
    "Key": "your-secret-key-minimum-32-characters-long"
  }
}
```

---

## 🏗️ Architecture

### Technology Stack
- **Framework**: .NET 8
- **API**: FastEndpoints (high-performance minimal API)
- **Database**: PostgreSQL + MartenDB (Event Sourcing)
- **Authentication**: JWT Bearer Tokens
- **Resilience**: Polly (retry, circuit breaker)
- **Testing**: xUnit + TestContainers

### Key Design Patterns
- **Event Sourcing**: All changes stored as events
- **CQRS**: Separate read/write models via projections
- **Repository Pattern**: `IDocumentSession` abstracts data access
- **Dependency Injection**: Constructor injection throughout
- **Async/Await**: Non-blocking I/O

---

## 🧪 Attendance POC (Real-World Example)

See how BarakoCMS handles a real-world scenario with sensitive data and workflows.

### Features
- **Sensitive Fields**: SSN (Hidden), BirthDate (Masked)
- **RBAC**: Different views for SuperAdmin, HR, Standard users
- **Workflows**: Auto-email on submission
- **Idempotency**: Duplicate submission prevention

### Run POC
```bash
cd AttendancePOC
dotnet run

# Seeds sample data automatically
# Try: GET /api/contents with different role tokens
```

---

## 🐛 Troubleshooting

### Database Connection Failed
**Error**: `Npgsql.NpgsqlException: Failed to connect`

**Solutions**:
1. Check PostgreSQL is running: `docker ps`
2. Start database: `docker compose up -d`
3. Verify connection string in `appsettings.json`
4. Check port 5432 is not blocked

### Port Already in Use
**Error**: `IOException: Failed to bind to http://localhost:5000`

**Solutions**:
1. Change port in `Properties/launchSettings.json`
2. Kill process using port 5000: `lsof -ti:5000 | xargs kill -9` (macOS/Linux)

### Concurrency Conflict (412)
**Error**: `412 Precondition Failed` when updating content

**Cause**: Content version changed since you loaded it

**Solution**:
1. Refresh content to get latest version
2. Retry update with new version number

### Health Check Returns Unhealthy
**Error**: `/health` endpoint shows database unhealthy

**Solutions**:
1. Verify PostgreSQL is running
2. Check connection string
3. Ensure database user has proper permissions

---

## 📚 Additional Resources

### Documentation
- **[DEVELOPMENT_STANDARDS.md](DEVELOPMENT_STANDARDS.md)** - Field types, naming conventions, validation
- **[CHANGELOG.md](CHANGELOG.md)** - Version history
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Contribution guidelines
- **[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)** - Community standards

### For AI Agents
- **[llms.txt](llms.txt)** - Codebase context for LLMs
- **[.cursorrules](.cursorrules)** - Coding standards for AI assistants
- **[CITATIONS.cff](CITATIONS.cff)** - Citation metadata

---

## 🤝 Contributing

We welcome contributions! All contributors must sign our **Contributor License Agreement (CLA)**.

### Why CLA?

The CLA protects both you and the project:
- ✅ You retain ownership of your contributions
- ✅ You grant BarakoCMS permission to use your code
- ✅ Prevents legal issues around commercial licensing
- ✅ Enables future dual-licensing (CE/EE) without re-permission

### How It Works

1. **First-Time Contributors**: 
   - Open a Pull Request
   - @cla-assistant will automatically comment
   - Click the link to sign the CLA (takes 30 seconds)
   - Your PR will be automatically updated

2. **After Signing**:
   - Your GitHub username is permanently recorded
   - No need to sign again for future PRs
   - Start contributing immediately!

**CLA Status**: [![CLA assistant](https://cla-assistant.io/readme/badge/BaryoDev/barakoCMS)](https://cla-assistant.io/BaryoDev/barakoCMS)

See [CLA.md](CLA.md) for full agreement text.

### Development Workflow
1. Fork the repository
2. Create feature branch: `git checkout -b feature/amazing-feature`
3. Make changes and add tests
4. Ensure tests pass: `dotnet test`
5. Commit: `git commit -m 'Add amazing feature'`
6. Push: `git push origin feature/amazing-feature`
7. Open Pull Request (CLA will be requested automatically)

### Code Standards
- Follow existing code style (FastEndpoints vertical slices)
- Add tests for new features
- Update README if adding user-facing features
- Keep PRs focused and small

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

---

## 📄 License

**Apache License 2.0**

- ✅ Commercial use allowed
- ✅ Modification allowed
- ✅ Distribution allowed
- ✅ Patent use allowed
- 📝 Attribution required

See [LICENSE](LICENSE) for full terms.

---

## 💖 Support This Project

<a href='https://ko-fi.com/T6T01CQT4R' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi3.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>

If BarakoCMS helps you build better applications, consider supporting the development!

---

## 👨‍💻 Author

**Arnel Robles**  
GitHub: [@arnelirobles](https://github.com/arnelirobles)

**Built with ❤️ by developers, for developers.**
