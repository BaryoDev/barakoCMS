# Kubernetes Deployment

BarakoCMS includes a production-ready suite of Kubernetes manifests in the `/k8s` directory, designed for "Zero Downtime" updates and easy self-hosting.

---

## 📂 Manifest Overview

| File                       | Purpose                                                                        |
| -------------------------- | ------------------------------------------------------------------------------ |
| `00-namespace.yaml`        | Creates an isolated `barako-cms` namespace.                                    |
| `01-configmap.yaml`        | Stores non-sensitive config (Environment, Database Host).                      |
| `02-secret.yaml`           | Stores sensitive keys (DB Passwords, JWT Key). **Change these in production!** |
| `03-postgres.yaml`         | A **StatefulSet** for the database with persistent storage.                    |
| `04-postgres-service.yaml` | Internal network DNS for the database.                                         |
| `05-deployment.yaml`       | The HA application deployment (2 replicas) with rolling updates.               |
| `06-service.yaml`          | The LoadBalancer/Service to expose the app.                                    |
| `07-backup-cronjob.yaml`   | Automated daily backups to persistent storage.                                 |

---

## 🚀 Quick Start (Local Testing)

Prerequisite: **Docker Desktop** (with Kubernetes enabled) or **Minikube**.

### 1. Build the Image
So your local cluster can see it without a registry push:
```bash
docker build -t barakocms:latest .
```

### 2. Apply Manifests
Deploy the entire stack in one command:
```bash
kubectl apply -f k8s/
```

### 3. Verify
Check the status of your pods:
```bash
kubectl get pods -n barako-cms -w
```
Wait until `Status` is `Running` for both the App and Postgres.

### 4. Access
*   **Docker Desktop**: http://localhost
*   **Minikube**: `minikube service barako-cms-service -n barako-cms`

---

## ☁️ Production Deployment

### Important Modifications
Before deploying to a public cloud (DigitalOcean, AWS, Linode), you **MUST** update `02-secret.yaml`:

```yaml
apiVersion: v1
kind: Secret
# ...
stringData:
  JWT__Key: "CHANGE_THIS_TO_A_LONG_RANDOM_STRING_PRODUCTION"
  POSTGRES_PASSWORD: "CHANGE_THIS_DB_PASSWORD"
```

### Managed Database (Recommended)
For serious production use, we recommend using a Managed Database (AWS RDS, DigitalOcean Managed DB) instead of hosting Postgres yourself.

1.  **Delete** `03-postgres.yaml`, `04-postgres-service.yaml`, and `07-backup-cronjob.yaml` (managed DBs handle backups).
2.  **Update** `05-deployment.yaml` environment variables to point to your managed DB host.
