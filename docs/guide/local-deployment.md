# Local Deployment Guide

This guide explains how to run BarakoCMS locally on your machine. We support two modes:
1.  **Production Simulation** (Recommended for testing the deployment stack).
2.  **Development Mode** (For contributing code).

## 1. Production Simulation (Full Stack)

Run the entire stack (API, Admin UI, Database, Reverse Proxy) exactly as it would run on a Cloud VM, but on your local machine using Docker.

### How to Run
We have an automated script that sets up local domains using `127.0.0.1.nip.io`.

1.  **Prerequisites**: Docker Desktop (or Engine) installed and running.
2.  **Run the Script**:
    ```bash
    ./scripts/run-local.sh
    ```

### What Happens?
-   It generates a `.env` file with local secrets.
-   It configures **Caddy** to serve HTTPS on local domains.
-   It builds the Next.js Admin UI and .NET API.
-   It starts the containers.

### Accessing the App
Once the script finishes:
-   **Admin Dashboard**: [https://admin.127.0.0.1.nip.io](https://admin.127.0.0.1.nip.io)
-   **API Endpoint**: [https://api.127.0.0.1.nip.io](https://api.127.0.0.1.nip.io)

> **Note**: Your browser will warn you about "Self-Signed Certificates". This is normal for localhost. Click "Advanced" -> "Proceed to localhost (unsafe)" to continue.

---

## 2. Development Mode

If you want to modify code and see changes instantly (Hot Reload), run the services directly on your machine.

### Prerequisites
-   .NET 8 SDK
-   Node.js 18+
-   Docker (for Database only)

### Steps

1.  **Start Database**:
    ```bash
    docker compose up -d postgres
    ```

2.  **Run API**:
    ```bash
    cd barakoCMS
    dotnet run
    ```
    *API will listen on `http://localhost:5005`*

3.  **Run Admin UI**:
    ```bash
    cd admin
    npm install
    npm run dev
    ```
    *UI will listen on `http://localhost:3000`*
