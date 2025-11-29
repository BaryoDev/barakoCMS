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

## Usage

### 1. Configure Services and Middleware

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

Add the PostgreSQL connection string to your `appsettings.json`:

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

## Running the Application

Once configured, run your application:

```bash
dotnet run
```

Navigate to `http://localhost:5000/swagger` (or your configured port) to access the BarakoCMS API documentation and interface.

## Architecture

- **API**: FastEndpoints (REPR Pattern)
- **Data**: MartenDB (Document Store + Event Sourcing)
- **Auth**: JWT Bearer
