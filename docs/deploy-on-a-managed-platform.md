# Deploying barakoCMS on a managed platform

barakoCMS is one container and one PostgreSQL database. Any platform that runs a container and
offers managed Postgres can host it: Azure App Service with Azure Database for PostgreSQL, AWS
Fargate with RDS, or Google Cloud Run with Cloud SQL. You do not manage a server on this path.

BaryoVM is not used here, and neither is `docker-compose.prod.yml`. The platform's own pipeline pulls
the image and runs it. The VM procedure is [deploy-in-production.md](deploy-in-production.md).

Every claim below about the image and the application comes from this repository's code or from
running the published `4.1.0` image. Claims about a platform's own behaviour are marked
**(platform docs, not tested here)**: check them against your provider before relying on them.

## The container

| | |
| --- | --- |
| Image | `ghcr.io/baryodev/barako-cms:<version>` |
| Pull | Anonymous. No registry credentials needed. |
| Architectures | `linux/amd64` and `linux/arm64` (checked on `4.1.0` and `latest`) |
| Port | `8080`, plain HTTP. The image sets `ASPNETCORE_URLS=http://+:8080`. |
| User | Non-root (`app`, uid 1654) |
| Entrypoint | `dotnet BarakoCMS.Suite.dll` |

`barako-cms` is built from `Dockerfile.suite` and carries every module. `barako-cms-decaf` is built
from `Dockerfile` and carries the core only. Both are published by `.github/workflows/release.yml`,
which refuses to publish a version tag unless both architectures are in its manifest.

Pin a version. Do not deploy `latest`: a platform that restarts one instance can then run a
different build from its neighbour.

TLS is the platform's job. The container never terminates TLS; the platform's load balancer or
front end does, and forwards HTTP to port 8080.

## Environment variables

ASP.NET Core reads `__` in an environment variable name as a section separator, so
`JWT__Key` is the setting `JWT:Key`.

### Required

| Variable | What happens without it |
| --- | --- |
| `DATABASE_URL` or `ConnectionStrings__DefaultConnection` | Startup throws `No database connection string. Set ConnectionStrings:DefaultConnection or the DATABASE_URL environment variable.` |
| `JWT__Key` | Startup throws `JWT:Key must be configured and at least 32 characters (256 bits) for security.` A value starting `REPLACE_THIS` is also refused. |
| `InitialAdmin__Password` | The first boot generates a password and prints it once to the log. Set it so the first sign-in does not depend on reading a log line. |

Both errors above were produced by the `4.1.0` image. In those runs the exception was logged but the
process had not exited 60 seconds later, so do not count on a crash loop to tell you. Kestrel never
binds, so the platform's health check fails, and the log says why.

`DATABASE_URL` takes the `postgres://user:password@host:port/database` form managed providers hand
out. `sslmode` in the query string is honoured, and SSL defaults to `Require` when it is absent
(`ResolveConnectionString` in `barakoCMS/Extensions/ServiceCollectionExtensions.cs`). Percent-encode
special characters in the password. If both are set, `DATABASE_URL` wins.

### Set these too

| Variable | Why |
| --- | --- |
| `CORS__AllowedOrigins` | Comma-separated origins of the console and your site. The default is localhost only, so a browser on any real origin fails CORS. |
| `InitialAdmin__Username` | Defaults to `admin`. |
| `App__BaseUrl` | The public origin the API puts in links it hands out. |
| `AllowedHosts` | The host names the API answers to. Defaults to `*`. |
| `Secrets__Key` | Protects stored webhook and connector secrets. Falls back to `JWT__Key` when unset, which couples the two, see [SECURITY.md](../SECURITY.md). |
| `BarakoCMS__Modules__Enabled` | Which modules run, comma-separated. Unset, every module in the image runs and the log says so. |

`ASPNETCORE_ENVIRONMENT` is not set in the image, so the app runs as `Production`. That is what you
want: Production applies schema with `CreateOnly` (see below) and keeps Swagger off unless
`Swagger__Enabled=true`.

`docker-compose.prod.yml` and `quickstart/docker-compose.yml` show every other setting in context.
Keep secrets in the platform's secret store (Key Vault, Secrets Manager, Secret Manager) and inject
them as environment variables.

### Behind the platform's load balancer

Rate limiting and the audit log key on the client IP. Behind a load balancer the container sees the
balancer's address unless forwarded headers are on:

```
ForwardedHeaders__Enabled=true
ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
```

With `Enabled=true` and no `KnownProxies` or `KnownNetworks`, startup refuses, because a forwarded
header from an unnamed peer is whatever the client wrote. Name the network your balancer connects
from. On a platform where you cannot know that range, leave forwarded headers off and accept that
every request appears to come from the balancer, which puts all clients in one rate limit bucket
(100 requests a minute, 5 sign-in attempts per 15 minutes).

## The database

Any PostgreSQL the platform manages works. The application needs one database and a role that can
create tables in it: on a fresh database the first boot creates the whole schema.

- **Azure Database for PostgreSQL flexible server.** Requires SSL, which matches the `DATABASE_URL`
  default. **(platform docs, not tested here)**
- **Amazon RDS for PostgreSQL.** Put the task and the instance in the same VPC and allow 5432 from
  the task's security group. **(platform docs, not tested here)**
- **Cloud SQL for PostgreSQL.** Cloud Run can reach it over a private IP through a VPC connector or
  through the Cloud SQL connection, which exposes a Unix socket. For the socket, use
  `ConnectionStrings__DefaultConnection=Host=/cloudsql/<instance-connection-name>;Database=...;Username=...;Password=...`
  rather than `DATABASE_URL`, since a URL cannot carry a socket path. **(platform docs, not tested here)**

Backups are the managed database's point-in-time recovery. `db-backup` in the compose file does not
run on this path. [backup-and-restore.md](backup-and-restore.md) still applies to a restore, and
nothing is written outside the database unless you turn on S3 file storage.

## Health probes

Four anonymous endpoints, mapped in `UseBarakoCMS`:

| Path | Checks | Use it for |
| --- | --- | --- |
| `/health/live` | Process memory only | Liveness |
| `/health/ready` | Database, free disk, memory, startup seed | Readiness, or the load balancer's health check |
| `/health` | Everything above plus workflow projection lag | Dashboards, not probes |
| `/health/build` | Nothing. Returns `{"sha":"<commit>"}` | Proving which build is answering |

Probe bodies are `{"status":"Healthy"}` and carry no check names. The disk check wants 512 MB free
on `/` (`HealthChecks__MinimumFreeDiskMegabytes`), and the memory check fails above 4096 MB private
memory (`HealthChecks__MaxPrivateMemoryMegabytes`). Lower the second if your instance is smaller,
or liveness will never fail.

Boot is slow on purpose. The `barako-cms` image applies the schema and seeds roles and the admin
**before** Kestrel binds, so no probe answers during that window. Give the startup or first health
check a grace period: the Kubernetes manifest allows 150 seconds (`k8s/05-deployment.yaml`).

Do not point liveness at `/health`. It runs the database check, so one database restart fails
liveness on every instance at once and the platform restarts all of them (#281).

## File storage

By default uploads go into PostgreSQL (`PostgresFileStorage`, provider `postgres`). The container
writes nothing it needs to keep to its own filesystem, so a platform's ephemeral disk loses nothing
and no volume is required.

To keep files out of the database, point the Files.S3 module at a bucket. It stays dormant until a
bucket is set:

```
Files__S3__Bucket=my-bucket
Files__S3__Region=eu-west-1
Files__S3__AccessKey=...
Files__S3__SecretKey=...
```

`ServiceUrl` targets an S3-compatible service instead of AWS, and `PublicBaseUrl` serves public
files from the bucket or a CDN rather than through the API. [BarakoCMS.Files.S3/README.md](../BarakoCMS.Files.S3/README.md)
has every option.

The current section is `Modules:Files.S3`, and `Files:S3` is a deprecated root section that still
works and logs a warning. The environment variable for the current section contains a dot
(`Modules__Files.S3__Bucket`). Some platforms reject a dot in a variable name, and Azure App Service
on Linux is reported to replace it **(platform docs, not tested here)**. The `Files__S3__*` form
above avoids the question. Azure Blob and Google Cloud Storage are not supported directly; GCS has
an S3-compatible XML API, which has not been tried with this module.

The Files.S3 client takes an access key and secret. It does not read an IAM role, managed identity
or workload identity, so on AWS create a user or key for it and store the pair as secrets.

## Scaling out

Running more than one instance against the same database is a supported case, and the parts that
would race are already serialised in Postgres:

- **Schema application is safe across instances.** Startup takes a blocking Postgres advisory lock
  (`SchemaApplyLock`, key `8242026003`) around the module schema preflight and Marten's
  `ApplyAllConfiguredChangesToDatabaseAsync`. A second instance starting at the same moment waits,
  then finds nothing to do. The lock is held on its own session, so an instance that dies mid-apply
  releases it when its connection drops. `SchemaApplyLockTests` covers it.
- **Projections run once.** The async daemon runs in `HotCold` mode, which takes an advisory lock per
  projection, so one instance runs each. That is what stops a workflow email or webhook firing once
  per instance.
- **The scheduled-content and workflow-run retention sweeps** use `pg_try_advisory_lock`, so one
  instance does each tick.

Two instances of `4.1.0` started together against an empty database both came up healthy, with one
admin user created. That is one run, not a proof.

What does not coordinate across instances:

- **Rate limits are per instance.** They are held in memory, so with three instances a client gets
  up to three times the limit.
- **A revoked session keeps working for up to 30 seconds** on another instance. A password change,
  an administrator's password reset or turning on MFA revokes the user's tokens, and the instance
  that handled it clears its cached session epoch at once (`RevokeRefreshTokens`). Every other
  instance holds its cached copy for `SessionEpochService.CacheDuration`, 30 seconds.
- **Rolling deploys overlap.** While an old and a new instance run side by side, both can process
  the workflow projection, so an action can fire twice. If that is unacceptable, stop old instances
  before starting new ones and accept the downtime. `k8s/05-deployment.yaml` explains the same
  trade-off.

## Upgrades and migrations

Production applies schema with Marten's `AutoCreate.CreateOnly`. It creates anything missing and
never alters or drops an existing object. A fresh database needs no migration step, and most
upgrades need none either. An upgrade that needs a change to an existing object refuses to start
rather than attempting a live migration, and a module that wants such a change is named in the log
by the module schema preflight.

When a release needs a SQL step, it ships under `migrations/<version>/` and the upgrade notes say so.
Run it against the managed database before rolling out the new image, from anywhere with `psql`
and network access (Cloud Shell, a bastion, a one-off task):

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f migrations/<version>/<file>.sql
```

[upgrading-to-4.0.md](upgrading-to-4.0.md) describes checking the schema without starting the server
with `dotnet barakoCMS.dll db-assert`. That command is in the `barako-cms-decaf` image only. The
`barako-cms` image ignores the argument and starts the server, which was confirmed on `4.1.0`. Run
the check from the decaf image of the same version as a one-off job. That the decaf image answers
`db-assert` as documented is read from `barakoCMS/Program.cs`, not run here.

## Per platform

The sections above apply everywhere. What follows is only what differs.

### Azure App Service with Azure Database for PostgreSQL

- Create a Linux web app from a container, image `ghcr.io/baryodev/barako-cms:<version>`, public
  registry.
- Set `WEBSITES_PORT=8080` so App Service routes to the right port. **(platform docs, not tested here)**
- Put the variables above in App settings, secrets as Key Vault references.
- Set the health check path to `/health/ready`. **(platform docs, not tested here)**
- Scale out in the App Service plan. See "Scaling out" for what that means here.
- Use the `Files__S3__*` form for S3 settings, not the dotted one.

### AWS Fargate with RDS

- One ECS service, one task definition, container port 8080, behind an Application Load Balancer.
- Set the target group health check to `/health/ready` on port 8080.
- Do not use an ECS container health check that shells out to `curl` or `wget`. Neither is in the
  image (checked on `4.1.0`). Use the load balancer's check instead.
- Inject secrets from Secrets Manager or Parameter Store in the task definition.
- Set the ALB health check grace period on the service long enough to cover boot. **(platform docs,
  not tested here)**
- Fargate offers arm64 (Graviton), and the image runs there.

### Google Cloud Run with Cloud SQL

- Deploy `ghcr.io/baryodev/barako-cms:<version>` with container port 8080. Cloud Run sets `PORT=8080`
  by default, which matches. If your Cloud Run does not pull from ghcr.io directly, mirror the image
  into Artifact Registry. **(platform docs, not tested here)**
- Configure a startup probe and a liveness probe as HTTP probes on `/health/live`. Cloud Run has no
  separate readiness probe. **(platform docs, not tested here)**
- **Keep CPU allocated outside requests and set a minimum of one instance.** The workflow projection,
  scheduled publishing, token cleanup and retention sweeps are background services. With request-only
  CPU they are throttled between requests, and with scale to zero they do not run at all.
  **(platform docs, not tested here)**
- Connect to Cloud SQL as described under "The database".

## Not covered here

- Nothing on this page has been deployed to App Service, Fargate or Cloud Run by this repository.
  The application behaviour was checked against the code and the published image; the platform steps
  were not run.
- The console, [barakoBrew](https://github.com/BaryoDev/barakoBrew), deploys from its own repository.
  Put its origin in `CORS__AllowedOrigins`.
- `k8s/` is for a Kubernetes cluster you run, managed or not. Its probes and secrets match this page.
