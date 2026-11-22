# Kubernetes manifests

A single-cluster deployment: namespace, config, secrets, a single-replica Postgres StatefulSet, the
app, an Ingress and a nightly backup CronJob.

```bash
# edit 02-secret.yaml, 01-configmap.yaml, 05-deployment.yaml and 08-ingress.yaml first
kubectl apply -f k8s/
```

`k8s/observability/` holds a Grafana dashboard and is deliberately outside that apply. It is not a
Kubernetes manifest, and while it sat in this directory `kubectl apply -f k8s/` failed on it.

## Before you apply

1. `01-configmap.yaml`. Set `AllowedHosts` to your public hostname. In Production the API refuses to
   start when it is `*` or empty. It takes a semicolon list if you serve more than one host.
   `Security__AllowAnyHost: "true"` accepts any host on purpose, if you really want that.
2. `02-secret.yaml`. Replace every placeholder. The database password appears twice
   (`POSTGRES_PASSWORD`, and inside `ConnectionStrings__DefaultConnection`) and both have to match.
   Use sealed-secrets or external-secrets for anything real.
3. `05-deployment.yaml`. The image tag is pinned. Bump it to the version you mean to run. Set the
   `Host` header on the startup, liveness and readiness probes to the same hostname as
   `AllowedHosts`. The probes hit the pod address, which is not in `AllowedHosts`, so without the
   header every probe gets a 400 and the pod never becomes ready.
4. `08-ingress.yaml`. Replace `barakocms.example.com` with your hostname, in both `tls.hosts` and
   `rules.host`, and check that `ingressClassName` and the cert-manager issuer match your cluster.
   No ingress controller and no cert-manager ship here.

If the Ingress host and `AllowedHosts` differ, every real request gets a 400.
`The_Ingress_hosts_and_every_probe_Host_header_are_in_AllowedHosts` in
`BarakoCMS.Tests/KubernetesManifestTests.cs` fails when an Ingress host or a probe Host header is not
in `AllowedHosts`, or when a probe sends no Host header.

## Security context

`05-deployment.yaml` sets this on the app pod:

- Runs as user 1654 and group 1654 (the image's `app` user), with `runAsNonRoot: true`.
- `seccompProfile: RuntimeDefault`.

And this on the `barako-cms` container:

- `readOnlyRootFilesystem: true`.
- `allowPrivilegeEscalation: false`.
- `capabilities.drop: ["ALL"]`.

The root filesystem is read-only, so the only writable paths are two `emptyDir` volumes: `/tmp`
(512Mi limit) and `/home/app` (16Mi limit). If a module needs to write somewhere else, mount an
`emptyDir` or a PVC at that path. Do not turn off the read-only root.
`The_app_Deployment_runs_non_root_read_only_with_no_capabilities` pins these settings.

Only the app Deployment has this. `03-postgres.yaml` and `07-backup-cronjob.yaml` set no
`securityContext`, so Postgres and the backup job run with the image defaults.

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
