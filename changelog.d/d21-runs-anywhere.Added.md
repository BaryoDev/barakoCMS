D21 records that barakoCMS is a container and a Postgres database, so it runs on a VM, on App
Service, on Fargate, on Cloud Run or on Kubernetes, and that owning the software is a separate
decision from managing the servers. It also records that BaryoVM is one deployment option rather
than the path, and that scaling out is already safe because schema application and projections
both take Postgres advisory locks.
