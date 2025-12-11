# BarakoCMS

**The AI-Native, High-Performance Headless CMS for .NET 8.**

BarakoCMS is engineered for **Speed**, **Extensibility**, and **Robustness**. Built on the bleeding edge with [FastEndpoints](https://fast-endpoints.com/) and [MartenDB](https://martendb.io/), it delivers a developer-first experience that is both human-friendly and agent-ready.

[![CLA assistant](https://cla-assistant.io/readme/badge/BaryoDev/barakoCMS)](https://cla-assistant.io/BaryoDev/barakoCMS)

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

## 🚀 What's New in v1.2 (Stabilization Release)

> **Production-Ready Features**: Data integrity, async processing, and resilience patterns

### ✅ Optimistic Concurrency Control
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

#### Create Workflow Definition
```bash
POST /api/workflows

{
  "name": "New Blog Post Notification",
  "triggerContentType": "blog-post",
  "triggerEvent": "Created",
  "conditions": {
    "status": "Published"
  },
  "actions": [
    {
      "type": "SendEmail",
      "config": {
        "to": "editors@example.com",
        "subject": "New blog post published",
        "body": "{{data.title}} by {{data.author}}"
      }
    }
  ]
}
```

**How It Works**:
1. Content is created/updated → Event saved to DB
2. Async Daemon picks up event → Calls WorkflowProjection
3. WorkflowProjection loads matching workflow definitions
4. WorkflowEngine executes actions (email, SMS, webhooks)
5. All async - zero API latency impact

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

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

### Development Workflow
1. Fork the repository
2. Create feature branch: `git checkout -b feature/amazing-feature`
3. Make changes and add tests
4. Ensure tests pass: `dotnet test`
5. Commit: `git commit -m 'Add amazing feature'`
6. Push: `git push origin feature/amazing-feature`
7. Open Pull Request

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
