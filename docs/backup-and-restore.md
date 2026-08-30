# Backup and restore

barakoCMS does not back itself up. The database is yours, and so is the backup. This page is what
runs, where it writes, and how to restore, with the restore proved rather than assumed.

`scripts/restore-check.sh` runs this whole procedure in CI on every pull request: it takes a backup
with the same script the deployments run, destroys the database, restores it, and boots the app
against the result.

## What runs

Every deployment path now runs the same hardened script, `scripts/backup-cron.sh`.

| Deployment | Service | Where backups go |
| --- | --- | --- |
| `docker-compose.yml` (development) | `db-backup` | `./backups` on the host |
| `docker-compose.prod.yml` | `db-backup` | the `postgres_prod_backups` volume |
| `quickstart/docker-compose.yml` | `db-backup` | the `backups` volume |
| `k8s/` | `db-backup` CronJob | the `barako-backups` PVC |

Defaults: 02:00 daily, 14 days retained in production and 7 elsewhere. Override with
`BACKUP_CRON_SCHEDULE` and `BACKUP_KEEP_DAYS`.

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

**docker compose**

```bash
docker compose stop app
gunzip -c ./backups/barako_backup_2026-08-30_02-00-00.sql.gz \
  | docker compose exec -T postgres psql -U postgres -d barako_cms -v ON_ERROR_STOP=1
docker compose start app
```

`scripts/restore-db.sh` wraps this with a confirmation prompt and a listing of available archives.

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
