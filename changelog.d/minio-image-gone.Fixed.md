- **Every CI run started failing because an upstream image disappeared.** MinIO archived the project
  and the `minio/minio` repository on Docker Hub now answers "pull access denied ... repository does
  not exist" to an anonymous pull, so `S3FileStorageTests` could not start its container and every
  merge queue run failed with it. The same tag is still served from quay.io, where MinIO published in
  parallel, so `BarakoCMS.Tests/S3FileStorageTests.cs` pulls from there instead. A registry change,
  not a version change.
