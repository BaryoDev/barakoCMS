# Upgrading from 3.x to 4.0

4.0 will not boot against a 3.x database until you apply two SQL files. This page is what to run,
what each statement does, and what to do when it goes wrong.

The whole sequence, including the rollback below, is proved in CI by `scripts/upgrade-check.sh`,
which stands up a real 3.21.0 database through the released image, upgrades it, then rolls it back
and boots 3.21.0 again against the result. It checks the schema with both the core host and the
Suite, and boots the Suite, which is what the published image runs. If a step here stops being
true, that job goes red. The job then runs again from a database 4.1.0 created, leaving out the two
`4.0.0` files, and rolls that one back to 4.1.0.

## Why there is a migration at all

Production runs Marten's `AutoCreate.CreateOnly`. It creates objects that are missing, so a fresh
database sets itself up, but it never alters an existing one. That is deliberate: the alternative
(`CreateOrUpdate`) retries a failing migration on every write, so a schema mismatch arrives as
random 500s on user requests rather than as a failure you can see.

4.0 moved Marten from 8.37 to 9.30. Four of core's database objects changed, so the first boot
against a 3.x database hits `CreateOnly`, refuses, and exits non-zero. Nothing is written and
nothing is lost; the host simply does not start.

The published image runs the Suite, core plus every module, and two modules need something too.
Files declares an index on `mt_doc_stored_files.ParentFileId` that 3.x never created. On an existing
database `CreateOnly` will not add it, and the module schema preflight refuses to start without it,
naming `Files: public.mt_doc_stored_files`. Forms (new in 4.2) declares a table,
`mt_doc_public_forms`, that a database from before the module does not have. `CreateOnly` would
create it on first boot, but `db-assert` below reports it as outstanding until it exists, so the
Forms file creates it up front and the boot then has nothing to write. A host that loads no module
(the decaf image, or your own host without Files or Forms) does not need those two files, and
running them anyway is harmless.

## Before you start

Take a backup. This migration drops two columns, and while both are empty in every barakoCMS
database (see below), a backup is the only thing that makes the step reversible if yours is not.

Check the two columns really are empty:

```sql
select count(snapshot) as snapshot, count(snapshot_version) as snapshot_version
from public.mt_streams;
```

Both must be `0`. They are Marten 8's inline stream-snapshot columns, and barakoCMS never enabled
stream snapshots, so they are NULL for every row by construction. If yours are not zero, this
database used a feature this project does not, and you should stop and ask on the issue tracker
rather than dropping them.

The migration checks this itself and refuses rather than trusting you to have read this paragraph.
Run it with `--single-transaction`, as below, and a refusal leaves the database exactly as it was.

## The upgrade

Stop the 4.0 deploy from starting yet, and with 3.x stopped, run the `psql` lines this prints,
from a checkout of the release you are deploying:

```bash
scripts/upgrade-check.sh --list                       # coming from 3.x
FROM_VERSION=4.1.0 scripts/upgrade-check.sh --list    # coming from 4.0 or 4.1
```

`--list` builds nothing and starts nothing. It reads the files under `migrations/` and prints one
`psql` line for each, in the order CI applies them: core's files oldest folder first, then the
module files (the ones a module's project embeds), then the rollback. A folder no newer than
`FROM_VERSION` is left out, so coming from 4.0 or 4.1 the `4.0.0` file is not listed. Run the core
and module lines now and keep the rollback lines for later. The stored files index is the one line
without `--single-transaction`: its header says why.

The two Marten files bring the event store up to the Marten version the release you are deploying
runs. Each explains itself in its header. Skip a file whose directory is newer than that release.

The event correlation file (4.6.0) adds two nullable columns to `mt_events` and replaces the
function that appends events, so each event can record the request that wrote it
([tracing.md](tracing.md)). It runs after the `4.4.0` file, which replaces the same function.
Coming from 4.5 it can be applied while 4.5 is still serving, then deploy, like the two Marten
files: 4.5 writes an event with an INSERT that names its own columns, so the two new nullable
columns do not stop it, and it does not call the function. CI applies the file under a running
4.1 and writes through it. An old instance that restarts after the file fails its own start-up
schema assertion, so do not leave long between the file and the deploy.

With a 4.6.0 or later image, `db-migrate` applies these files in place of the `psql` lines.
See [migrations.md](migrations.md), and run `db-migrate --status` first: CI proves the `psql` route
from 3.x and does not yet run `db-migrate` against a 3.x database.

The user file moves the unique indexes on username and email to their lowercased, trimmed forms,
which is what sign-in compares. If two existing accounts differ only by case, such as
`Admin@example.com` and `admin@example.com`, it refuses, changes nothing, and lists both accounts by
id. Rename or remove one of each pair and run it again. It does not pick one for you. A 4.0 or 4.1
database needs this file too.

The share links file creates `mt_doc_site_share_links`, empty. The `4.0.0` file creates the same
table, but only since 4.2.0, so coming from 3.x today this file changes nothing. A database that was
already on 4.0 or 4.1 ran the `4.0.0` file before the table was in it, and this file is the only
thing that gives it one: without it `db-assert` reports the table as outstanding. Coming from 4.0 or
4.1, leave the `4.0.0` file out and run the rest, which is the sequence CI runs from 4.1.0. It is
safe to run twice.

The file ends by turning row level security off on the table, which is the state `db-patch` emits
with `Tenancy:DatabaseEnforcement` off, but only when the table carries no policy. With enforcement
on and the table already there, the tenant policy and forced row level security are left alone and
the file changes nothing. With enforcement on and the table absent, which is a 4.0 or 4.1 database,
the file creates the table without the policy, and `db-assert` run with enforcement on then reports
the policy as outstanding. Add it with `db-apply`, described under
[Schema changes after 4.0](#schema-changes-after-40);
[tenancy-at-the-database.md](tenancy-at-the-database.md) has the queries that show it is there.

The tenant profile file changes data and no schema, so `db-assert` passes with or without it. It
moves each tenant's logo, about text, location, social handle, email and contact link from the
tenant document into that tenant's published `site` entry, and prints a `NOTICE` for every tenant
and value it leaves where it was, with the reason. The API answers from the site entry where the
site type declares the field and from the tenant document where it does not, so a tenant the file
left alone still answers. [multi-tenancy.md](multi-tenancy.md#the-tenant-profile) has the rules,
how to remove a value left behind, and the query that lists what is still on tenant documents. It is
safe to run twice, and worth running again after a tenant it left alone publishes its site entry.

Run every file with the API stopped. A running API keeps a transaction open for as long as it
runs, and an index built `CONCURRENTLY` waits for every transaction older than itself, so against
a live API it never finishes.

The Files file builds its index `CONCURRENTLY`, which is why it runs on its own, without
`--single-transaction`. The refresh token file is a plain build inside a transaction: with the API
stopped the table takes moments, and an invalid index left by an earlier `CONCURRENTLY` attempt is
dropped and built again. The Forms file creates one empty table, and the 4.6.0 Forms file creates
the two empty tables a form uses to verify an email field. All of them are safe to run twice.

The Email file creates the empty table Email.Resend uses to record which tenant sent each email,
so a later bounce can be put back on that tenant. It is safe to run twice.

The ExternalAuth file creates the empty table that ties an OpenID Connect provider account, by
issuer and subject, to a user. Nothing writes to it until a provider is configured under
`Oidc:Providers`. It is safe to run twice.

The sensitivity file (4.6.0) changes data, not schema. It gives the seeded HR role the
`view_sensitive` capability, which is what its name used to grant, and rewrites the role names in
every field's `visibleToRoles` to role ids. Stop the API, run it, then start 4.6.0. The order
matters here more than for an index: 4.5 and earlier match those lists against the role names in
the token, so an earlier release serving a migrated database masks every listed field for every
role on its list, and only SuperAdmin reads them, until 4.6.0 is running. Nothing is disclosed in
that state and no value is changed. 4.6.0 on a database the file has not reached is safe to serve,
since it still matches names. A role named HR under any id but the seeded one is not granted, and
the file says so in a notice that names the id. It is safe to run twice.

The membership file (4.7.0) adds a unique index on a membership's user and tenant, so one person
holds one membership per tenant. If two rows already share a user and a tenant it refuses, changes
nothing, and says how many pairs there are; its header has the query that lists them. It does not
pick which row to keep. It is safe to run twice.

The tenant policy file (4.7.0) matters only with `Tenancy:DatabaseEnforcement` on. The share links,
Forms and collection syncs files, and the `4.0.0` file, end by taking the tenant policy off the
table they create, and run again by hand on an enforced database they take it off a table that had
it. This file reads whether the database enforces tenancy from its other tables, and where it does,
puts the same policy back on any of those tables that lacks it. With enforcement off it changes
nothing. It is safe to run twice.

Then confirm the schema matches what 4.0 expects, without starting the server. The command is an
argument to the 4.0 image, which hands it to the host instead of booting the web app. With compose,
from the directory holding your compose file and `.env`:

```bash
docker compose pull
docker compose run --rm --no-deps app db-assert
```

The service is `app` in `docker-compose.prod.yml` and `api` in `quickstart/docker-compose.yml`.
`run` gives the command the service's environment, so it checks the same database the deploy will
use. Without compose, pass the same connection string and JWT key the deploy uses:

```bash
docker run --rm \
  -e ConnectionStrings__DefaultConnection="$CONNECTION_STRING" \
  -e JWT__Key="$JWT_KEY" \
  ghcr.io/baryodev/barako-cms:<version> db-assert
```

Exit code 0 means you can deploy. Non-zero prints the exact statements still outstanding.

The image runs `BarakoCMS.Suite.dll`. Through 4.1.0 the Suite ignored these commands and started
the web app against that database instead, so use a tag newer than 4.1.0 for this step even when
the version you are deploying is older. The decaf image (`barako-cms-decaf`) runs the core host and
has always answered them.

Now start 4.0 normally.

## What the migration does

| Object | Change | Why it is safe |
| --- | --- | --- |
| `mt_events` | adds `bdata bytea NULL` | Additive and nullable. Existing rows are untouched. |
| `mt_streams` | drops `snapshot`, `snapshot_version` | Marten 8 columns for a feature barakoCMS never enabled, NULL in every row. |
| `mt_quick_append_events` | replaced | Marten 9 changed its signature and body. A function, no data. |
| `mt_safe_unaccent` | replaced | Marten 9 schema-qualifies the `unaccent` call. Body only, and nothing depends on it. |

No event is rewritten, no document is touched, and the projection daemon keeps its stored
progression, so it resumes where it left off rather than replaying every event and re-firing every
workflow side effect.

## When it goes wrong

**4.0 exits at startup with `Cannot derive schema migrations ... AutoCreate.CreateOnly`.** The
migration has not been applied, or only partly. Run `db-assert` to see exactly what is outstanding,
then apply the file again. Most of it is idempotent, but four statements are not: the `DO $guard$`
block reads `mt_streams.snapshot`, which the first run removes, and the three `mt_streams` column
changes carry no `IF EXISTS` or `IF NOT EXISTS`. Because the command above runs
`--single-transaction`, a second run aborts and rolls back rather than leaving the database part
way. If `db-assert` says the migration is outstanding after a failed run, take the outstanding
statements from the file by hand rather than re-running the whole thing.

**You need to go back to 3.x.**

> **Rolling back is lossy, and some of what it drops cannot be recovered afterwards.** Have these
> to hand *before* you start:
>
> - **The email provider API key.** `mt_doc_email_settings` is dropped. The stored key is encrypted
>   and nothing decrypts it for display, so it cannot be read out first.
> - **Every connector credential**, for the same reason (`mt_doc_connectors`,
>   `mt_doc_connector_secrets`).
> - **An export of your URL redirects, query definitions and request definitions.** Those tables are
>   dropped and the data is not carried anywhere else.
>
> Also lost: queued jobs and workflow runs, the webhook delivery log, and which content types were
> forms (the submissions themselves are ordinary entries and stay). Every Scheduled entry is
> rewritten back to Draft, so anything waiting to publish will need rescheduling. Twelve tables are
> dropped in total; the file lists them with a comment on each.

Stop 4.0, then run the rollback lines `scripts/upgrade-check.sh --list` prints, newest first,
with `FROM_VERSION` set to the release you are going back to.

The list stops at the release you name, and from the default `FROM_VERSION` it goes all the way
to 3.x. Going back to 4.2 or later it leaves both `4.2.0` files out, as it must: 4.2 declares the
share links table, and running that rollback there drops every link for nothing.

Newest first. An earlier release asserts its own schema at startup. What it reports, and refuses
to boot over, is an index or column it does not declare on a table it does declare, which is why
the index and column rollbacks have to run. A whole table it does not declare is left alone and does
not stop it booting: the rollback files drop those tables so the database is back to what that
release built, not because the release needs it. One is left behind on a rollback to 4.0 or 4.1:
`mt_doc_public_forms`, whose only drop is in `rollback-to-3.x.sql`. It is harmless there, and CI
boots 4.1.0 beside it.

The tenant profile file copies each tenant's profile from its published site entry back onto the
tenant document, where a release before 4.6.0 reads it. It fills blanks only and removes nothing
from the site entry, so nothing is lost. For a value the forward file moved, the earlier release
then serves what the site entry holds now, an edit made on 4.6.0 included. A value still on the
tenant document is not overwritten: for a tenant the forward file left alone or was never run for,
and for a field where the two sides differed, the earlier release serves the tenant document's
value, as it did before the upgrade, even where 4.6.0 was answering with a different one from the
site entry.

The collection syncs file drops `mt_doc_collection_syncs`. That loses the sync schedules and field
mappings, which nothing else records; the entries those syncs wrote are ordinary content and are
untouched.

The refresh token file drops the index on the token hash. A release before 4.5.0 looks refresh
tokens up by their plain value, which 4.5.0 no longer stores, so anyone who signed in or refreshed on
4.5.0 has to sign in again after a rollback.

The share links file drops `mt_doc_site_share_links`, and every share link with it. Only the hash
of each key is stored, so the links cannot be saved first and put back: after upgrading again,
create new ones and send them out. Going back to 4.0 or 4.1, stop after the two `4.2.0` files and
leave `rollback-to-3.x.sql` out, which is the rollback CI runs from 4.1.0. Going back to 3.x, that last
file drops the same table, and running both is harmless.

The user file puts the username and email unique indexes back on the stored values, which is where
3.x declares them.

The Email file drops `mt_doc_sent_emails`, which an earlier release does not declare. It loses only
which tenant sent each email; a bounce reported after the rollback is recorded
without a tenant, as it was before.

The ExternalAuth file drops `mt_doc_external_identities`, which an earlier release does not declare.
It loses which OpenID Connect provider account belongs to which user. The users stay. After
upgrading again, each person is linked again by email the next time they sign in through the
provider, which needs the provider to vouch for the address.

That restores the two `mt_streams` columns as NULL, which is what they were, and removes `bdata`.
It also drops the Files `ParentFileId` index, which the 3.x Suite refuses to start alongside.
Events appended while 4.0 was running stay: they are ordinary events that 3.x reads fine. The one
thing rollback cannot preserve is a binary event payload in `bdata`, and barakoCMS opts no event
into binary serialization, so that column is NULL in every row. Check it if you are unsure:

```sql
select count(bdata) from public.mt_events;
```

## Schema changes after 4.0

The same route applies to any 4.x patch that needs a schema change, which is why the commands are
part of the host rather than a one-off script:

```bash
# writes the delta and its rollback into the current directory, changes nothing
docker compose run --rm --no-deps -v "$PWD:/out" --user "$(id -u)" app db-patch /out/upgrade.sql
# verify only, non-zero when the schema is behind
docker compose run --rm --no-deps app db-assert
# apply it
docker compose run --rm --no-deps app db-apply
```

The mount and `--user` are there because the image runs as a non-root user that cannot write to
your directory otherwise. A host built from the NuGet packages answers the same commands when its
`Program.cs` ends with `app.RunBarakoCommandsAsync(args)`, as both hosts in this repository do:
`dotnet YourHost.dll db-assert`. Through 4.5.0 that line was `app.RunJasperFxCommands(args)`, which
still runs these three commands and does not know `db-migrate`.

From 4.6.0 the files under `migrations/` do not have to be applied one by one. With the API
stopped, `db-migrate` runs the ones a database has not had and records each in a ledger, and on a
database that already has them it records them without running them. [migrations.md](migrations.md) has the command, what
its first run does on an existing database, and what it prints.

`db-patch` writes two files: `upgrade.sql` and `upgrade.drop.sql`, the second being the rollback.
Read both before running either. The point of the reviewed-file route is that a destructive
statement is visible before it runs, not after.

## Other 4.0 notes

**Every package retargets from `net8.0` to `net10.0`.** Your host has to be on .NET 10. This is the
largest break in the release and it is not something a migration can help with.

**A failed startup now exits non-zero.** It used to exit 0, so a broken deploy reported success to
CI, `docker run`, systemd and Kubernetes. If your pipeline was relying on the old behaviour to get
past a failing start, it will now stop, which is the point.

**A missing connection string fails at startup outside Development**, naming the setting, instead of
substituting a dummy that connects to localhost and fails later for an unrelated-looking reason.

**`/metrics` needs a scrape key.** The Prometheus endpoint used to answer anyone who could reach the
API, which handed out route names, per-endpoint traffic and process internals. It now refuses unless
`Metrics:ScrapeKey` (env `Metrics__ScrapeKey`) is set and the caller presents it. With nothing set it
returns 404, so scraping stops on upgrade until you configure it.

Set the key on the host:

```bash
Metrics__ScrapeKey=$(openssl rand -hex 32)
```

Then give it to Prometheus. `authorization` sends it as a bearer token, which the endpoint accepts:

```yaml
scrape_configs:
  - job_name: barakocms
    authorization:
      credentials: <the same value>
    static_configs:
      - targets: ['barakocms:8080']
```

The `X-Metrics-Key` header works too, for a scraper that would rather not use `Authorization`:

```yaml
    http_headers:
      X-Metrics-Key:
        values: ['<the same value>']
```

A wrong or missing key returns 401 while a key is configured, and 404 while none is, so the status
code tells you which of the two you are looking at. The key is a shared secret rather than a user, so
keep it out of the repository and rotate it like any other credential.

**Administrative endpoints gate on capabilities, not role names.** Roles, tenants, tenant
members, users and user groups now ask for a capability the caller's roles carry (`manage_roles`,
`manage_tenants`, `manage_tenant_members`, `manage_users`, `manage_user_membership`,
`manage_user_groups`) instead of matching `SuperAdmin` or `Admin` by name, and so does every
first-party module.

**This is the one behaviour change on upgrade.** `Auth:LegacyRoleFallback` was `true` through 3.x,
so a role name still opened the gate it used to. From 4.0 it defaults to `false`. The seeder
backfills the capabilities onto the four system roles on the next start, and it now adds what a role
is missing rather than only filling an empty list, so a deployment that runs the seeder reaches
everything it used to and there is nothing to do.

A deployment that does not run the seeder, or that curates its system roles by hand, sets the old
behaviour back:

```bash
Auth__LegacyRoleFallback=true
```

Do that before upgrading if you are unsure, then grant the capabilities and remove it. The flag is
still there and still supported; only the default moved.

A role created through `POST /api/roles` can now be granted administrative access without a code
change, and a role named `Editor` gains nothing from its name. Modules gating on `Roles(...)` are
unaffected. See `docs/access-control.md`.

**Self-registration no longer creates an account.** `POST /api/auth/register` records the request
and emails a single-use token that is good for 24 hours; the account appears when the token comes
back to `POST /api/auth/register/verify`. Until then no user document exists, which is the point:
external sign-in matches a provider's verified email to a local account by address alone, so a user
row holding an address nobody proved handed its real owner's Google sign-in to whoever registered it
first.

Two things change for a caller. The response is now the same whether or not the address is already
registered, so a client that read the old "Username or Email already exists" error has nothing to
read. A request that fails validation, a password below the minimum length for instance, still
answers 400 as it did. And registration needs a working email provider: with the mock provider
the token is logged and never delivered, so nobody can finish registering. Configure
`BarakoCMS.Email.Resend` (or your own `IEmailService`) before you turn a public registration form
on, and set `App:BaseUrl` so the email carries a link rather than a bare token.

To keep the old behaviour, set both of these. It will not start with only the first:

```bash
Auth__RequireEmailVerification=false
Auth__AcknowledgeUnverifiedRegistration=true
```

**A workflow action's `Secret` parameter is now encrypted regardless of action type.** Only a
Webhook action's `Secret` was protected before; a custom action reusing that parameter name was
shown as protected (`secretSet` in the response) while it was actually stored in clear. It is now
encrypted the same way for every action type.

**A `Secret` saved before it was ever encrypted now refuses to send, and says to recreate the
workflow.** This covers a Webhook action created before #524, or a custom action created before
this change, whose `Secret` was written straight into the store. It cannot be decrypted, because it
was never encrypted, and there is no endpoint that edits a saved workflow's parameters, so the only
fix is to create a new workflow with the secret entered again. The failure row says exactly that,
distinct from the message a rotated `Secrets:Key` produces, which asks you to re-enter the secret
instead. Check any workflow using a `Secret` parameter after upgrading; one that predates encryption
stops delivering rather than sending unprotected.
