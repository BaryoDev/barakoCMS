- **Owning the software was being confused with running the servers.** D21 records that barakoCMS is a
  container and a Postgres database, so it runs on a VM, on App Service, on Fargate, on Cloud Run or
  on the Kubernetes manifests already in `k8s/`, and that BaryoVM is one deployment option rather than
  the path. It also writes down something nobody had: scaling out is already safe, because
  `SchemaApplyLock` takes a blocking advisory lock and projections are leased per projection, so
  several instances starting at once serialise rather than race.
