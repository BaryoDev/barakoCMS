# Backup and restore

barakoCMS does not back itself up. The database is yours, and so is the backup. This page is what
runs, where it writes, and how to restore, with the restore proved rather than assumed.

`scripts/restore-check.sh` runs this whole procedure in CI on every pull request: it takes a backup
with the same script the deployments run, destroys the database, restores it, and boots the app
against the result.

## What runs

Every deployment path takes a nightly backup that is checked before it is published.

| Deployment | Service | Database | Where backups go |
| --- | --- | --- | --- |
| `docker-compose.yml` (development) | `app` | `barako_cms` | `./backups` on the host |
| `docker-compose.prod.yml` | `app` | `${DB_NAME:-barako_cms}` | the `postgres_prod_backups` volume |
| `quickstart/docker-compose.yml` | `api` | `${DB_NAME:-barakocms}` | the `backups` volume |
| `k8s/` | Deployment `barako-cms` | `barako_cms` | the `barako-backups` PVC |

The three compose stacks run `scripts/backup-cron.sh`. The quickstart runs its own copy,
`quickstart/scripts/backup-cron.sh`, because that folder is meant to be copied out of the
repository; `scripts/check-quickstart-backup-script.sh` fails CI when the two differ. The Kubernetes
CronJob carries the same logic inline, because a CronJob has no repository to mount; if you change
one, change both.

Defaults: 02:00 daily, 14 days retained in production and 7 elsewhere. Override with
`BACKUP_CRON_SCHEDULE` and `BACKUP_KEEP_DAYS`.

**The service name, database name and backup location all differ between these stacks.** That is
why the restore section below is split by deployment rather than given once: a restore command
copied from the wrong row silently targets the wrong database.

## Copying each backup off the host

`/backups` lives on the same host as Postgres, so losing the host, its disk or the volume takes the
database and every retained backup with it. The compose stacks can copy each backup, once it has
passed its checks, to S3-compatible storage somewhere else. It is off unless `BACKUP_S3_BUCKET` is
set, and a stack that sets nothing runs exactly as before.

| Variable | Meaning |
| --- | --- |
| `BACKUP_S3_BUCKET` | The bucket. Setting it turns the copy on. |
| `BACKUP_S3_PREFIX` | Prepended to each file name. Default `barako-backups/`. |
| `BACKUP_S3_REGION` | Default `us-east-1`. |
| `BACKUP_S3_ENDPOINT` | For R2 or a self-hosted store, such as `https://<account>.r2.cloudflarestorage.com`. Left empty for AWS. |
| `BACKUP_S3_ACCESS_KEY`, `BACKUP_S3_SECRET_KEY` | The key the copy is written with. Both are required once the bucket is set. |

How it behaves:

- The backup container has no S3 client, so with the bucket set it installs `curl` from its own
  Alpine repositories when it starts, and signs requests with curl's built-in SigV4. A host that
  cannot reach those repositories stops the container at start, with `BACKUP FAILED` naming the
  reason, rather than failing at 02:00.
- A failed upload fails the run the same way a bad dump does: `BACKUP FAILED` in the log and a
  non-zero exit. The local copy is kept, and rotation is skipped for that run, so the backups on
  the host are not thinned out while none is leaving it.
- The keys are handed to curl in a file only the container's user can read, never on its command
  line.
- Retention off the host is the bucket's job, not the script's. The script never deletes anything
  there.
- Each archive goes up in one request, which S3 caps at 5 GB. A compressed dump past that needs a
  different tool.
- Without `BACKUP_S3_ENDPOINT` the AWS address is `https://<bucket>.s3.<region>.amazonaws.com`, so a
  bucket name with dots in it fails the certificate check. Use a name without dots, or set
  `BACKUP_S3_ENDPOINT=https://s3.<region>.amazonaws.com`, which addresses it by path.
- `BACKUP_S3_PREFIX` cannot be empty; set it to the folder you want, ending in `/`.

**Use a separate bucket, or at least a separate prefix, and a key that can only write.** If the app
host is compromised, a key that can delete the off-host copies is a key that removes your recovery.
On AWS, a policy that allows uploading and nothing else:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "s3:PutObject",
      "Resource": "arn:aws:s3:::my-barako-backups/barako-backups/*"
    }
  ]
}
```

Turn on versioning on the bucket too, so even an overwrite under the same name keeps the earlier
object. R2 and most self-hosted stores have an equivalent: an API token or key scoped to object
write on that bucket.

A lifecycle rule then decides how long the copies are kept. For example, 90 days on AWS:

```json
{
  "Rules": [
    {
      "ID": "expire-barako-backups",
      "Filter": { "Prefix": "barako-backups/" },
      "Status": "Enabled",
      "Expiration": { "Days": 90 },
      "NoncurrentVersionExpiration": { "NoncurrentDays": 90 }
    }
  ]
}
```

```bash
aws s3api put-bucket-lifecycle-configuration --bucket my-barako-backups --lifecycle-configuration file://lifecycle.json
```

To restore from the copy, download the object and feed it to the restore steps below exactly as a
local archive. `scripts/offsite-backup-check.sh` does that in CI against SeaweedFS: it uploads a
backup, restores the uploaded copy into a new database and counts the rows, then checks that a
refused upload fails the run and keeps the local archive.

Google Cloud Storage speaks the S3 API with HMAC keys and may work with `BACKUP_S3_ENDPOINT` set
to `https://storage.googleapis.com`, but that has not been tested. Check an upload and a restore
against a real bucket before relying on it. The Kubernetes CronJob in `k8s/` does not have the copy.

## Why the script is not `pg_dump | gzip`

Because that reports success when it has failed. `gzip` succeeds on empty input, and in a pipeline
its exit code is the one you get, so a dump that could not connect leaves a 20-byte archive and a
green log. That is not hypothetical here: the credentials were wrong for months and every nightly
backup was an empty gzip claiming to be fine.

So the script dumps to a plain file first, checks `pg_dump`'s own status, requires the result to
exceed a minimum size, greps it for the `PostgreSQL database dump` header, gzips it, tests that the
archive decompresses, and only then moves it to its final name. A failed run leaves nothing behind
that looks like a backup. Rotation happens only after a success, so a bad night never deletes the
last good copy.

## Restoring

Stop the application first. A restore into a database the app is writing to will not be the database
you backed up.

**docker compose, development stack**

```bash
docker compose stop app
gunzip -c ./backups/barako_backup_2026-08-30_02-00-00.sql.gz \
  | docker compose exec -T postgres psql -U postgres -d barako_cms -v ON_ERROR_STOP=1
docker compose start app
```

**docker compose, production stack**

Archives live in a named volume rather than a host directory, so read them from inside the backup
container:

```bash
docker compose -f docker-compose.prod.yml stop app

# List what is there.
docker compose -f docker-compose.prod.yml exec db-backup ls -1 /backups

docker compose -f docker-compose.prod.yml exec -T db-backup \
    sh -c 'gunzip -c /backups/barako_backup_2026-08-30_02-00-00.sql.gz' \
  | docker compose -f docker-compose.prod.yml exec -T postgres \
      psql -U "${DB_USER:-postgres}" -d "${DB_NAME:-barako_cms}" -v ON_ERROR_STOP=1

docker compose -f docker-compose.prod.yml start app
```

**quickstart stack**

The service is `api`, not `app`, and the database defaults to `barakocms` rather than `barako_cms`:

```bash
cd quickstart
docker compose stop api

docker compose exec -T db-backup sh -c 'gunzip -c /backups/barako_backup_2026-08-30_02-00-00.sql.gz' \
  | docker compose exec -T postgres psql -U "${DB_USER:-postgres}" -d "${DB_NAME:-barakocms}" -v ON_ERROR_STOP=1

docker compose start api
```

**Kubernetes**

```bash
kubectl -n barako-cms scale deployment/barako-cms --replicas=0

kubectl -n barako-cms run restore --rm -it --restart=Never \
  --image=postgres:16-alpine \
  --overrides='{"spec":{"volumes":[{"name":"b","persistentVolumeClaim":{"claimName":"barako-backups"}}],
                "containers":[{"name":"restore","image":"postgres:16-alpine","stdin":true,"tty":true,
                "volumeMounts":[{"name":"b","mountPath":"/backups"}]}]}}' \
  -- sh -c 'gunzip -c /backups/$(ls -1 /backups | tail -1) \
      | PGPASSWORD=$PGPASSWORD psql -h postgres-service -U postgres -d barako_cms -v ON_ERROR_STOP=1'

kubectl -n barako-cms scale deployment/barako-cms --replicas=2
```

`ON_ERROR_STOP=1` is not optional. Without it `psql` reports success after skipping every statement
it could not apply, which is the same failure shape as the empty gzip.

## Restoring into a database that is not empty

Do not. Restoring over an existing schema can appear to work because the objects the dump failed to
carry are already there, so you learn nothing about whether the archive is complete.

Drop and recreate first:

```sql
drop database barako_cms with (force);
create database barako_cms;
```

The CI drill does exactly this, and asserts the database has zero tables before restoring, so a
restore that quietly relies on leftovers fails there rather than in front of a customer.

## After a restore

Check three things, in this order:

1. **The app boots.** It applies the Marten schema at startup under `CreateOnly`, so a restore that
   lost an object fails loudly here rather than later. If it exits non-zero, read the error: it
   names the object.
2. **You can sign in.** Proves the users, roles and password hashes came back.
3. **Content is served.** `GET /api/contents` should report the row count you expect.

The projection daemon resumes from `mt_event_progression`, which the dump carries, so workflows do
not re-fire for events that were already processed. If that table were lost, the first boot would
replay every event and send every email again, which is why the drill checks the app rather than
just the row counts.

## What is not covered

- **Point-in-time recovery.** These are nightly snapshots. Losing up to a day of writes is the
  stated exposure, and closing it means WAL archiving, which is a Postgres decision rather than a
  barakoCMS one.
- **Files.** The Files module stores bytes in Postgres by default, so they are inside the dump. With
  the S3 module configured they are not, and that bucket needs its own backup.
- **An RPO or RTO.** There is no measured recovery time to quote in a contract. The CI drill gives a
  lower bound on a small database and nothing more.
