---
description: Deploy the full stack (Database, Backend, Frontend) to Fly.io
---

This workflow will guide you through deploying your PostgreSQL database, .NET Backend, and Next.js Frontend to Fly.io.

### Prerequisites
- You must have `flyctl` installed and be logged in (`fly auth login`).

### Phase 1: Create the Database

1. Create a new Postgres cluster.
   ```bash
   fly postgres create --name barako-db --region sin --vm-size shared-cpu-1x --initial-cluster-size 1 --volume-size 1
   ```
   *(Adjust `--region` close to you, e.g., `sin` for Singapore, `sjc` for San Jose).*
   **Note:** Save the `connection string` and `username/password` from the output!

### Phase 2: Deploy the Backend (.NET 10)

1. Initialize the backend app (Run from Project Root).
   ```bash
   fly launch --name barako-api --dockerfile Dockerfile --internal-port 8080 --region sin --no-deploy
   ```
   *(Select `Yes` to copy configuration if asked. Say `No` to adding a Postgres DB since we made one separately, or `Yes` to attach the existing one if it offers).*

   This writes `fly.toml`. It stays out of the repository (`.gitignore` covers it), because `app` is
   a name unique across all of Fly, so a committed one either collides for the next person or points
   their `fly deploy` at somebody else's app. Pick your own `--name`.

   Add these to the generated file. They turn off the three things that expect a cluster or a
   writable disk, neither of which a Fly machine gives you here:

   ```toml
   [env]
     Kubernetes__Enabled = "false"
     HealthChecksUI__Enabled = "false"
     Serilog__WriteToFile = "false"
   ```

2. Attach the Database to the Backend.
   ```bash
   fly postgres attach --app barako-api barako-db
   ```
   *This automatically sets the `DATABASE_URL` secret.*

3. Deploy the Backend.
   ```bash
   fly deploy
   ```
   *Wait for it to finish. Note the URL (e.g., `https://barako-api.fly.dev`).*

### Phase 3: Deploy the Frontend (Next.js)

1. Navigate to the admin directory.
   ```bash
   cd admin
   ```

2. Initialize the frontend app.
   ```bash
   fly launch --name barako-admin --dockerfile Dockerfile --internal-port 3000 --region sin --no-deploy
   ```

3. Set the Backend API URL (Use the URL from Phase 2).
   ```bash
   fly secrets set NEXT_PUBLIC_API_URL=https://barako-api.fly.dev
   ```

4. Deploy the Frontend.
   ```bash
   fly deploy
   ```

### Verification
- Visit your frontend URL (e.g., `https://barako-admin.fly.dev`).
- Sign in as the initial admin. Set the password yourself before the first boot:
  ```bash
  fly secrets set --app barako-api InitialAdmin__Username=admin InitialAdmin__Password='<a 12+ char password>'
  ```
  Leave `InitialAdmin__Password` unset and the seeder generates one and prints it once, so you would
  have to read it back out of `fly logs --app barako-api`.
