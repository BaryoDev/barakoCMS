# BarakoCMS

A modern, headless CMS built with .NET 8, FastEndpoints, and MartenDB (PostgreSQL).

## Features

- **Headless Architecture**: API-first design for flexibility.
- **High Performance**: Built on [FastEndpoints](https://fast-endpoints.com/) for minimal overhead.
- **Document Database**: Uses [MartenDB](https://martendb.io/) on top of PostgreSQL for flexible content storage.
- **Event Sourcing**: Content changes are versioned using Event Sourcing, providing a full audit trail and history.
- **Authentication**: Built-in JWT Authentication.
- **Swagger UI**: Interactive API documentation.

## Getting Started

### Prerequisites

- .NET 8 SDK
- PostgreSQL Database

### Setup

1.  **Clone the repository**:
    ```bash
    git clone https://github.com/yourusername/barakoCMS.git
    cd barakoCMS
    ```

2.  **Configure Database**:
    Update `barakoCMS/appsettings.json` with your PostgreSQL connection string:
    ```json
    "ConnectionStrings": {
      "DefaultConnection": "Host=localhost;Database=barako_cms;Username=postgres;Password=postgres"
    }
    ```

3.  **Run the Application**:
    ```bash
    dotnet run --project barakoCMS
    ```
    Access Swagger UI at `http://localhost:5000/swagger`.

## NuGet Package

BarakoCMS is available as a NuGet package for embedding into existing ASP.NET Core applications.

### Installation

```bash
dotnet add package BarakoCMS
```

### Usage

In your `Program.cs`:

```csharp
using barakoCMS.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register BarakoCMS services
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Use BarakoCMS middleware
app.UseBarakoCMS();

app.Run();
```

Ensure you have the connection string configured in your `appsettings.json`.

## Publishing to NuGet

To publish this package to NuGet.org:

1.  **Create an API Key**: Go to [NuGet.org](https://www.nuget.org/account/apikeys) and create a new API key.
2.  **Run the Publish Script**:
    ```bash
    ./publish_nuget.sh
    ```
3.  **Push**: The script will generate the `.nupkg` file and show you the command to push it using your API key.

## Testing

The solution includes integration tests in `BarakoCMS.Tests`.

```bash
dotnet test
```

*Note: Tests require a running PostgreSQL instance.*

## Architecture

- **API**: FastEndpoints (REPR Pattern)
- **Data**: MartenDB (Document Store + Event Sourcing)
- **Auth**: JWT Bearer
