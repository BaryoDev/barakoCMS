# Kubernetes manifests

A single-cluster deployment: namespace, config, secrets, a single-replica Postgres StatefulSet, the
app, an Ingress and a nightly backup CronJob.

```bash
# edit 02-secret.yaml and 08-ingress.yaml first
kubectl apply -f k8s/
```

`k8s/observability/` holds a Grafana dashboard and is deliberately outside that apply. It is not a
Kubernetes manifest, and while it sat in this directory `kubectl apply -f k8s/` failed on it.

## Before you apply

1. `02-secret.yaml`. Replace every placeholder. The database password appears twice
   (`POSTGRES_PASSWORD`, and inside `ConnectionStrings__DefaultConnection`) and both have to match.
   Use sealed-secrets or external-secrets for anything real.
2. `08-ingress.yaml`. Replace `barakocms.example.com` with your hostname, and check that
   `ingressClassName` and the cert-manager issuer match your cluster. No ingress controller and no
   cert-manager ship here.
3. `05-deployment.yaml`. The image tag is pinned. Bump it to the version you mean to run.

## What this does not cover

Postgres is a single replica on a PVC, with no replication and no failover. That is workable for a
small instance and it is not a highly available database. The backup CronJob writes to its own PVC
in the same cluster, so copy those dumps somewhere else.

## Probes

`/health/live` backs the liveness probe and runs process-only checks. `/health/ready` backs the
readiness probe and covers the database, disk, and the startup seed. They are deliberately
different: a database outage has to fail readiness and not liveness, or a shared Postgres blip
restarts every replica at once. `/health` is the full report, for dashboards.

`/health/build` is not a probe. It answers with the commit the image was built from, so a deploy can
prove it is running the build it just pushed rather than the one that was already there. The commit
is passed in at image build time as `BARAKO_BUILD_SHA`; an image built without it answers `unknown`.

## Validating these manifests

CI applies everything here to a throwaway kind cluster with `--dry-run=server --validate=strict`, via
`scripts/check-k8s-manifests.sh`. Run it locally the same way against any cluster:

```bash
kind create cluster
bash scripts/check-k8s-manifests.sh k8s
```

Server side, and not `--dry-run=client` or `kubeconform`, because neither parses a resource quantity:
both accepted `memory: "128Mw"` when it was in this directory.

## Demo content

Nothing here sets `Seed__DemoContent`, so a first boot creates the system roles and the admin from
`02-secret.yaml` and nothing else. Set `Seed__DemoContent: "true"` in `01-configmap.yaml` if you
want the sample AttendanceRecord content type, its records and its email workflow.
