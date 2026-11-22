# Kubernetes manifests

A single-cluster deployment: namespace, config, secrets, a single-replica Postgres StatefulSet, the
app, an Ingress and a nightly backup CronJob.

```bash
# edit 02-secret.yaml, 01-configmap.yaml, 05-deployment.yaml and 08-ingress.yaml first
kubectl apply -f k8s/00-namespace.yaml -f k8s/01-configmap.yaml -f k8s/02-secret.yaml

# the role script, from the repository file, so there is no second copy to keep in step
kubectl create configmap barako-db-app-role -n barako-cms \
  --from-file=db-app-role.sql=scripts/db-app-role.sql --dry-run=client -o yaml | kubectl apply -f -

# Postgres, then the role Job (04), which waits for Postgres to answer
kubectl apply -f k8s/03-postgres.yaml -f k8s/04-postgres-service.yaml -f k8s/04-role-job.yaml -f k8s/07-backup-cronjob.yaml
kubectl wait -n barako-cms --for=condition=complete job/barako-db-app-role --timeout=10m

# the app, once the role exists
kubectl apply -f k8s/05-deployment.yaml -f k8s/06-service.yaml -f k8s/08-ingress.yaml
```

The API connects as a database role without superuser, and refuses to start as a superuser outside
Development. `04-role-job.yaml` creates that role, so the Deployment is applied only after the Job
completes. If the Job fails, `kubectl logs -n barako-cms job/barako-db-app-role` says why. The
Job and the files above also apply together with `kubectl apply -f k8s/` on a cluster you can
afford to let the pods restart until the role exists.

A finished Job is not run again until it is deleted. After changing `DB_APP_PASSWORD`, delete the
Job (`kubectl delete job -n barako-cms barako-db-app-role`) and apply it again. Its logs go when
the Job is removed, an hour after it finishes (`ttlSecondsAfterFinished`).

`k8s/observability/` holds a Grafana dashboard and is deliberately outside that apply. It is not a
Kubernetes manifest, and while it sat in this directory `kubectl apply -f k8s/` failed on it.

## Before you apply

1. `01-configmap.yaml`. Set `AllowedHosts` to your public hostname. In Production the API refuses to
   start when it is `*` or empty. It takes a semicolon list if you serve more than one host.
   `Security__AllowAnyHost: "true"` accepts any host on purpose, if you really want that.
2. `02-secret.yaml`. Replace every placeholder. There are two database passwords and they differ.
   `POSTGRES_PASSWORD` is the owner's. `DB_APP_PASSWORD` is the API's role, and it appears twice
   (`DB_APP_PASSWORD`, and inside `ConnectionStrings__DefaultConnection`) and both have to match.
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

Postgres (`03-postgres.yaml`) starts as root, because its entrypoint prepares the data directory and
then drops to the postgres user. It keeps CHOWN, DAC_OVERRIDE, FOWNER, SETGID and SETUID, drops the
rest, and has a read-only root with emptyDirs at `/tmp` (64Mi) and `/var/run/postgresql` (8Mi). The
backup job (`07-backup-cronjob.yaml`) runs as the image's postgres user (70) with a read-only root,
no capabilities and `fsGroup: 70` so it can write the backups PVC.
`Postgres_and_the_backup_job_keep_their_hardening` pins both, and `scripts/check-k8s-start.sh`
starts them on kind in CI.

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

`bash scripts/check-k8s-start.sh k8s` starts Postgres and the backup job for real, takes one backup and
deletes the namespace afterwards. It does not apply the app Deployment, so it pulls no app image.

## Demo content

Nothing here sets `Seed__DemoContent`, so a first boot creates the system roles and the admin from
`02-secret.yaml` and nothing else. Set `Seed__DemoContent: "true"` in `01-configmap.yaml` if you
want the sample AttendanceRecord content type, its records and its email workflow.
