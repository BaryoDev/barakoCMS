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

### 2. Configure Database & Admin

Add the PostgreSQL connection string, JWT key, and **Initial Admin** credentials to your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=barako_cms;Username=postgres;Password=postgres"
  },
  "JWT": {
    "Key": "your-super-secret-key-that-is-at-least-32-chars-long"
  },
  "InitialAdmin": {
    "Username": "admin",
    "Password": "SecurePassword123!"
  }
}
```

*   **DefaultConnection**: Your PostgreSQL connection string.
*   **InitialAdmin**: These credentials will be used to create the first admin user automatically when the application starts.

### 3. Run and Access

Run your application:
```bash
dotnet run
```
Navigate to `http://localhost:5000/swagger` to access the API.

---

## Usage Guide

### Step 1: Login as Admin

1.  Open Swagger UI (`/swagger`).
2.  Go to `POST /api/auth/login`.
3.  Execute with the credentials you configured in `appsettings.json` (e.g., `admin` / `SecurePassword123!`).
4.  Copy the **Token** from the response.
5.  Click **Authorize** at the top of Swagger and paste the token (format: `Bearer <token>`).

### Step 2: Define Content Structure

Before creating content, you must define its structure (Content Type). Let's create a "Blog Post".

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

### Step 3: Create First Post

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

### Step 4: Query Content

You can fetch your content via the API to display on your frontend.

**Get All Content:**
`GET /api/contents`

**Get Single Content:**
`GET /api/contents/{id}`

**Example Response:**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "contentType": "Blog Post",
  "data": {
    "title": "Welcome to BarakoCMS",
    "slug": "welcome-to-barakocms",
    "body": "<p>This is my first post using BarakoCMS!</p>",
    "tags": ["cms", "dotnet", "headless"]
  },
  "version": 1,
  "createdAt": "2023-10-27T10:00:00Z",
  "updatedAt": "2023-10-27T10:00:00Z"
}
```

## Architecture

- **API**: FastEndpoints (REPR Pattern)
- **Data**: MartenDB (Document Store + Event Sourcing)
- **Auth**: JWT Bearer
