# Aspire spike (#1025)

**Recommendation: neither, for now.** Do not adopt Aspire for local development or for cloud
targets yet. Revisit for local development once #691 settles the API's traces, and only after a
run on the arm64 dev box with barakoBrew and barakoPress in it, which this spike could not do (see
[What was not done](#what-was-not-done)). The evidence below is enough to say adoption is not free
and not yet justified; it is not enough to rule Aspire out.

Nothing shipped changed. `aspire/BarakoCMS.AppHost` is not in `barakoCMS.sln`, nothing references
it, CI never restores or builds it, and no image or compose file uses it.

## The AppHost

`aspire/BarakoCMS.AppHost/Program.cs`, on Aspire 13.5.4 (MIT, 2026-09-15; 13.6.x was under two
weeks old):

- the API as a project: `BarakoCMS.Suite`, the host the image runs, named by path;
- Postgres 16 (`Aspire.Hosting.PostgreSQL`) and SeaweedFS 4.46 as containers, each with its own
  container name and volume so nothing collides with a live stack;
- barakoBrew and barakoPress as containers from their published images, the image names read
  from configuration (`Images:Brew`, `Images:Press`, `Images:Tag`) because they live in other
  repositories and are not verified here;
- `ConnectionStrings__DefaultConnection`, the S3 service URL, `CMS_URL` and `NEXT_PUBLIC_API_URL`
  wired from resource references and endpoints instead of hand-set addresses;
- the database password, the JWT key and the admin password as generated secret parameters, so the
  source holds no literal and a first run needs no setup.

Four things it took that a reader would not expect:

1. **A dependency clash.** Aspire.Hosting.AppHost 13.5.4 needs KubernetesClient 19.0.2 or later,
   and `Directory.Packages.props` pins 18.0.13 for the core, with transitive pinning on. Restore
   fails with NU1109. The AppHost carries `VersionOverride="19.0.2"`, which is safe only because it
   runs the API as a separate process. Adopting Aspire inside any shipped project would mean moving
   the core's KubernetesClient first.
2. **No launch profile.** The Suite has no `launchSettings.json` (CLAUDE.md section 3 keeps them
   out), so `AddProject` found no endpoint and `WithHttpHealthCheck` threw at startup. The endpoint
   is declared in the AppHost instead.
3. **Fixed container names and a killed AppHost do not mix.** Stopping the AppHost with a signal
   left Aspire's orchestrator (DCP) and the named containers running. Every later start then hung in
   DCP startup and failed with `TaskCanceledException` after four minutes, until the orchestrator
   processes and containers were removed by hand. The ticket asks for own container names for
   safety on a shared box; with Aspire that safety has a cleanup cost.

4. **A project reference reaches into every shipped lock file.** With the Suite as a
   `ProjectReference`, restoring the AppHost evaluated the Suite and everything under it for the
   AppHost's runtime identifier and wrote a `linux-x64` section into the `packages.lock.json` of
   the core and every module. The solution's locked restore then failed with NU1004 on all of them.
   The AppHost names the Suite by path (`AddProject("api", "<path>.csproj")`) instead, which keeps
   the two restores apart. Any AppHost in this repository would have to do the same.

## Timings

On this session's machine: a 4-core x86_64 cloud container, shared with another session's
integration tests (load average 2 to 4 throughout), not the arm64 dev box. Treat them as
indicative only.

| Path | To a healthy API (`/health` 200) |
| --- | --- |
| `quickstart/docker-compose.yml`, `docker compose up -d`, image already local | 17.1 s |
| AppHost, `dotnet run --no-build`, first run | AppHost reported started after 34.2 s, with Postgres and SeaweedFS up; the API's health was not captured (the probe matched the wrong process) |
| AppHost, later runs | hung in DCP startup, see item 3 above |

The compose figure uses a stand-in for the 4.9.0 image built locally from the `v4.9.0` tag, because
`ghcr.io` image pulls are refused by this session's network policy. A clean-clone comparison would
add the image pull to compose and a `dotnet build` of the Suite (about two minutes here) to Aspire.

## `aspire publish` to Docker Compose

`dotnet run --project aspire/BarakoCMS.AppHost -- --operation publish --output-path <dir>` wrote a
`docker-compose.yaml`, an `.env` with every parameter left empty (no secret was written to disk)
and `aspire-manifest.json`. It also logged two failed pipeline steps, `publish-manifest`
("Sequence contains more than one matching element") and a `docker-compose-down` step looking for a
deployed file, and still exited 0: a publish step in CI would need its log read, not its exit code.

Against `quickstart/docker-compose.yml`, what it gets right:

- the services and the wiring between them: the API's connection string, the S3 service URL and
  the clients' API address are all derived, not typed;
- named volumes for Postgres and SeaweedFS, and one private network.

What it drops or changes:

- **Health checks.** Every `depends_on` is `service_started`; the quickstart waits for Postgres to
  be `service_healthy` before the API starts.
- **Backups.** No `db-backup` service and no `backups` volume. The restore path (#282) is absent.
- **Postgres setup.** No `pg_stat_statements` preload, none of the memory and planner settings, and
  no `postgres-extensions` step.
- **Ports.** The API and clients are only `expose`d on the internal network. The quickstart
  publishes the API on a chosen port; barakocms-site's deploy binds services to loopback for the
  edge proxy. Neither shape comes out.
- **The `barako-edge` network** the site deploy joins: there is no way to say it from the AppHost
  without hand-written compose afterwards.
- **Restart policies**, except on the dashboard (`restart: always`).
- **Something added that should not be:** `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on the API.
  barakoCMS deliberately ships forwarded headers off and trusts only named proxies
  (`ForwardedHeaders__KnownProxies__0`); turning them on for any sender lets a client choose its own
  address for rate limiting and audit.
- **A dashboard container** from `mcr.microsoft.com/dotnet/nightly/aspire-dashboard:13.5`, a nightly
  image, in what would be a production compose file.
- Database-specific variables the API never reads (`DEFAULTCONNECTION_URI`, `..._JDBCCONNECTIONSTRING`
  and others), with the password repeated in several of them.

barakocms-site's `deploy/cms-compose.yml` is in another repository outside this session's access,
so it was compared only through the points above that the ticket names (backups, health checks,
loopback ports, `barako-edge`).

## Azure Container Apps and AWS App Runner

Not exercised: nothing was deployed, as the ticket requires, and this session cannot reach #590 or
BaryoDev/BaryoVM#81 to read their plans. From the publish output above, the same gaps would carry
into any publisher that starts from this model: health checks, backups and the forwarded-headers
posture would each need hand-written overrides on top of the generated output, which is the
hand-written configuration a publisher is meant to remove.

## Tracing

Not shown. barakoCMS exports traces only when `Tracing:Otlp:Endpoint` is set
(`TracingOptions.EndpointKey`, `docs/tracing.md`). Aspire sets the standard
`OTEL_EXPORTER_OTLP_ENDPOINT`, which barakoCMS does not read, so as written the dashboard would show
the API's console logs and no span of a press request crossing into it. The AppHost could pass the
dashboard's OTLP address as `Tracing__Otlp__Endpoint`; that was not tried here, and what the spans
cover (and whether the API should read the standard variable) is #691's question.

## What was not done

- **barakoBrew and barakoPress were not run.** Their images are on `ghcr.io`, and this session's
  network policy refuses `ghcr.io` blob downloads. The AppHost declares them; nothing here shows
  them starting, signing in or rendering a page.
- **Not on the arm64 dev box.** This ran on an x86_64 cloud container. The arm64 timings the ticket
  asks for are still to be taken.
- **No signed-in console or rendered press page timing**, for both reasons above.
- **No dashboard trace**, for the reason in [Tracing](#tracing).

## Why neither, for now

Locally, Aspire's offer is service discovery and a tracing dashboard. The discovery works, but the
quickstart already wires the API in one file, and the dashboard shows no API spans until the
AppHost maps its endpoint onto `Tracing:Otlp:Endpoint` and #691 settles what the spans cover. Against
that: a dependency clash with the core, an orchestrator that leaves state behind when stopped
roughly, and a tool every contributor would install. For cloud targets, the generated compose drops
the health checks and backups and changes the forwarded-headers posture, so it cannot replace the
reviewed compose files; it would be a starting point to edit, which is what they already are.
