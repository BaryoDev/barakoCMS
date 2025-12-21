# Deploying to Fly.io

This guide covers the steps to deploy the BarakoCMS API and Admin Dashboard to [Fly.io](https://fly.io/).

## Prerequisites

1. **Install Flyctl**:
   - **macOS**: `brew install flyctl`
   - **Linux**: `curl -L https://fly.io/install.sh | sh`
   - **Windows**: `pwsh -Command "iwr https://fly.io/install.ps1 | iex"`

2. **Login to Fly.io**:
   ```bash
   fly auth login
   ```

## 1. Deploying the Backend API

### Step 1: Initialize the App
Run this in the root of the `barakoCMS` project (where the Dockerfile is located).
```bash
fly launch
```
- Select "Yes" to copy configuration if prompted.
- Do **not** deploy yet; we need to set secrets.

### Step 2: Set Secrets
Configure your critical environment variables as secrets.
```bash
fly secrets set \
  DATABASE_URL="postgres://..." \
  JWT__Key="your-long-secure-key" \
  CORS__AllowedOrigins="https://your-admin-app.fly.dev"
```

### Step 3: Performance Scaling (Mandatory)
BarakoCMS is a high-performance application (using Marten and background seeding) and **MUST be scaled to at least 1GB of RAM** to prevent startup crashes (OOM Killed).
```bash
fly scale memory 1024
```
*Note: The default 256MB/512MB is insufficient for the initial data seeding process.*

### Step 4: Deploy
```bash
fly deploy --remote-only
```

> [!TIP]
> Fly.io secrets are automatically injected as environment variables. BarakoCMS uses the `__` (double underscore) separator to map to nested JSON configuration. 
> - `CORS:AllowedOrigins` becomes `CORS__AllowedOrigins`
> - `Kubernetes:Enabled` becomes `Kubernetes__Enabled` (Default: `false`)

---

## 2. Deploying the Admin Dashboard

The Admin UI is a Next.js application. Navigate to the `admin` folder before launching.

### Step 1: Initialize
```bash
cd admin
fly launch
```

### Step 2: Configure Environment
Set the API URL for the frontend.
```bash
fly secrets set NEXT_PUBLIC_API_URL="https://your-api-app.fly.dev"
```

### Step 3: Scale & Deploy
Scale to 512MB for reliable Next.js server-side rendering.
```bash
fly scale memory 512
fly deploy --remote-only
```

---

## FAQ & Common Issues

### 1. The "Confirm" button is unclickable or hangs.
**Cause**: Usually a CORS block or the API is unresponsive due to memory pressure.
**Fix**: 
- Ensure `CORS__AllowedOrigins` on the API matches your Admin UI URL exactly.
- Verify API logs: `fly logs -a <your-api-app>`. If you see "OOM Killed", scale memory to 1GB.

### 2. Login returns "400 Bad Request".
**Cause**: Validation error or database connection issue.
**Fix**: 
- Check API logs to see which field failed validation.
- Ensure `DATABASE_URL` is correct and the database is reachable.

### 3. API Health check returns 502/503.
**Cause**: Startup hang or port mismatch.
**Fix**:
- BarakoCMS listens on port `8080` by default. Ensure `fly.toml` matches this `internal_port`.
- If OOM crashes persist, verify scaling: `fly scale show`.

### 4. Kubernetes Logs/Monitoring is missing.
**Cause**: Config-Driven Control is enabled.
**Fix**:
- By default, `Kubernetes:Enabled` is `false` to prevent errors on non-K8s platforms (like Fly.io).
- To enable: `fly secrets set Kubernetes__Enabled="true"`.

---

## Monthly Cost Estimation (Fly.io)

| Component      | Machine Type  | RAM     | Estimated Monthly Cost |
| -------------- | ------------- | ------- | ---------------------- |
| **API Server** | shared-cpu-1x | 1024 MB | ~$5.70                 |
| **Admin UI**   | shared-cpu-1x | 512 MB  | ~$3.50                 |
| **Postgres**   | shared-cpu-1x | 512 MB  | ~$3.50                 |
| **Total**      |               |         | **~$12.70**            |

*Note: Fly.io offers a free allowance that might cover a portion of these costs depending on your usage and region.*

## Suggested Resources
- [Fly.io Documentation](https://fly.io/docs/)
- [Next.js Deployment on Fly](https://fly.io/docs/js/frameworks/nextjs/)
- [Marten Data Persistence](https://martendb.io/)
