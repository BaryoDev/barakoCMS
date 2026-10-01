# Tenant isolation at the database

By default, tenant isolation in barakoCMS is enforced by the application. Marten adds a `tenant_id`
filter to every query, and the caller's token is checked against the tenant in the URL. That is the
whole of it: a bug that opens a session without a tenant has nothing underneath it.

`Tenancy:DatabaseEnforcement` puts Postgres underneath it, as a second boundary. It is off by
default and turning it on is not a settings change.

## What it does and does not do

**It does** stop one tenant's session reading or writing another tenant's rows, enforced by
Postgres rather than by the filter Marten adds. A session opened for tenant A returns no rows of
tenant B, and a write carrying tenant B's id is refused by the database.

**It does not** catch a session opened with no tenant at all. That was the original hope and it is
not achievable: Marten represents "no tenant" as the default tenant, so a session without one runs
with `app.tenant_id` set to `*DEFAULT*` and sees exactly the default partition, which is where a
single-deployment site keeps its real content. Postgres cannot tell "I forgot to say which tenant"
from "I mean the default one", because nothing distinguishes them.

**It does not** cover the event store. `mt_events` and `mt_streams` are outside Marten's row level
security, so the tenant filter on those remains the application's alone.

## Turning it on

Three steps, in this order.

### 1. Create the role and hand it the tables

```bash
psql -d barako -v app_role=barako_app -v app_password='...' \
     -f migrations/tenancy/001-app-role.sql
```

Run it as a superuser, once. It creates a `NOSUPERUSER` login role and makes it the owner of the
schema and of every table, sequence and function. Ownership rather than grants, because Marten
issues DDL and a non-owner cannot.

It is idempotent, and rerunning it re-asserts `NOSUPERUSER` on a role that has since been granted
superuser.

### 2. Point the application at that role

```
ConnectionStrings__DefaultConnection=Host=postgres;Database=barako;Username=barako_app;Password=...
```

**This is the step that matters, and it is why the setting alone does nothing.** A Postgres
superuser bypasses row level security completely, whatever is on the table. Every compose file and
k8s config in this repository connects as `postgres`, which is a superuser. Turning the setting on
while still connecting as one would create policies on every conjoined table, have them appear in
`pg_policies`, satisfy any check that asks whether row level security is enabled, and enforce
nothing.

The application refuses to start in that configuration rather than run while appearing to be
protected. If you see it refuse, the connection string is the thing to fix, not the setting.

### 3. Turn it on

```json
{ "Tenancy": { "DatabaseEnforcement": true } }
```

Marten creates the policies as part of its schema management on the next start.

## Checking it worked

```sql
SELECT rolname, rolsuper FROM pg_roles WHERE rolname = 'barako_app';
SELECT tablename, rowsecurity FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;
SELECT tablename, policyname FROM pg_policies WHERE schemaname = 'public';
```

`rolsuper` must be false. Every `mt_doc_` table for a conjoined document type should show
`rowsecurity` true and carry a `marten_tenant_isolation` policy.

A check that only reads `pg_policies` is not enough on its own. Policies exist and do nothing when
the connection is a superuser, which is the failure this whole page is arranged around.

## Turning it off

Set `Tenancy:DatabaseEnforcement` to false and restart. The policies stay on the tables and stop
being consulted, because the application is still connecting as a role they bind; nothing needs
undoing to get back to the application-only behaviour. Point the connection string back at a
superuser only if you also want the policies bypassed, which is a strange state to be in
deliberately.

## Background work

The workflow runner, the job queue worker, the workflow run and webhook delivery retention sweeps,
and the startup pass that encrypts stored workflow credentials work across every tenant. With
enforcement off each of them starts from one query that reads across tenants: the job queue reads
every tenant's rows in it, and the others use it to list the partitions that hold rows, then work
through those one at a time. With it on they cannot, because that query runs outside any tenant and
the policy holds it to one, so they list tenants from the registry instead: every `Tenant`, active
or not, plus the default partition. Two consequences:

- Each pass costs one query per registered tenant. The workflow runner polls every five seconds
  when idle and starts again from the registry for every attempt it claims. The job queue probes
  each queue every `Jobs:StorageProbeSeconds` (60 by default) and pays the same again for every
  batch it claims while draining. The credential pass runs once per start, so it pays it once.
- Rows in a partition with no `Tenant` document are not reached. Register the tenant to bring them
  back into scope.

A partition with no `Tenant` document is not only a slug somebody typed into an `X-Tenant` header.
It is also the ordinary shape of a single-tenant deployment reached over a subdomain: the slug is
taken from the host name (`cms` for `cms.example.com`), nobody ever created a `Tenant` for it, and
everything that deployment stores is in that partition. Background work does not reach it while
enforcement is on.

### The credential pass

At every start the API encrypts the credential parameters on a workflow's own actions that were
stored before 4.2. With enforcement on it does that for registered tenants and the default
partition, and for nothing else:

- A workflow in a partition with no `Tenant` document is not read, and its credential parameters
  stay as they were stored.
- Credentials inside a Conditional action's `ThenActions` or `ElseActions` are not covered by this
  pass, with enforcement on or off.

The application role cannot count rows the policy hides from it, so the pass cannot warn about a
partition it does not see. It logs one line at Information level on every start with enforcement on,
whether or not anything changed:

```
Read 12 stored workflow(s) in 4 of 4 partition(s) looking for credential parameters to encrypt
```

If that reads fewer workflows than the deployment has, some are in a partition the registry does not
list. To find them, run this as a superuser (the policy does not bind one), in the database the API
uses. It lists each workflow partition that has no `Tenant` document:

```sql
select w.tenant_id, count(*) as workflows
from public.mt_doc_workflowdefinition w
where w.tenant_id <> '*DEFAULT*'
  and not exists (
    select 1 from public.mt_doc_tenants t where t.data ->> 'Slug' = w.tenant_id
  )
group by w.tenant_id
order by w.tenant_id;
```

For each row, create a tenant with that slug (`POST /api/tenants`), then restart the API. The pass
visits it on that start. If a partition fails (a row that cannot be read, for one), the pass logs an
error naming the tenant, leaves that partition's remaining workflows as they are, and goes on to the
next one; the line above then shows fewer partitions read than listed.

## Two things to know before you deploy it

**Connection footprint.** The tenant is set as a session variable, so the connection has to carry it
for the length of the work rather than being handed back to the pool between statements. Measure
this on your own load before turning it on somewhere busy: no pool size is configured anywhere in
this repository, so Npgsql's default of 100 per pool applies and there is no ceiling to compare
against.

**Transaction-pooling PgBouncer breaks it silently.** A session-level `SET` does not survive a
connection being handed to another client mid-transaction-pool. Nothing in this repository uses
PgBouncer today. If you put one in front, run it in session pooling mode or leave this off.
