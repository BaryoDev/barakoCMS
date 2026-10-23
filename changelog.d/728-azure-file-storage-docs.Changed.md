- **The managed platform guide says how uploads work on Azure.** `PostgresFileStorage` is durable
  across restarts and scale-out, so App Service needs nothing extra for uploads. Azure Blob is not
  supported, and the guide names the S3-compatible route (`Files.S3` with `ServiceUrl`). Docs only,
  no route or contract change.
