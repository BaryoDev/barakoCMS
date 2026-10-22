# Upgrading from 4.x to 5.0

5.0 takes a database a 4.x release created. It will not boot against one until the files under
`migrations/` that the database lacks have been applied. This page is which files those are, how
to apply them, and how to go back.

**Coming from 3.x?** There is no direct upgrade from 3.x to 5.0. Upgrade to 4.9 first with
[upgrading-to-4.0.md](upgrading-to-4.0.md), from a checkout of the `v4.9.0` tag and with the 4.9.0
image, start 4.9.0 once against the result, and then come back here as a 4.9 install.

The sequence below, including the rollback, is proved in CI by the `Upgrade from 4.x` job
(`scripts/upgrade-check.sh`). It stands up a real database through the released 4.9.0 image,
upgrades it to the working tree, checks the schema with both the core host and the Suite, boots the
Suite in Production, appends an event to a stream that already existed, waits for the projection
daemon to resume, then rolls back and boots 4.9.0 again. It runs the same from a database 4.1.0
created. If a step here stops being true, that job goes red.

## Why there is a migration at all

Production runs Marten's `AutoCreate.CreateOnly`. It creates objects that are missing, so a fresh
database sets itself up, but it never alters an existing one. A release that changes an existing
object ships a SQL file for it, and the host refuses to start until the file has run.
[migrations.md](migrations.md) explains the files, the ledger and `db-migrate`.

5.0 moves Marten from 9.40.0 to 9.49.0 and JasperFx from 2.75.2 to 2.84.1. Marten changed the body
of one database function, `mt_quick_append_events`. barakoCMS does not call it: it appends events in
Marten's Rich mode, with its own `INSERT`. But the host's start-up check compares every Marten object
with what the build declares, the function body included, so the new body has to be in the database.
That is `migrations/5.0.0/marten-9-49-quick-append-events.sql`: a function replacement, no data
touched.
Other 5.0 changes add their own files under `migrations/5.0.0/`, each explained in its header.

The Comments module (5.2.0) adds `migrations/5.2.0/253-comments.sql`, which creates its `mt_doc_comments`
table and index. It runs only where the module is enabled, and `rollback-253-comments.sql` drops the
table and the comments in it.

## Before you start

Take a backup, and check you can restore it ([backup-and-restore.md](backup-and-restore.md)). A
failed upgrade's remedy is a restore.

## The upgrade

With the 4.x API stopped, run `db-migrate` with the 5.0 image, then check the schema, then start
5.0. With compose, from the directory holding your compose file and `.env`, after setting the image
tag to the 5.0 release:

```bash
docker compose pull
docker compose stop app
docker compose run --rm --no-deps app db-migrate --status
docker compose run --rm --no-deps app db-migrate
docker compose run --rm --no-deps app db-assert
docker compose up -d
```

The service is `app` in `docker-compose.prod.yml` and `api` in `quickstart/docker-compose.yml`.
`db-migrate` runs, in order, every shipped file the ledger does not record, and records each one
([migrations.md](migrations.md#what-a-run-does) says what it prints). `db-assert` exits 0 when the
schema is what 5.0 declares, and otherwise prints the statements still outstanding.

The Marten file can go in while 4.x still serves: no barakoCMS build calls the function it
replaces. When it is the only pending file,
`db-migrate` can run under the serving 4.x, then deploy. Do not leave long between the two: a 4.x
instance that restarts after the file fails its own start-up schema check.

To apply the files with `psql` instead, print the lines from a checkout of the 5.0 release, with
`FROM_VERSION` set to the release the database is on:

```bash
FROM_VERSION=4.9.0 scripts/upgrade-check.sh --list
```

`--list` builds nothing and starts nothing. It prints one `psql` line for each file, in the order
CI applies them: core's files oldest folder first, then the module files, then the rollback. A
folder no newer than `FROM_VERSION` is left out. Run the core and module lines now and keep the
rollback lines for later. Coming from a release before 4.9, the list also holds the 4.x files that
release lacks: read their headers in [upgrading-to-4.0.md](upgrading-to-4.0.md), which describes
each of them.

## Going back to 4.x

Stop 5.0, then run the rollback lines `scripts/upgrade-check.sh --list` prints, newest first, with
`FROM_VERSION` set to the release you are going back to. Then remove each rolled-back file's ledger
row with the 5.0 image, which knows the keys, before you start 4.x again:

```bash
docker compose run --rm --no-deps app db-migrate --forget core/5.0.0/marten-9-49-quick-append-events
```

The Marten rollback puts back the 9.40 body of `mt_quick_append_events`, which every 4.x release
from 4.6.0 declares. Nothing is lost: no build calls the function, and events appended while 5.0 ran
stay on their streams and read fine under 4.x.

## Other 5.0 notes

**A module or host compiled against Marten or JasperFx types rebuilds against 9.49.0 and 2.84.1.**
Marten no longer replays a `40001` serialization failure, and its default read policy no longer
retries a command timeout, so code that relied on either retry now sees the error.

**The CI check that proves the upgrade is now named `Upgrade from 4.x`.** It used to be
`Upgrade from 3.x`, and started from 3.21.0 as well as 4.1.0. A fork that requires the check by
name in branch protection has to require the new name.
