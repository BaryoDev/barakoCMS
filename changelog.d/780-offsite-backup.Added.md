- **The nightly backup never left the host it protects.** The compose stacks can now copy each
  checked backup to S3-compatible storage (AWS, R2, a self-hosted store) by setting
  `BACKUP_S3_BUCKET` and its key. Off unless the bucket is set, so a stack that sets nothing runs
  as before. A failed upload fails the run loudly, keeps the local archive and skips rotation.
  `docs/backup-and-restore.md` has a write-only key policy and a lifecycle rule example. A new CI
  check uploads a backup to SeaweedFS and restores from the uploaded copy.
