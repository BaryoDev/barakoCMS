#!/usr/bin/env bash
# Sends the Kubernetes manifests to a real API server and fails if it refuses them.
#
#   scripts/check-k8s-manifests.sh [dir]      # default: k8s
#
# Needs a reachable cluster. CI creates a throwaway kind cluster for this; locally,
# `kind create cluster` is enough. Nothing is left running: every resource except
# the namespace goes through --dry-run=server, so the API server parses, defaults,
# validates and admits each object and then throws the result away.
#
# Why a cluster, when a schema validator needs none:
#
#   kubectl --dry-run=client --validate=strict  ACCEPTS memory: "128Mw".
#   kubeconform -strict against upstream schemas ACCEPTS memory: "128Mw".
#
# Both were run against the manifests as they stood before #280 was fixed, and both
# passed. A resource quantity is a string in the OpenAPI schema, so schema validation
# has nothing to check it against; only the API server runs the quantity parser. That
# is the exact bug this gate exists for, so a validator that cannot see it is not the
# gate, however cheap it is to run.
#
# scripts/testdata/k8s-known-bad/ holds those pre-#280 manifests. CI runs this script
# against them too, and fails if they pass.
set -euo pipefail

DIR="${1:-k8s}"
[ -d "$DIR" ] || { echo "check-k8s-manifests: $DIR is not a directory" >&2; exit 1; }

command -v kubectl >/dev/null || { echo "check-k8s-manifests: kubectl not found" >&2; exit 1; }

if ! kubectl cluster-info >/dev/null 2>&1; then
  echo "check-k8s-manifests: no reachable cluster. Start one first (kind create cluster)." >&2
  exit 1
fi

# The namespace is applied for real, and it is the only thing that is. A server dry
# run does not create anything, so without this every namespaced object in the set
# fails with "namespaces barako-cms not found" and the run turns into eight identical
# errors that hide whatever the real problem was.
kubectl apply -f k8s/00-namespace.yaml >/dev/null

echo "== server-side validation: $DIR =="
kubectl apply --dry-run=server --validate=strict -f "$DIR"
echo "== the API server accepted every manifest in $DIR =="
