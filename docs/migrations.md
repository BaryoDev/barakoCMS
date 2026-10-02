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

With compose, from the directory holding your compose file and `.env`, with the API stopped:

```bash
docker compose pull
docker compose run --rm --no-deps app db-migrate
docker compose run --rm --no-deps app db-assert
docker compose up -d
```

Without compose, pass the same connection string and JWT key the deploy uses:

```bash
docker run --rm \
  -e ConnectionStrings__DefaultConnection="$CONNECTION_STRING" \
  -e JWT__Key="$JWT_KEY" \
  ghcr.io/baryodev/barako-cms:<version> db-migrate
```

`db-migrate` applies the shipped SQL files. `db-assert` then checks that the schema is what the
build declares. They answer different questions, so run both.

| Command | What it does | Exit code |
|---|---|---|
| `db-migrate` | Runs every migration the ledger lacks, in order. | 0 when all of them are recorded, 1 when it stopped |
| `db-migrate --status` | Prints each migration and its state. Changes nothing and does not create the table. | 0 when every shipped migration has a matching row, otherwise 1 |
| `db-migrate --record <key>` | Writes the row for a file you applied by hand. Does not run it. | 0, or 1 for a key this build does not ship |
| `db-migrate --forget <key>` | Removes a row, after you rolled the file back by hand. | 0, or 1 for a key this build does not ship |

A key is `owner/version/name`, exactly as `--status` prints it, such as
`core/4.2.0/site-share-links` or `Files/4.2.0/stored-files-parent-index`. It is matched exactly,
case included.

A host built from the NuGet packages answers the command when its `Program.cs` ends with
`Environment.ExitCode = await app.RunBarakoCommandsAsync(args);`, as both hosts in this repository
do. That call also runs `db-assert`, `db-patch` and `db-apply`, and serves when no command is named.

## What a run does

1. Takes a session advisory lock (key `8242026901`) on its own connection. If another `db-migrate`
   holds it, this one prints that, changes nothing and exits 1. It does not wait.
2. Creates the ledger table if it is not there, and reads it.
3. Stops, before running anything, if a recorded migration's checksum is not the checksum of the
   file this build ships. See [A file that changed](#a-file-that-changed).
4. Goes through the shipped migrations in order: core's first, by version then file name, then each
   module's. A migration that has a row is left alone.
5. Runs each remaining file in a transaction together with its ledger row, and prints
   `applied    <key>`. A notice the file raises is printed as `notice     <key>  <text>`.
6. Stops at the first file that fails, prints `failed     <key>` with the database's error, and
   exits 1.

What commits together: a file and its row. If the file fails, or the process dies part way through
it, the transaction rolls back, there is no row, and the next run starts that file from the top.
Files before it stay applied and recorded.

One kind of file cannot run in a transaction: one that builds an index `CONCURRENTLY`. Such a file
says so with a `-- barako:no-transaction` line. It is executed first and recorded after, so a
process that dies between the two leaves the change without its row and the next run executes the
file again. A file marked that way has to be safe to run twice, and has to hold one statement.

There is no time limit on a file. An index build or a backfill takes as long as the table needs.
Stopping the process cancels it, and a transactional file rolls back.

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
- Otherwise the file is run and recorded with state `applied`.

Every file released through 4.5.0 carries a skip query that looks for the object the file creates
(the table, the index, the column, or the function body). `ShippedMigrationTests` fails if one of
them loses it, and `MigrationHostTests` runs each query against a current schema and expects true. So on a database that is up to date with 4.5.0, the first `db-migrate` of 4.6.0 prints ten
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

If you applied a 4.6.0 or later file by hand before running `db-migrate`, record it so the run does
not execute it again:

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

A released migration is not edited. A further change ships as a new file. The ledger enforces
that: when a row's checksum differs from the file this build ships, `db-migrate` prints

```
changed    core/4.6.0/<name>  recorded <checksum>, this build ships <checksum>
```

runs nothing at all, and exits 1. `--status` prints the same line and exits 1.

Line endings are not part of the checksum, so a CRLF checkout builds the same file. Everything
else is, comments included.

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

With `Tenancy:DatabaseEnforcement` on, the app connects as a role that is not a superuser, and
`db-migrate` connects as the app does. A file that says it needs a superuser (it refuses with an
error otherwise) is applied by hand as that superuser and then recorded with `--record` as the app
role. Do not run `db-migrate` itself as a different role than the app: the ledger table would be
created owned by that role and the app could not read it.

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

Three files under `migrations/` belong to modules and ship from them:
`4.2.0/stored-files-parent-index.sql` (Files), `4.2.0/forms-public-forms.sql` (Forms) and
`4.5.0/email-sent-emails.sql` (Email.Resend). Every other file under `migrations/<version>/` ships
from core, including files added later, with no list to update.

## Writing a migration file

- Put it at `migrations/<major.minor.patch>/<name>.sql`. Letters, digits, dots, dashes and
  underscores in the name. The build picks it up.
- Put its rollback beside it as `rollback-<name>.sql`. A file whose name starts with `rollback-` is
  never shipped as a migration. Any other name is.
- Plain SQL only: no `psql` meta-commands, and no `BEGIN` or `COMMIT`, since the command supplies
  the transaction.
- If it must not run where its change is already in place, add one line with a query returning
  one boolean, true when the database does not need the file:
  `-- barako:skip-when: select to_regclass('public.mt_doc_things') is not null`.
  The query runs in a read-only transaction.
- If it cannot run in a transaction, add `-- barako:no-transaction`, keep it to one statement and
  make it safe to run twice.
- A line starting `-- barako:` that is not one of those two stops the command, so a misspelt
  directive is not read as a comment.

## Limits

- The ledger lives in the `public` schema, where the shipped files also name their tables. A host
  that moves Marten to another schema is not covered.
- One database. Tenants are conjoined in one set of tables here, so a migration covers every
  tenant in one run; there is nothing per tenant to apply.
- `scripts/upgrade-check.sh` still applies the files with `psql`. It does not yet run `db-migrate`
  against a 3.x database.
- A host that mixes package versions, such as core 4.6.0 with a Files package older than the one
  that ships its migration, gets no Files migration from either.
