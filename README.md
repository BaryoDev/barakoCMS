# BarakoCMS

A modern, headless CMS built with .NET 8, FastEndpoints, and MartenDB (PostgreSQL).

## Features

- **Headless Architecture**: API-first design for flexibility.
- **High Performance**: Built on [FastEndpoints](https://fast-endpoints.com/) for minimal overhead.
- **Document Database**: Uses [MartenDB](https://martendb.io/) on top of PostgreSQL for flexible content storage.
- **Event Sourcing**: Content changes are versioned using Event Sourcing, providing a full audit trail and history.
- **Authentication**: Built-in JWT Authentication.
- **Swagger UI**: Interactive API documentation.

## Installation

Install the BarakoCMS package into your ASP.NET Core project via NuGet:

```bash
dotnet add package BarakoCMS
```

## Quick Start Guide

### 1. Setup Project

Update your `Program.cs` to register and use BarakoCMS:

```csharp
using barakoCMS.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register BarakoCMS services
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Use BarakoCMS middleware (Auth, Swagger, FastEndpoints)
app.UseBarakoCMS();

app.Run();
```

### 2. Configure Database

Add the PostgreSQL connection string and JWT key to your `appsettings.json`:

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

*Note: Ensure you have a running PostgreSQL instance.*

### 3. Run and Access

Run your application:
```bash
dotnet run
```
Navigate to `http://localhost:5000/swagger` to access the API.

---

## Usage Guide

### Step 1: Create First Admin User

Since the system starts empty, you need to create your first user.

1.  Open Swagger UI.
2.  Go to `POST /api/auth/register`.
3.  Execute with your details:
    ```json
    {
      "username": "admin",
      "email": "admin@example.com",
      "password": "SecurePassword123!"
    }
    ```
4.  **Login** using `POST /api/auth/login` to get your **JWT Token**.
5.  Click **Authorize** in Swagger and paste the token (format: `Bearer <token>`).

### Step 2: Define Content Structure

Let's create a "Blog Post" content type.

1.  Go to `POST /api/content-types`.
2.  Define the schema:
    ```json
    {
      "name": "Blog Post",
      "fields": {
        "title": "string",
        "slug": "string",
        "body": "richtext",
        "tags": "array"
      }
    }
    ```

### Step 3: Create Content

Now, let's add a blog post using the structure we just defined.

1.  Go to `POST /api/contents`.
2.  Create the content:
    ```json
    {
      "contentType": "Blog Post",
      "data": {
        "title": "Welcome to BarakoCMS",
        "slug": "welcome-to-barakocms",
        "body": "<p>This is my first post using BarakoCMS!</p>",
        "tags": ["cms", "dotnet", "headless"]
      }
    }
    ```

### Step 4: Retrieve Content

You can fetch your content via the API to display on your frontend (React, Vue, Blazor, etc.).

- **Get All**: `GET /api/contents` (Implement filtering by ContentType if needed)
- **Get Single**: `GET /api/contents/{id}`

## Architecture

- **API**: FastEndpoints (REPR Pattern)
- **Data**: MartenDB (Document Store + Event Sourcing)
- **Auth**: JWT Bearer
