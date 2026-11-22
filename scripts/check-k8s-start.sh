#!/usr/bin/env bash
# Starts the hardened Postgres StatefulSet and the backup CronJob on a real cluster and takes one
# backup. check-k8s-manifests.sh proves the API server accepts these manifests; this proves the
# pods run with a read-only root, dropped capabilities and a non-root backup user.
#
#   scripts/check-k8s-start.sh [dir]      # default: k8s
#
# Needs a reachable cluster (CI uses kind). The app Deployment is not applied, so no app image is
# pulled. The namespace is deleted when the script ends, pass or fail.
set -euo pipefail

DIR="${1:-k8s}"
NS=barako-cms
[ -d "$DIR" ] || { echo "check-k8s-start: $DIR is not a directory" >&2; exit 1; }

command -v kubectl >/dev/null || { echo "check-k8s-start: kubectl not found" >&2; exit 1; }
command -v openssl >/dev/null || { echo "check-k8s-start: openssl not found" >&2; exit 1; }

if ! kubectl cluster-info >/dev/null 2>&1; then
  echo "check-k8s-start: no reachable cluster. Start one first (kind create cluster)." >&2
  exit 1
fi

cleanup() {
  kubectl delete namespace "$NS" --wait=false >/dev/null 2>&1 || true
}
trap cleanup EXIT

diagnose() {
  echo "== start check failed, pod state ==" >&2
  kubectl -n "$NS" get pods -o wide >&2 || true
  kubectl -n "$NS" describe pods >&2 || true
  for pod in $(kubectl -n "$NS" get pods -o name 2>/dev/null); do
    echo "== logs: $pod ==" >&2
    kubectl -n "$NS" logs "$pod" --all-containers --tail=100 >&2 || true
  done
}
trap 'diagnose' ERR

kubectl apply -f "$DIR/00-namespace.yaml" -f "$DIR/01-configmap.yaml" >/dev/null

# A random password per run. It is only ever passed to kubectl, never printed.
PW=$(openssl rand -hex 24)
kubectl -n "$NS" create secret generic barako-secrets \
  --from-literal=POSTGRES_PASSWORD="$PW" >/dev/null

kubectl apply -f "$DIR/03-postgres.yaml" -f "$DIR/04-postgres-service.yaml" \
  -f "$DIR/07-backup-cronjob.yaml" >/dev/null

echo "== waiting for Postgres to start with its read-only root =="
kubectl -n "$NS" rollout status statefulset/postgres --timeout=300s

# An empty schema dumps to about 650 bytes, under the job's BACKUP_MIN_BYTES floor of 1000, so give
# the dump something to hold.
kubectl -n "$NS" exec postgres-0 -- env PGPASSWORD="$PW" psql -h 127.0.0.1 -U postgres -d barako_cms \
  -v ON_ERROR_STOP=1 -c \
  "CREATE TABLE start_check AS SELECT g AS id, md5(g::text) AS note FROM generate_series(1,200) g;" >/dev/null

echo "== running one backup as uid 70 with a read-only root =="
kubectl -n "$NS" create job --from=cronjob/db-backup db-backup-start-check >/dev/null
kubectl -n "$NS" wait --for=condition=complete job/db-backup-start-check --timeout=180s

LOG=$(kubectl -n "$NS" logs job/db-backup-start-check)
echo "$LOG"
grep -q "Backup complete" <<<"$LOG" || { echo "check-k8s-start: the backup log has no 'Backup complete'" >&2; false; }

echo "== Postgres and the backup job started and took a backup =="
