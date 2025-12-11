# Getting Started

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (for PostgreSQL)

## Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/baryodev/barakoCMS.git
   cd barakoCMS
   ```

2. **Start the Database**
   ```bash
   docker-compose up -d
   ```

3. **Run the Application**
   ```bash
   cd barakoCMS
   dotnet run
   ```
   The API will be available at `http://localhost:5000` (or configured port).

## First Steps

1. **Access Swagger UI**: Go to `http://localhost:5000/swagger` to explore the API.
2. **Authenticate**: Use the default admin credentials (if configured) or the seeded users from the `AttendancePOC`.
   - **SuperAdmin**: See `appsettings.json` or console output.
   - **HR Manager**: `hr_manager` / `HRPassword123!`
   - **Standard User**: `john_viewer` / `UserPassword123!`
