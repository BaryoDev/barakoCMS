- **A deployment could boot on the placeholder JWT signing key.** The startup check enforced only a
  minimum length, and the key shipped in `k8s/02-secret.yaml` is a length-valid placeholder, so an
  operator applying the manifests unedited ran a signing key that is public in the repository and
  anyone could forge tokens. Startup now rejects the shipped placeholder as well as a short one.
