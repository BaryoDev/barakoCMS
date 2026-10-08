# Migrations: the ledger and `db-migrate`

Production runs Marten with `AutoCreate.CreateOnly`: a missing table is created on start, an existing
one is never altered. A release that changes an existing object, or has to rewrite stored data,
ships a SQL file for it. Through 4.5.0 those files were applied by hand with `psql` and nothing
recorded which had run. From 4.6.0 the host keeps a ledger and has one command that applies what
the ledger lacks.

- The ledger is the table `public.barako_migrations`: one row per migration, with its owner
  (`core` or a module name), its id (`<version>/<file name>`), the SHA-256 of the file, a state and
  when it was recorded.
- Core and each module ship their files inside their own assembly. A module's files are considered
  only while that module is enabled.
- `db-migrate` is an argument to the image, like `db-assert`.

## Running it

Stop the API first, then migrate, check, and start. With compose, from the directory holding your
compose file and `.env`:

```bash
docker compose pull
docker compose stop app
docker compose run --rm --no-deps app db-migrate
docker compose run --rm --no-deps app db-assert
docker compose up -d
```

Without compose, stop the service, then pass the same connection string and JWT key the deploy
uses:

```bash
docker run --rm \
  -e ConnectionStrings__DefaultConnection="$CONNECTION_STRING" \
  -e JWT__Key="$JWT_KEY" \
  ghcr.io/baryodev/barako-cms:<version> db-migrate
```

Why stopped: a file takes locks on the tables it changes and waits behind every open transaction
on them, and an index built `CONCURRENTLY` waits for every transaction older than itself. A running
API keeps a transaction open for as long as it runs, so against a live API that build does not
finish, and the command sits there holding the migration lock. Before the first file it runs,
`db-migrate` counts the other sessions that have a transaction open on the database and prints

```
warning    1 other session(s) have a transaction open on this database. Stop the API before migrating: ...
```

It warns and carries on. It cannot tell an API from a backup job, so the decision stays yours.

If you cannot stop the service first, run `db-migrate --status` while it serves, which only reads.
If nothing is `pending`, running `db-migrate` records the rest without executing a file. Some files
say in their header that they can go in while the old build still serves: the two Marten event
store files (4.3.0, 4.4.0) and `core/4.6.0/event-correlation-metadata`. When those are the only
pending files, `db-migrate` can run under the old build; it prints the warning above and goes on,
and each of them is safe to run again. Deploy soon after, since an old instance that restarts
fails its own start-up check. For any other pending file, take the downtime: there is no supported
way to run it under a serving API.

`db-migrate` applies the shipped SQL files. `db-assert` then checks that the schema is what the
build declares. They answer different questions, so run both.

| Command | What it does | Exit code |
|---|---|---|
| `db-migrate` | Runs every migration the ledger lacks, in order. | 0 when all of them are recorded, 1 when it stopped |
| `db-migrate --status` | Prints each migration and its state. Changes nothing and does not create the table. | 0 when every shipped migration has a row and none is `changed`, otherwise 1 |
| `db-migrate --record <key>` | Writes the row for a file you applied by hand. Does not run it. | 0, or 1 for a key this build does not ship |
| `db-migrate --forget <key>` | Removes a row, after you rolled the file back by hand. | 0, or 1 for a key this build does not ship |

A key is `owner/version/name`, exactly as `--status` prints it, such as
`core/4.2.0/site-share-links` or `Files/4.2.0/stored-files-parent-index`. It is matched exactly,
case included.

A host built from the NuGet packages answers the command when its `Program.cs` ends with
`Environment.ExitCode = await app.RunBarakoCommandsAsync(args);`, as both hosts in this repository
do. That call also runs `db-assert`, `db-patch` and `db-apply`, and serves when no command is named.
A host that still ends with `app.RunJasperFxCommands(args)` has no `db-migrate`, though its start
log will name the command: change that line first.

## What a run does

1. Takes a session advisory lock (key `8242026901`) on its own connection. If another `db-migrate`
   holds it, this one prints that, changes nothing and exits 1. It does not wait.
2. Creates the ledger table if it is not there, and reads it.
3. Stops, before running anything, if a migration that was run or recorded here has a different
   checksum than the file this build ships. See [A file that changed](#a-file-that-changed).
4. Goes through the shipped migrations in order: core's first, by version then file name, then each
   module's. A migration that has a row is left alone.
5. Runs each remaining file in a transaction together with its ledger row, and prints
   `applied    <key>`. A notice the file raises is printed as `notice     <key>  <text>`.
6. If the file has a skip query, asks it again after the file has run. It has to answer true now.
   If it does not, the file is failed and nothing is recorded.
7. Stops at the first file that fails, prints `failed     <key>` with the database's error, and
   exits 1.

What commits together: a file and its row. If the file fails, or the process dies part way through
it, the transaction rolls back, there is no row, and the next run starts that file from the top.
Files before it stay applied and recorded.

One kind of file cannot run in a transaction: one that builds an index `CONCURRENTLY`. Such a file
says so with a `-- barako:no-transaction` line. It is executed first and recorded after, so a
process that dies between the two leaves the change without its row and the next run executes the
file again. A file marked that way has to hold one statement, and has to carry a skip query or a
`-- barako:rerunnable` line; the command refuses one that has neither.

### Stopping a run

There is no time limit on a file. An index build or a backfill takes as long as the table needs.

Ctrl+C, and the SIGTERM that `docker stop` sends, are caught. The command asks the server to
cancel the statement in flight, the file's transaction rolls back, the lock is released, and it
prints `cancelled  <key>` and exits 1. A `no-transaction` index build that is cancelled leaves an
invalid index behind; see the next section.

### A run that was killed

`kill -9`, a container removed without a stop, or a lost network connection give the command no
chance to cancel anything. The server already has the whole file and keeps executing it until the
statement ends or it next notices the client is gone. Until then that session holds its table locks
and the migration lock, and the next `db-migrate` prints

```
Another db-migrate run holds the migration lock on this database. Nothing was changed. ...
```

with no run alive. This finds the session:

```sql
select a.pid, a.state, a.xact_start, left(a.query, 80) as query
from pg_locks l
join pg_stat_activity a on a.pid = l.pid
where l.locktype = 'advisory'
  and (l.classid::bigint << 32) | l.objid::bigint = 8242026901
  and l.objsubid = 1;
```

Either wait for it, or end it with `select pg_cancel_backend(<pid>);` (the statement) or
`select pg_terminate_backend(<pid>);` (the session). A transactional file then rolls back and has
no row, and the next run starts it from the top.

### An index build that was interrupted

A `CONCURRENTLY` build that is cancelled or killed leaves the index in place and marked invalid.
`CREATE INDEX CONCURRENTLY IF NOT EXISTS` then skips, because the name exists. `db-migrate` does
not record that as done: the file's skip query, asked again after the file ran, still answers
false, so the run prints

```
failed     Files/4.2.0/stored-files-parent-index  the file ran and its skip-when query still answers false, ...
```

and exits 1. Find and drop the invalid index, then run `db-migrate` again:

```sql
select indexrelid::regclass from pg_index where not indisvalid;
drop index concurrently public.mt_doc_stored_files_idx_parent_file_id;
```

## A database that predates the ledger

Every deployment that started before 4.6.0 has applied files by hand and has no ledger. Some of
those files must not run a second time: `migrations/4.0.0/3.x-to-4.0.sql` rewrites content
statuses and replaces a function a later file replaces again, and several files end by turning row
level security off on a table, which would remove a tenant policy on a database with
`Tenancy:DatabaseEnforcement` on.

The rule the first run follows is the same rule every run follows, for any migration with no row:

- If the file carries a `-- barako:skip-when:` query and it answers true, the change is already in
  the database. The file is not run. It is recorded with state `baselined` and the run prints
  `baselined  <key>  (not run: its skip-when query found the change already in place)`.
- Otherwise the file is run and recorded with state `applied`. That includes a file with no skip
  query at all.

Every file released through 4.5.0 carries a skip query that looks for the object the file creates
(the table, the index, the column, or the function body). Two tests hold each of the ten on both
sides: `MigrationSkipQueryTests` builds a database that lacks the change and expects the query to
answer false, runs the file, and expects true; `MigrationHostTests` expects true on a current
schema. So on a database that is up to date with 4.5.0, the first `db-migrate` of 4.6.0 prints ten
`baselined` lines for a Suite host (seven for a core-only host), runs only the files 4.6.0 added,
and ends with a line such as `2 applied, 10 baselined, 0 already recorded.`

A file is never recorded as `applied` unless this command ran it. `baselined` always means "not run,
because the database did not need it", and it is always printed.

On a database that is behind, the same rule runs what is missing: a file whose skip query answers
false is run. By those queries a 4.1 database gets `core/4.0.0/3.x-to-4.0` baselined and the 4.2.0
and later files applied, and a 3.x database gets all of them applied, in order. CI does not run
`db-migrate` against a real 3.x or 4.1 database yet (see [Limits](#limits)), so on such a database
run `db-migrate --status` first and compare it with what you know was applied. Read
[upgrading-to-4.0.md](upgrading-to-4.0.md) too: the checks it lists before the 4.0.0 file still
apply, and the user file still refuses when two accounts differ only by case.

A file added from 4.6.0 on that has no skip query is run on a database with no ledger, whether or
not it was applied there by hand. Such a file has to say `-- barako:rerunnable`, and a test fails
if one has neither line. If you applied one by hand and would rather it did not run again, record
it first:

```bash
docker compose run --rm --no-deps app db-migrate --record core/4.6.0/<name>
```

Run `db-migrate --status` first if you want to see what the run would do. `pending` will run,
`not-needed` will be recorded as baselined without running.

## A new database

A database nothing has started against has no `mt_doc_users` table. No shipped migration applies
to it, because the first start creates every object in its current shape. A normal start,
`db-apply` and `db-migrate` each record every shipped migration as `baselined` on such a database
and run none. A start and `db-apply` write those rows before they create the schema, so one that
dies in between leaves rows and no tables, and the next start creates the tables.

Without this, the first `db-migrate` on an install that began on a release with the ledger would
run every file that has no skip query against a schema that was already current.

## A file that changed

A released migration file is never edited, comments included. A further change ships as a new
file. In this repository a test holds that: `ShippedMigrationTests.A_released_migration_file_has_not_been_edited`
pins the checksum of every file in a released version, so an edit fails in CI, not at a deploy.

The ledger enforces it at the database too. When a row's checksum differs from the file this
build ships, what happens depends on the row's state:

- `applied` or `recorded`: the file's content is what this database is taken to have. `db-migrate`
  prints

  ```
  changed    core/4.6.0/<name>  recorded <checksum>, this build ships <checksum>
  ```

  runs nothing at all, and exits 1. `--status` prints the same line and exits 1.
- `baselined`: the file never ran on this database and never will, so the edit cannot have split
  what ran from what is recorded. `db-migrate` prints a `warning` line with both checksums and
  carries on, and `--status` marks the line and still exits 0. The warning repeats on every run
  until you accept the file with `--record`.

Line endings are not part of the checksum, so a CRLF checkout builds the same file. Everything
else is.

If the file this build ships is right and the database already has its change, record it again
with `db-migrate --record <key>`. That replaces the checksum and sets the state to `recorded`.

## Rolling back

`db-migrate` never runs a rollback file. Each migration still has its `rollback-*.sql` beside it
under `migrations/<version>/`, applied by hand, newest first, as
[upgrading-to-4.0.md](upgrading-to-4.0.md) describes.

After rolling a file back, remove its row, with the image you are rolling back from:

```bash
docker compose run --rm --no-deps app db-migrate --forget core/4.6.0/<name>
```

A row left behind says the file is applied, so the next upgrade would skip it and `db-assert` or
the start would then report the missing object. The ledger table itself can stay: an older
release does not declare it and ignores it.

## Applying a file by hand

The files stay readable under `migrations/<version>/`, each with the `psql` command in its header.
Applying one by hand still works. Follow it with `db-migrate --record <key>`, or for a file
that has a skip query just run `db-migrate`, which finds the change in place and baselines it.

## With tenancy enforced at the database

With `Tenancy:DatabaseEnforcement` on, the app connects as a role that is not a superuser, and
`db-migrate` connects as the app does.

- A file that says it needs a superuser (it refuses with an error otherwise) is applied by hand as
  that superuser and then recorded with `--record` as the app role. Do not run `db-migrate` itself
  as a different role than the app: the ledger table would be created owned by that role and the
  app could not read it.
- A file that creates a table creates it without the tenant policy, because the policy is
  something the app adds when it creates the table itself. That applies to
  `Forms/4.2.0/forms-public-forms`, `Forms/4.6.0/forms-email-verification`,
  `core/4.3.0/collection-syncs` and `core/4.2.0/site-share-links` when their table is missing. `db-assert` then reports the policy as outstanding. Add it with
  `db-apply`, then run `db-assert` again:

  ```bash
  docker compose run --rm --no-deps app db-migrate
  docker compose run --rm --no-deps app db-apply
  docker compose run --rm --no-deps app db-assert
  ```

  This is the same gap the hand route had, written up in the header of
  `migrations/4.2.0/site-share-links.sql`. [tenancy-at-the-database.md](tenancy-at-the-database.md)
  has the queries that show the policy is there.
- `core/4.7.0/tenant-policy-restore` puts that policy back on core's tables among them
  (`mt_doc_site_share_links`, `mt_doc_collection_syncs`) and on
  `mt_doc_content_type_sourcing_policies`, wherever one lacks it and another table in `public`
  carries it, copying the policy from that table. It covers a table one of those files created
  before the app added the policy, and one a hand run of a file took it off again. With no table
  carrying the policy, which is enforcement off, it changes nothing.
- `Forms/4.7.0/forms-tenant-policy-restore` does the same for the Forms tables
  (`mt_doc_public_forms`, `mt_doc_form_email_verifications`, `mt_doc_form_email_budgets`). It ships
  from Forms, so it runs after the Forms files that create those tables, including on a database
  where Forms is turned on later.

## What a start does with the ledger

A normal start never runs a migration. It reads the ledger, and when a shipped migration has no
matching row it logs one warning naming the pending ids, the ones that will be baselined and the
ones whose checksum changed. If the ledger cannot be read the start logs that and carries on.

When the module schema preflight refuses a module (see MODULES.md), the refusal names that
module's pending migration ids and `db-migrate`.

## Modules

A module ships its migrations in its own assembly, as embedded resources named
`barako-migrations/<version>/<name>.sql`. The ledger records them under the module's `Name`.
MODULES.md has the project file lines.

- A module that is not enabled contributes no files. Its existing rows stay, and `--status` lists
  them as `not-loaded`.
- Core's files run before any module's. One module's files do not run in any promised order
  relative to another module's.
- Enabling a module on a database that has never had it: its tables do not exist, so a file that
  alters one of them needs a skip query that is also true when the table is missing (the first
  start creates the table current). `Files/4.2.0/stored-files-parent-index` is the example.

A file under `migrations/` belongs to a module when that module's project embeds it, and ships
from that module. `scripts/upgrade-check.sh --list` prints those files under their own heading.
Every other file under `migrations/<version>/` ships from core, including files added later, with
no list to update. `scripts/check-module-versions.sh` counts a change to a file a module links
from `migrations/` as a change to that module, so the module's version has to move with it.

## Writing a migration file

- Put it at `migrations/<major.minor.patch>/<name>.sql`. Letters, digits, dots, dashes and
  underscores in the name. The build picks it up.
- Put its rollback beside it as `rollback-<name>.sql`. A file whose name starts with `rollback-` is
  never shipped as a migration. Any other name is.
- Plain SQL only: no `psql` meta-commands, and no `BEGIN` or `COMMIT`, since the command supplies
  the transaction.
- Say what happens where the change is already in place. Either add one line with a query
  returning one boolean, true when the database does not need the file:
  `-- barako:skip-when: select to_regclass('public.mt_doc_things') is not null`,
  or, if running the file a second time changes nothing, add `-- barako:rerunnable`. A file from
  4.6.0 on with neither fails `ShippedMigrationTests`. The skip query runs in a read-only
  transaction before the file, and is asked again after it: the file has to make it true.
- If it cannot run in a transaction, add `-- barako:no-transaction` and keep it to one statement.
- A line starting `-- barako:` that is not one of those three stops the command, so a misspelt
  directive is not read as a comment.
- Once the version it is in has been released, do not edit it, not even a comment. Pin its
  checksum in `ShippedMigrationTests.Released` in the release commit; the test names the line to add.

## Limits

- The ledger lives in the `public` schema, where the shipped files also name their tables. On a
  host that moves Marten to another schema, `db-migrate` prints that it cannot tell what the
  database has had, records nothing and exits 1, and a start leaves the ledger alone.
- One database. Tenants are conjoined in one set of tables here, so a migration covers every
  tenant in one run; there is nothing per tenant to apply.
- `scripts/upgrade-check.sh` still applies the files with `psql`. It does not yet run `db-migrate`
  against a 3.x database.
- A host that mixes package versions, such as core 4.6.0 with a Files package older than the one
  that ships its migration, gets no Files migration from either.
- A connection pooler in transaction mode does not keep a session advisory lock on one server
  session. Point `db-migrate` at the database directly.
