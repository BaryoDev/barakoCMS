# Upgrading from 5.9 to 6.0

6.0 is a major release. This page lists every change you have to act on, each with the setting or
code to change and the pull request or issue that made it. Read it top to bottom once before you
start, then use the headings as a checklist.

## Which version you can upgrade from

Upgrade to 6.0 from **5.9.0**, the start CI proves, with the released 5.9.0 image pinned by digest.
Three files follow it, all under `migrations/6.0.0/`: the rollup marks table, the rollup sweep
state table and an index on `createdAt` for rollup windows. `db-migrate` applies them in the
upgrade below. 5.10.0 ships no file, so a 5.10.0 database has the same schema as a 5.9.0 one. CI
does not start from 5.10.0 or any other 5.x release. For any other 5.x release, run
`FROM_VERSION=<yours> scripts/upgrade-check.sh --list` from a checkout of the 6.0 release first. A
list of only the 6.0.0 files means the schema is the one 5.9.0 has. A file from an earlier folder
applies too, before the 6.0.0 ones.

A database from 4.x or from an earlier 5.x release still runs through the plan in
`migrations/`, and the `Upgrade from 4.x` job still starts from 4.9.0 and 4.1.0. That job proves
the files apply and roll back. It does not seed the data below, and the 6.0 steps on this page
(the app role, the start checks) are not part of what it exercises. Go to 5.9.0 first and come
back here as a 5.9 install, with [upgrading-to-5.0.md](upgrading-to-5.0.md) for a 4.x database and
[upgrading-to-4.0.md](upgrading-to-4.0.md) for a 3.x one.

The `Upgrade from 4.x` job (`scripts/upgrade-check.sh`) runs this on a real database:

1. It builds the working tree, starts Postgres, checks the image's build commit matches the
   `v5.9.0` tag, and starts the 5.9.0 image.
2. It seeds data through the 5.9 API: a content type whose entries are event sourced and one whose
   entries are plain documents, with entries in each (the event sourced one created, published and
   edited, so its stream holds three events), a workflow that sets a field on create, a role, a
   group, a redirect, a connector, a station, an API key, and a second tenant with CDN purge
   settings and an entry of its own. It reads each record back, comparing its fields to what it
   sent.
3. It stops 5.9.0 and checks `db-assert` passes on the core host and on the Suite host, since there
   is no migration to apply.
4. It boots the Suite in Production, signs in, and reads every seeded record back through the API.
5. It appends an event to the stream that already existed and waits for the projection daemon to
   resume.
6. It creates a new entry to prove the stored workflow still runs, and reads the second tenant
   back.
7. It stops the new build, boots 5.9.0 again against the same database, and reads every seeded
   record back from 5.9.0.

If a step here stops being true, that job goes red. What it cannot seed is listed under
"What CI does not seed" at the end.

## Before you start

Take a backup, and check you can restore it ([backup-and-restore.md](backup-and-restore.md)). The
remedy for a failed upgrade is a restore.

Then work through the next five sections: "Settings to change before you upgrade", "Limits and
checks that start differently", "The upgrade", "Going back to 5.9" and "Code that stops
compiling". The first two are things 6.0 refuses to start without, or starts differently. The
third and fourth are the steps and the way back. The fifth is code. "What CI does not seed"
follows them and says what the upgrade check cannot cover.

## Settings to change before you upgrade

**Run the app role job before `db-migrate` or `db-assert`, and set `DB_APP_PASSWORD`
([#1447](https://github.com/BaryoDev/barakoCMS/pull/1447)).** `docker-compose.prod.yml` and
`quickstart/docker-compose.yml` refuse to start without `DB_APP_PASSWORD`, because the API
connects as the app role `DB_APP_USER` (default `barako_app`) and no longer as the database owner.
Pick a random value that differs from `DB_PASSWORD`. With `docker-compose.secrets.yml`, add
`secrets/db_app_password` and change `secrets/db_connection_string` to the app role's user and
password. A deployment that sets `DATABASE_URL` points it at the app role too. On a 5.x volume the
role does not exist yet, so the order matters (the commands are in "The upgrade" below). The job
refuses an owner password that is an example value or equals `DB_APP_PASSWORD`, so set a real
`DB_PASSWORD` first. `docker-compose.hub.yml` runs the same job and needs `DB_APP_PASSWORD` too
([#1450](https://github.com/BaryoDev/barakoCMS/pull/1450)). For a Kubernetes install,
`k8s/04-role-job.yaml` creates the role and `k8s/02-secret.yaml` connects as it.

**A Production host that connects as a superuser needs `Database:AllowSuperuser=true`
([#1450](https://github.com/BaryoDev/barakoCMS/pull/1450)).** The API stops at start when its role
is a superuser, has `BYPASSRLS`, or belongs to `rds_superuser`, `azure_pg_admin` or
`cloudsqlsuperuser`. Connect as a role without those, which `scripts/db-app-role.sql` creates. To
run as a superuser on purpose, set `Database__AllowSuperuser` to `true`. In `BarakoCMS.Testing`, a
`BarakoTestHost` run as Production against a superuser container sets
`o.Settings["Database:AllowSuperuser"] = "true"`. A host you build by hand with `AddBarakoCMS` has
to register an `IHostEnvironment` to count as Development, since every start guard reads
Development from it and a host with none counts as Production.

**List your host names in `AllowedHosts`, and choose a first admin password that is not an example
([#1433](https://github.com/BaryoDev/barakoCMS/pull/1433)).** Outside Development, `AllowedHosts`
set to `*`, `0.0.0.0`, `[::]` or empty stops start. Set it to the names the API answers to, for
example `api.example.com`, separated by `;`. To accept any host on purpose set
`Security__AllowAnyHost` to `true`, which logs one warning. A Kubernetes `httpGet` probe sends the
pod address as `Host`, so add a `Host` header to the probe once you list real hosts (the shipped
`k8s/05-deployment.yaml` does). `InitialAdmin:Password`, when set, must be at least 12 characters,
must not equal the user name or email, and must not be an example value such as
`changeme-in-production`. Left unset, the first boot still generates one. In tests,
`BarakoTestHost` defaults `AllowedHosts` to `localhost;127.0.0.1`.

**Replace an example JWT key or database password
([#1442](https://github.com/BaryoDev/barakoCMS/pull/1442)).** Outside Development the app refuses
to start on a `JWT:Key` copied from the compose files, the quickstart `.env.example` or the docs,
and on an example database password (`postgres`, `change-me-to-a-strong-password`,
`changeme-in-production`, `local-only-password`, `password`, or anything starting `REPLACE_THIS`)
in `ConnectionStrings:DefaultConnection`, `DATABASE_URL` or `PGPASSWORD`. The error names the
setting and never repeats the value.

**Pin `BARAKO_TAG` in `docker-compose.hub.yml` and `quickstart/docker-compose.yml`
([#1442](https://github.com/BaryoDev/barakoCMS/pull/1442)).** It is required now, for example
`BARAKO_TAG=6.0.0` in `.env`.

**Move credentialed connectors to https
([#1475](https://github.com/BaryoDev/barakoCMS/pull/1475)).** A connector with a bearer token,
Basic auth, an API key header or OAuth2 client credentials no longer sends them over plain `http`,
except to a host and port listed in `Webhooks:AllowedHosts`. Loopback is not exempt. The probe,
sends, fetches and the token request to `TokenUrl` are refused before any secret is read. Change
each credentialed connector's `baseUrl` and `TokenUrl` to `https`, or list an internal receiver
you run in `Webhooks__AllowedHosts__0`. A connector with `auth` set to `None` is unchanged.

**Check that a saved query or request does not have a slug ending in a line feed
([#1461](https://github.com/BaryoDev/barakoCMS/pull/1461)).** A name, slug, handle, locale, email
or similar value that ends with a line feed is refused now, where it used to be accepted. A saved
query or request definition with such a slug can no longer be opened by that slug: rename or
remove it before upgrading. An entry whose slug or email field holds such a value keeps it on an
edit that sends it back unchanged. The API contract moves from 16 to 17, ExternalAuth's from 1 to
2 and Files' from 2 to 3. barakoBrew 1.14.0 accepts 17, so deploy it before or with 6.0.

**Send a timestamp with a plain HMAC inbound webhook
([#1385](https://github.com/BaryoDev/barakoCMS/issues/1385)).** This lands with #1385 and is not
in the code yet. 5.8.0 deprecated an `Hmac` inbound webhook with no `TimestampHeader`, and 6.0
refuses it. Name a `TimestampHeader`, so the sender signs `{timestamp}.{body}` inside the five
minute window. A sender that cannot send a timestamp needs a receiver that sets
`AllowBodyOnlySignature` to `true` on the `InboundWebhookRequest` it builds, in code, which keeps
the body-only check. There is no connector setting for it. Svix is unchanged.

## Limits and checks that start differently

**Kestrel limits are tighter ([#1443](https://github.com/BaryoDev/barakoCMS/pull/1443)).** The
request headers timeout is 15 seconds (the framework default is 30). `MaxConcurrentConnections` is
10000 and `MaxConcurrentUpgradedConnections` is 1000 (the framework default is unlimited). A node
that held more raises `Kestrel:Limits:MaxConcurrentConnections` or
`Kestrel:Limits:MaxConcurrentUpgradedConnections`, for example for more than 1000 realtime
WebSocket clients at once. The host now binds `Kestrel:Limits:*` over these values, so an existing
`Kestrel:Limits` entry starts to apply. `Kestrel:Limits:MaxRequestBodySize` is refused at start,
with a message naming `RequestLimits:MaxBodyBytes`, the one body size key. A zero or negative
connection cap, headers timeout, keep-alive timeout or `RequestLimits:MaxBodyBytes` is refused at
start with a message naming the key. The caps are per process, so behind Caddy they count the
proxy's connections, not users. The AI embedding client now has a 30 second timeout and a 1 MB
response cap, and a webhook response body read has a deadline.

**The shipped compose files run read-only and non-root
([#1442](https://github.com/BaryoDev/barakoCMS/pull/1442)).** All four compose files set a
read-only root file system, drop every Linux capability (Caddy adds `NET_BIND_SERVICE`, Postgres
adds `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETGID` and `SETUID`, the backup container adds `SETUID`
and `SETGID`), set `no-new-privileges`, and set a memory limit and a process limit. The API
runs as uid 1654 with `/tmp` and `/home/app` as tmpfs mounts, and `k8s/05-deployment.yaml` sets
the matching `securityContext`. Postgres, Caddy and the backup container still start as root and
carry the label `org.barakocms.hardening.root` with the reason. The limits are variables:
`API_MEM_LIMIT`, `API_PIDS_LIMIT`, `API_TMP_SIZE`, `DB_MEM_LIMIT`, `DB_PIDS_LIMIT`,
`EXTENSIONS_MEM_LIMIT`, `EXTENSIONS_PIDS_LIMIT`, `BACKUP_MEM_LIMIT`, `BACKUP_PIDS_LIMIT`,
`PROXY_MEM_LIMIT` and `PROXY_PIDS_LIMIT`. A value of `0` or `-1` removes a limit, so run
`bash scripts/check-compose-hardening.sh --env-file .env docker-compose.prod.yml` before
`docker compose up`; it fails on a missing or non-positive limit. Raise `API_MEM_LIMIT` before a
large import. The backup container writes under `/tmp`, and the optional off-host S3 copy installs
`curl` at start and needs `BACKUP_READ_ONLY=false`. A copy of a compose file you edited has to
carry the same settings, or you lose them.

**The API port is published on `127.0.0.1` only
([#1442](https://github.com/BaryoDev/barakoCMS/pull/1442)).** `docker-compose.yml` and
`quickstart/docker-compose.yml` publish `127.0.0.1:5005:8080`. In the quickstart set
`API_BIND=0.0.0.0` to reach it from another machine, and put TLS in front first.

**The container health check is the binary
([#1442](https://github.com/BaryoDev/barakoCMS/pull/1442)).** `dotnet barakoCMS.dll --health-probe [path]`
asks `http://127.0.0.1:<port>` (the port from `ASPNETCORE_URLS`, default 8080), sends the first
non-wildcard `AllowedHosts` entry as `Host`, defaults to `/health/live`, and exits 0 on a 2xx and
1 otherwise. `BARAKO_HEALTH_PROBE_TIMEOUT_SECONDS` changes its 3 second timeout.

**Response headers are stricter ([#1433](https://github.com/BaryoDev/barakoCMS/pull/1433)).**
Outside Development the Content-Security-Policy adds `frame-ancestors 'none'`, `base-uri 'self'`,
`form-action 'self'` and `object-src 'none'`. Every response adds `Permissions-Policy` (camera,
microphone, geolocation, accelerometer, gyroscope, magnetometer, payment and usb off),
`Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Resource-Policy: same-origin`. File
downloads (`/api/files/{id}` and `/api/public/files/{id}`) send `Cross-Origin-Resource-Policy:
cross-origin`, so a front end on another origin still loads stored images and media. Caddy no
longer sends `Server` or `Via` headers ([#1445](https://github.com/BaryoDev/barakoCMS/pull/1445)).
A front end that embedded the API in a frame, or read a response cross-origin outside the file
routes, needs a check.

**`db-migrate` creates the tables a release adds
([#1471](https://github.com/BaryoDev/barakoCMS/pull/1471)).** After the ledger run it creates the
tables, functions and indexes the loaded modules declare that are missing entirely, so `db-assert`
passes before the API starts. A table that exists is never altered. `db-migrate --status` lists
each missing table as `pending    create <name>`.

**Two metrics appear sooner ([#1414](https://github.com/BaryoDev/barakoCMS/pull/1414)).**
`barakocms_api_key_refusals_total` shows at 0 from the first request and
`barakocms_auth_mail_dropped_total` from start, instead of at the first refusal or drop.

**Secrets can be files ([#1445](https://github.com/BaryoDev/barakoCMS/pull/1445)).** The API reads
`BARAKO_SECRETS_DIR` (default `/run/secrets`) as configuration, one file per key with `__` as the
section separator, and a file wins over an environment value. If a directory by that name exists
on your host with files in it, they now apply. A custom host opts in with
`builder.Configuration.AddSecretFilesFromEnvironment()` right after `WebApplication.CreateBuilder`.

Options added in 6.0 that change nothing until you set them: `Content:RequireContentType`, which
refuses an entry whose content type has no definition
([#1472](https://github.com/BaryoDev/barakoCMS/pull/1472)), and the console card parts in
`ConsoleCardContent` ([#1473](https://github.com/BaryoDev/barakoCMS/pull/1473)).
[configuration.md](configuration.md) lists every key with its default and what a bad value does
([#1462](https://github.com/BaryoDev/barakoCMS/pull/1462)).

`TemplateHoles.Names` and `TemplateHoles.FirstNonPublic` in `BarakoCMS.Abstractions` throw
`TemplateScanException` for a template over the scan cap or when the scan times out. A module that
calls them catches it.

## The upgrade

With 5.9 stopped, run the app role job, then `db-migrate`, then `db-assert`, then start 6.0. With
compose, from the directory holding your compose file and `.env`, after setting `BARAKO_TAG` and
`DB_APP_PASSWORD`:

```bash
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml stop app
docker compose -f docker-compose.prod.yml run --rm postgres-extensions
echo "job exit code: $?"
```

Carry on only when the exit code is 0. On a 5.x volume the app role does not exist until the job
has run, so `db-migrate` and `db-assert` cannot sign in before it, and once it exists they fail as
a non-owner until the job has run. Then:

```bash
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-migrate --status
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-migrate
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-assert
docker compose -f docker-compose.prod.yml up -d
```

From 5.9.0 `--status` lists the three 6.0.0 files. `db-migrate` applies them, records them and
creates any table the 6.0 modules declare that is missing. The index file builds `CONCURRENTLY`,
outside a transaction, and waits for every transaction older than itself, which with 5.9 stopped is
none. A build that is interrupted leaves an invalid index and `db-migrate` exits 1;
[migrations.md](migrations.md#an-index-build-that-was-interrupted) has the fix. `db-assert` exits 0
when the schema is what 6.0 declares. [deploy-in-production.md](deploy-in-production.md#upgrading) has the
same steps with the reasons, and [migrations.md](migrations.md) explains the ledger.

With the 6.0 `docker-compose.prod.yml` in place, that last `up -d` also recreates the `proxy` network
once, to add the range Docker hands out
automatic addresses from (`PROXY_IP_RANGE`, default `10.87.51.8/29`), so the app can no longer take
Caddy's address on a fresh start. It restarts `caddy` and `app` briefly and touches no data. A
container you attached to `proxy` by hand stops the old network being removed, so disconnect it
first. If you set `PROXY_SUBNET`, also set `PROXY_IP_RANGE` to a part of it that leaves
`PROXY_ADDRESS` out ([deploy-in-production.md](deploy-in-production.md#client-ips-behind-caddy)).

A new migration does not need a superuser from 6.0. Run ad hoc SQL as the app role, never as the
owner ([#1449](https://github.com/BaryoDev/barakoCMS/pull/1449)). The hand-run migrations that
still need one run with `PGOPTIONS='-c search_path=pg_catalog,pg_temp'`; `scripts/upgrade-check.sh
--list` prints them that way.

A restore changes too ([#1449](https://github.com/BaryoDev/barakoCMS/pull/1449)). It runs the
owner job first, drops the `CREATE EXTENSION` and `COMMENT ON EXTENSION` lines from the stream, and
loads the dump as the app role, so the app role owns what it creates. `scripts/restore-db.sh` does
this, and [backup-and-restore.md](backup-and-restore.md) has the commands.

To print the files a database needs, from a checkout of the 6.0 release, with `FROM_VERSION` set to
the release the database is on:

```bash
FROM_VERSION=5.8.0 scripts/upgrade-check.sh --list
```

From 5.9.0 the list is the three 6.0.0 files and their rollback files, and so it is from 5.10.0.
`--list` builds nothing and starts nothing.

## Going back to 5.9

With 6.0 stopped, run the three 6.0.0 rollback files newest first, in the order `--list` prints
them, then remove their rows with `db-migrate --forget core/6.0.0/<name>` from the 6.0 image
([migrations.md](migrations.md#rolling-back)). The two rollup files leave their tables in place,
since 5.9 never reads them, and the index file drops the `createdAt` index. CI proves that 5.9.0
boots again after those files and reads every seeded record against a database the 6.0 build has
used, connecting as the database owner on both sides. It does not prove the way back after the app role job has run,
because the job changes object ownership and the check does not run it. After that job, the sure
way back is the restore from the backup you took first.

## Code that stops compiling

The members below were marked `[Obsolete]` with "removal planned for barakoCMS 6.0" in 5.x and
are gone in 6.0, once #1383 and #1384 land. Each line says what to move to. The groups follow the two issues that remove
them: [#1383](https://github.com/BaryoDev/barakoCMS/issues/1383) for the members that name a
Marten type and [#1384](https://github.com/BaryoDev/barakoCMS/issues/1384) for the rest.

### Module contract, no Marten types (#1383)

This section lands with #1383 and is not in the code yet.

- `IBarakoModule.SeedAsync(IDocumentSession, IServiceProvider, CancellationToken)`: override
  `SeedAsync(IModuleSeedContext, CancellationToken)`. `ModuleSeedContexts.Over`, which forwarded
  the old form, is removed.
- `IBarakoModule.ConfigureMarten(StoreOptions)`: override `ConfigureSchema(IModuleSchema)`. The
  host no longer calls the old member or warns about it.
- `CapabilityDefaults.GrantAsync(IDocumentSession, ...)` and
  `ModuleCapabilities.GrantAsync(IDocumentSession, ...)`: call the overloads that take the seed
  context.
- `IContentLifecycleHook.Session` on `ContentLifecycleContext`, and the Marten-typed
  `OnBeforeSaveAsync(ContentLifecycleContext, CancellationToken)`: implement
  `BeforeSaveAsync(IContentHookContext, CancellationToken)` and read through
  `IContentHookContext.Reader`. Take a lock with `LockAsync`, load with `LoadAsync<T>` and store
  with `Store<T>` (added in 5.9.0).
- `ModuleContract.Version` and `ModuleContract.MinimumSupported` move past 1. A module that
  declares contract version 1 is refused at load. Rebuild it against the 6.0 `BarakoCMS.Abstractions`
  package.

A module that builds on the contract package alone also needs `[assembly: BarakoModuleAssembly]`
(`barakoCMS.Modules`). 5.9.0 still scanned a library without it and logged a warning naming it;
6.0 stops scanning it, so the module is not loaded
([#1402](https://github.com/BaryoDev/barakoCMS/issues/1402)). This lands with #1402 and is not in
the code yet.

### Events, content and workflows (#1384)

This section lands with #1384 and is not in the code yet.

- The seven content event constructors without `OccurredAt` (`ContentCreated`, `ContentUpdated`,
  `ContentStatusChanged`, `ContentScheduled`, `ContentSensitivityChanged`, `ContentTransitioned`
  and `ContentFieldSensitivityChanged`): pass `OccurredAt`, so a rebuild reproduces the timestamps.
  Events already stored keep their stored timestamps.
- `Content.Apply(ContentCreated)`, `Apply(ContentUpdated)` and `Apply(ContentStatusChanged)`:
  call the overloads that take a `DateTime`.
- `IContentWriter.Create` and `Append`, and `ContentWriter.Create` and `Append`: call
  `CreateAsync` and `AppendAsync`.
- `IWorkflowAction.ExecuteAsync`: implement `RunAsync`.
- `IWorkflowEngine`: register a trigger and fire it through `IWorkflowTriggers`.
- The `ValidateAsync` overload without `existing` on `IContentValidatorService`: pass `existing`.
  A compiled module that calls the two argument form, such as `BarakoCMS.Import` 4.0.0, gets a
  `MissingMethodException`, which is why the contract version move above refuses it at load.

### Tenant, roles, files and middleware (#1384)

This section lands with #1384 and is not in the code yet.

- `Tenant.LogoUrl`, `About`, `Location`, `LocationUrl`, `SocialHandle`, `Email` and `ContactUrl`:
  read the field of the same name on the tenant's site entry. `Tenant.Branding`: put a site's
  colours and theme in the tenant's site entry. `Tenant.IsActive`: read `Tenant.Status`. A stored
  tenant that still carries those fields loads.
- `SystemRoles.HRRoleId` and `DataSeeder.HRRoleId`: use `DemoHrRoleId`. The role itself stays.
- `LegacyRoles` in `AiCapabilities`, `DiagnosticsCapabilities`, `FeatureFlagCapabilities`,
  `FileCapabilities`, `ImportCapabilities` and `PortabilityCapabilities`: read the capability
  defaults instead.
- Files: `ImageSharpResizer` becomes `SkiaImageResizer`, and the `ImageVariants` constructor
  without a `FileStorageResolver` is replaced by the one that takes it.
- Portability: `GET /api/portability/export` and `POST /api/portability/import`, with
  `PortabilityBundle`, `ContentRecord`, `ImportRequest` and `ImportReport`, are removed. Use
  `GET /api/config/export` and `POST /api/config/plan` and `/api/config/apply`, which take a
  Portability bundle as a document.
- `PerformanceMiddleware`: remove it from a pipeline you built by hand. Nothing in the host uses it.
- `DeliveryCache.PurgeTags`: remove the call. Purge on publish computes its tags with
  `DeliveryTagSet.ForCommitAsync`.
- The `/api/schemas` alias of `GET /api/content-types` was deprecated until 6.0. Call
  `/api/content-types`.

### Constructors removed from core services

Merged in [#1418](https://github.com/BaryoDev/barakoCMS/pull/1418). Each service now has one public
constructor, and the dependencies a host does not always register default to null on it. Call the
one named after each signature:

- `ContentWriter(session, policy)`, `(session, policy, configuration)` and
  `(session, policy, configuration, batch)`: call
  `ContentWriter(session, policy, configuration, batch = null, logger = null)`.
- `ContentValidatorService(session)` and `(session, files)`: call
  `ContentValidatorService(session, files = null, dataSources = null)`.
- `OtpService(session, email, config, logger)`: call
  `OtpService(session, email, config, logger, mail = null)`.
- `TemplateVariableExtractor(session)`: call
  `TemplateVariableExtractor(session, permissions, sensitivity, configuration)`, with null for what
  you do not have.
- `WorkflowSchemaValidator(pluginRegistry, session)` and `(pluginRegistry, session, configuration)`:
  resolve `IWorkflowSchemaValidator`. The only constructor left is internal.
- `TenantSessionFactory(store, tenant)`: call `TenantSessionFactory(store, tenant, batch = null)`.
- `TenantDomainMap(entries)` and `(entries, activeSlugs)`: call
  `TenantDomainMap(entries, activeSlugs, paused)`, with `[]` for what you do not have.
- `SmtpEmailService(options, settings)`, `(options, settings, logger)` and the four argument
  public form: call `SmtpEmailService(options, settings, logger = null, tenantSettings = null)`.
  `BarakoCMS.Email.Smtp` goes to 5.0.0 for this.

Two more constructor changes came with the signing key and metrics work:

- `TokenIssuer` takes an `IJwtMinter` as its last parameter, and the three argument
  constructor is removed. `MfaService` takes an `IJwtMinter` as a new last parameter.
  `MfaChallengeToken.Create`, `StepUpToken.Create`, `PreviewToken.Create` and `ViewerToken.Create`
  take an `IJwtMinter` first. Code that called `JwtBearer.CreateToken` itself injects `IJwtMinter`
  and calls `Mint(audience, expiresAt, claims)`
  ([#1412](https://github.com/BaryoDev/barakoCMS/pull/1412)). `AddBarakoCMS` registers it and it
  reads `JWT:Key` from the host's own configuration.
- `AuthMailQueue(IServiceScopeFactory, ILogger<AuthMailQueue>)` and the `ScheduledContentService`
  constructors without an `IMetricFactory` are removed. Both take a trailing `IMetricFactory`, so a
  host that builds them by hand passes its own. `WorkflowMetrics`, `JobMetrics`,
  `ApiKeyRefusals`, the auth mail drop count and the projection lag gauge are no longer static. A
  test that builds a host itself can call `ServiceCollectionExtensions.UseIsolatedMetricsRegistry()`
  ([#1414](https://github.com/BaryoDev/barakoCMS/pull/1414)).

### BarakoCMS.Accounting is removed

([#1377](https://github.com/BaryoDev/barakoCMS/pull/1377)) The package, its endpoints under
`/api/accounting`, the capabilities `view_ledger` and `post_journal_entries`, and the
`Accountant` role seeding are gone. Remove the package reference and `Accounting` from
`BarakoCMS:Modules:Enabled`, since the host refuses to start on a module name it does not know.
`INumberAllocator` stays in core, and so does the `accounting.je` counter row, which core no longer
reads or writes. `NumberAllocators` is removed. Existing roles keep the two capabilities they
hold. They are unknown capabilities now, refused on a role update only when
`Roles:RefuseUnknownCapabilities` is on.

Nothing in core drops the tables. To drop them, take a backup and run
`migrations/removed/drop/accounting.sql`, which drops the four `mt_doc_accounting_*` tables and
deletes the `Accounting` ledger rows from `barako_migrations`. Every row in them is lost. Accounts
and journal entries stored as content entries stay in the content tables. The file header gives
the `psql` command.

### Test hosts

`BarakoTestHost` defaults `AllowedHosts` to `localhost;127.0.0.1`, and a host run as Production
needs `Database:AllowSuperuser` against a superuser container (both above). A custom test host
that reads the signing key reads it from its own configuration
([#1419](https://github.com/BaryoDev/barakoCMS/pull/1419)): MFA challenge, step-up, preview and
viewer tokens validate with the same key bytes they are signed with, so a `JWT:Key` holding a
character outside ASCII now works. Startup logs a warning for one and does not refuse it.

## What CI does not seed

The check seeds through the 5.9 API, so it cannot write what the host writes itself, or what
needs setup outside the API:

- Rows the host writes: refresh and revoked tokens, number counters, the outbox, pending builds,
  pending CDN purges and idempotency records.
- Data of the modules, which the check does not write to.
- The default tenant's CDN purge settings, which are host configuration. A second tenant carries
  them in the check instead.

`db-assert` still checks the schema for a database that holds them, and no migration touches their
rows.
