# BarakoCMS Development Standards

This document defines the coding standards and conventions for developing with and extending BarakoCMS. These standards ensure consistency, maintainability, and clarity across the codebase.

## Table of Contents

- [Field Types](#field-types)
- [ID Generation and Handling](#id-generation-and-handling)
- [Naming Conventions](#naming-conventions)
- [Validation Patterns](#validation-patterns)
- [Content Type Definitions](#content-type-definitions)
- [Status and Sensitivity Enums](#status-and-sensitivity-enums)
- [Code Examples](#code-examples)

---

## Field Types

When defining Content Types, use the following type identifiers in the `Fields` dictionary. These types map to C# types and JSON serialization patterns.

### Supported Field Types

| Type       | C# Type           | Description        | Example Value            |
| ---------- | ----------------- | ------------------ | ------------------------ |
| `string`   | `string`          | Text data          | `"John Doe"`             |
| `int`      | `int`             | Integer numbers    | `42`                     |
| `bool`     | `bool`            | Boolean values     | `true` or `false`        |
| `datetime` | `DateTime`        | ISO 8601 timestamp | `"2023-12-05T10:00:00Z"` |
| `decimal`  | `decimal`         | Decimal numbers    | `99.99`                  |
| `array`    | `List<object>`    | JSON array         | `["tag1", "tag2"]`       |
| `object`   | `Dictionary<...>` | Nested JSON object | `{"key": "value"}`       |

### Type Mapping Details

**String (`string`)**
```csharp
// Field Definition
{ "Name": "string" }

// Data Storage
{ "Name": "Juan Dela Cruz" }
```

**Integer (`int`)**
```csharp
// Field Definition
{ "Age": "int" }

// Data Storage
{ "Age": 25 }
```

**Boolean (`bool`)**
```csharp
// Field Definition
{ "Attended": "bool" }

// Data Storage
{ "Attended": true }
```

**DateTime (`datetime`)**
```csharp
// Field Definition
{ "Date": "datetime" }

// Data Storage (ISO 8601 format)
{ "Date": "2023-12-05T10:00:00Z" }
```

**Decimal (`decimal`)**
```csharp
// Field Definition
{ "Price": "decimal" }

// Data Storage
{ "Price": 99.99 }
```

**Array (`array`)**
```csharp
// Field Definition
{ "Tags": "array" }

// Data Storage
{ "Tags": ["tech", "cms", "dotnet"] }
```

**Object (`object`)**
```csharp
// Field Definition
{ "Metadata": "object" }

// Data Storage
{ "Metadata": { "author": "John", "version": "1.0" } }
```

### Type Validation

> [!IMPORTANT]
> BarakoCMS uses a **schema-less** approach where field types are defined as strings in the `ContentType.Fields` dictionary. Type validation is **not enforced** by the core library. Content data is stored as `Dictionary<string, object>`, allowing maximum flexibility.

If you need type validation, implement it in your application layer using:
- Custom validators in your endpoints
- Pre-processing hooks in custom services
- Client-side validation in your frontend

---

## ID Generation and Handling

### Standard Pattern

All entities in BarakoCMS use **GUID (Globally Unique Identifier)** for primary keys.

**Entity Definition:**
```csharp
public class MyEntity
{
    public Guid Id { get; set; }
    // ... other properties
}
```

### ID Generation Rules

**Rule 1: Server-Side Generation**
```csharp
// ✅ CORRECT: Generate ID in the endpoint
var contentId = Guid.NewGuid();
var @event = new ContentCreated(contentId, ...);
```

**Rule 2: Use `Guid.NewGuid()` for Creation**
```csharp
// ✅ CORRECT: All creation endpoints
public override async Task HandleAsync(Request req, CancellationToken ct)
{
    var entity = new MyEntity
    {
        Id = Guid.NewGuid(),
        // ... other properties
    };
    
    _session.Store(entity);
    await _session.SaveChangesAsync(ct);
}
```

**Rule 3: Client Provides ID for Updates**
```csharp
// ✅ CORRECT: Update endpoints receive ID from client
public class UpdateRequest
{
    public Guid Id { get; set; }  // Client must provide this
    public Dictionary<string, object> Data { get; set; }
}
```

### When to Generate IDs

| Operation  | ID Source        | Example                             |
| ---------- | ---------------- | ----------------------------------- |
| **Create** | Server generates | `Guid.NewGuid()` in endpoint        |
| **Update** | Client provides  | `Id` field in request               |
| **Delete** | Client provides  | URL parameter: `/api/contents/{id}` |
| **Get**    | Client provides  | URL parameter: `/api/contents/{id}` |

### Examples from Codebase

**Content Creation:**
```csharp
// From: barakoCMS/Features/Content/Create/Endpoint.cs
var contentId = Guid.NewGuid();
var @event = new ContentCreated(contentId, req.ContentType, req.Data, req.Status, userId);
```

**Content Type Creation:**
```csharp
// From: barakoCMS/Features/ContentType/Create/Endpoint.cs
var contentType = new ContentType
{
    Id = Guid.NewGuid(),
    Name = req.Name,
    Slug = req.Name.ToLower().Replace(" ", "-"),
    Fields = req.Fields,
    CreatedAt = DateTime.UtcNow
};
```

**User Registration:**
```csharp
// From: barakoCMS/Features/Auth/Register/Endpoint.cs
var user = new User
{
    Id = Guid.NewGuid(),
    Username = req.Username,
    Email = req.Email,
    RoleIds = roleIds,
    PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
};
```

---

## Naming Conventions

### Field Names (PascalCase)

When defining fields in Content Types, use **PascalCase** for field names.

```csharp
// ✅ CORRECT
{
    "Name": "string",
    "Attended": "bool",
    "BirthDay": "datetime",
    "SSN": "string"
}

// ❌ INCORRECT
{
    "name": "string",           // lowercase
    "attended": "bool",         // lowercase
    "birth_day": "datetime",    // snake_case
    "s-s-n": "string"          // kebab-case
}
```

### Content Type Names (Human Readable)

Use descriptive, human-readable names for Content Types. These appear in UI and API responses.

```csharp
// ✅ CORRECT
"Attendance Record"
"Purchase Order"
"Blog Post"
"Product Category"

// ❌ AVOID
"attendance_record"  // No underscores
"PO"                 // Too abbreviated
"blogpost"          // Single word without spaces (use "Blog Post")
```

### Slug Generation (kebab-case)

Slugs are **auto-generated** from Content Type names. The system converts spaces to hyphens and lowercases everything.

```csharp
// System automatically converts:
"Attendance Record" → "attendance-record"
"Purchase Order"    → "purchase-order"
"Blog Post"         → "blog-post"
```

**Slug Generation Code:**
```csharp
// From: barakoCMS/Features/ContentType/Create/Endpoint.cs
Slug = req.Name.ToLower().Replace(" ", "-")
```

### Model Property Names (PascalCase)

All C# model properties use **PascalCase**:

```csharp
// ✅ CORRECT
public class Content
{
    public Guid Id { get; set; }
    public string ContentType { get; set; }
    public Dictionary<string, object> Data { get; set; }
    public ContentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

---

## Validation Patterns

BarakoCMS uses **FluentValidation** integrated with **FastEndpoints** for request validation.

### Standard Validator Pattern

```csharp
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.PropertyName)
            .NotEmpty()
            .WithMessage("Custom error message");
    }
}
```

### Common Validation Rules

**Not Empty:**
```csharp
RuleFor(x => x.ContentType).NotEmpty();
RuleFor(x => x.Data).NotEmpty();
```

**String Length:**
```csharp
RuleFor(x => x.Username).NotEmpty().MinimumLength(3);
RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
```

**Email Validation:**
```csharp
RuleFor(x => x.Email).NotEmpty().EmailAddress();
```

**Enum Validation:**
```csharp
RuleFor(x => x.NewStatus).IsInEnum();
RuleFor(x => x.Status).IsInEnum();
```

**GUID Validation:**
```csharp
RuleFor(x => x.Id).NotEmpty();
```

### Validation Examples from Codebase

**Content Creation Validator:**
```csharp
// From: barakoCMS/Features/Content/Create/Models.cs
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.ContentType).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();
    }
}
```

**User Registration Validator:**
```csharp
// From: barakoCMS/Features/Auth/Register/Models.cs
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}
```

**Status Change Validator:**
```csharp
// From: barakoCMS/Features/Content/ChangeStatus/Models.cs
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum();
    }
}
```

---

## Content Type Definitions

### Best Practices

1. **Plan Your Schema First**
   - Define all fields before creating the Content Type
   - Consider future needs (adding fields later requires data migration consideration)

2. **Use Descriptive Field Names**
   - Clear, self-documenting names (e.g., `FirstName` not `FN`)
   - Consistent naming across Content Types

3. **Choose Appropriate Types**
   - Use `datetime` for dates, not `string`
   - Use `bool` for true/false, not `int` or `string`
   - Use `decimal` for currency, not `int` or `double`

4. **Document Complex Objects**
   - If using `object` type, document the expected structure
   - Consider creating a separate Content Type for complex nested data

### Example: E-Commerce Product

```json
{
  "name": "Product",
  "fields": {
    "Name": "string",
    "Description": "string",
    "Price": "decimal",
    "StockQuantity": "int",
    "IsActive": "bool",
    "Categories": "array",
    "Specifications": "object",
    "ReleaseDate": "datetime"
  }
}
```

### Example: Blog Post

```json
{
  "name": "Blog Post",
  "fields": {
    "Title": "string",
    "Body": "string",
    "Author": "string",
    "PublishedDate": "datetime",
    "Tags": "array",
    "FeaturedImageUrl": "string",
    "ViewCount": "int",
    "IsFeatured": "bool"
  }
}
```

---

## Status and Sensitivity Enums

### ContentStatus Enum

Controls the publication state of content.

```csharp
public enum ContentStatus
{
    Draft = 0,      // ✏️ Work in progress, not visible to public
    Published = 1,  // ✅ Live and visible
    Archived = 2    // 📦 Soft-deleted, hidden but recoverable
}
```

**Usage in API:**
```json
{
  "contentType": "article",
  "status": 1,  // Published
  "data": { ... }
}
```

**State Transitions:**
```
Draft ──────────> Published
  ↑                   ↓
  └─────────────── Archived
```

### SensitivityLevel Enum

Controls data visibility based on user roles.

```csharp
public enum SensitivityLevel
{
    Public = 0,     // 🌐 Visible to all authenticated users
    Sensitive = 1,  // 🔒 Masked for non-SuperAdmin (field-level policies apply)
    Hidden = 2      // 🚫 Completely hidden from non-SuperAdmin
}
```

**Usage in API:**
```json
{
  "contentType": "attendance-record",
  "sensitivity": 1,  // Sensitive
  "data": {
    "Name": "John Doe",
    "SSN": "123-45-6789",  // Hidden for Standard users
    "BirthDay": "1990-01-01"  // Masked as "***" for Standard users
  }
}
```

### Sensitivity Best Practices

1. **Public (0)** - Default for non-sensitive content
   - Blog posts, product listings, public announcements

2. **Sensitive (1)** - Personal or business-sensitive data
   - Employee records, customer information
   - Use with field-level policies for granular control

3. **Hidden (2)** - Highly confidential
   - Financial records, internal documents
   - Only SuperAdmin can view

**Field-Level Sensitivity Configuration (AttendancePOC Example):**
```json
{
  "SensitivityPolicies": {
    "AttendanceRecord": {
      "SSN": {
        "Level": "Hidden",
        "AllowedRoles": ["SuperAdmin"]
      },
      "BirthDay": {
        "Level": "Sensitive",
        "MaskValue": "***",
        "AllowedRoles": ["SuperAdmin"]
      }
    }
  }
}
```

---

## Code Examples

### Complete Content Type Workflow

This example shows the entire workflow from creating a Content Type to managing content.

#### 1. Create Content Type

**Request:**
```bash
curl -X POST "http://localhost:5000/api/content-types" \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Event Registration",
    "fields": {
      "ParticipantName": "string",
      "Email": "string",
      "PhoneNumber": "string",
      "EventDate": "datetime",
      "NumberOfAttendees": "int",
      "HasDietaryRestrictions": "bool",
      "DietaryNotes": "string",
      "RegistrationFee": "decimal",
      "EmergencyContact": "object"
    }
  }'
```

**Response:**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "message": "Content Type created successfully"
}
```

#### 2. Create Content (Draft)

**Request:**
```bash
curl -X POST "http://localhost:5000/api/contents" \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "contentType": "event-registration",
    "status": 0,
    "sensitivity": 0,
    "data": {
      "ParticipantName": "Maria Santos",
      "Email": "maria@example.com",
      "PhoneNumber": "+63-912-345-6789",
      "EventDate": "2024-01-15T09:00:00Z",
      "NumberOfAttendees": 2,
      "HasDietaryRestrictions": true,
      "DietaryNotes": "Vegetarian",
      "RegistrationFee": 500.00,
      "EmergencyContact": {
        "Name": "Pedro Santos",
        "Phone": "+63-917-654-3210"
      }
    }
  }'
```

#### 3. Update Content

**Request:**
```bash
curl -X PUT "http://localhost:5000/api/contents/{CONTENT_ID}" \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "id": "{CONTENT_ID}",
    "data": {
      "ParticipantName": "Maria Santos",
      "Email": "maria@example.com",
      "PhoneNumber": "+63-912-345-6789",
      "EventDate": "2024-01-15T09:00:00Z",
      "NumberOfAttendees": 3,
      "HasDietaryRestrictions": true,
      "DietaryNotes": "Vegetarian, no nuts",
      "RegistrationFee": 750.00,
      "EmergencyContact": {
        "Name": "Pedro Santos",
        "Phone": "+63-917-654-3210"
      }
    }
  }'
```

#### 4. Publish Content

**Request:**
```bash
curl -X PUT "http://localhost:5000/api/contents/{CONTENT_ID}/status" \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "id": "{CONTENT_ID}",
    "newStatus": 1
  }'
```

### Custom Endpoint Implementation

If you're extending BarakoCMS with custom endpoints, follow this pattern:

```csharp
using FastEndpoints;
using Marten;

namespace MyApp.Features.MyFeature;

public class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MinimumLength(3);
    }
}

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/myfeature");
        Claims("UserId");  // Require authentication
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Get authenticated user ID
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);

        // Your business logic here
        var entity = new MyEntity
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow
        };

        _session.Store(entity);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response 
        { 
            Id = entity.Id,
            Message = "Entity created successfully" 
        });
    }
}
```

---

## Reference Implementation: AttendancePOC

For a complete, working example of BarakoCMS in action, refer to the **AttendancePOC** project included in the repository.

**Key Features Demonstrated:**
- Custom Content Type (`AttendanceRecord`)
- Field-level sensitivity (`SSN` hidden, `BirthDay` masked)
- Role-based access control (SuperAdmin, HR, Standard users)
- Workflow integration (email notifications)
- Comprehensive test coverage

**Location:** `/AttendancePOC`

**Files to Study:**
- [`AttendancePOC/Seeder.cs`](file:///Users/arnelirobles/barakoCMS/AttendancePOC/Seeder.cs) - Data seeding patterns
- [`AttendancePOC/Services/AttendanceSensitivityService.cs`](file:///Users/arnelirobles/barakoCMS/AttendancePOC/Services/AttendanceSensitivityService.cs) - Custom sensitivity logic
- [`AttendancePOC/appsettings.json`](file:///Users/arnelirobles/barakoCMS/AttendancePOC/appsettings.json) - Field-level policies
- [`AttendancePOC.Tests/AttendanceTests.cs`](file:///Users/arnelirobles/barakoCMS/AttendancePOC.Tests/AttendanceTests.cs) - Integration tests

---

## Additional Resources

- **[README.md](file:///Users/arnelirobles/barakoCMS/README.md)** - Getting started guide and API reference
- **[CONTRIBUTING.md](file:///Users/arnelirobles/barakoCMS/CONTRIBUTING.md)** - Contribution guidelines
- **[.cursorrules](file:///Users/arnelirobles/barakoCMS/.cursorrules)** - AI assistant coding standards
- **[llms.txt](file:///Users/arnelirobles/barakoCMS/llms.txt)** - AI context file

---

## Quick Reference Card

| **Concept**        | **Pattern**                                                       | **Example**                       |
| ------------------ | ----------------------------------------------------------------- | --------------------------------- |
| Field Types        | `string`, `int`, `bool`, `datetime`, `decimal`, `array`, `object` | `"Name": "string"`                |
| ID Generation      | `Guid.NewGuid()` on create                                        | `Id = Guid.NewGuid()`             |
| Field Names        | PascalCase                                                        | `FirstName`, `IsActive`           |
| Content Type Names | Human readable                                                    | `"Blog Post"`, `"Product"`        |
| Slugs              | Auto-generated kebab-case                                         | `"blog-post"`, `"product"`        |
| Status             | 0=Draft, 1=Published, 2=Archived                                  | `"status": 1`                     |
| Sensitivity        | 0=Public, 1=Sensitive, 2=Hidden                                   | `"sensitivity": 0`                |
| Validation         | FluentValidation in `RequestValidator`                            | `RuleFor(x => x.Name).NotEmpty()` |

---

---

## Runtime Validation

BarakoCMS now includes optional runtime validation to enforce development standards. This ensures that data stored in the system conforms to the defined schema and naming conventions.

### What is Validated?

1.  **Field Types**: Ensures fields in `ContentType` use only allowed types (`string`, `int`, `bool`, `datetime`, `decimal`, `array`, `object`).
2.  **Field Names**: Enforces **PascalCase** for field names in `ContentType`.
3.  **Content Data**: Validates that data values match the types defined in the `ContentType` schema.

### Configuration

Validation is enabled by default but can be configured in `appsettings.json`:

```json
{
  "BarakoCMS": {
    "StrictValidation": true,
    "ValidationOptions": {
      "EnforceFieldTypes": true,
      "EnforcePascalCaseFieldNames": true,
      "ValidateDataTypes": true
    }
  }
}
```

### Error Messages

If validation fails, the API returns a `400 Bad Request` with detailed error messages:

**Invalid Field Type:**
```
Field 'Name' has invalid type 'varchar'. Allowed types are: string, int, bool, datetime, decimal, array, object.
```

**Invalid Field Name:**
```
Field 'first_name' must be PascalCase (e.g., 'FirstName').
```

**Data Type Mismatch:**
```
Field 'Age' expects type 'int' but received 'string' ("twenty-five").
```

### Troubleshooting

-   **"Field expects type 'string' but received 'string'":** This usually happens if you send a JSON object or array where a string is expected, or if there's a mismatch in how the JSON serializer handles the value. Ensure your JSON payload matches the expected type.
-   **"Cannot resolve scoped service...":** This is a known issue if you try to inject `IQuerySession` into a singleton validator. Use `Resolve<IQuerySession>()` instead.

---

*Last Updated: 2025-12-05*
