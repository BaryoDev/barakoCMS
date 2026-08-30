# Upgrading from 3.x to 4.0

4.0 will not boot against a 3.x database until you apply one SQL file. This page is what to run,
what each statement does, and what to do when it goes wrong.

The whole sequence is proved in CI by `scripts/upgrade-check.sh`, which stands up a real 3.21.0
database through the released image and upgrades it. If a step here stops being true, that job goes
red.

## Why there is a migration at all

Production runs Marten's `AutoCreate.CreateOnly`. It creates objects that are missing, so a fresh
database sets itself up, but it never alters an existing one. That is deliberate: the alternative
(`CreateOrUpdate`) retries a failing migration on every write, so a schema mismatch arrives as
random 500s on user requests rather than as a failure you can see.

4.0 moved Marten from 8.37 to 9.30. Four database objects changed, so the first boot against a 3.x
database hits `CreateOnly`, refuses, and exits non-zero. Nothing is written and nothing is lost; the
host simply does not start.

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

Stop the 4.0 deploy from starting yet, and with 3.x stopped:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.0.0/3.x-to-4.0.sql
```

Then confirm the schema matches what 4.0 expects, without starting the server:

```bash
dotnet barakoCMS.dll db-assert
```

Exit code 0 means you can deploy. Non-zero prints the exact statements still outstanding.

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
then apply the file again. It is safe to re-run: every statement in it is idempotent in effect.

**You need to go back to 3.x.** Stop 4.0, then:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.0.0/rollback-to-3.x.sql
```

That restores the two `mt_streams` columns as NULL, which is what they were, and removes `bdata`.
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
dotnet barakoCMS.dll db-patch upgrade.sql   # writes the delta and its rollback, changes nothing
dotnet barakoCMS.dll db-assert              # verify only, non-zero when the schema is behind
dotnet barakoCMS.dll db-apply               # apply it
```

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
