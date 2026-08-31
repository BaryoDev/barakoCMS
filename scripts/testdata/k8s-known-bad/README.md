# Known-bad Kubernetes manifests

`05-deployment.yaml` here is `k8s/05-deployment.yaml` exactly as it stood at commit
`5b7e75f^`, the last commit before #280 was fixed. Its memory request is `128Mw`,
which is not a quantity Kubernetes can parse. It sat in the repo because nothing in
CI ever read those files.

It is checked in so the gate that now reads them has something real to fail on.
CI runs `scripts/check-k8s-manifests.sh` against this directory and fails the build
if the API server accepts it. A gate that only ever runs on manifests known to be
good cannot tell you it is working.

Two things that pass this file, and are therefore not the gate:

    kubectl apply --dry-run=client --validate=strict -f 05-deployment.yaml   # exit 0
    kubeconform -strict -summary 05-deployment.yaml                          # exit 0, "is valid"

Do not fix the file. Its being wrong is the point.
