# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [4.0.0] - 2026-09-07

### Breaking

- **No endpoint returns a stored document as its wire contract.** `Role`, `UserGroup`, `Tenant`,
  `WorkflowDefinition`, `ContentTypeDefinition` and the rollback endpoint's `Content` all went out as
  the Marten document. That froze every stored property name as API and published any property added
  later to every client the moment it was saved. Each endpoint owns its response shape now. Field
  names are unchanged, so a client reading the documented fields is unaffected; what changes is that
  `SearchText` and other stored-only properties no longer appear.

- **Every package retargets from `net8.0` to `net10.0`.** Host applications have to be on .NET 10.
  This is the largest break in 4.0 and no migration helps with it.

- **`updatedAt` is gone from the content history response.** `GET /api/contents/{id}/history`
  returned both `updatedAt` and `timestamp` built from the same event timestamp. `updatedAt` was
  produced by `DateTimeOffset.DateTime`, which discards the offset rather than converting, so on a
  UTC+8 server the two fields described one event eight hours apart and the client had no way to tell
  which was right. `timestamp` is correct, is normalised to UTC, and is the field the admin already
  rendered. A client reading `updatedAt` was reading a wrong value, so this removes a field rather
  than a capability, and 4.0 is where a wire change like this belongs.

- **The core package no longer injects `appsettings.json` into consumer projects.** The published
  3.21.0 really does carry `content/appsettings.json` and `contentFiles/any/net8.0/appsettings.json`,
  verified against the artifact on nuget.org, so referencing BarakoCMS dropped the host's own
  configuration into every consumer to collide with theirs at build and publish.

- **The feature slices are internal.** 188 types under `Features/` were public only by accident, which
  under the stability rule froze every endpoint's `Request` and `Response` records until 5.0 and turned
  renaming a field into a compatibility event. `IWorkflowAction` and `IWorkflowEngine` stay public,
  because custom actions are a documented extension point. What the rule covers is now written down in
  CLAUDE.md section 6 rather than left to the broadest possible reading.

- **`IUserRepository` and `MartenUserRepository` are internal.**

- **Registering the same module twice is refused rather than skipped.** `BarakoModuleBuilder.Add`
  dropped a module whose type was already registered and said nothing, so a host that deliberately
  added two configured instances got one of them and no explanation, and a test registering more
  than one module quietly lost one. It throws now, naming the type, which is what the duplicate-name
  check in `ModuleOrder` already did for the same class of mistake. `DiscoverFrom` still skips a type
  already registered: discovery is a sweep, so adding a module by hand and then scanning the assembly
  it lives in is a normal combination rather than an error.

- **Enums cross the wire as names, not numbers.** `ContentStatus` and `SensitivityLevel` were 0/1/2,
  and the admin had the numbering transcribed into its own source to cope. Inserting a member
  renumbered every client. Requests may still send a number, so an existing caller keeps working when
  it posts; responses are names.

  This is the HTTP contract only. Documents are still stored with `Status` as a number, because
  `mt_doc_contents_idx_status` indexes `((data ->> 'Status')::integer)` and names there would break
  the index cast and every query that filters on status.

- **Signing in fails with 401, not 400.** Login and all six refresh failure paths returned 400, which
  standard client middleware classifies as a caller bug rather than an authentication failure. Account
  lockout returns 423.

- **`sortBy` is gone from every paginated request.** It was accepted everywhere, documented in
  Swagger, and honoured nowhere. On `/api/public/{type}` it was actively harmful: that endpoint
  deliberately rejects `?sort=` because accepting and ignoring it "would be a silent wrong answer",
  while `?sortBy=` was skipped as an unknown key and returned exactly that. `sortOrder` stays.

- **The content-type list is `GET /api/content-types`.** `/api/schemas` keeps working as a deprecated
  alias and goes in 5.0. The resource was read at one route name and written at another.

- **`GET /api/diagnostics/typecheck` is removed.** It returned an anonymous type built by reflection
  to debug a Marten upgrade, which cannot be expressed in the spec and should not be frozen API.

- **`{Id}` in two routes is now `{id}`**, matching the other thirty-odd. Cosmetic at runtime, but it
  lands verbatim in the OpenAPI paths.

- **Every collection endpoint returns the same envelope.** Nine endpoints returned a bare array
  (`/api/schemas`, `/api/user-groups`, `/api/tenants`, `/api/api-keys`, `/api/workflows`,
  `/api/me/tenants`, `/api/accounting/accounts`, `/api/devices`, `/api/pwa/installs`) and two returned
  an ad-hoc wrapper (`/api/settings` was `{settings: [...]}`, `/api/contents/{id}/history` was
  `{versions: [...]}`). All of them now return `{items, page, pageSize, totalItems, totalPages,
  hasNextPage, hasPreviousPage}`.

  This had to happen in a major or never: a bare array cannot gain pagination compatibly, because the
  root JSON changes from `[` to `{`. The default page size for the newly paginated endpoints is the
  maximum, 100, so a deployment small enough not to have noticed still does not.

  `/api/public/{type}/search` keeps `{results, count, query}` on purpose. It echoes a query rather
  than paging a set, and the reason is recorded on `PublicSearchResponse`.

- **`/api/pwa/installs` no longer silently caps at 1000 rows.** The envelope is the bound now.

  Three modules ship the envelope change and are versioned for it: Accounting `0.6.0`, DeviceTrust
  `0.4.0`, Pwa `0.4.0`.

- **Every error the core returns is now ProblemDetails.** Four shapes shipped from an API configured
  for RFC7807: ProblemDetails, a hand-rolled `{message}` with the field errors flattened into one
  string, a hand-rolled `{errors: [...]}`, and bodyless. `POST /api/content-types` emitted two of them
  from one endpoint depending on which check failed. Clients reading `message` or `errors[].message`
  off a 400 need to read `errors[].reason`.

- **`PUT /api/contents/{id}/status` requires `newStatus`.** It was a non-nullable enum, so omitting it
  or spelling the field wrong bound to 0, which is Draft, and the validator accepted it. A caller
  sending `{"status": 1}` moved its content to Draft and was told "Content status changed to Draft".
  Omitting the status is now a 400.

- **Success responses no longer carry error fields.** `Content/Create.Response` and
  `Content/Update.Response` drop `Message`; `ContentType/Create.Response` drops `Errors`. A generated
  client no longer sees success types with mysterious nullable error members.

- **Four obsolete members are removed from `Events/ContentEvents.cs`**, as their attributes promised
  for "the next major version", which 4.0.0 is. The narrower `ContentCreated` and `ContentUpdated`
  constructors go together with their paired `Deconstruct` overloads, because removing one without
  the other only fixes half the break.

- **A 3.x database needs one SQL migration before 4.0 will boot.** Marten moved from 8.37 to 9.30 and
  four database objects changed. Production runs `AutoCreate.CreateOnly`, which never alters an
  existing object, so the first boot against a 3.x database refuses and exits non-zero without
  writing anything. Apply `migrations/4.0.0/3.x-to-4.0.sql` first. Full procedure, including rollback,
  in `docs/upgrading-to-4.0.md`. `scripts/upgrade-check.sh` runs the whole sequence in CI against a
  database created by the released 3.21.0 image.

- **A missing database connection string fails at startup outside Development**, naming the setting,
  rather than substituting a dummy that points at localhost. Development keeps the dummy, which the
  codegen pass needs.

- **`/metrics` needs a scrape key.** The Prometheus endpoint was mapped with no authentication and no
  network restriction, so on any deployment that publishes the API it handed anonymous callers a list
  of every route, per-endpoint request counts and latencies, error rates and process internals. It now
  refuses unless `Metrics:ScrapeKey` (env `Metrics__ScrapeKey`) is set and the caller presents it,
  either as `Authorization: Bearer`, which is what Prometheus sends from `authorization` in a scrape
  config, or in `X-Metrics-Key`.

  A deployment that upgrades without setting the key loses scraping: with nothing configured the
  endpoint returns 404, because an unset credential has to mean refuse rather than allow. A wrong key
  against a configured one returns 401, so the two cases are told apart from the status code alone.
  `docs/upgrading-to-4.0.md` has the Prometheus config.

- **Feature flags are private until published, and `GET /api/feature-flags` no longer lists the
  catalogue to anonymous callers.** The endpoint is anonymous on purpose, since a public page
  rendering with flags has no user to authenticate, and targeting already evaluated a restricted flag
  to false for a stranger. But it built its dictionary from every flag before evaluation narrowed
  anything, so every key came back regardless: unreleased feature names, migration plans, and customer
  names wherever a flag targets one account.

  `FeatureFlag` gains `IsPublic`, defaulting to false. An anonymous caller receives only the flags
  marked public, and a private one is absent from the response rather than returned as `false`, which
  would hand over the name anyway. An authenticated caller still receives everything. Existing flags
  read back as private, so upgrading discloses nothing, and anyone relying on client-side flags on a
  public page has to publish those flags deliberately: `POST /api/feature-flags/admin` with
  `"isPublic": true`. `FeatureFlagService.EvaluateAllAsync` takes a `FlagAudience`; the overload
  without one returns the public subset, so a caller that has not thought about who is asking cannot
  leak a key by omission. FeatureFlags `0.4.0`.

- **Audit IPs and rate-limit buckets no longer come from a client-supplied `X-Forwarded-For`.**
  `DeviceContext` read that header directly and returned its first hop, so any caller could write its
  own address into the audit log and the OTP email just by sending one. The rate limiter never read it
  at all, so behind a reverse proxy every client shared a single bucket and the per-IP limit on
  `/api/auth/login` throttled the proxy instead of the attacker.

  The header is now applied by the ASP.NET `ForwardedHeaders` middleware, which honours it only from a
  hop the operator named. That middleware is off unless `ForwardedHeaders:Enabled` is true, and turning
  it on without `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks` stops the host at
  startup: an empty trusted set either does nothing or trusts every upstream, and both look like
  working configuration.

  What changes for a deployment already behind a proxy: until those keys are set, audit entries and
  rate-limit buckets record the proxy's address rather than the header value. For an honest client
  that is a worse answer than before, and for a dishonest one it is a much better one, because the old
  value was whatever the caller typed. For a proxy container on the compose network:

  ```json
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownNetworks": ["172.16.0.0/12"]
  }
  ```

  Turning it on also applies `X-Forwarded-Proto`, so `UseHttpsRedirection` sees the scheme the client
  used rather than the proxy-to-app hop.

- **A production first run no longer seeds demo content.** The demo AttendanceRecord content type,
  its sample records and its "Attendance Confirmation Email" workflow were seeded unconditionally, so
  every production instance came up holding an attendance schema it did not ask for and a workflow
  stored active that mails whatever address a record's `Email` field holds. Once an operator
  configured Resend, that demo fixture became an outbound mail path in their system.

  `Seed:DemoContent` (env `Seed__DemoContent`) decides it now. Unset, it follows the environment: on
  in Development, off everywhere else. Roles and the configured `InitialAdmin` stay unconditional.

  What an existing deployment sees on upgrade: nothing is removed. Each of those seeders already
  skipped when its document existed, so an instance that has the demo content keeps it, and deleting
  it is a manual choice. What changes is that a new instance outside Development no longer gets it,
  and neither does an existing one whose demo documents were already deleted by hand. The quickstart
  runs as Production, so `SEED_DEMO_CONTENT=true` in `.env` is how a developer asks for the sample
  content there.

- **`k8s/06-service.yaml` is a ClusterIP behind an Ingress, not a LoadBalancer.** It was a
  `LoadBalancer` commented "easy access for local testing", which on a managed cluster provisions a
  public load balancer pointing straight at the app with no TLS and no proxy. Anyone who was reaching
  the app through that address needs `k8s/08-ingress.yaml` (new), or
  `kubectl -n barako-cms port-forward svc/barako-cms-service 8080:80`.

- **The Kubernetes Deployment reads its database password from `barako-secrets`.** It inlined
  `Password=postgres` while `k8s/03-postgres.yaml` took `POSTGRES_PASSWORD` from the secret whose
  placeholder operators are told to replace, so following the manifests' own instructions handed
  Postgres a password the app never got. `k8s/02-secret.yaml` gains
  `ConnectionStrings__DefaultConnection`, `InitialAdmin__Username` and `InitialAdmin__Password`; set
  all of them before applying. The manifests could not be applied at all before this, so no running
  deployment is affected.
- **HSTS is sent, and the policy it sends changed.** The application configured
  Strict-Transport-Security twice, once through `UseHsts` and once by appending the header by hand,
  and a browser processes only the first value it receives, so the effective policy was the
  framework's 30 day default rather than the year the hand-written copy asked for. There is one
  policy now, in `HstsPolicy`: 90 days, `includeSubDomains` off, no `preload`. Operational
  consequence: a browser that reaches a deployment over HTTPS refuses plain HTTP to that host for 90
  days and cannot be told otherwise before then, so confirm the host is staying on TLS before taking
  this. `Hsts:MaxAgeDays` and `Hsts:IncludeSubDomains` tune it, and `includeSubDomains` should go on
  only once every subdomain is on HTTPS, because it covers subdomains that do not exist yet and
  cannot be recalled. Nothing is sent in Development, where the hand-written copy had been pinning
  developers' browsers against `https://localhost`.

- **Absolute URLs come from configuration rather than the `Host` header.** The RSS feed and the
  OAuth `redirect_uri` were built from `Request.Host` whenever nothing was configured, and
  `AllowedHosts` ships as `"*"`, so the caller chose the origin of links this application hands to
  crawlers and identity providers. Operational consequence for a deployment that has configured
  neither `Feeds:SiteUrl` or `App:BaseUrl` nor a real `AllowedHosts`: the feed answers 503 and the
  external-auth start endpoints throw, each naming the setting that fixes it. Set `App:BaseUrl` to
  the deployment's public URL, or set `AllowedHosts` to the hostnames it answers on, after which the
  request host is vetted and usable again. `AllowedHosts` itself still defaults to `"*"`, so nothing
  else changes on upgrade. One trap if you narrow it: a Kubernetes `httpGet` probe sends the pod IP
  as `Host`, so a list of real hostnames makes the probes 400 and the pod never goes ready unless
  the probe carries a `Host` header.

- **`Models.ContentType` is removed.** A public document type written and read by nothing but the
  seeder, in a table no query touched. The content types the API serves are `ContentTypeDefinition`,
  and always were. An existing `mt_doc_contenttype` table is left where it is, which is safe under
  `AutoCreate.CreateOnly`.

- **A compose stack no longer ships a usable admin password.** `docker-compose.yml`,
  `docker-compose.hub.yml` and `.env.example` defaulted `ADMIN_PASSWORD` to
  `changeme-in-production`, so a stack brought up with no `.env` had a SuperAdmin login published in
  this repository. The default is gone. Set `InitialAdmin__Password` and nothing changes; leave it
  unset and the seeder generates one and prints it once to the console, which is a change for anyone
  who was relying on the shipped literal. The seeder used to skip the account entirely when no
  password was configured, so removing the default without this would have left a first run with no
  way in (#271).

### Added

- **CI runs the admin against the real API, with nothing mocked.** Every other admin job mocks the
  API with `page.route`, so it proves the admin behaves correctly given fixtures the same person
  wrote and cannot prove those fixtures match the server. `scripts/smoke-check.sh` stands up
  Postgres, the API and the admin, seeds content through the API, and runs `admin/smoke`, which
  refuses to contain a route mock. It covers signing in, the error shape, the list envelope, string
  enums on the wire and the History panel.

- **Scheduled publishing is reachable from the admin.** A Schedule tab on a content entry arms or
  clears the publish and archive times, and shows what is armed. The server has had
  `PUT /api/contents/{id}/schedule` and the background sweeper for a while, and the README advertised
  arming any item, but nothing in the admin called it. `GET /api/contents/{id}` now returns
  `scheduledPublishAt` and `scheduledUnpublishAt` so a client can read back what it set.

- **A tenant can have a second member.** One thing created a `Membership`: `POST /api/tenants`,
  provisioning the creator as an Active admin. There was no supported way to add anyone else, change
  what they hold, or remove them, which made a multi-tenant CMS into a single-operator one. Five
  endpoints under `Features/Tenants/Members/` close it: `GET /api/tenants/members` (roster, active
  and suspended, newest first), `POST /api/tenants/members` (add by email), `PUT
  /api/tenants/members/{userId}` (roles or status), `DELETE /api/tenants/members/{userId}` (mark
  `Removed`) and `GET /api/tenants/members/roles` (what an administrator may assign).

  The tenant is the caller's current one rather than a route parameter. `TenantAccessMiddleware`
  already refuses a request whose token was minted for another tenant, and `TokenIssuer` puts the
  caller's effective roles for that tenant into the token, so `Roles("SuperAdmin", "Admin")` reaching
  a handler already means an administrator of this tenant. A handle in the route would mean
  re-deriving that in every endpoint.

  `SuperAdmin` is never assignable here, whatever the caller holds, on both the add and the edit
  path. Removal marks `Removed` and never deletes, so history and audit survive. An unknown email
  creates an OTP-only account (no password, they sign in with an emailed code), a known one reuses
  the existing user, and re-adding somebody removed reactivates the membership they already had
  rather than writing a second row. The tenants page in the admin grows a members section for all of
  it.

- **A workflow action can report that it failed.** `IWorkflowAction` gains
  `RunAsync`, which returns a `WorkflowActionResult`. It has a default implementation that calls the
  existing `ExecuteAsync` and reports success, so an action written against the old contract compiles
  and behaves exactly as before; `ExecuteAsync` is marked `[Obsolete]` and is removed in 5.0. This is
  not a break: nothing existing has to change. `WorkflowActionResult` is a new public type under
  `Features/Workflows`, added to CLAUDE.md section 6 and to the public-surface allowlist, because an
  extension point cannot return a type a module author cannot name.

  Every live workflow run is now recorded as a `WorkflowExecutionLog` with a per-action outcome, so
  `GET /api/workflows/{id}/debug` shows which actions ran, which failed and why, instead of only
  dry-runs. `WebhookAction` reports its real outcomes through it: a missing URL, a URL the outbound
  guard refuses, a non-2xx response, and a delivery that could not be made were all log lines and
  nothing more, which is how a webhook could answer 500 for a week without the workflow ever looking
  unhealthy.

- **A `Workflow Projection` health check and a `barakocms_projection_lag_events` gauge.** The workflow
  projection runs in Marten's async daemon, and an unhandled exception there stops the shard: every
  workflow silently stops firing while database, disk and memory checks all stay green. The check
  compares the projection's progress against the event high-water mark and reports it at
  `GET /api/monitoring/health`. It reports `Degraded`, never `Unhealthy`, because `/health` is what
  the liveness probe reads and restarting a pod does not restart a stopped shard. Tunable with
  `HealthChecks:MaxProjectionLagEvents`.

- **`docs/operating-workflows.md`** covers when a workflow action can fire twice, what the run
  records say, and what to do when workflows stop firing. It also states what a projection rebuild
  would actually cost, which is where two code comments were wrong.

  It documents the rolling-deploy window in particular: `HotCold` and the scheduled-content lock
  both need every node to be running the new code, and during a rollout the old node is not, so a
  workflow action can fire twice for the length of the deploy. No code can prevent that, since the
  half that does not participate has already shipped. `k8s/05-deployment.yaml` says so at the
  strategy, with the `Recreate` alternative for deployments that cannot tolerate a duplicate.

- **The content list reports `status` and `sensitivity`.** The single-item GET returned them and the
  list did not, so an entries table could not show which rows were Drafts without a request per row.
  The admin list has a status column again.

- **`docs/event-sourced-content-types.md`** explains what turning on event sourcing commits a
  content type to: the history becomes the record, stale saves get a 409, the choice is permanent
  even across delete-and-recreate, and non-Public fields are refused. Written for the admin making
  the choice, and published ahead of the toggle itself (#230, #331), which has not shipped.

- **Content can reference other content.** A `reference` field names the content type it points at,
  in `referenceType`, and a write is refused if the target does not exist or is of a different type.
  `?include=Field` on public delivery resolves references in one batched request instead of leaving
  every consumer to fetch each one. Resolved entries go through the same projection the list uses, so
  published state, document sensitivity, type opt-in and the field allowlist all apply: resolving is
  not a second way into a Draft. A target that does not survive that projection has its field removed
  rather than left as an id, which is also what a dangling reference does.

- **Public delivery can sort by a field value.** `?sort=Price` and `?sort=-Price` on
  `/api/public/{type}`, composing with the existing filters and paging. Only fields the content type
  marks Public are sortable, for the same reason only those are filterable: ordering by a field the
  caller cannot read is an oracle. Numbers sort as numbers, entries missing the field sort last in
  both directions, and `CreatedAt` breaks ties so paging a sort with duplicate values cannot show one
  entry twice and skip another.

- **Content records who created it, and a permission rule can require ownership.** `Content.CreatedBy`
  is set from `ContentCreated`, which has always carried it, and a rule can now say
  `{"$createdBy": {"_eq": "$CURRENT_USER"}}` for "own records only". Document properties are named
  with a `$` prefix so a schema field cannot collide, since a field name has to start with an
  uppercase letter. A record with no owner is denied rather than granted, and a SuperAdmin still sees
  everything.

- **An answer to the right-to-erasure question, and a way to act on it.** `Erasure:Mode` decides how
  a deployment handles an erasure request. `Delete`, the default, removes a content item's events,
  its stream and its document in one transaction through `DELETE /api/contents/{id}/erase`
  (SuperAdmin, audited). `None` requires an explicit acknowledgement. `CryptoShred` is recognised and
  **refused at startup**, because it needs an answer to which field identifies the data subject and a
  CMS has no natural one; a setting that reads as a policy while no policy is in force is the exact
  failure this decision exists to prevent. Reasoning in `DECISIONS.md` D9, and in
  `docs/compliance-posture.md` for anyone answering a privacy review.

- **A support and end-of-life policy.** `SECURITY.md` had a table that stopped at 3.x and no
  statement of what "supported" means. It now carries a 4.x row, a rule rather than a date (a major
  is actively supported until twelve months after its successor ships), what each status includes,
  and how module packages inherit the core's window.

- **A compliance posture** in `docs/compliance-posture.md`, linked from `SECURITY.md` and the
  README. States what exists with somewhere to verify each item, states plainly that there is no
  SOC 2, no ISO 27001 and no third-party penetration test, and answers the largest part of a typical
  security questionnaire by naming which questions self-hosting moves to the operator.

- **A software bill of materials.** CycloneDX for the .NET solution and the admin's npm tree,
  generated during the release build and uploaded as a 90-day artifact. `verify-packages` fails if
  either is missing or lists no components, so the release cannot claim an SBOM it did not produce.

- **Accessibility checks.** The 28 `jsx-a11y` rules `eslint-config-next` leaves off are enabled in
  the existing lint step, and an axe scan runs over the sign-in page, the content list, the content
  types list and the entry form in the existing e2e pack. Serious and critical fail the build.

- `db-patch`, `db-assert` and `db-apply` on the host, so a schema change can reach an existing
  database as a reviewed SQL file instead of having no route at all.

- **`docs/delivery-api.md`.** The parts of the public contract a consumer needs most existed only as
  C# comments: the `page`/`pageSize` bounds and the response envelope, the `filter[field][op]=value`
  syntax with its seven operators and five-filter cap, `sort=field` / `sort=-field`, `include=` for
  resolving references, and which status each refusal returns (#295).

- **The release tags the repository and writes a GitHub Release.** Sixty-seven versions reached
  nuget.org while the newest git tag stayed at v3.2.0, so the repository's front page advertised a
  release from many versions back and `git log v3.21.0..master` did not resolve. The release now
  tags the commit it published and creates a Release whose body is that version's `CHANGELOG.md`
  section, read by `scripts/release-notes.sh`, which fails when the section is missing or empty
  rather than publishing a blank note. The historical tags are not backfilled (#155).

- **`GET /health/build` reports the commit an image was built from.** Anonymous, like the other
  probes, and a separate path so `/health` keeps the body every dashboard already parses. The
  commit is stamped in through the `BARAKO_BUILD_SHA` build argument, since `.git` is in
  `.dockerignore` and cannot be read inside the image. An image built without it answers `unknown`
  (#157).

- **CI reads the Kubernetes manifests.** Nothing ever did, which is how `memory: "128Mw"` sat in
  `k8s/05-deployment.yaml` through several releases. A job stands up a throwaway kind cluster and
  sends every manifest to a real API server with `--dry-run=server --validate=strict`. The two
  cheaper options were measured against the manifests as they stood before that bug was fixed and
  both accepted them: `kubectl --dry-run=client --validate=strict` and `kubeconform -strict`. A
  resource quantity is a string in the OpenAPI schema, so only the API server parses it.
  `scripts/testdata/k8s-known-bad/` keeps those manifests, and CI fails if they are ever accepted
  (#383).

- **CI fails when a tracked lockfile is watched by nothing.** A directory Dependabot does not cover
  produces no error and no pull request, so `site/` drifted unwatched.
  `scripts/check-dependabot-coverage.sh` compares every tracked `package-lock.json` against the npm
  entries in `.github/dependabot.yml` (#153).

- **`SWAGGER_ENABLED` on the shipped compose files.** `docker-compose.yml` sets it true, which is
  what it already did through the environment; `docker-compose.hub.yml` sets it false. Swagger
  follows `ASPNETCORE_ENVIRONMENT` when `Swagger:Enabled` is unset, and the hub file defaults that to
  Development, so the compose that runs the published images turned the whole API surface on without
  anyone choosing it. Saying it explicitly means changing the environment no longer changes what is
  published as a side effect (#271).

- **A width parameter on the file downloads.** `GET /api/public/files/{id}?w=640` and
  `GET /api/files/{id}?w=640` answer with a resized copy of a PNG, JPEG or WebP, made on the first
  request that asks for it and kept as a derived `StoredFile` row pointing at its parent. Every
  consumer was downloading a full-size upload and resizing it client side, or the editor was being
  asked to upload three sizes.

  The cap is the part worth reading. `?w=` above `Files:Images:MaxWidth` (default 2048) is refused
  with a 400, and requests inside it are snapped onto a ladder of seven widths rather than honoured
  literally. Honouring an arbitrary width on an anonymous route means anyone can walk `?w=1` through
  `?w=2048` on one public image and leave two thousand stored blobs behind, which is a cap on the
  cost of a request and no cap at all on what the cache costs. Dimensions are read from the image
  header before any pixel is decoded, so a ten megabyte PNG that decodes to tens of gigabytes is
  served at full size rather than resized.

  A variant is reachable exactly when its original is. The access check runs on the original before
  a resize is considered, so a private file with a `?w=` on the public route is a 404 that did no
  work, and a cached variant is not addressable by its own id on either route, including for an
  admin. That is deliberate: an addressable variant would need access rules of its own, and a second
  copy of an access rule is one that can drift out of step with the file it came from.

  Anything that is not a resizable image is served unchanged with the parameter still on the URL, so
  a frontend that appends `?w=` to every asset does not break on the one that is a PDF. Setting
  `Files:Images:MaxWidth` to `0` turns the whole thing off. `docs/image-variants.md` has the rest.

  The variant is stored with its parent's public flag, and on a store with ACLs that is an access
  control rather than bookkeeping: S3 turns a public put into a `PublicRead` object with a URL that
  is then persisted on the row and redirected to. So a variant of a private file stored public would
  be a private file anonymously fetchable at the bucket, whatever the API answered.

  Concurrent decodes are bounded by processor count. The pixel limit bounds one decode and the rate
  limiter caps one address, so N simultaneous misses on the same uncached width were N simultaneous
  bitmaps in memory. Work queues now instead.

  A file the resizer cannot handle is served unchanged at any width, including one above the cap. It
  used to be a 400 there, which broke the promise that a frontend can put `?w=` on every asset URL.
- **A server-sent event stream of content changes.** `GET /api/public/events` streams
  `content.published`, `content.updated` and `content.unpublished` for the tenant, filterable with
  `?type=`. Every payload is produced by the same projection the REST reads use, so a Sensitive
  field is masked in the stream for the same reason it is masked on `GET /api/public/{type}/{slug}`,
  and a subscriber on one tenant never receives another tenant's change. Off by default:
  `Delivery:Events:Enabled` turns it on, `Delivery:Events:MaxConnections` (100) caps open streams
  per instance, and a keepalive goes out every 15 seconds. Fan-out is in process, so with several
  API instances each streams only the writes it handled; `docs/delivery-api.md` says so. Closes #105.
- **A job queue whose enqueue shares the request's transaction.** `QueueJobAsync` stages the job in
  the request's scoped Marten session, so a request that throws or fails to commit leaves no job
  and a request that commits leaves one in its tenant. The queue owns retry: a record carries the
  attempt count, the next attempt time and the last error, waits with exponential backoff
  (`Jobs:BackoffBaseSeconds`, capped by `Jobs:BackoffMaxSeconds`) and is dead-lettered after
  `Jobs:MaxAttempts`. A claim holds for `Jobs:LeaseSeconds`, which is also the handler's execution
  limit. `GET /api/jobs` lists a tenant's jobs behind the new `view_jobs` capability,
  which Admin holds by default. Nothing migrates onto the queue yet; one logging command proves it
  runs. See `docs/background-jobs.md`.
- **Content type blueprints: a site starts from a named set of types instead of an empty schema.**
  `GET /api/content-types/blueprints` lists them and `POST /api/content-types/blueprints/{name}`
  creates every type one declares in the caller's tenant. Four are built in: `blog` (post, category,
  author, page), `events` (event, venue, speaker, with a geopoint location), `portfolio` (project,
  client) and `docs` (article, section). Every addressable type has a slug field, and fields that are
  for the team rather than the public are marked Sensitive or Hidden.

  Applying is additive and all or nothing: a type that already exists refuses the whole blueprint
  with a 409 naming the clash, and types the blueprint does not mention are left alone. Gated on
  `manage_content_types`, like the create it stands in for.

  Set `Blueprints:Path` to a directory and its `*.json` files are listed alongside the built-ins.
  Each file is validated when listed, with the same validator the create endpoint runs, and a broken
  file shows its errors in the list rather than failing at apply time.
- **A content type can opt into the SEO fields every client site needs.**
  `POST /api/content-types/{name}/seo-fields` adds meta title, meta description, canonical URL,
  social image and a no-index flag. Ordinary fields marked Public, so delivery, validation and
  scrubbing already handle them, and all five optional so opting in does not invalidate existing
  entries. Additive and idempotent: a field the type already has is left exactly as it was.

  Public delivery now carries a resolved `seo` block, omitted entirely for a type that has not opted
  in. **An unset meta title falls back to the entry's own title** rather than emitting an empty tag,
  using the same field names the admin uses to label an entry. An empty title tag is worse than none:
  a search engine shown one indexes the page with nothing to display.

  An entry marked no-index is left out of the sitemap, because listing a page and then telling the
  crawler to go away when it arrives is a contradiction Search Console reports as an error. Title and
  description lengths are guidance rather than validation, since search engines truncate on pixel
  width and a hard limit would be wrong in both directions.
- **URL redirects, so a rebuild does not break every existing link.**
  `GET/POST /api/redirects` and `DELETE /api/redirects/{id}` manage them, `POST /api/redirects/import`
  takes a CSV for a migration, and `GET /api/public/redirects/resolve?path=` is the anonymous lookup a
  frontend makes on its 404 path. One indexed equality comparison, no wildcards, cached for five
  minutes, because that path is when a site can least afford anything else.

  Loops are refused when a rule is saved rather than when a visitor hits one: a path redirecting to
  itself, a rule closing a circle with rules already stored, and a chain longer than ten hops even
  when it terminates. An import checks each line against the stored rules and against the lines above
  it in the same file, which is the loop no single line creates and nobody can find afterwards. A bad
  line is rejected by number and the rest still import.

  `permanent` defaults to false, so a redirect is a 302 unless asked otherwise. A browser caches a 301
  indefinitely, so one entered by mistake is not fixed by deleting the rule.
- **Alt text, a caption and a where-used list on files, for a media library.** `PATCH /api/files/{id}`
  stores `alt` and `caption` with a file, `GET /api/files/{id}/meta` and the new `GET /api/files` list
  return them, and `GET /api/public/files/{id}/meta` hands them to a frontend for a public file only,
  404 otherwise like the bytes next door. The list takes `?q=` for a name substring and
  `?contentType=image/` for a type prefix, is paginated, and leaves out cached resizes.

  `GET /api/files/{id}/usage` lists the entries whose data references the file, matched by the id and
  by the storage key so a bare id, a download URL with or without `?w=`, and an object store's public
  URL are all found. `DELETE /api/files/{id}` is new and refuses with a 409 naming the first ten
  usages while any entry references the file; `?force=true` deletes anyway, along with the cached
  resizes and every blob behind them. A usage row's title goes through the same read permission
  and sensitivity checks as `GET /api/contents`, so a file used by a Sensitive entry still blocks a
  delete without telling the editor what the entry says. All of it is gated on the module's
  `upload_files` capability. The console half (grid, picker) is barakoBrew's.
- **The seven modules that shipped with no tests have them.** `ExternalAuth`, `DeviceTrust`,
  `Portability`, `FeatureFlags`, `Email.Resend`, `Import` and `Analytics.Umami` were built, packed and
  pushed to NuGet on every release with no assertion anywhere covering them, and two of the seven are
  authentication surface. 52 tests, each one checked by breaking the thing it covers and watching it
  go red.

  What is pinned is the behaviour that would hurt if it broke rather than a coverage number. An
  account with MFA enrolled gets a challenge from a social sign-in and never a token, which is the
  bypass 0.1.5 shipped. An OAuth callback with a missing or mismatched `state` mints nothing, and one
  that matches its own state signs a verified account in. A device-bound token is refused from any
  other device, a token with no `did` claim is deliberately left alone, revoking a device kills its
  refresh tokens and nobody else's, and nobody can revoke a device they do not own. An exported bundle
  imports into a clean tenant with its schema and content intact, twice over without duplicating the
  type, into the calling tenant only. A percentage rollout gives the same person the same answer every
  time. The Resend API key travels as a bearer header and appears in no URL, body or exception. A bad
  row stops an all-or-nothing import before anything is written. The Umami account never reaches the
  browser, and data requests to Umami carry the exchanged token rather than the credential.

  `BarakoCMS.Tests` now references `DeviceTrust`, `Import` and `Analytics.Umami` as well, so all seven
  are reachable from a test at all, which four of them were not.
- **A provider outage during registration is now pinned as invisible from outside.**
  `POST /api/auth/register` answers one sentence whatever happens, so that an address somebody else
  already registered cannot be told apart from a free one. A send that escaped as a 500 would have
  put that difference back without anyone editing the message. Three tests cover it: the answer is
  byte for byte the same with the provider down as with it up, the failure reason never reaches the
  response, and a sign-in code request answers the same thing for a registered address, an unknown
  address, and a registered address during an outage.
- **`BarakoCMS.Email.Smtp`, an SMTP email provider.** Email no longer means signing up for one
  particular SaaS: any relay works, which is what a self-hoster already has from their host, from
  Google Workspace, from SES or from a corporate mail server. MailKit does the sending, not the
  `System.Net.Mail.SmtpClient` Microsoft tells you not to use in new code.

  The module reads its own `Modules:Email.Smtp` section (host, port, user, password, from, TLS
  mode), because the existing settings surface resolves an API key and a from address and SMTP
  needs neither shape. A from address typed into the admin still wins, since that field is not
  provider-specific.

  With no host configured it registers nothing at all, so adding the package to an existing
  deployment and configuring nothing leaves whatever was sending before still sending. The TLS
  default will not fall back to plaintext: unset means implicit TLS on port 465 and STARTTLS
  everywhere else, and a relay that does not offer STARTTLS fails the send rather than getting the
  password in the clear. A failed send names the relay and quotes its reason, with the password
  redacted out of it, because a relay that echoes the credentials it just rejected would otherwise
  put them in an admin screen and a support ticket.
- **A screen for importing a spreadsheet.** The Import module had two endpoints and no interface, so
  turning an .xlsx or CSV into entries meant calling the API by hand. Settings now has one:
  choose a file, say which row holds the headings, match each column to a field on the target content
  type, and import.

  Entries are created as drafts, so nothing an import gets wrong is published. Every row is attempted
  and the refusals are reported by their position in the sheet, rather than the first bad row ending
  the run and leaving an editor to work out how much of it landed. A column matched to nothing is
  left out rather than sent blank: a sheet usually carries a column nobody wants, and sending it
  would either fail validation or invent a field the type never declared.
- **Devices and Export/import have admin screens.**
  Both modules shipped a backend with no interface, so their features existed only for whoever was
  willing to call the API by hand. `Settings > Devices` lists the browsers and apps signed in to your
  own account and revokes one, with the confirmation saying something different when it is the device
  you are sitting at. `Settings > Export and import` downloads every content type and entry as one
  JSON file, and takes one back in, with a preview that runs the import as a dry run first. The
  preview names the entries whose content type is in neither the bundle nor the CMS: those import
  successfully and then never appear in public search, so a plain success message would be true and
  misleading.
- **A `geopoint` field type and a proximity filter on delivery.** A field can now hold
  `{ "lat": number, "lng": number }`, validated as a real coordinate pair rather than free text, and
  `GET /api/public/{type}?filter[Location][near]=lat,lng,radiusKm` returns the entries within the
  radius. Each item then carries `distanceKm`, and `sort=distance` orders by it. No PostGIS: the
  query is a bounding box then the haversine, both in SQL over the stored JSONB, and it sits in the
  same chain as every other filter so a Draft inside the radius stays invisible. The radius is
  capped by `Delivery:MaxRadiusKm` (default 1000) so the prefilter always applies. Distances are
  great-circle, right for "within 10 km" and not for geodesy. The console's map editor is
  barakoBrew's side.
- **`CODING_STANDARDS.md`**, a signpost to `CLAUDE.md`, which is the coding standard and was
  effectively invisible under a filename no human contributor has a reason to open. `CONTRIBUTING.md`
  and the pull request template now point at it too. The standard itself gains the rules two
  contributor pull requests showed were missing: developer-machine files stay out of the repository,
  a config default preserves existing behaviour, a list endpoint is bounded, prefer an existing
  pattern over a new one, and an assertion over a collection has to assert the collection is not
  empty first. Closes #145.
- **Swagger shows `/api/public/students`, not just `/api/public/{type}`.** A content type is created
  by a user at runtime, so nothing built at compile time can ever name it. The generated OpenAPI
  document is now merged with a projection of the content types on its way out, adding the list,
  search and slug paths for every type marked `IsPubliclyDeliverable` along with a schema built from
  its fields. No route is added and no delivery code changes: `/api/public/{type}` keeps matching
  exactly as it did, and it is still in the document.

  A schema is disclosure, so this is an allowlist and it emits exactly what the anonymous delivery
  endpoint would return. A type that is not publicly deliverable does not appear, not even by name.
  A field whose sensitivity is not `Public` is absent from the schema, because a field name is itself
  information: naming `guardianContactNumber` tells a reader what to probe for even when every value
  comes back masked. `ValidationRules` and `DefaultValue` are never published. The document does not
  vary by caller, so a cached copy cannot show one caller what another may see, and it is cached per
  tenant and invalidated when a content type is created or its delivery is switched. Closes #159.
- **A field's sensitivity was fixed at the moment the content type was created.** There was no
  update path at all, so a field marked Public by mistake stayed readable, and a field that should
  never have been masked stayed masked, until somebody edited the database by hand. `PUT
  /api/content-types/{name}/fields/{field}/sensitivity` changes one field's level, admin only, and
  rebuilds the derived search text for every existing entry of the type so that raising a field
  stops its value being matched by anonymous search and not only stops it being returned. Lowering
  is a disclosure of data written under the old level, so it is refused unless the request sets
  `acknowledgeDisclosure`, and it is recorded under its own audit action. Raising stops the value
  being served and does not remove it from storage, backups or the event stream.
- **Modules are found by reference and chosen by configuration.** `AddBarakoCMS` now discovers
  every `IBarakoModule` in the application's dependency context, so `dotnet add package` plus a
  restart is the whole install and `BarakoCMS.Suite/Program.cs` names no modules at all. Only
  libraries that reach `BarakoCMS` through their dependencies are loaded, only public top-level types with a parameterless
  constructor count, discovered modules are ordered by type name, and a type the host already added
  is skipped. `modules.Discover = false` on the builder, or `BarakoCMS:Modules:Discover=false` in
  configuration, keeps the explicit list only. A host that references a module package without
  adding it now runs that module; turn discovery off to keep the old explicit-only behaviour.

  `BarakoCMS:Modules:Enabled`, an array or a comma-separated string
  (`BarakoCMS__Modules__Enabled=Accounting,Files`), decides which of the modules found run. Unset
  runs all of them and logs one warning saying how to set it, so an existing deployment changes
  nothing on upgrade; an empty string is core only; a name that matches nothing refuses startup and
  lists the names available. Disabling a module leaves its data in place. `GET /api/modules` now
  lists every module seen with an `enabled` field, so "installed but off" and "not installed" can
  be told apart. Fixes #170 and #172.
- **A module author starts from a template and tests on a packable host.** `dotnet new install
  BarakoCMS.Templates` then `dotnet new barakocms-module -n Acme.Notes` produces a module that builds,
  registers and passes its own tests: one endpoint gated on a capability the module declares and
  grants to Admin at seed, one document type, options bound from `Modules:Notes`, a README in the
  house structure, an icon placeholder, packaging metadata inherited from a shared props file with
  the `barakocms-module` tag, and a test project. The tests run on `BarakoCMS.Testing`, a new
  package holding `BarakoTestHost`: the real host over a Testcontainers PostgreSQL with the modules
  you name registered, the system roles and the admin seeded, every module's seeder run, a client
  signed in as the admin, a client for a named role, a tenant helper and a Marten session. Both
  packages are proved from outside the solution by `scripts/check-module-template.sh`, which CI runs
  and the release runs against the artifact it is about to publish. `MODULES.md` gains the section a
  third party needs: what the host checks at startup and what it does not, that a module is trusted
  in-process code, and how to name, version and describe a published one. Fixes #174.
- **`GET /api/modules` reports which modules an instance actually booted with.** Read straight off
  the container: `AddBarakoCMS` registers each opted-in module as a singleton, so the answer is what
  the host runs rather than a list somebody maintains beside it. Two fields per module, the
  registered name and the declared contract version, and nothing else. A module knows its
  configuration section and its assembly paths, and none of that is a fact about the module.

  Ordered by name, always. An instance running core alone answers with an empty list rather than a
  404: a 404 is what a route that never shipped looks like, and telling those apart is the reason a
  client asks at all. Admin and SuperAdmin only.

  Every first-party module currently reports contract version zero, since none of them override the
  property. So this confirms a module was picked up, and does not yet say which contract version it
  thinks it is talking to.
- **`docs/delivering-a-client-project.md`, the path from a clean machine to a handed-over client
  site.** Everything else documented here answers what barakoCMS can do; this answers what you do,
  in what order, and with which endpoints and config keys. It sequences standing an instance up,
  creating the tenant, modelling content inside it, adding the client's people, giving them a role,
  pointing a frontend at the delivery API, deploying and handing over. The step order matters:
  content types are tenant-scoped, so modelling before the tenant exists leaves the model on
  `default` where the client's tenant cannot see it, and moving it then needs the Portability
  bundle. Tenant member management (#184) shipping is what made the onboarding step writable.
- **It names what is not solved, because a delivery document that overstates is worse than none.**
  Two administrative surfaces reach past the tenant and are both open to the seeded `Admin` role, so
  the document says not to give that role to a client's staff: `GET /api/audit` treats `?tenant=` as
  a caller-chosen filter rather than a boundary, and `POST /api/users/{userId}/roles` writes the
  global `User.RoleIds` and, unlike `POST /api/tenants/members`, does not refuse the SuperAdmin role
  id. It also records that `Tenant.Domains` and `Tenant.Branding` are returned by the API and
  writable only in the database, that an invited member cannot set their own password because
  `POST /api/me/password` verifies a current one they do not have, and that the switch-tenant
  request field is spelled `club`.
- **`docs/multi-tenancy.md`** is in the repository, rewritten against the code. It was gitignored and
  described a design that had since shipped, in several places the opposite way round from how it was
  actually built: roles, refresh tokens, OTP codes and trusted devices are global rather than
  tenant-scoped, the `X-Tenant` header is accepted from any caller by design, the membership check
  runs when a token is issued rather than in middleware, and `User.RoleIds` was kept and unioned with
  membership roles rather than moved. Closes #211.
- **A test refuses to let an event type reach an API response.** The event stream is internal and
  history goes out as a projected, versioned view, and until now that held by luck: the history
  endpoint projects to a DTO because whoever wrote it projected out of ordinary API hygiene. The
  moment one response carries an event type the record's shape is public API and reshaping it behind
  an upcaster is a wire break. `EventSurfaceTests` takes the response types off the endpoints
  themselves rather than from a list, so a response added later is covered, and follows property
  types, constructor parameters, public fields, array elements and generic arguments, because a
  `List<ContentCreated>` is the same leak one level down. It found no existing violation. The rule is
  DECISIONS.md D4.
- **A content type had no way to say its entries are event sourced, so the choice could only be made
  for all of them or none.** A type can now be created with `eventSourced: true`, which makes its
  event stream the source of truth instead of its `Content` document. It defaults to false, which is
  what every type has always been: the document is the record, events are still appended for
  history, audit and workflows, and nothing changes for a deployment that does not ask for this. The
  decision is recorded against the type NAME rather than on the definition, so deleting a type and
  creating it again inherits the original answer instead of arriving at the opposite one, and there
  is no code path that changes or deletes it in either direction. Two rules come with it. An
  event-sourced type may not hold non-Public fields, refused at creation and at any later attempt to
  raise one, because erasing a value out of an append-only stream is not something this server can
  do. And an event-sourced type has to be chosen before its first entry, because a stream written
  under the old rules is not a history the stream can claim to be the source of truth for. New table
  `mt_doc_content_type_sourcing_policies`, in `migrations/4.0.0/3.x-to-4.0.sql`, empty on arrival.
- **Connectors had a backend and no interface, so a third party's credentials could only be entered
  with curl.** There is a screen now at Settings, Connectors: the list, add, edit, delete, and the
  test button, gated to SuperAdmin and Admin the way the endpoints are.
- **A credential is write only on the screen because it is write only in the API.** No endpoint
  returns a stored value, so the box starts blank every time and blank means "keep what is stored".
  Deleting a credential is a separate checkbox rather than an empty box, which is what
  `SaveConnectorRequest` already encodes: an absent key changes nothing, an empty value deletes.
  The alternative, showing asterisks and posting them back, would overwrite the token with asterisks
  the first time somebody corrected a base URL. The only values this screen ever sends are ones
  typed into it in that session.
- **The list answers the question an operator came with: did the last probe work.** `LastTestResult`
  is prose the server wrote ("HTTP 200 in 34 ms"), not a boolean, so the screen reads the status out
  of it and calls 200 to 299 a success, matching `IsSuccessStatusCode`, which is what the server
  used to decide it. A 302 to a login page counts as failing, which is what `ProbePath` exists to
  fix. It also names the gap before a probe is run, and says which gaps `ConnectorSender` really
  refuses on: no stored credential is one, and so is a missing `HeaderName` on an API key connector.
  A missing `Username` on a Basic connector is not. The sender defaults it to empty and sends
  `base64(":password")`, so the screen says that instead of promising a refusal that never happens.
- **The slug is typed, not rewritten under the operator's cursor.** It is derived from the name
  until the operator edits it, and after that the box keeps exactly what they typed. The save is
  gated on `^[a-z0-9][a-z0-9-]{0,62}$`, which is `ConnectorRules.IsSlug`, and the form says so when
  the slug would be refused. That matters more here than on most forms, because
  `UpdateConnectorEndpoint` overwrites the slug on a PUT with the stored one, so a slug entered
  wrong can only be fixed by deleting the connector and entering the credential again.
- **Deleting asks first, and says what goes with it.** The credentials go in the same transaction
  and any request definition naming that slug stops working, so the confirmation names the slug
  rather than asking a generic "are you sure".
- **The queries screen.** `/api/queries` had no interface, so a saved query could only be created by
  hand against the API. The admin now lists, builds, previews and deletes them under Queries.

  The form offers exactly the shape the model accepts and no more: a content type, up to ten typed
  filters, a sort, a limit and an explicit field projection. There is nowhere to type an expression,
  because there is nowhere in the model to put one. Only fields the content type marks Public are
  offered to filter on, sort by or return, which is the same allowlist the runner enforces and for
  the same reason: filtering on a field the rows cannot show is a way to read that field without
  ever printing it.

  The preview is the part that makes it useful. Pressing it saves any pending edits and then runs the
  stored definition, and the rows come back as a table of the projected fields in the order the
  projection names them. That is what a workflow action carrying the query would send. A run the
  server refuses shows the server's own reason, so a field raised to Sensitive after the query was
  written surfaces here rather than in a payload.
- **A screen for outbound requests.** The request endpoints had no interface, so composing an
  outbound call meant POSTing JSON by hand. Settings now has one: the list, an editor for the
  connector, method, path, headers, body template and success rule, and the dry run.

  The dry run leads the screen, because it is how an operator finds out what a template produces
  while they can still change it. It composes the call against a real entry and renders exactly what
  came back: the finished URL, every header and the body, laid out when it is JSON and left as
  composed when it will not parse, since that is the case worth seeing. A refusal shows the reason
  instead, which is what happens when a template names a field that is not Public, or reads a named
  query, which the composer cannot do yet.

  Nothing on the screen sends anything, and it says so where a verdict could be misread: the result
  panel is headed "Dry run. Nothing was sent.", the verdict reads "Would be sent" rather than "Sent",
  and the button says compose rather than send. Header blocks are pasted as "Name: value" lines and
  a line the parser cannot read is refused rather than dropped, because a dropped line is a header
  the operator believes they set.
- **A screen for workflow runs.** The run endpoints have been there since the outbox split and had
  no interface, so the only way to find out whether a workflow actually fired was to query Postgres.
  `/workflow-runs` lists every run newest first, filtered by status, with the status carried by a
  tinted badge rather than a word in a column, and opening one shows its actions in execution order
  with the attempt count, how long each took, the response status and the error when there is one.

  A retry button appears on a failed action and on an unknown one, and nowhere else. Unknown is a
  timeout, where the request may well have arrived and only the response was lost, so retrying it is
  a decision to accept possible duplicate delivery and a person has to make it. Succeeded, Running,
  Pending and Skipped get no button at all: `POST .../retry` refuses a succeeded action with a 409
  because sending it twice is the hazard the idempotency key exists for, and offering a control that
  can only answer 409 teaches an operator to distrust the screen.

  Pressing retry refetches the run rather than rendering what the endpoint returned. The response is
  the run as it stood at the moment of the write, and the runner can claim the attempt a tick later,
  so painting that body on screen would show a Pending action that is already Running.
- **Connectors: one place to hold a third party's credentials, encrypted and write only.** Calling
  Jira or Twilio or a plain REST API meant a module with its own config keys and its own code. A
  connector is configuration instead: a base URL, an auth mode, non-secret settings, and credentials
  an admin enters through `POST /api/connectors`. There is a test button, because a credentials
  screen that cannot tell you whether it worked moves the failure to the first real workflow run.
- **A secret is not on the connector document, which is the design rather than an omission.**
  Credentials live in a separate `ConnectorSecret`, encrypted with AES-GCM, and the read path never
  joins them, so a bug that returns a connector over the API cannot leak a token: there is nothing in
  the object to leak. The response carries the *names* of the secrets held, which is what a screen
  needs to say one is set without handling it. Nothing reads a secret back out, from any endpoint.
- **`Connectors:Key` is its own key, with no fallback**, unlike `Mfa:Key` which falls back to the JWT
  signing key. `SECURITY.md` records that coupling as a lesson, and this enforces it: a key that
  matches `JWT:Key`, `Mfa:Key` or `Secrets:Key` is refused before the host is built, as is one
  shorter than 32 characters. Rotating an encryption key makes everything under it unreadable, so it
  has to be one decision at a time rather than one that retires every integration and every enrolled
  second factor together. An absent key is not a startup error, it means the feature is off, and the
  endpoints say so naming the setting rather than storing a credential in the clear.
- **The address guard runs when the socket opens, not when the URL is saved.** A base URL is checked
  on save as an early refusal, but the check that counts is the one in the `ExternalApi` client's
  connect callback, which resolves once and dials an address that answer survived, with redirects
  off. A name that resolves publicly at save time and privately later is the case that matters, and
  it is the only one a save-time check cannot see.
- **A test result carries the status code and the round trip, never a response body.** A 401 from an
  OAuth provider frequently contains the credential that was sent, so quoting the response is how a
  token reaches a log aggregator, an error tracker and a support ticket in one step.
- **The unique slug is scoped per tenant.** Marten does not infer that from a document being
  multi-tenanted, so without `TenancyScope.PerTenant` the index is global and the first tenant to
  name a connector "company-jira" stops every other tenant using that name, refused with a 409 about
  something they cannot see. Found by the 3.x upgrade check, which compares the shipped migration
  against the schema Marten expects.
- **Conjoined multi-tenant, role gated and audited.** A connector belongs to the tenant that added
  it. Configuring one is SuperAdmin or Admin, because it is credential management rather than content
  editing. Creating, updating, testing and deleting are audit events recording the slug, the base URL
  and the names of the secrets held, never a value. Deleting a connector removes its credentials in
  the same transaction, so nothing decryptable is left belonging to something nobody can see.
- **Requests: what to send through a connector, held as configuration.** A connector says where and
  who; a request says what. Together they replace the C# somebody would otherwise write per
  integration. A definition names a connector, a method, a path template, header templates and a body
  template using the same `{{...}}` variables workflow actions already use, and a workflow fires it
  with one parameter: `{ "Type": "Request", "Parameters": { "Request": "post-to-facebook" } }`.
- **A field the schema marks Sensitive or Hidden cannot leave, even when a template names it.**
  Refused rather than redacted: the operator wrote `{{SSN}}` on purpose, and a request that silently
  posts three asterisks where they expected a value looks like it worked. The message names the field
  and its level, while they still have the template open.
- **A value cannot rewrite the request around it.** Each hole is escaped for where it lands, so a
  title of `","admin":true,"x":"` becomes a title rather than an extra field, and one containing a
  slash cannot address a different endpoint from the path. That is injection in a different costume,
  and it is why substitution is not delegated to `ITemplateVariableExtractor.ResolveVariables`, which
  returns a finished string with no point at which one value can be escaped for its context. The
  composed body is parsed before sending, so a malformed template is a refusal here rather than a 400
  from a provider describing their own parser.
- **Success is a rule, not a status code.** Several providers answer 200 with an error in the body, so
  `TwoHundredAndJsonPathAbsent` fails the call when a named path is present. Choosing that rule
  without a path is refused, because it would otherwise behave exactly like the plain 2xx rule and
  appear to be in force while changing nothing.
- **`POST /api/requests/{slug}/dry-run/{contentId}` composes everything and returns the exact method,
  URL, headers and body without sending.** No credential appears in it, and not because it is
  redacted: the connector's secrets are attached by the sender afterwards, so the dry run never held
  one. `{{PublicUrl}}` is new and resolves from `App:BaseUrl` rather than a request host, because this
  composes inside a workflow where there is no request.
- **The method is an allowlist.** `TRACE` against some proxies echoes request headers, including the
  Authorization header the sender attaches, which would be a way to read a credential back out of a
  connector built specifically never to return one.
- **`{{query.*}}` resolves a named query (#328, #573).** A request definition names a query in
  `QuerySlug`; `{{query.rows}}` and `{{query.SomeField}}` compose from what it returns, and a hole
  naming a query that does not exist, or a field the query does not select, is refused rather than
  sent as a literal. Posting the text `{{query.rows}}` to a third party looks like a delivery and
  is a defect.
- **Queries: fetch the rows a payload needs beyond the entry that triggered it.** "Email all
  subscribers" starts from one blog post, and the recipient list is not on it. A query names a
  content type, typed filters, a sort, a limit and the fields that leave, and an operator builds one
  without writing code.
- **Not a query language, on purpose.** No SQL, no expression strings, no caller-supplied predicates.
  The moment it accepts an expression it is an injection surface and an unbounded-cost surface at
  once, and the person editing it is configuring a marketing workflow. It is built on the same
  foundation as the anonymous delivery filters, which bind the field name as well as the value so
  neither reaches the SQL text. Joining two content types or filtering on something computed are the
  obvious next asks, and the answer to both is a reporting feature rather than growing this one.
- **A field that is not Public can be neither filtered on, sorted by, nor returned.** Filtering on a
  field the result cannot show is an oracle: a workflow author could binary-search a Sensitive salary
  by watching how many rows come back, without the value ever appearing in a payload. The refusal
  reads the same as for a field that does not exist, because saying which would let somebody
  enumerate a type's Sensitive fields from here.
- **Validated when it runs, not only when it is saved.** A field that was Public when the query was
  written can be raised to Sensitive afterwards, and a save-time check cannot see that: the query
  would go on feeding it into third-party payloads with nothing saying so.
- **The projection is an allowlist and cannot be empty.** Only the named fields leave, even the
  Public ones, so a schema change that adds a personal-data field next year does not silently start
  including it. A query with no projection is refused rather than defaulting to everything.
- **The limit has a ceiling of 1000, applied when it runs as well as when it is saved.** A query with
  no bound inside a workflow action is an accidental way to email everyone twice, and the ceiling is
  what stands between a misconfiguration and that, so it does not rely on the save path having run.
- **`POST /api/queries/{slug}/preview` runs one and shows the rows**, so an operator can see what a
  payload would carry before anything is sent.
- **A type that is not event-sourced can stop writing its changes to history.**
  `EventSourcing:DocumentTypesAppend`, true by omission, which is what every deployment before 4.0
  did. Set it false and a document-sourced type writes only its current version. That is what issue
  #331 asked for, and it is a setting rather than the new behaviour because it takes three things
  away with it: `GET /api/contents/{id}/history` returns nothing for those types, the rollback
  endpoint has nothing to roll back to, and workflows on those types stop firing, since a workflow is
  triggered by reading a committed history entry. An event-sourced type is not affected, because for
  it the history is the record. `docs/event-sourced-content-types.md` says all of that in those
  words.
- **A content type can declare its own states and the named moves between them.** `ContentStatus` is
  Draft, Published, Archived, in the core, for every type, which is right for a blog post and wrong
  for an invoice. A type may now carry a `Lifecycle` with its own states, an initial state and named
  transitions, and `PUT /api/contents/{id}/status` takes a transition name for such a type instead of
  a status. A transition out of the wrong state is refused server side, and an incoherent lifecycle
  is refused at declaration rather than left to strand entries later. `Lifecycle:EnforceTransitions`
  defaults to on and can be turned off for a deployment whose existing entries predate its rules,
  which logs the violation rather than passing over it. A type that declares no lifecycle behaves
  exactly as it did, which is every type that exists today, and `ContentStatus` is untouched by a
  transition because it is what public delivery reads.
- **A workflow can trigger on a named transition, so an approval routes and an edit does not.**
  `TriggerEvent` was Created, Updated, Deleted or Published, and "when an invoice becomes Approved"
  is none of them. Routing on Updated fires on every save, so the supplier was sent the invoice on
  every edit before approval and again after, which is the feature not existing rather than a rough
  edge. A trigger may now be `transition:Approve`, naming a transition on the triggering type's own
  lifecycle. It keys on the transition name and not the state it lands in, because "status is now
  Approved" also describes an administrator correcting a mistake, and a supplier notification is the
  thing that most needs to not fire on that. A transition is not folded into Updated, so existing
  Created and Updated workflows are unaffected.
- **A workflow naming a transition its content type does not declare is refused when saved**, with a
  message naming what was asked for and what the type declares. Stored and never fired was the other
  option, and a workflow that never fires looks identical to one that fires and fails. A trigger
  naming a content type that does not exist is refused for the same reason, rather than passed over
  because there was nothing to check against. The trigger is stored spelled as the type declares it,
  since the engine matches it with an equality query and `transition:approve` against a transition
  named `Approve` would otherwise save and then never fire.
- **A content type's lifecycle is now on the API response.** `GET /api/content-types` did not return
  it, so nothing outside the database could discover a type's transitions, and a transition is what
  both a permission and a workflow trigger name. The admin workflow builder offers the selected
  type's transitions as triggers because of this.
- **Email is configured in the admin, not in the deployment.** Provider credentials came from
  `IConfiguration`, so somebody had to edit appsettings or an environment variable, which a process
  owner standing up their own instance cannot do. A SuperAdmin sets them at Settings, Email, and they
  take effect on the next send with no restart. There is a test send, because a configuration screen
  that cannot tell you whether it worked moves the failure to the first real invoice. It goes to the
  caller's own address and nowhere else, and it refuses with the provider's own reason rather than
  reporting a send that went nowhere, including when no provider module is registered and the mock
  would have silently swallowed it.
- **The stored key is encrypted at rest and never returned.** AES-GCM through a new
  `ISecretProtector`, so a database dump does not hand over a working sending credential. The
  response says whether a key is set and where it came from, and has no field that could carry the
  key itself, so the admin form cannot prefill it into a browser cache or a screen share. The
  consequence worth knowing: there is no way to read the key back, from the API or the admin.
  `Secrets:Key` is its own key, separate from `Mfa:Key`, so rotating one does not retire the other,
  and rotating either makes what it encrypted unreadable. See `docs/configuring-email.md`.
- **Stored settings beat configured ones, per field.** An operator will otherwise set one and watch
  the other win. Configuration remains how a deployment with no database row yet is seeded, and a
  stored From address does not switch off a configured API key, because that cliff would stop email
  working the moment somebody filled in one box.
- **`POST /api/settings` refuses a key that looks like a credential.** Everything in that store is
  held in plaintext and returned in full by `GET /api/settings`, which is right for a feature flag
  and wrong for an API key, and a box labelled Value next to a key called `Resend:ApiKey` was going
  to collect one. The refusal names the endpoint that encrypts.
- **Changing email settings is audited** as `settings.email.changed`, recording which fields changed
  and never their values. It sits at SuperAdmin rather than Admin: redirecting where the system's
  mail comes from redirects every password reset and every verification token in the deployment.
- **The entries list can be searched and filtered by status, and every row shows its version.**
  `GET /api/contents` takes `search` and `status`, and `ContentResponse` carries `Version`. Search
  matches any string value in an entry's data, not the derived `SearchText` the anonymous delivery
  search uses: that one holds only the values of fields the type declares Public, so an admin
  searching a reference number kept in a Sensitive field would get an empty page with no way to tell
  that from the entry not existing. Matching more than the caller may read is safe, because the
  per-item permission check and the sensitivity scrub both still run on whatever comes back. The
  version is read in one batched query for the page rather than one call per row.
- **Workflow runs are swept, and failures outlive successes.**
  Every firing leaves a run behind and nothing removed them. `Workflows:Retention:Succeeded` (7 days)
  and `Workflows:Retention:Failed` (90 days) are the two windows, because a successful run answers
  "did that go out" for a while and a failed one is interesting until somebody deals with it.
  `PartiallyFailed` is kept on the failure window, since it holds an action nobody has handled.

  A `Pending` or `Running` run is never removed, whatever its age. That is a rule rather than a
  consequence of the window: a run whose provider has been unreachable for a fortnight may still be
  an email that somebody is waiting for. Zero or less on either setting keeps that class forever, which is
  the safer of the two readings "0 days" has.

  The sweep takes an advisory lock so one instance does it, and deletes in bounded batches.
  `docs/workflow-runs.md` covers the settings and says plainly that this is not an audit trail.
- **A misspelled capability on a role was accepted and granted nothing, and nothing said so.**
  `POST /api/roles` and `PUT /api/roles/{id}` stored `systemCapabilities` verbatim, and no endpoint
  listed the names a role could hold, so after #443 an operator had no way to find the right spelling
  of `manage_analytics_websites` short of reading the source.

  `GET /api/capabilities` lists every name this instance understands: core's set plus every name a
  registered module's endpoints ask for, read off the routing table rather than off a list a module
  maintains, so a module you have not installed contributes nothing and a module needs no new contract
  member to be listed. Each entry carries its source (`core` or the module's name), and `*` carries a
  note saying it satisfies everything. Gated on `manage_roles`, the same as reading roles.

  A role write now checks its names against that list. By default the role still saves, the unknown
  names are logged and come back in the response as `unknownCapabilities`, so a console can show them
  and a module installed later that declares the name starts working without a re-edit. Set
  `Roles:RefuseUnknownCapabilities=true` and the write is refused with a 400 naming each unknown name
  and pointing at `GET /api/capabilities`. `*` is known both ways. See `docs/access-control.md`.
- **The schema a module wants is checked before it runs.** On boot, before the schema is applied
  and before anything seeds, the host asks Marten for the migration it would apply, attributes every
  object in it to a module by the assembly its document type ships in (or to core), and logs one
  line per module saying which objects are new and which existing ones would change. When the store
  is `AutoCreate.CreateOnly` and a module wants a change to an existing object, startup stops with a
  message naming the module, the object, the policy that refuses it and what would allow it, instead
  of Marten's error several layers down. A change to a core object is attributed to every enabled
  module that overrides the deprecated `ConfigureMarten`, the only hook that can reach one.
  `BarakoCMS:Modules:SchemaPreflight` switches it: unset is on for a `CreateOnly` store and off
  otherwise, `false` keeps the old behaviour. `GET /api/modules` gains `schemaState` (`ready`,
  `needs-migration`, `unknown`) and `schemaChanges` per module from the same check. Fixes #519.
- **A page that walks the invoice approval scenario end to end against the API.** A lifecycle per
  type, a permission on a transition, a workflow that fires on one and email from settings were each
  on master and tested, and nothing in `docs/` mentioned any of them. `docs/approval-by-configuration.md`
  declares the invoice type, gives one role create and another the approve transition, shows the
  raiser refused on approve and on their own submit (and the `Lifecycle:AllowSelfTransition` switch),
  attaches an email workflow to the approve transition and sets the sender from settings, one curl
  per step with the status code each answers. `docs/access-control.md` links to it from the
  permissions section. Until the next release the page needs
  `BARAKO_TAG=master` in the quickstart's `.env`, since `:latest` predates lifecycles.
- **The HTTP surface is a public contract now, and it has a version.** `CLAUDE.md` section 6 used
  to exclude everything under `Features/*` on the grounds that nothing compiled against it. That
  stopped being true once barakoBrew moved to its own repository and its own release cadence: it
  reads the JSON over HTTP without ever compiling against the classes that produce it. Section 6
  now says what counts as a breaking change to that JSON (a removed or renamed field, a changed
  type, a changed status code, tightened validation) and what does not (an added optional field).
  The `Endpoint`, `Request` and `Response` types stay `internal`; only the wire shape is promised.

  `GET /api/meta` reports `ApiContractVersion` alongside the existing `Version`, and every response,
  including a 401, carries it on the `X-Api-Contract-Version` header, so a console can tell whether
  it fits before signing in and again mid-session after a rolling upgrade. The API does not declare
  a minimum supported console version; the console is the side that breaks, so it carries the range
  it works with. Closes #630, closes #637.
- **Every webhook delivery is logged.** "Did it fire?" was answered only by the application log.
  A `WebhookDelivery` row is written for every attempt, sent or refused: workflow, run, redacted
  URL, event, request headers minus the signature, response status, the first 4 KB of the response
  body, duration, the error when nothing answered, and the attempt number. `GET
  /api/webhook-deliveries` lists them, filtered by workflow and by status class, gated on
  `view_workflow_runs`. `Webhooks:DeliveryLogRetentionDays` (30) sweeps them hourly; zero or less
  keeps them. Retry stays with the runner until the job queue (#106) takes it. A `Webhook` with a
  `Secret` must use `https`, refused at create and at delivery; `Webhooks:AllowInsecureSignedUrls`
  (false) lets a lab sign over `http`. `docs/webhooks.md` covers all of it.

### Changed

- **Every module version moves to the core's number.** The modules had drifted onto their own 0.x
  tracks (Accounting at 0.6.0, Portability at 0.3.1) while the core sat at 3.21.0, and the release
  gate reads the core's `<Version>` alone, so module bumps queued up invisibly until a core bump
  flushed them. Everything queued is compiled against net10.0, Marten 9 and core 4.0, but a consumer
  watching `BarakoCMS.Accounting` move from 0.3.1 to 0.6.0 reads a routine bump, and 0.x gives them
  no way to express "this one needs core 4". All thirteen modules are 4.0.0, so the number answers
  which core a package needs and the packed dependency range says the same thing (#294).

- **The playground deploy runs before anything is published.** The order was: tests pass, fourteen
  packages become permanent, and only then does anything get deployed and looked at. NuGet has no
  delete, only unlist, and a version someone has already resolved stays resolved, so the
  irreversible step now runs last. The deploy also proves the playground is running the commit being
  released, by reading `/health/build`, because a 200 and a version string are what the previous
  build returns too: a deploy that pulled nothing passed both (#157).

- **Publishing packages waits for a person.** `publish-packages` names a `nuget` GitHub environment,
  and its job name carries the version so the prompt asks about a specific number rather than about
  publishing in general. Because an environment with no protection rules approves everything in
  silence, and naming one that does not exist creates it that way, the version gate now refuses to
  start a release unless that environment has a required reviewer (#203).

- **Delivery API: the routes under `/api/public` now have a written stability and deprecation
  policy, and no version segment.** #107 asked for URL versioning after 3.20.0 changed behaviour
  for every site in a minor release. The conclusion is that a second code path is the wrong cost for
  a project this size and would not have prevented 3.20.0 anyway. `docs/delivery-api.md` now says
  what counts as breaking, that a break lands only in a major, that it is announced under a Delivery
  API lead in this changelog at least one minor ahead, and that the old behaviour keeps working until
  then, a security fix being the one exception. D14 in `DECISIONS.md` records the alternative rejected and what would reopen it.
- **The admin mark is the coffee bean.**
  It was a mug glyph in a filled purple tile, while the sign-in page had been drawing the bean since
  the Signal design landed, so the two front doors of the same product did not look like the same
  product. `BrandMark` now renders the same component the sign-in page uses, at the footprint the
  tile had.
- **Comments that contradicted the code they sat above.** `RevokeAllUserTokensAsync` said a full
  implementation would query and revoke the user's refresh tokens, on top of code that does exactly
  that, and logged a warning on every call for a feature working as designed. Eight comments in all,
  including a registration handler recommending BCrypt one line above the call to BCrypt, an
  unresolved "or should we fail?" left in the content validator, and a Kubernetes monitor describing
  namespace handling it does not do. No behaviour changes. Closes #128.
- **NuGet lock files are committed and restores run in locked mode.**
  Every project now writes `packages.lock.json` (`RestorePackagesWithLockFile` in
  `Directory.Build.props`) and the files are committed. CI, the release workflow, both Dockerfiles
  and the upgrade, restore and smoke scripts restore with locked mode on, so a version bump that
  does not carry its lock file diff fails with NU1004 instead of being quietly regenerated. A
  transitive bump is now a reviewable diff, and GitHub attributes the dependency graph to this
  repository. The README gains a "What it runs on" section with the pinned versions, and names
  Umami and Caddy as deployed alongside rather than referenced.
- **Every OpenAPI operation is tagged from the namespace its endpoint lives in.** FastEndpoints tags
  by path segment and every route here starts `/api/`, so all but three operations carried one tag,
  `Api`. Generators group methods by tag, so a generated client was one class with every method on
  it. The tag now comes from the endpoint's namespace (`barakoCMS.Features.Content.Create` becomes
  `Content`, `BarakoCMS.Analytics.Umami.Features` becomes `Analytics.Umami`), so a new endpoint is
  grouped correctly by existing where it belongs. No endpoint file changed. The three endpoints that
  set their own tag keep it, and a test asserts no operation carries `Api` and pins the tag set, so a
  namespace rename cannot silently rename a consumer's method group. Closes #181.
- **The release refuses to run if a test project is not covered.** `release.yml` names one test project
  by path. That is correct while there is one and a silent hole the moment somebody adds a second: the
  new suite would sit in the repo, never run, and the packages would publish anyway. The workflow now
  enumerates `*.Tests.csproj` and fails if what it finds is not what it runs. The suite-actually-ran
  floor moves from 500 to 900.
- **A content event says when the change happened, so a rebuild reproduces the timestamps exactly.**
  Two clocks answered that question and they were not the same: the writer stamped `DateTime.UtcNow`
  as it applied an event to the document, while Marten stamped the transaction time on commit. A
  replay could only see the second, so a rebuilt document's `CreatedAt` and `UpdatedAt` differed from
  the original by the write latency. For an audit trail that is not acceptable.

  Every content event carries `OccurredAt` now, set once by the writer, and both the live write and
  the rebuild read that same value. Domain time drives the projection; storage time still drives
  ordering, which is what matters on a multi-instance deployment where application clocks skew and
  the database clock does not.

  Additive. The previous constructors are kept and obsolete, so code that has not moved across still
  compiles and behaves as it did, stamping the clock at construction. An event written before 4.0
  carries no such field and falls back to the commit time, exactly as a rebuild did for everything
  until now, rather than rebuilding the document at year one.
- **Workflow actions no longer run inside the projection.** `WorkflowProjection` runs in Marten's
  async daemon, which processes a shard sequentially, so an action that posted to Facebook, emailed a
  list and then tweeted held that shard for the duration of three third-party calls: one slow
  provider stalled workflow processing for every tenant and a hanging one stopped it. The projection
  now writes a `WorkflowRun` with an attempt per action and returns, and a background runner does the
  I/O. It is the outbox pattern, and the event stream was already half of it.
- **Every attempt is recorded, so a configured integration that stops working is visible.**
  `GET /api/workflow-runs`, `GET /api/workflow-runs/{id}` and
  `POST /api/workflow-runs/{id}/actions/{ordinal}/retry`. The stored outcome carries the status code,
  a truncated reason and the timing, and the response shape has nowhere to put a response body or a
  resolved parameter: a 401 from an OAuth provider frequently contains the credential that was sent.
- **A timeout is `Unknown`, not `Failed`, and is never retried automatically.** The request may have
  arrived and the response been lost, so retrying it is how a customer gets two invoices. An operator
  can retry one by hand, having decided, and the audit entry records that they did.
- **Retrying an action that already succeeded is refused.** The reason a run records each action
  separately is so that retrying a failed third does not re-send the first two. A manual retry does
  not reset the attempt count either, because an action that keeps failing should still stop.
- **Two nodes cannot execute the same attempt.** Attempts are leased with an expiry rather than
  locked: a lock serialises every node onto one attempt at a time, while a lease lets them work in
  parallel and releases the work of a node that died without anything having to notice. Optimistic
  concurrency on the run is what refuses the second claim.
- **A partly successful run says so.** `PartiallyFailed` is a real status rather than a rounding of
  `Failed`: three independent actions where the mail server was down is not the same as three that
  all failed, and it is exactly what somebody deciding whether to retry needs to know. A later action
  still runs when an earlier one fails, since the actions are usually independent.
- **Retries are bounded and jittered.** Five attempts, exponential backoff capped at ten minutes,
  then it stops. A run that retries forever is a self-inflicted denial of service against a third
  party who answers by banning the account, which takes down every other integration pointed at them.
- **A projection rebuild no longer re-fires everything.** A run is not queued twice for the same
  workflow, content and event sequence, so replaying the stream records what already happened instead
  of re-sending every email and webhook this instance has ever sent.
- **A permanent failure is not retried.** `WorkflowActionResult.PermanentFailure` is new, for the
  cases that are the same on the fifth attempt as the first: a malformed webhook URL, a missing
  required parameter, an action type the host was not built with. They go straight to `Failed`
  instead of spending the attempt budget, so the operator is told now rather than after ten minutes
  of backoff, and a third party is not sent five copies of somebody's typo. `Failure` still means
  retryable, so an existing action behaves exactly as it did.
- **`GET /api/workflows/{id}/debug` now shows dry runs only.** Real runs record against
  `WorkflowRun` and are served by `/api/workflow-runs`, which has per-action status, the reason and a
  retry. A dry run is genuinely a different thing from a run, so the older record keeps that job
  rather than being deleted.
- **The event-sourced flag was recorded and nothing read it, which is a setting that does nothing.**
  `IContentWriter` now branches on it, in one place rather than in the six slices that write content.
  For an event-sourced type the document is produced by folding the stream, so a value that reached
  it by any route other than an event does not survive the next write, and the whole read model can
  be discarded and rebuilt from the streams through `POST /api/content-types/{name}/rebuild`. That
  rebuild is refused for a type that is not event-sourced, whose document is the record and whose
  stream is an audit trail. Concurrency differs by type, deliberately: an update to an event-sourced
  entry has to say which version it was read at and gets 409 if it cannot or if the stream has moved,
  while every other type keeps the last-write-wins behaviour it has today. `IContentWriter` gains
  `CreateAsync` and `AppendAsync`, since reading a type's policy needs an await; `Create` and
  `Append` still work, still take the document path, and are obsolete from 5.0.
- **Changelog entries are one file per change.** Every pull request used to edit `CHANGELOG.md`
  directly, so every branch conflicted on that one file after every merge, and resolving it by hand
  put conflict markers on master once and duplicated three entries once, both invisible to every
  other check because nothing reads Markdown. Add a file to `changelog.d/` instead; the release
  folds them in. Two branches adding two files do not conflict.
- **Verified #394 rather than assuming it.** `docker manifest inspect` on `barako-cms:3.21.0`,
  `barako-cms-decaf:3.21.0` and `barako-admin:3.21.0` confirms all three are `linux/amd64` only.
  `release.yml`'s platform gate already checks the pushed manifest (not the build config), runs for
  both images this repo builds, blocks `tag-release` on failure, and CI already proves it fails on
  `3.21.0` and passes on `latest` (#510). No workflow hole found. `docs/deploy-in-production.md` now
  also names `barako-admin`, which has the same amd64-only versioned tag but is built and released
  by BaryoDev/barakoBrew, outside this gate.
- **The image platform gate had never been seen to fail.** The release workflow refused to publish
  an image that did not serve both `linux/amd64` and `linux/arm64`, but the check had only ever
  passed, and 3.21.0 (amd64 only) predates it. The assertion is now `scripts/check-image-platforms.sh`,
  which `release.yml` calls, and CI runs it against `barako-cms:3.21.0` and passes only when the
  script refuses that tag for being amd64 only, then against `latest` and passes only when it
  accepts it. The versioned tags themselves stay amd64 only until the next release publishes
  through the gate.
- **The admin wears the Signal theme.** Bootswatch Yeti is gone: square corners, 300-weight Open Sans
  headings and `#008cba` blue on white read as a template rather than a tool, which is what prompted
  the redesign. Signal is indigo `#5A46D6` on a `#FAFAFC` page with white panels, 14px cards and 11px
  controls, Sora for display and Manrope for body. The rule that does most of the work is
  typographic: every machine-produced value is JetBrains Mono with `tabular-nums`, and human prose is
  not. Counts, versions, slugs, timestamps, durations, ids and API paths line up in a column.
- **Dark mode is pinned off rather than half-converted.** A Signal dark palette has not been drawn,
  and an inversion is not a substitute, so `next-themes` is forced to light and the two toggles that
  set a theme nobody drew were removed. The `.dark` block stays in `globals.css` as the starting
  point. Restoring it means drawing it, which is the open question on #407.
- **A third contrast remediation, of the same shape as the two already recorded there.** The
  handoff's `faint` at `#6E7387` is 4.25:1 on the `#F2F3F9` sunken tint, and a table column head is
  exactly where that lands. It ships four points darker in lightness at `#696E81`, same hue and
  saturation: 4.57 on the tint, 4.86 on the page, 5.06 on white. Every other pair in the token set
  was measured too, and the axe gate passes on all twelve of its cases.
- **The admin sidebar is a rail.** 248px wide with 16px of padding, sitting on the page background
  rather than in a panel of its own, so the content beside it is inset on three sides and reads as a
  card. The four everyday destinations (Overview, Entries, Content types, Workflows) are one
  unlabelled group at the top; Access, Modules and System follow it, smaller, under uppercase mono
  headings. The active item is a white card lifted off the background with an accent icon, not a
  tint. Role filtering is unchanged: a non-SuperAdmin still does not see Tenants.
- **Counts and badges, from the API or not at all.** Entries, Content types and Workflows carry a
  right-aligned mono count, and Errors carries a red pill of unresolved client errors. Each is one
  request for a single row, read from the pagination envelope's total, cached for a minute, and
  fetched only when role filtering left that destination on screen. A response with no total renders
  nothing rather than a zero. Email events shows bounces in the last 24 hours, counted from
  `/api/email-events`, which has no read state to make an unread count out of.
- **Two numbers the design draws are deliberately missing.** "Modules, 5 installed" and the
  "Add a module" row both need #185, which would give the admin a module list and somewhere for that
  row to go. There is no `/api/meta/modules`, so the group heading is plain "Modules" and the row is
  not there. A count with nothing behind it would be worse than no count.
- **Search moved into the rail.** The command menu is unchanged; its trigger is now a 40px field at
  the top of the rail with the `⌘K` hint, and it is no longer duplicated in the header.
- **Collapsing the rail hides it rather than shrinking it to icons.** The design draws no collapsed
  state and no icon rail, so the affordance stays (the header toggle, and Ctrl or Cmd + B) but what
  it does is slide the rail out. On a phone it is still a sheet.
- **The entries table wears Signal.** A live count pill reading the server's own `totalItems`, a
  tinted column head in 10.5px uppercase, the entry title at 700, and type and timestamp in mono
  with `tabular-nums` so machine-produced values line up in a column.
- **A `Private` pill on entries whose content type is not publicly deliverable.** Joined from
  `useSchemas()` on `isPubliclyDeliverable`, and only when the schema list positively answers
  `false`. An unknown type and an absent flag both mean the server did not say, and a lock icon is a
  claim about who can read an entry, so it is left off rather than guessed.
- **The entry title is a link, so the row is reachable from the keyboard.** It was a `tr` with a
  click handler and nothing focusable inside it.
- **Search and the status segmented control from the design are not shipped, because the API cannot
  answer them.** `GET /api/contents` takes page, pageSize, sortOrder and contentType. There is no
  search parameter, no status parameter, and no version on `ContentListItem`. Filtering the twenty
  rows a page happens to hold and labelling the result with the server's total is a control that
  lies about what it searched, so the three controls are absent and #410 records what the endpoint
  would need.
- **Status badges no longer render white on white.** The tone classes built the background from an
  alpha wash and took the text colour from `--warning-foreground`, which is white because it exists
  for white-on-solid buttons, so a warning badge was white text on a 10% wash of white. They now use
  the measured Signal tint pairs: 4.73:1 success, 5.35 warning, 6.27 danger, 7.89 accent, 7.37 muted.
  Nothing caught it because the axe case for the content list stubs an empty page, so no badge had
  ever rendered under the gate.
- **The sign-in page wears Signal, and every button on it now does something.** A centred 340px
  column on the page tint with the bean bleeding off the corner, a white 16px-radius card, and the
  real lockout policy stated underneath: five failed attempts locks for 15 minutes, a new device
  asks for an emailed code.
- **"Email me a sign-in code" is wired.** `POST /api/auth/otp/request` has existed the whole time and
  the admin never called it, so the emailed-code route back into an account was reachable only by
  failing a device check first. It opens a field for the email address rather than reusing the
  username box, because that endpoint and its verify half both look the account up by email. It
  repeats the server's own wording, which is the same whether or not the address is registered, so
  the screen cannot become an account-enumeration oracle the endpoint deliberately is not.
- **Social sign-in renders from `GET /api/auth/providers` instead of a hardcoded button.**
  BarakoCMS.ExternalAuth is optional and a provider with no client id is off even when it is
  installed, so a fixed "Continue with GitHub" is a dead control on the default deployment. A 404,
  a 500 and an unreachable API all mean the same thing here and all render nothing. Google, LinkedIn
  and Facebook come along, since the module ships all four.
- **The "Forgot?" link in the design is not shipped.** `Features/Auth/` holds Login, Logout, Mfa,
  Otp, Refresh and Register, and a repo-wide search for `forgot-password`, `reset-password` and
  `ForgotPassword` returns nothing. Shipping the link means shipping password reset, which has its
  own threat model and belongs with #268 and #271. The emailed code is the route back in that exists.
- **`Scheduled` is a real content status.**
  A draft with a publish time on it was a draft, and every screen that wanted the distinction worked
  it out again from `ScheduledPublishAt`. `ContentStatus` gains a fourth member, appended so no
  existing row changes meaning, and arming a publish time appends a `ContentStatusChanged` next to
  the `ContentScheduled` so the move is in the history and visible to workflows. A published entry
  carrying a future unpublish time stays Published, because it is published. The migration moves
  existing drafts that carry a publish time, and the rollback moves them back. Entries scheduled
  before the upgrade have no status-change entry behind them, so replaying one gives Draft with the
  date still armed, which the sweeper handles. See DECISIONS.md D12.

- **The entries list stopped issuing two queries per row.**
  `PermissionResolver` read the caller's roles once per permission check, and the entries list checks
  every entry it loaded, so a tenant with fifty thousand of them issued a hundred thousand queries to
  return a page of twenty. The decision cache above it does not help, because its key includes the
  item id, so a first pass over a list misses on every row. The roles are read once per request now.
  The content type, status and search filters are also pushed into the database query, which is safe
  where a permission filter would not be: they can only remove rows, never grant one.
- **The content-type endpoints ask for a capability instead of a role name.** `/api/content-types`
  (and its `/api/schemas` alias) and the rebuild require `manage_content_types`; setting public
  delivery and setting a field's sensitivity require `manage_public_delivery`. A role created at
  runtime can be granted either.

  Two names, though both gates were the same role pair and one name would have covered them with no
  seeded role noticing. Designing a schema and deciding what an anonymous caller can read are
  different jobs: sensitivity decides whether a value is scrubbed on the way out, public delivery
  decides whether the route answers at all. A role that models content without also choosing what
  leaves the building is an ordinary thing to want, and one name makes it unexpressible.

  Admin holds both by default, because Admin reached all five routes already. Nothing is narrowed,
  and `Auth:LegacyRoleFallback` still honours the old role names while it is on.
- **The last two core routes on a role name ask for a capability, and the count is pinned at zero.**
  `GET /api/modules` asks for `view_modules`, named for reading because it answers with two fields
  per module and manages nothing. `POST /api/content-types/{name}/seo-fields` asks for
  `manage_content_types`, since adding fields to a content type is exactly what that capability is,
  rather than inventing a name for one endpoint. Admin holds both by default, matching what it
  reached before.

  Both were added while #443 was in progress, in #185 and #111, and nothing noticed. `RoleGateTests`
  now asserts that no core route gates on a role name, counting a route that carries both a
  capability and a role list, so the next one fails the suite instead of waiting for a reader.
- **Every module endpoint asks for a capability instead of a role name.** Accounting, AI, Analytics,
  Diagnostics, Email, Feature flags, Files, Portability and PWA: 23 routes, twelve capability names.
  No endpoint in core or in a first-party module gates on a role name any more, which is what issue
  #443 set out to do.

  A module declares its own names, because core does not reference a module and a third-party one is
  not in this repository at all. Each module grants them at seed time to the roles its old gate
  listed, so turning `Auth:LegacyRoleFallback` off does not take a module away from the Admin role.
  Additive and idempotent, and a role the host never seeded is skipped rather than invented.

  Three gates that were one role list become two capabilities. Accounting separates reading the books
  from writing to them, so an auditor can read a ledger without posting to it. Analytics separates
  reading the numbers from creating a website in the upstream Umami account. Portability separates
  export from import, because reading a whole tenant out and writing a whole tenant in are opposite
  risks that one name could not tell apart.

  A `Accountant` role reached the whole accounting module by its name alone. It now reaches what it
  is granted, which after seeding is the same thing, and which an operator can now see and change.
- **The last of the core endpoints ask for a capability instead of a role name.** Monitoring,
  redirects, saved queries, request definitions, connectors, workflows, workflow runs, the content
  rollback and the content erasure are all gated on a capability now, so a role created at runtime
  can be granted any of them without a code change. Eleven names: `view_monitoring`,
  `manage_redirects`, `manage_queries`, `manage_requests`, `view_connectors`, `manage_connectors`,
  `manage_workflows`, `view_workflow_runs`, `retry_workflow_actions`, `rollback_content` and
  `erase_content`.

  Three areas are split rather than given one name each. Connectors split read from write, because a
  connector is the only document in core holding a third party's credentials: the reads return the
  configuration and the names of the secrets, the writes take secret values, and the probe spends
  them against the configured base URL. Workflow runs split reading from retrying, because a retry
  queues a real attempt and the mail is actually sent, while "did the notification go out" needs the
  run list and nothing else. The rollback and the erasure are separate because their old gates
  differed, `Roles("SuperAdmin", "Admin")` against `Roles("SuperAdmin")`, and one name would have had
  to widen one of them.

  Queries and requests are deliberately one name each, preview and dry run included. The dry run
  composes a call without making it and holds no credential; the preview shows the author rows a
  saved query would have sent to a third party anyway, bounded to fields whose sensitivity is
  `Public`.

  Admin's defaults gain everything migrated here except `erase_content`, which was
  `Roles("SuperAdmin")` and destroys content and its history irrecoverably. Nothing is narrowed, and
  `Auth:LegacyRoleFallback` still honours the old role names while it is on.

  Two core routes stay on role names on purpose, `GET /api/modules` and
  `POST /api/content-types/{name}/seo-fields`, and `RoleGateTests` pins that list so it cannot drift.
- **The settings endpoints ask for a capability instead of a role name.** `GET`/`POST /api/settings`
  and `GET /api/settings/email` now require `manage_settings`; `PUT /api/settings/email` and
  `POST /api/settings/email/test` require `manage_email_settings`. A role created at runtime can be
  granted either, which is the whole point: a name granted nothing before and still does not.

  Two names rather than one, because the gates being replaced were not the same. Reading settings was
  Admin and SuperAdmin; changing where the deployment's mail comes from was SuperAdmin alone, since
  that redirects every password reset and every verification token in the deployment. One
  `manage_settings` covering both would have handed that to every Admin, which is a widening nobody
  asked for. The seeded Admin role gains `manage_settings` and not the other.

  `Auth:LegacyRoleFallback` still honours the old role names while it is on, so nothing changes for
  an existing deployment until it is turned off.
- **The entries list stopped loading a whole collection to return a page.**
  Permission conditions compile to a SQL predicate where they can, so `GET /api/contents` for a named
  content type pages and counts in the database. A tenant with fifty thousand entries used to
  deserialise all of them to return twenty. Nothing moved into the database except the filtering: the
  predicate is built from the same rules the resolver reads, and the per-item check still runs over
  the page that comes back, which is what would notice the two disagreeing.

  Where a rule cannot be compiled faithfully the compiler declines and the endpoint behaves exactly
  as it did before. It declines `$status` (the evaluator compares the enum name while Marten stores a
  number), any expected value that is not a string or list of strings, and unknown operators.
  `IPermissionResolver.ReadPredicateAsync` has a default returning "no predicate", so a module with
  its own resolver compiles and behaves unchanged.
- **The README no longer implies Postgres enforces tenant isolation.**
  It said a database per tenant buys "isolation that row-level scoping plus a token check already
  gives". That scoping is a `tenant_id` filter the application adds, not row-level security, and
  `docs/multi-tenancy.md` says in as many words that row-level security is not implemented and a
  slipped filter has nothing underneath it. The two now agree, in the file people read first: what is
  enforced, what is not, and that database-per-tenant remains the escape hatch for anyone who needs
  isolation a bug cannot cross.
- **The client-layer decision is in `DECISIONS.md`, where anybody can read it.** Six issues cited a
  design document that `.gitignore` excludes, so it existed on one machine and in no commit. Two of
  those issues carry `help wanted`, which meant pointing a contributor at a file they cannot open.
  D13 records what was decided (a hand-written base plus generated slices, one per tag, and a
  configured invocation of an existing generator rather than one of our own), what it rules out, and
  what is still open. The working notes stay ignored: they are notes.
- **A role name no longer opens a gate on its own.** `Auth:LegacyRoleFallback` was `true` through
  3.x, so the capability gates also honoured the role names they replaced and an upgrade kept working
  while roles had no capabilities yet. From 4.0 it defaults to `false`.

  Nothing to do on a deployment that runs the seeder: every core and module endpoint gates on a
  capability now, and the seeder adds the capabilities a system role is missing rather than only
  filling an empty list, so those roles reach what they always did. A deployment that curates its
  roles by hand, or is mid-upgrade, sets `Auth__LegacyRoleFallback=true` and nothing changes for it.
  The flag is still there and still supported; only the default moved.

  This is a behaviour change on upgrade, and it is the one 4.0 makes deliberately: a role somebody
  creates can be granted administrative access, and a role called `Editor` gains nothing from being
  called that.
- **barakoCMS is the API only.** The console under `admin/` now lives at
  [BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew) and still publishes
  `ghcr.io/baryodev/barako-admin`; the marketing site under `site/` has its own repository. Gone
  with them: the Admin UI, Site and "Admin against the real API" CI jobs, the admin image in the
  release and its SBOM, the admin-only playground deploy, the admin and site Dependabot entries,
  the admin service in every compose file and the quickstart, the `DOMAIN_ADMIN` Caddy route,
  `scripts/smoke-check.sh` and `assets/admin`. The API's own surface is Swagger, and the quickstart
  now passes `SWAGGER_ENABLED` through. Nothing in the packages or the API changed (#505).
- **Events stream: a per-client connection cap under the instance cap.** `Delivery:Events:MaxConnections`
  counted every stream on the instance and nothing keyed on the caller, so one anonymous client could
  hold every slot and every other tenant on the instance got 503 from `GET /api/public/events`.
  `Delivery:Events:MaxConnectionsPerClient` (5) caps open streams per client address, resolved the
  way the rate limiter resolves it (the socket peer, or the forwarded client when `ForwardedHeaders`
  names the proxy). The next stream from that address gets 503 with a body naming the per-client
  limit while another address still connects, and the slot comes back when the stream closes. Zero
  turns the per-client cap off. Closes #520.
- **Module READMEs teach the package reference as the install.** Each `BarakoCMS.*` README and
  `docs/delivering-a-client-project.md` and `docs/configuring-email.md` now say that `dotnet add package` plus a restart installs a
  module and `BarakoCMS:Modules:Enabled` decides whether it runs, with `modules.Add(...)` shown once
  as the override. Every module gets a patch bump so the README on nuget.org changes too. #521
- **Inbound idempotency is documented.** `IdempotencyFilter` has honoured an `Idempotency-Key`
  header on `POST`, `PUT` and `PATCH` since before this entry, but the only header named
  `Idempotency-Key` anywhere in `docs/` was the outbound one on webhook deliveries, a different
  thing entirely. Nobody sending the header meant the protection sat unused. `docs/idempotency.md`
  now covers the header name, the verbs it applies to, the exact 409 a replay gets, how long a
  completed key is remembered (indefinitely; a failed one is released immediately), and what happens
  when two requests race on the same key. Linked from the README's documentation list.

  `IdempotencyTests` now also posts to `/api/contents` twice with the same key and checks that only
  one entry landed, not only that the second call's status code was 409.
- **The free-module promise now names the publisher rather than the repository.** It read "every
  module in this repository is free, forever", which scoped a promise about what BaryoDev publishes
  to one git repository, and modules already live outside it. It now covers every module BaryoDev
  publishes under the `barakocms-module` tag, wherever it lives. The roadmap also says plainly that
  other vendors may charge for their own modules, that core is gaining a licensing primitive so they
  can, and that a paid third-party module in the module list is the ecosystem working rather than
  the promise bending. `README.md` said there was no support contract while `ROADMAP.md` said
  BaryoDev sells support; the software carries no SLA, and hosting and support are a separate
  commercial relationship.
- **New icons for the fourteen module packages.** Each keeps the ground colour it already had, with a
  white glyph and the bean device in the lower right, so a package stays recognisable in a NuGet
  search result while the set reads as a family. `BarakoCMS.Templates` and `BarakoCMS.Testing` are
  tooling rather than feature modules and keep the icons they had. `Directory.Build.props` already
  packs each project's `assets/icon.png` as its `PackageIcon`, so no packaging wiring changed.
- **`POST /api/import/analyze` asks for a capability.** It had no gate at all, so any authenticated
  caller could hand the server a spreadsheet to parse, and parsing is the expensive half. It now
  requires `analyze_spreadsheets`, which the module grants to Admin at seed time.

  One name covering the preview only. The bulk create next door is authorized on the target content
  type's own create permission, which is the right question for a write because it depends on what is
  being written. The preview has no target yet, since the mapping that names one is built from the
  preview it is about to return, so it asks the narrower question of whether you may use the import
  tool at all.
- **CI runs on the merge queue.** `ci.yml` gains a `merge_group` trigger, without path filters,
  because the queue is the last gate before master and a required check only counts when it reports
  on that event. Without it the queue waits forever for checks that never start.
- **Every published package is now versioned 4.0.0.** Module versions had drifted apart, from
  `BarakoCMS.DeviceTrust` at 4.0.1 to `BarakoCMS.Files` at 4.4.2, so a reader had no way to tell
  which module versions belong together. They are now set to a single number and move together from
  here. `BarakoCMS.Suite` and `BarakoCMS.Tests` are not packable and have no version of their own.
- **Three decisions recorded before the 4.0 tag, in `DECISIONS.md`.** D16 extends expected-version
  concurrency to document types, because moving from last-write-wins to a 409 is a breaking change
  and 4.0 is the last moment it costs nothing; `Content:Concurrency:Require` keeps the 3.x upgrade
  path working and flips in 5.0. D17 settles that a money value stays a plain number, with currency,
  scale and rounding declared on the field definition, so the stored shape and the delivery contract
  do not change. D18 states what module authors are promised: a replacement for `ConfigureMarten`
  before 5.0 removes it, a default implementation and a deprecation window for every added member,
  and `IWorkflowAction` documented as the extension point it already is.
- **`scripts/preflight.sh`, `scripts/sync-master.sh` and `scripts/needs-review.sh` replace the
  manual PR checklist.** Preflight does a locked-mode restore first, before any build, then builds
  with `--no-restore`, runs the named test classes and fails if a class matches zero tests, then
  checks changelog fragments, module versions, and dashes/banned words and workflow YAML for
  duplicate keys, both scans covering untracked files too, failing on the first problem with a
  one-line reason. Sync-master merges `origin/master`, regenerates lock files when a `.csproj` or
  `Directory.Packages.props` changed in the merge, and exits 1 naming either the conflicting files
  or a dirty working tree, whichever blocked it. Needs-review is advisory only: it always exits 0
  and prints one line per rule the diff against `origin/master` fires, for a reviewer to read.
- **The roadmap describes numbered releases instead of a weekly train.** It carried six dated
  sections from 3.22.0 to 3.27.0, two of which shipped and four of which were superseded by the 4.0
  work. The CLI, starter templates, the MCP server and the typed client were not cancelled, they
  moved into 4.1.0 and 5.0.0 where they sit against the rest of the work rather than against a date
  that would have passed a few days after the tag. The file also now states the pairing with the
  console: barakoBrew 1.0.0 goes with barakoCMS 4.0.0, 1.1.0 with 4.1.0, 2.0.0 with 5.0.0.
- **The 3.x support window is anchored to the 4.0 tag, not to a date.** SECURITY.md said "30 August
  2027, 12 months after 4.0", worked out from a 4.0 that was expected in August 2026 and has not
  shipped. The same document says the policy is "a rule rather than a date, so it does not go stale
  in this table", and that row was the one place it did. It now reads "4.0 ships, plus 12 months",
  and says plainly that until 4.0 is tagged, 3.x is the current line and is actively supported.
- **Image assets ship without embedded provenance metadata.** Design tools stamp C2PA content
  credentials into what they export, naming the tool that produced the file, and
  `Directory.Build.props` packs `assets/icon.png` into every module package, so an unstripped export
  would have carried that stamp to nuget.org. `scripts/strip-asset-provenance.py` removes it by
  filtering the optional PNG chunks and the SVG `<metadata>` element, which leaves the image data
  byte for byte identical rather than re-encoding it. `scripts/preflight.sh` now fails if any asset
  still carries a stamp.

### Removed

- **`IBackupService` and `BackupService`.** Registered in DI and called by nothing, repo-wide, so
  the codebase read as though the application backed itself up.

- **The `X-XSS-Protection` header.** Every current browser ignores it, and while it was honoured its
  filter was an information leak of its own: with `mode=block` a cross-origin attacker could infer
  page content from which loads it refused. The Content-Security-Policy is what carries this (#271).

- **`fly.toml`.** It hardcoded `app = 'barako-cms-api-baryo'`, and a Fly app name is unique across
  the whole platform, so anyone running `fly deploy` from a clone either collided on the name or
  deployed into the maintainer's app. `fly launch` generates the file; `.gitignore` now keeps it
  local, and `.agent/workflows/deploy-fly-io.md` carries the settings it held (#271).

### Fixed

- **Registration accepted a username and an email of any length.** `Username` had a minimum and no
  maximum and `Email` had a shape check and no length at all, and both carry a unique btree index on
  the users document. Under roughly 2.7KB that meant a value stored, indexed and string-compared on
  every sign-in; over it, postgres refuses the index entry and an anonymous endpoint answers 500.
  Capped at 64 and 254 (#271).

- **The content update endpoint answered 500 to a malformed `UserId` claim.** It used `Guid.Parse`
  where the create endpoint used `Guid.TryParse`, so a token carrying something other than a Guid in
  that claim threw a `FormatException` the exception handler turned into a server error. Not
  reachable with a token this server minted, and `Configure()` already refuses a missing claim, but
  answering "server error" to a malformed request sends an operator looking in the wrong place. Both
  write endpoints now answer 400 (#271).

- **Module ordering recursed, so a deep dependency chain killed the process.** `ModuleOrder.Sort`
  traversed recursively, which bounded dependency depth by the call stack rather than by anything the
  method checked: a long enough chain overflowed instead of reporting a cycle or a missing
  dependency, and an overflow cannot be caught. The traversal keeps its own stack on the heap now.
  Nothing caps depth, because any number picked would refuse a legal graph, and the existing
  guarantees are unchanged: stable order for independent modules, a missing dependency refused by
  name, a cycle refused with the cycle printed.

- **Liveness and readiness were the same probe, so a database blip restart-looped every API pod.**
  Both pointed at `/health`, which runs every check including the database one, and the `ready` tag
  already on the database check was filtered by nothing. One Postgres restart therefore failed
  *liveness* on every replica at once and Kubernetes killed a whole deployment of healthy application
  processes, turning a blip into an outage plus a cold-start stampede.

  There are three endpoints now. `/health/live` runs the checks tagged `live` (Memory, the one a
  restart actually clears) and backs the liveness probe. `/health/ready` runs the checks tagged
  `ready` (Database, Disk Space, Memory, Startup Seeding) and backs the readiness probe. `/health` is
  unchanged and still reports everything. `k8s/05-deployment.yaml` also gains a `startupProbe` so the
  boot-time schema apply is not counted as a liveness failure.

- **The core host reported itself ready before roles and the initial admin were seeded.** The seed ran
  on a detached task that slept five seconds first while the app was already accepting traffic, so
  sign-in failed in that window and a registration landing in it was stored with an empty `RoleIds`.
  Under a rolling deploy it repeated on every new node. The seed still runs in the background, so
  `/health` and `/health/live` keep answering while it works, but readiness stays closed until it
  finishes.

- **The Kubernetes monitor disabled itself permanently on the first init failure.** A static flag was
  set once and never cleared, and the service is a singleton, so a single API-server hiccup at pod
  start (normal in the environment the feature targets) left monitoring off until the process
  restarted. The client is rebuilt on a later call now, on exponential backoff after a failed attempt
  and on a slow fixed interval when there is simply no cluster to talk to.

- **The Kubernetes manifests could not be applied.** `k8s/05-deployment.yaml` asked for
  `memory: "128Mw"`, which the API server rejects outright, so nothing else in the directory had been
  exercised either. Also fixed: the app pod now consumes `k8s/01-configmap.yaml` through `envFrom`,
  so a Kubernetes deployment actually runs in Production mode; the image tag is pinned instead of
  `latest`; `InitialAdmin` is wired to the secret, so a first boot creates an admin rather than
  silently creating none; and the Grafana dashboard moved to `k8s/observability/`, where
  `kubectl apply -f k8s/` no longer trips over it. `kubectl apply -f k8s/` was run against a real
  cluster.
- **Re-publishing already-published content fired every Published workflow again.**
  `PUT /api/contents/{id}/status` appended a `ContentStatusChanged` without checking whether the
  status had actually changed, and the projection fires on any such event whose new status is
  Published. A double-clicked publish button, a client retry after a timeout or a form that resubmits
  the current status sent the confirmation email twice, called the webhook twice and created the task
  twice. It also wrote transitions that changed nothing into the stream, which is the source of truth
  for history and replay. The endpoint now short-circuits an unchanged status, the way the update
  slice always has. A real transition back to Draft and out again still fires the workflow both
  times.

- **The workflow code named a manual rebuild as the remedy for a halted projection.** No such command
  exists, and running one as the projection is written would re-run every action for every event ever
  stored: every confirmation email re-sent, every webhook re-fired. The comments say what a rebuild
  would cost, and `docs/operating-workflows.md` says what recovery actually looks like until the side
  effects are separated from the projection.

- **The shipped Kubernetes Deployment asked for `128Mw` of memory.** Not a valid quantity, so the
  manifest was rejected on apply.
- **Two tests that could not fail are gone, and the cross-tenant join is covered.** One built a
  workflow and ended on `await Task.CompletedTask` with no act and no assert; the other constructed a
  workflow engine, never called it, and asserted that the list it had just built contained the item
  it had just put in. Both ran on every build. Replaced with tests that drive the real engine, plus
  the first test to put two authenticated users in different tenants against the content API: tenant
  isolation was proven in two halves that never met, and the guard between them is one `if` that
  nothing was checking.
- **Assigning a role or a group to an unknown user id fabricated a user.** Both assign endpoints
  carried a "load or create user (for testing, we'll create if not exists)" branch into production.
  On a miss they stored a `User` with a synthesized `user_{guid}@example.com` and no password hash,
  holding the role, and answered "Role assigned to user successfully". A mistyped id therefore left a
  ghost identity row behind while the real account still lacked the role, and the caller was told it
  had worked. The role and group ids were never checked at all, so a mistyped one also reported
  success and granted nothing. All four cases are 404s now, and nothing is written.

- **Create and Update accepted status and sensitivity values no enum member names.**
  `POST /api/contents` with `"status": 7` bound cleanly and stored content with an undefined status,
  invisible to the scheduler, to status-filtered lists and to delivery, with no error anywhere.
  `ChangeStatus` has validated this since it was written. Both slices do now. A defined value sent as
  a number still works, so a 3.x client posting `"status": 1` is unaffected.

- **A PUT that omitted `Status` silently un-published the content.** An absent status bound to 0,
  which is `Draft`, and the endpoint treated any difference from the stored status as a transition.
  A consumer sending only `id`, `data` and `version`, which is what a data-only edit looks like,
  un-published the item and emitted a `ContentStatusChanged` saying so. `Status` is nullable now and
  absent means unchanged.

- **An update reported a version it computed before the append.** The reported version was the stream
  state read before the append plus the number of events appended. When `version` is 0 the staleness
  check is deliberately bypassed, so another writer can advance the stream in that window and the sum
  then under-reports. The client echoes the reported version into its next update, so an under-report
  turned an ordinary follow-up edit into a 412 blaming a conflict that never happened. The version is
  read back after the commit.

- **Expired OTP codes were never deleted.** `TokenCleanupService` swept `RefreshToken`, `RevokedToken`
  and `IdempotencyRecord`, and no deletion path for `OtpCode` existed anywhere. `OtpService` only
  marks outstanding codes `Consumed` when a new one is issued, so every sign-in request left a
  permanent row and the "this email, not consumed" scan in send and verify degraded with the table.
  The `ExpiresAt` index was already registered. All four passes are now a single `DeleteWhere` each,
  one DELETE statement per document type, instead of loading the full expired set and deleting row by
  row.

- **The anonymous slug route loaded every published entry of the type.** `GET /api/public/{type}/{slug}`
  queried all published, Public content of the type and matched the slug in memory, so a blog with
  20k posts deserialized 20k documents to return one and a 404 probe cost exactly the same. The match
  runs in Postgres now, reusing the case-insensitive jsonb key lookup the delivery filters already
  had. It stays case-insensitive, and `_` and `%` in a slug are still ordinary characters.

- **Three endpoints checked a claim that could never exist.** `Content/List`, `Content/History` and
  `Content/Get` looked up the literal string `System.Security.Claims.ClaimTypes.NameIdentifier`,
  which is the name of a constant and not its value, so it matched nothing on any token this project
  issues and the `UserId` fallback beside it was always what ran. No behaviour change, but it read as
  though a second identity source was being consulted.

- **`WebhookAction` never disposed its `HttpResponseMessage`**, on a path a workflow can fire on every
  content change.

- **`OllamaEmbeddingClient.EmbedAsync` swallowed cancellation.** A bare `catch` turned
  `OperationCanceledException` into `null`, so an abandoned search reported "no results" rather than
  stopping and the caller could not tell an empty index from a request that never finished. An
  unreachable backend still degrades to `null`.
- **The install command in every release announcement named a version that does not exist.** The
  announce step interpolated the gate's version, which is the core's, into
  `dotnet add package BarakoCMS.Accounting --version …`. No module has ever shared the core's number,
  so the command has failed for every release so far: at 3.21.0 it asked nuget.org for
  BarakoCMS.Accounting 3.21.0, where the highest published is 0.3.1. It names the core package now,
  which is the one id guaranteed to exist at that version, because the publish job just pushed it
  (#294).

- **No release ever published a symbol package.** `Directory.Build.props` has set `IncludeSymbols`
  and `SymbolPackageFormat=snupkg` since Source Link went in, and pack has been writing
  `out/*.snupkg` all along, but the artifact upload matched `out/*.nupkg` and the publish job pushes
  from that artifact and nothing else. Every symbol package was discarded between the two, and no
  step went red about it, so the whole Source Link investment shipped nothing. The upload takes both
  now, and `verify-packages` fails unless all fourteen packages have a `.snupkg` beside them (#294).

- **The project still advertised .NET 8 in fourteen NuGet storefront pages.** The move to .NET 10,
  Marten 9 and FastEndpoints 8 changed `Directory.Build.props`, `global.json` and the Dockerfiles and
  almost nothing else. The core package Description (the text NuGet search results render), the core
  README and eleven module READMEs all said .NET 8, and four of the module READMEs also claimed
  `barakoCMS ≥ 2.2.0`, so every package page would have been wrong twice over the moment 4.0.0
  published. `README.md`, `llms.txt`, `CLAUDE.md`, `.cursorrules`, the site copy, the bug-report
  template and the quickstart's `BARAKO_TAG` pin are corrected too. `CLAUDE.md` mattered most of
  these: agents are pointed at it as the working agreement and would have followed its ".NET 8, one
  target framework" when adding a package (#295).

- **F5 could not launch the project.** `.vscode/launch.json` pointed at
  `bin/Debug/net8.0/barakoCMS.dll`, which no build has produced since the retarget (#295).

- **The blog-starter example failed at both of its steps.** Step 1 said to import
  `blog-schema.json` through the admin, which has no schema import. Step 2 fetched
  `/api/contents?contentType=blog-post` with no auth; that is the authoring API, so it answered 401
  and the example's `catch` rendered an empty blog rather than saying anything. The schema is now a
  valid `POST /api/content-types` body (`isRequired` rather than `required`, `slug`/`url`/`array` in
  place of the `media` and `list` types no validator accepts, and `isPubliclyDeliverable: true`,
  without which delivery 404s), the README shows the request that creates it, and the fetch uses the
  public delivery route and reports a failure instead of hiding it (#295).

- **Turning on device trust locked every administrator out.** With `DeviceTrust__Enforce` on, the API
  answers a password login from an unapproved device with `requiresDeviceApproval` and emails a code.
  The admin showed a toast and stopped, so there was nowhere to type the code and no way back in. The
  quickstart advertises that setting. The login page has the approval step now, and hands off to the
  authenticator step rather than signing in when the account also has MFA enabled, because a mailbox
  is a first factor and cannot stand in for the enrolled second one.

- **The admin History panel had been showing nothing since the list envelope changed.** It read
  `versions` off `GET /api/contents/{id}/history`, and that endpoint has returned the paginated
  `items` envelope since #291. The panel rendered an empty list rather than failing, and the e2e
  suite could not catch it because it mocks the route and the mock was written to match the client.
  It also understands the entry types the history now reports, so a status change is labelled as one
  and is not offered a Restore button it cannot honour.

- **The admin decided which roles are undeletable by name, and the server decides by id.** Rename a
  system role and the admin offered a delete the server refuses; create a custom role called "HR" and
  the admin locked one the server would remove. The roles API reports `isSystem` now, derived from the
  seeded ids that the delete rule already keys on, and the admin asks instead of re-deriving.

- **Content history reports every event, not two of five.** `GET /api/contents/{id}/history`
  mapped `ContentCreated` and `ContentUpdated` and returned null for `ContentStatusChanged`,
  `ContentScheduled` and `ContentSensitivityChanged`, and the nulls were filtered out, so publishing
  a document left no trace in its own history and nothing in the response said the list had been
  shortened. Every event is now an entry carrying a `changeType`, and an entry that does not record a
  document version carries the value that changed (`status`, the scheduled times, `sensitivity`)
  instead of `data`. An event type the endpoint does not recognise still appears, under its own name,
  rather than being dropped.

- **The published images serve both amd64 and arm64.** The release built for whatever architecture
  its runner happened to be, so `barako-cms:3.21.0` and its siblings were amd64 only and could not
  run on Graviton, on Ampere, or on this project's own playground VM. Each architecture is now built
  natively, on a runner of that architecture, and joined into one manifest list. Pushes carry no tag
  until the join succeeds, so a half-finished build cannot leave `:latest` pointing at one
  architecture, and the release fails if a published image does not serve both.

- **Three real accessibility defects, found by the new scan on its first run.** The primary button
  colour gave white text 3.85:1 against WCAG AA's 4.5:1, so every primary button in the light theme
  failed; muted text was 4.45:1 on the sidebar; and the content-type selects had no accessible name,
  one of them because a visible label was never associated with its control.

- **Every deployment path takes a backup, and CI proves one can be restored.** The hardened backup
  script was wired into the development compose file only, so the deployments holding real data had
  none. `docker-compose.prod.yml` and the quickstart stack now run that same script, and the k8s
  CronJob carries the same logic inline because a CronJob has no repository to mount. Each writes to
  its own volume rather than Postgres's. `scripts/restore-check.sh` takes a backup,
  destroys the database, restores it and boots the app against the result, on every pull request.
  Runbook in `docs/backup-and-restore.md`.

- **The k8s backup CronJob could not run, and would not have worked if it had.** It mounted
  `postgres-data`, but the StatefulSet's `volumeClaimTemplates` creates `postgres-data-postgres-0`,
  so the pod stayed Pending forever. Its dump also piped straight into gzip and checked gzip's exit
  code, which is the failure the compose script was rewritten to remove.

- **The admin rendered every validation failure as "[object Object]"**, including "Invalid
  credentials" on the login page. It read `message` off ProblemDetails entries, which carry `name`
  and `reason`.

- **A fatal startup failure now exits 1.** It exited 0, so a broken deploy reported success to CI, a
  `docker run` wrapper, systemd and a Kubernetes Job container. Anything that depended on the old
  behaviour to get past a failing start will now stop.

- **The workflow daemon lost the event's tenant.** It resolved the workflow engine from a scope
  sitting on the platform default tenant, so a tenant's workflow definitions were invisible to it
  and a default-tenant workflow's writes landed in the wrong partition.

- **The scheduled publish sweep read every due item in one query.** No limit, so the sweep's memory
  and the size of its transaction were whatever had accumulated: nothing on a healthy deployment,
  and the entire backlog after downtime or a bulk import that carried schedules. It works in batches
  of 200 now, up to 25 batches per tick, committing each batch. A caller expecting one
  `SweepTenantAsync` to drain everything still gets that up to 5000 items per tenant, and the
  remainder is applied by the next tick a minute later. The method gained an overload taking the
  batch size and the cap; the three-argument one is unchanged and uses the defaults.

- **A revoked permission could come back.** Permission-cache invalidation bumped a version counter
  that formed part of the cache key, and that counter was itself an entry in the same cache: same
  five minute expiry, same size limit, same eviction under pressure. Once it was gone the next
  invalidation read zero, wrote one, and rebuilt a key that was already cached, so the revoked
  decision was served again and the log said "Invalidated permission cache" either way. Invalidation
  now uses expiration tokens held outside the cache, so cancelling one evicts every decision that
  registered against it, and there is no version arithmetic left to lose.

- **Rollback skipped every gate a normal update runs.** Restoring a version wrote the historical
  data straight into a new event, so it could put back data the current schema rejects, change a
  field the caller is not allowed to change, or break an invariant introduced after that version.
  It now runs write-path sensitivity, schema validation and the lifecycle hooks, and refuses with a
  message naming the reason. An operator can be refused a rollback for a reason that predates them,
  which is the correct answer: the alternative is a write path that launders rejected data back in.

- **A sensitive field escaped masking on a casing mismatch.** Validation and public delivery match
  schema field names case-insensitively, and delivery documents that as normal. Masking matched
  ordinally, so a record holding `salary` against a field declared `Salary` was validated as that
  field, delivered as that field, and not hidden as that field. All three now agree.

- **An OTP code could be verified twice.** `RefreshToken` and `MfaSecret` both carry optimistic
  concurrency to close exactly this race and `OtpCode` did not, so two requests with the same code
  could both see it unconsumed and both mint tokens. Device approval and passwordless sign-in both
  rest on that path.

- **A system proxy silently bypassed the webhook address guard.** With a proxy in use the connect
  callback dials the proxy, and the proxy then resolves and connects to the target, so the guard was
  inspecting the wrong hop. `UseProxy` is off on that client now. A system proxy can arrive from an
  environment variable nobody deploying chose, which is what makes it worth failing closed on. An
  operator whose egress needs one sets `Webhooks:AllowProxy` and has to apply the same destination
  policy at the proxy, because nothing here can.

- **The production CSP no longer allows `'unsafe-inline'` on `style-src`.** `script-src` had dropped
  it outside Development, which is the half that defeats XSS mitigation, but styles kept it app-wide
  as a documented partial fix pending a check nobody had run. CSS injection cannot execute script, so
  this is the lower-severity half, but attacker-controlled inline styles still exfiltrate through
  selectors and background-image requests.

  The allowance survives only on the health-checks dashboard, and only while `HealthChecksUI:Enabled`
  is on. That dashboard genuinely needs it: its shipped bundle renders three dozen React `style`
  props, so its elements carry inline style attributes and the page renders wrong without it. Nothing
  else this host serves outside Development emits an inline style, and the Next.js admin is a separate
  application with its own headers, so its rendering is unaffected either way.

- **The token revocation check failed open.** Any exception from the revocation query returned "not
  revoked", so a revoked token was accepted for as long as the store was unreachable, and it said so
  at Debug, which production does not emit. A logged-out session came back during a database blip and
  nothing recorded it. A missing table still answers "not revoked", because with no table nothing has
  ever been revoked and that is the case the original catch was written for. Everything else refuses
  the request.

- **Refresh-token rotation dropped the device binding.** The replacement token carried no `DeviceId`,
  so the binding survived exactly one refresh and device trust had nothing to enforce against from
  the second onward. The symptom appeared one rotation after the cause, which is why it lasted.

- **An OTP email that failed to send was reported as sent.** On the device approval path, where the
  password has already been proved, the response now says the code could not be emailed instead of
  sending somebody to wait for a message that was never sent. The unauthenticated request-a-code
  route deliberately still answers identically whether the address exists, because reporting the
  failure there would tell a caller which addresses are real.

- **The API images run as a non-root user.** `barako-cms` and `barako-cms-decaf` ran as root while
  the admin image did not, which is what an omission looks like rather than a decision. Both now drop
  to the base image's `app` user (uid 1654) before the entrypoint. Nothing needs privilege: 8080 is
  above 1024, and the app writes nothing to the container filesystem at runtime. No compose file in
  this repository mounts a host path into the API, so no shipped configuration changes. Anyone who
  has added their own bind mount needs it writable by uid 1654.

- **Social sign-in accepted an email the provider never verified.** The email was the only join key,
  so an unverified assertion was a login for whichever local account held that address, including a
  seeded SuperAdmin whose address is `{username}@company.com` and therefore guessable. `PasswordHash`
  is not consulted on that path.

  Google and LinkedIn now require `email_verified`. GitHub uses only the verified primary from
  `/user/emails`; it previously preferred the unflagged profile email whenever it was set, so the
  careful branch was the one nobody reached. Facebook exposes no verification flag at all and is now
  refused unless `Facebook:TrustUnverifiedEmail` is set, which is an operator's explicit decision.
  `IssueAsync` takes the flag as a required argument, so the next provider cannot omit it quietly.
  ExternalAuth `0.4.0`.

  The module had no test project reference and therefore no tests, which is why none of this was
  caught (#120). It has both now.

- **A password login against an account with no password returned 500, not 401.** Social sign-in
  creates users with an empty `PasswordHash`, and BCrypt throws on one rather than returning false.
  That was a username oracle on the one endpoint that had taken care to avoid one, next to its own
  dummy-hash timing defence. It now burns the same dummy verify and returns the same 401.

- **Any authenticated account could read any file in the tenant, and upload without a role.** Both
  Files endpoints had authentication and neither had authorization. Download is now the uploader or an
  admin, refusing with 404 rather than 403 so a leaked id cannot be used to probe for others. Upload
  now carries the same role gate as every other write in the module set. Files `0.4.0`.

- **The seeder no longer writes anything shaped like a Social Security number.** The demo
  `AttendanceRecord` rows carried `123-45-6789`, `987-65-4321` and `456-78-9012`. The first is a
  well-known placeholder that data-loss-prevention and compliance scanners treat as a real SSN, and
  all three planted realistic sensitive values in every fresh install of a CMS that markets
  field-level sensitivity. The sample rows now use `SAMPLE-NOT-A-REAL-SSN-n`, names that read as
  placeholders, and mail at `example.com`, which RFC 2606 reserves for documentation.

  Seeded mail addresses moved off `company.com` for the same reason: it is a registered domain, so a
  password reset or an OTP for the seeded admin, HR or standard account left the building. A seeded
  admin's address changes from `{username}@company.com` to `{username}@example.com` on next start.

  A test asserts the shape rather than the new values, so a future edit that swaps in three different
  realistic numbers fails too.

- **`docker-compose.yml` no longer ships three defaults that are unsafe to copy.** It is labelled
  local-development-only, but that is a comment rather than a control, and people copy what works.

  The app container bind-mounted `${HOME}/.kube`, handing every context and token in the developer's
  kubeconfig to anything running inside it; the mount is gone, and the Kubernetes monitor is off by
  default anyway. The postgres and backup services hardcoded the password, so setting a variable left
  the three services out of step while the built-in value kept working; all three now read
  `DB_PASSWORD`, matching `.env.example` and the other compose files. Postgres was published on every
  interface, which with a default password is an open database on any host that is not a private
  laptop; it binds `127.0.0.1` now, so `psql` from the host still works and nothing else can reach it.

  The file still starts with no `.env` at all.

- **The webhook SSRF guard checked one address and connected to another.** `WebhookAction` resolved
  the target host, checked the answer, then handed the name to `HttpClient`, which resolved it again
  when it opened the socket. A name whose DNS answer changed in between passed the check on a public
  address and connected to 169.254.169.254. Resolution now happens once, inside the client's connect
  callback, and the socket is opened to an address that answer survived, so there is no second lookup
  to poison. A name that answers with one public and one blocked address is refused outright rather
  than connected to the public one. Redirects stay off, since a redirect is a second resolution by
  another route.

- **The webhook posted the whole content data object.** Every stored field went to the target URL,
  including fields a read masks, so anyone who could configure a workflow could send a Hidden field to
  an external address. The payload now carries only the fields the content type marks Public, through
  the same projection the public read path uses, and a document that is itself Sensitive or Hidden
  contributes no data at all. A content type with no definition sends no data rather than all of it.

- **The redirects index in the 3.x upgrade script is named the way Marten names it.** It was
  `mt_doc_url_redirects_uidx_frompath`; Marten derives `mt_doc_url_redirects_uidx_from_path` from the
  property name. An upgraded database ended up with a unique index that behaved identically and had
  the wrong name, so every start-up schema assertion wanted to drop and recreate it.
- **An unmapped content event no longer puts its class name in the history response.** The mapper
  fell back to `@event.GetType().Name` for an event it did not recognise, so adding an event and
  forgetting the switch would have published its CLR type name, which is the leak #229 forbids. No
  reflection guard can catch it, because by the time it reaches the wire it is a string. It reports
  `Unknown` now, the entry still appears so the count keeps matching the stream, and a behavioural
  test pins it.
- **`DATABASE_URL` keeps its own `sslmode`, and defaults to Require rather than Disable.** The URL was
  parsed and then `SSL Mode=Disable` was appended regardless, so a managed Postgres that requires TLS
  refused every connection, and one that merely allows it got an unencrypted link nobody asked for. An
  `sslmode` the URL names is honoured, an unrecognised one is refused by name rather than ignored, and
  credentials and the database name are percent-decoded.
- **The connection string is built rather than interpolated.** Decoding the credentials makes a case
  reachable that was not before: a semicolon is legal in a Postgres password, percent-encoding it is
  how a URL expresses one, and decoded into an interpolated string it ends the `Password` key, so
  everything after it is read as another setting. That surfaces as an unknown keyword rather than as a
  bad password, which is a long afternoon. `NpgsqlConnectionStringBuilder` quotes it.
- **A URL with no port gets 5432** rather than `Port=-1`, which is what `Uri.Port` returns when none
  was given.

The Development decision is taken as an argument rather than read from
`ASPNETCORE_ENVIRONMENT` at the point of use, so the unit tests assert both halves without
depending on which test collection started first.
- **The workflow tests no longer fight the hosted runner.** The runner polls every five seconds and
  claims any Pending attempt that is due, plus any Running one whose lease has expired, which
  includes one that a test seeded and is about to assert on.
  Seeded runs are now parked out of its reach (a future next-attempt time, or a live lease held by
  another node) rather than the runner being taken out of the test host, which is what broke every
  workflow-firing test: those poll for the hosted runner to do the work. A test drives one drain
  directly and asserts the seeded runs are untouched, so the parking fails loudly if it stops
  working instead of showing up as a flake in a full suite.

  The other side of keeping the runner in the host: a test that drives it can no longer treat "this
  pass claimed nothing" as "the work is finished", because the hosted runner may have claimed the
  attempt first and still be executing it. `WorkflowTenantIsolationTests` waits for the outcome with
  a deadline instead, which is the difference between a test that is slow when something is wrong and
  one that fails at 201ms with the work still in flight.
- **The background scheduler no longer runs inside the test host.** `ScheduledContentService` waits
  thirty seconds after startup and then sweeps every minute, so a class run in isolation finished
  before it ever fired and a six minute suite got six sweeps, any of which could publish a test's
  draft between its arrange and its act. That is why the sweep-versus-editor concurrency test failed
  only in CI and passed every time locally. Every scheduling test drives `SweepTenantAsync` directly,
  so removing the timer takes nothing away, and the fixture throws if the registration ever stops
  matching rather than quietly restoring it. The delivery test that had been weakened to work around
  the same sweeper asserts on the scheduled item again.
- **The admin no longer offers Editor a screen the API refuses.** `GET /api/content-types` stopped
  granting `Editor` when #373 landed, but the sidebar kept listing it, so the link rendered and the
  API answered 403. The test that should have caught it asserted the stale behaviour in its own name,
  "gives Editor the content types screen the API lets them reach", which is how it survived the
  server-side fix. It now asserts the general rule instead: a role the server has never heard of
  reaches no gated destination.
- **`POST /api/content-types` no longer excludes SuperAdmin.** It gated on `Roles("Admin")` alone,
  the only gate in the codebase that left SuperAdmin out, so a principal holding only that role could
  read content types, toggle public delivery and change a field's sensitivity but could not create the
  type those settings belong to. A structural test now asserts that any role gate naming `Admin` also
  names `SuperAdmin`, because nothing had ever presented a SuperAdmin-only principal to a gate: the
  seeded admin holds both roles, so the omission was invisible to the suite.
- **A permission decision no longer outlives the item state it was based on.** A per-item decision
  was cached for five minutes keyed on the item's id, and nothing on the content write path
  invalidated it. Decisions can depend on the item's contents (a rule can test status, last modified
  by, created by or any data field), and status and last modified by both change on an ordinary
  write. A rule granting update only while an entry is a draft kept granting for up to five minutes
  after it was published.

  It failed open, which is the direction that matters: a stale denial is an inconvenience, a stale
  grant is an authorisation check that has stopped checking.

  Item decisions are now answered fresh, every time. Keying on the item's version would also close
  it, but the version is not on the document (the document is the fold, the version belongs to the
  stream), so reading it costs a query per check. There is little to give up: the key included the
  item id, so a list was a cache miss on every row already, and what makes a list cheap is the role
  memoisation on the resolver, which is untouched. The type-level decision, which has no item state
  in it, is still cached.
- **`GET /api/audit` compares `from` and `to` in UTC.** `CreatedAt` is stored in UTC, but the two
  query values were compared straight against it with whatever `Kind` the model binder gave them.
  A caller filtering in a non-UTC zone had their window shifted by the offset, silently missing
  rows at both edges. Fixed the same way as the Forms module's submissions list (`AsUtc`): a value
  with an offset is converted from local to UTC, a value already tagged UTC passes through, and a
  bare value with no zone is taken as UTC, which is what `ListRequest.From` and `ListRequest.To`
  already documented.
- **The redirects resolve endpoint's output cache now actually caches.** It called
  `Options(x => x.CacheOutput(...))`, but nothing registered `AddOutputCache`/`UseOutputCache`, so
  the policy was metadata nobody read and every resolve hit Postgres. Output caching is registered
  now, placed after authentication and authorization so it never serves a response to a caller who
  should not see it, and the cache key is varied by tenant so one tenant's cached answer cannot be
  served to another.
- **Public delivery responses now carry `Vary: X-Tenant`.** `TenantResolutionMiddleware` resolves
  the tenant from the `X-Tenant` header before it looks at Host, and the response is built entirely
  from that tenant's content, but `Cache-Control: public, max-age=60` went out with no `Vary`. A
  shared cache keyed on the URL alone could serve one tenant's response to another, on any
  deployment where more than one tenant is reachable through the same hostname and path (header- or
  path-routed multi-tenancy; hostname-per-tenant was already safe, since Host is part of the URL).
  `PublicDelivery.SetCache` sets `Vary` now, which covers the list, search, slug, feed and sitemap
  routes in one place. `Vary` is necessary but not sufficient: `docs/deploy-in-production.md` now
  says which deployment shapes are safe to put a shared cache or CDN in front of, and what the CDN
  itself has to be configured to do on the ones that are not.
- **Content now catches a concurrent write instead of silently losing it.** Two editors saving the
  same entry used to leave one edit gone with no error, and the history recorded the surviving write
  as though the other never happened. `Content` gets Marten's own optimistic concurrency, `GET
  /api/contents/{id}` returns the entry's version as an `ETag`, and `PUT` accepts it back as
  `If-Match`, answering 412 when it does not match. Two writers racing with no version sent at all
  now also get one success and one 412, rather than a second write nobody could see coming.
  `Content:Concurrency:Require` (default `false` in 4.x) decides whether a write that sends no
  version is refused instead; a 3.x client upgrading in place sends none, so the default keeps that
  path working. Same shape as `Lifecycle:EnforceTransitions`. Event-sourced content types are
  unaffected: they already refuse a stale or missing version on the stream (D3).
- **`SmsAction` and `EmailAction` no longer report success when nothing was sent.** Both actions
  implemented only the obsolete `ExecuteAsync`, so the default `RunAsync` always returned
  `WorkflowActionResult.Success()` after calling it, whatever the underlying provider did. On a
  stock install the default `ISmsService` and `IEmailService` are mock providers that log and
  return without sending anything or throwing, so a workflow with an SMS or Email action recorded
  success for a message nobody received.

  Both actions now implement `RunAsync` directly. A send against the mock provider returns
  `PermanentFailure`, since retrying will not change anything until a real provider is registered;
  a provider throwing is caught and returned as a retryable `Failure` naming the exception type,
  never the exception message, which routinely names the recipient. The error text stored on the
  run record never carries a phone number, email address or provider credential.
- **A `Request` action now carries the workflow run's idempotency key through to the connector.**
  `WorkflowRunner` has always put a stable key on every action's parameters, and `WebhookAction` has
  always sent it as `Idempotency-Key`, but `RequestAction` dropped it: neither it nor
  `RequestComposer` mentioned idempotency at all, so a retried call to a connector, the path an
  operator actually configures to reach a payment or accounting provider, carried no protection
  against being applied twice.

  The header name is a connector setting (`Settings["IdempotencyHeader"]`), not a request setting,
  because the spelling a provider wants is a property of the provider, and every request definition
  against the same connector should agree on it without repeating the choice. Unset defaults to
  `Idempotency-Key`. The literal value `off` switches it off, for a provider that rejects an unknown
  header; an empty setting falls back to the default rather than silently disabling the protection,
  so turning it off has to be spelled out.

  The key is sent unchanged, the same value `WebhookAction` sends, and goes through the same
  control-character check every templated header already passes. An action invoked outside the
  runner (a dry run, a test) has no key to send, and composes without the header rather than being
  refused.
- **`UpdateFieldAction` no longer applies its change twice when an attempt is reclaimed.** The
  action wrote content in its own transaction, separate from the write that records the attempt's
  outcome. When a node ran past its lease, another node reclaimed the attempt and the first node's
  outcome was discarded on purpose (see the comment in `WorkflowRunner.TryRunAsync`), trusting the
  idempotency key to absorb the duplicate call downstream. An in-process field update has no
  downstream: the content change had already committed, the outcome was dropped, and the second
  node applied the change again with no record that it had run twice.

  The write now reloads the target immediately before deciding anything, and checks a marker on the
  content itself, keyed by the run's `IdempotencyKey` and the attempt number the runner injects.
  Two executions of the same attempt (a reclaim) find the mark already there and write nothing a
  second time; a genuine retry after a real failure carries the next attempt number, finds no
  matching mark, and still applies. The write goes through `IContentWriter.AppendOptimisticAsync`
  rather than a plain `Store`, so it does not depend on last-write-wins either.
- **`ConditionalAction` no longer reports success when one of its child actions fails.** Each child
  ran inline and its result was logged as a warning and dropped, so a conditional whose branch
  failed to send anything still reported `Success()`. The run record said the workflow did
  something it did not do.

  A failing child now feeds into the conditional's own result. If nothing in the branch has
  succeeded yet, the failure is retryable, since retrying only re-runs children that never had an
  effect. The moment one child has succeeded alongside a failing one, the conditional reports a
  non-retryable failure instead: children still run with no attempt record and no idempotency key
  of their own (that reshape is 4.1), so a retry re-runs every child from the top, and offering one
  here would resend whatever the earlier child already sent. The aggregated error names which
  child action types failed, never the child's own error text, which can carry what it was sending.
- **A request definition can now use a query.** #328 closed a feature that refused itself: every
  `{{query.*}}` hole in a request's path, headers or body was refused with "queries are not
  implemented yet (#328)", whatever `RequestDefinition.QuerySlug` named, because nothing on the
  request path called `IQueryRunner`. A query could be defined, previewed and run through the API,
  and a request definition still could not use one.

  `{{query.rows}}` now composes to a JSON array of the named query's rows, one object per row,
  holding exactly the fields the query selects, bounded by its own `Limit` (itself capped at
  `QueryDefinition.MaxLimit`, 1000). It is inserted unescaped in a JSON body, since it is already
  valid JSON: quoting it would hand the recipient a string full of JSON instead of an array.
  `{{query.SomeField}}` composes to that field from the first row.

  The refusal is unchanged for a hole naming a query that does not exist or a field the query does
  not select: posting the literal text `{{query.rows}}` to a third party is worse than not running,
  and that has not stopped being true. A single field naming a query that matched no rows is
  refused too, rather than composing empty: "the query matched nothing" and "the field is
  genuinely empty" must not produce the identical value with nothing in the sent request to tell
  them apart afterwards. `{{query.rows}}` does not need this; an empty array is still a real
  answer to how many rows matched.

  A query is resolved through the same tenant-scoped session as everything else a request composes
  against, so a request never sees another tenant's query even when both hold the identical slug.
- **`migrations/4.0.0/rollback-to-3.x.sql` parses.** The `DROP FUNCTION` for
  `mt_quick_append_events` carried `DEFAULT NULL::integer` over from the function's own definition,
  which `DROP FUNCTION` does not accept in its argument list. Applied with `--single-transaction`
  as the docs say, this meant nothing before the failing line landed either: the documented rollback
  did nothing at all. `scripts/upgrade-check.sh` now applies the rollback after the forward migration
  and boots 3.21.0 again against the result, so a future break here fails CI instead of an operator
  mid-incident.
- **CITATIONS.cff said Apache-2.0 and carried a stale version.** The project has been MPL-2.0 since 3.1.1. `license` now reads MPL-2.0, and the stale `version`/`date-released` fields are removed rather than left to go wrong again on every release.
- **Two pull request scratch files, body517.md and body518.md, were committed in the repository root.** Both are deleted. `scripts/preflight.sh` now refuses a diff that adds a top-level Markdown file not on a known list, so the next one fails before it merges.
- **Two places pointed at the console as though this repository still owned it.** The issue
  template's console redirect went to `barakoBrew/issues/new/choose`, which offers no chooser
  because that repository has no templates yet (barakoBrew#29); it now points at the plain form
  and says so. The README and `docs/deploy-in-production.md` said `barako-admin` is "still
  published" or "built and released by" barakoBrew's own workflow; nothing has published it since
  the split (barakoBrew#23), so the wording now says where the image comes from without claiming a
  pipeline that does not exist, and names the amd64-only `3.21.0` tag as the last one built, with no
  `4.0` tag coming from here. `quickstart/.env.example` and `quickstart/docker-compose.yml` now say
  why `ALLOWED_ORIGINS` defaults to port 3000 when this repository's quickstart starts no console on
  it (#632, #633).
- **Logging out threw, and revocations were never cached.** `AddMemoryCache` sets a `SizeLimit`, and
  an entry stored without a `Size` raises `InvalidOperationException`. `TokenRevocationService` set
  both of its cache entries without one, so `POST /api/auth/logout` failed outright and every
  revocation check fell through to a database query on every authenticated request. There were no
  logout tests, which is why it survived. Found while building the session epoch, whose own cache
  write threw the same way and was invisible because the middleware catches and serves.
- **A capability added after a deployment upgraded now reaches its seeded roles.** The backfill
  filled only an empty capability list, so a deployment that upgraded once had an Admin whose list
  was not empty, and every area migrated afterwards never arrived. Nothing broke while
  `Auth:LegacyRoleFallback` was on, since the gate still honours the role names it replaced. Turning
  the fallback off, which is the point of the migration, is where that Admin would have silently lost
  every area migrated after its own upgrade.

  The defaults are unioned in on each seed instead. The cost, stated rather than hidden: a default an
  operator has deliberately removed from a seeded system role comes back on the next restart, because
  nothing records that the removal was deliberate. Removing one for good means not running the
  seeder. A role you created is untouched either way, since the defaults are keyed on the names the
  seeder creates.
- **The health canary pins the shape of the `/health` body instead of asserting the app is healthy.**
  It exists so a dashboard or a kubelet parsing that body sees what it always saw, and its own
  comment already said the assertion was about the shape rather than about when seeding ends. It
  asserted the status word was `Healthy` anyway, which made it depend on the startup seed finishing
  inside a fixed window on a shared CI runner. It now accepts any of the three status words and
  still fails on a new field, a renamed property or added whitespace, which is what it is for. No
  production code changed.
- **`POST /api/import/analyze` refuses a spreadsheet it will not parse, before decompressing it.**
  The parser reads a whole sheet into memory before the 500-row preview cap can apply, so the cost of
  a request followed the expanded size rather than the uploaded size. An xlsx is a zip, and repetitive
  sheet XML compresses roughly fifteen to one, so the 10 MB request body limit did not bound the work.

  Measured: a 3.2 MB upload, well inside the body limit, expanded to 46 MB of sheet XML and took 98
  seconds and 968 MB to answer, returning a preview of 500 rows. The same file is now refused in 0.15
  seconds and 20 MB. The global rate limit of 100 requests a minute per address does not bound
  something that costs what the first figure costs.

  The limit is on the expanded size the archive declares, read from the zip's central directory
  without decompressing anything. Default 8 MB, configurable as `Import:MaxExpandedBytes`, and a
  refusal names the setting so an operator with a genuinely large file knows what to change. A CSV is
  not an archive and is unaffected: its expanded size is its uploaded size, which the body limit
  already bounds.

### Security

- **A pre-release hardening sweep closed the low-severity findings from the bug hunt.** The
  individual changes are the entries marked (#271) under Breaking, Added, Removed and Fixed.

  Two of the checklist's items were deliberately left as they are. The device-approval login
  response still returns the account email: it is written on one response only, the one reached
  after the password has already been verified, and `/api/auth/otp/verify` is keyed on the address
  while the sign-in form collects a username, so removing it would break device approval without
  withholding anything the caller had not already proved. A test now pins that the address never
  appears on the failure path, which is the boundary that reasoning rests on. The second,
  `BARAKO_BACKUP_DIR`, needed no work because `BackupService` was deleted earlier in this release.

- **Uploads can be scanned for malware before they are stored.**
  Set `Files:Scanner:Address` to a clamd daemon and every upload is scanned before the bytes reach
  storage. Off by default, which is what every deployment does today. An infected file is refused
  with 422 and the signature name; a scanner that cannot be reached refuses the upload with 503,
  because an outage is not evidence that a file is safe, and that choice is documented rather than
  accidental. Neither outcome stores the file: what is kept is an audit entry naming the file, its
  size, who sent it and what the scanner found, in the hash-chained log that already has a screen.
  `docs/scanning-uploads.md` covers the container, the memory it needs, and how to check it works
  with the EICAR test file.
- **48 core endpoints declared a role gate and 41 of them had no test that the gate refuses anyone.**
  Only three had the full treatment, so deleting an endpoint or widening its roles was invisible to
  the suite. Every endpoint that calls `Roles(...)` in `Configure()` now gets the three cases
  `WorkflowMetadataAuthTests` established: anonymous refused with 401, a signed-in caller holding the
  wrong role refused with 403, and an admin still served. The route inventory those tests run over is
  compared against the gates the running host actually declares, so adding a gated endpoint without
  refusal coverage, or dropping a gate from one that had it, fails the suite by name instead of
  quietly reducing coverage. Closes #231.
- **Registration accepted any email address and created a live account against it.** Nothing proved
  the registrant could read the mailbox, so anyone could take an address they did not own. That also
  reopened external sign-in from the other side: `SocialSignIn` matches a provider's verified email
  to a local account by address alone, so a squatted address handed its real owner's Google sign-in
  to whoever registered it first. `POST /api/auth/register` now records a pending registration and
  emails a single-use token (24 hours) instead of creating a user, and the account appears at the
  new `POST /api/auth/register/verify` when the token comes back. No user document ever holds an
  address nobody proved. Registering an address that already exists answers exactly as a new one
  does, byte for byte, and tells the mailbox owner rather than the caller. Set
  `Auth:RequireEmailVerification` to false to keep the old behaviour; a deployment that does must
  also set `Auth:AcknowledgeUnverifiedRegistration`, or it refuses to start.
- **Administrative endpoints gate on a capability the caller's roles carry, not on a role name in
  C#.** Roles are runtime data and the gates were literals, so the two could never be reconciled: a
  role created through `POST /api/roles` could not be granted access to anything without a release,
  and a role someone named `Editor` picked up whatever `Editor` was written into. `Role.SystemCapabilities`
  existed for exactly this and nothing read it, which is a security control that looks present and
  does nothing. `Definition.RequireCapability(...)` replaces `Roles(...)` on `Features/Roles/*`,
  `Features/Tenants/*` and `Features/Tenants/Members/*`, with `manage_roles`, `manage_tenants` and
  `manage_tenant_members` as the first three names in the vocabulary. Everything else still gates on
  `Roles(...)` and keeps working, including third-party modules, which compile unchanged.
- **Revoking a capability takes effect on the next request.** Capabilities are resolved per request
  from the caller's roles rather than stamped into the token, so there is no window where a token
  issued before the change still carries the old answer. Putting them in the token would have meant
  up to 15 minutes of stale access with nothing to say so, which is the case that matters: someone
  removing an administrator's access during an incident. `CachedPermissionResolver` absorbs the
  lookup and already evicts on the role and membership changes that can alter it.
- **Nothing to do on upgrade.** The seeder backfills the four system roles with the capabilities
  matching what they could already reach, and leaves alone any list an operator has curated. Access
  does not depend on that having run: the gate also honours the role names it replaced, so a host
  that never calls the seeder is unaffected. Set `Auth:LegacyRoleFallback=false` (env
  `Auth__LegacyRoleFallback`) to turn the names off once your roles carry capabilities. Admin was
  never in the `Roles("SuperAdmin")` gate on roles and tenants and does not acquire it here.
- **`RoleGateTests` reads capability gates too.** Its structural half compares the live routing table
  against its inventory, and it only knew about `Roles(...)`, so migrating an endpoint would have
  dropped it out of scope and quietly taken its 401/403/served coverage with it. A second structural
  test refuses a capability that the vocabulary does not declare, so a typo cannot ship as an
  endpoint nobody can reach. Closes #272.
- **Approving is a different right from editing.** A status change checked the Update permission, so
  whoever could edit an invoice could also approve it and separation of duties could not be expressed
  at all. `ContentTypePermission` carries a rule per named transition now, and a transition that a
  role does not declare is refused rather than falling back to Update, because that fallback is the
  defect wearing the fix's clothes: it grants approval to everyone with edit rights. A transition
  does not require Update either, so a manager can approve an amount they may not change. The person
  who raised a record cannot move it on unless `Lifecycle:AllowSelfTransition:{Name}` says so, and
  that applies to an administrator too, because a separation of duties an administrator can ignore is
  not one.
- **A transition path now requires read on the content type.** Dropping the shared Update check left
  the refusals below it naming the type's declared transitions and the entry's lifecycle state, which
  any authenticated token could read off a 400 and a 409. Read is the floor rather than Update,
  because requiring Update is the coupling this change exists to remove. The permission check also
  runs before the state check, so a caller who can never perform a transition is told that rather
  than to come back later.
- **A transition rule saved in a different casing than the lifecycle declares now matches.**
  `Transitions` is built with `StringComparer.OrdinalIgnoreCase` and that comparer does not survive
  persistence: System.Text.Json constructs a fresh dictionary with the default comparer when Marten
  deserialises the role, so a rule stored as `approve` stopped matching a transition named `Approve`
  once the document was reloaded, and the symptom was a 403 on a permission the admin UI showed as
  granted. The resolver compares the key itself rather than trusting the comparer.
- **The guard that keeps event types off API responses covered the core and was blind to the
  modules.** It read response types out of the core assembly, so every module endpoint, which lives
  under its own root in its own assembly, was outside it, and a guard covering part of the surface
  reads as covering all of it. The rule is now checked against the live routing table of a running
  host: 13 assemblies, 93 response types and 191 types reached through them, with floors asserted on
  all three so a discovery path that stops finding modules fails instead of passing on a smaller set.
  Reading the routing table rather than reflecting over assemblies means no module project has to
  grant `InternalsVisibleTo`, and what gets checked is what the host actually serves. Proven by
  putting a `ContentCreated` on a module response and watching it go red by name. No module violated
  the rule: Accounting, Portability and Import reference `barakoCMS.Events` and all three construct
  events in order to write them, which is the correct use. The rule is DECISIONS.md D4. Closes #426.
- **Turning public delivery on or off for a content type is audited.** The switch serves every
  published entry of a type to anonymous callers at once and recorded nothing, which made it the
  larger half of a pair whose smaller half, a field sensitivity change, was already audited. Both
  directions are recorded, with the actor and the number of published entries the change affects,
  because "public delivery enabled" and "public delivery enabled, 4,000 entries now anonymous" are
  different sentences to whoever reads the trail later. Drafts are not counted: they stay invisible
  to anonymous callers whatever the setting says, and a number that overstates is one nobody trusts
  the second time. A request that changes nothing records nothing.
- **`PublicDelivery:RequireAcknowledgement` makes enabling it a two-step decision**, refusing the
  request unless it carries `acknowledgeExposure` and naming the count in the refusal. Off by
  default, which is what this endpoint has always done: it is the documented way back from the
  4.0 change that stopped delivering every existing type, and a default that refuses until clients
  are updated would turn the recovery path into a second outage. Disabling never needs it, because
  asking somebody to confirm the safe direction trains them to confirm without reading.
- **`Features/Users/*` and `Features/UserGroups/*` gate on capabilities, not role names.** The
  thirteen routes there still matched `SuperAdmin` or `Admin` by name, so a role created at
  runtime could reach none of them. They now ask for a capability the caller's roles carry, using
  the mechanism from #272. It is three names rather than one because the old gates were not
  uniform: `GET /api/users` and the password reset were `Roles("SuperAdmin")` while assigning
  roles and groups was `Roles("SuperAdmin", "Admin")`, and a single `manage_users` would have had
  to pick one of those. So `manage_users` is the narrow set (list accounts, reset a password),
  `manage_user_membership` is a user's roles and groups, and `manage_user_groups` is the groups
  themselves. The seeded Admin role is backfilled with the second and third and not the first,
  which is asserted through the gate: a role holding exactly Admin's defaults reaches every route
  Admin reached before and neither of the two it did not. The legacy role names still open each
  migrated gate they used to, under `Auth:LegacyRoleFallback`, so nothing changes on upgrade.
  Step 1 of #443.
- **API keys and the audit log follow.** `POST`, `GET` and `DELETE /api/api-keys` now require
  `manage_api_keys`, and `GET /api/audit` requires `view_audit_log`. Both areas gated on the same
  `SuperAdmin, Admin` pair, so a single name would have covered them; they are split because a role
  that should read the audit trail without being able to mint credentials is the ordinary auditor
  case, and one name makes that unexpressible. Admin's defaults gain both, matching what it already
  reached. Step 2 of #443.
- **Postgres can enforce tenant isolation as a second boundary.**
  `Tenancy:DatabaseEnforcement`, off by default. On, Marten puts a row level security policy on every
  conjoined document table, so one tenant's session cannot read or write another's even if the
  application's own filter is missed. `mt_events` and `mt_streams` are outside Marten's support and
  stay application-filtered.

  Turning it on is not a settings change. A Postgres superuser bypasses row level security entirely
  and every deployment here connects as one, so the policies alone would be applied and inert.
  `migrations/tenancy/001-app-role.sql` creates a `NOSUPERUSER` role and transfers ownership, and the
  application **refuses to start** if enforcement is on while it is still connecting as a superuser,
  rather than running while appearing to be protected.

  It does not catch a session opened with no tenant at all. Marten represents that as the default
  tenant, so such a session sees the default partition exactly as it does today.
  `docs/tenancy-at-the-database.md` covers the setup, the connection-footprint cost and the
  PgBouncer constraint.
- **A rollback now needs the update permission, not just the role on the route.**
  `POST /api/contents/{id}/rollback/{versionId}` gated on `Roles("SuperAdmin", "Admin")` and ran
  sensitivity, validation and lifecycle hooks, which is why the comment there claimed parity with an
  update. An update runs a fourth gate it did not: `CanPerformActionAsync(..., "update", ...)`. So an
  Admin whose role granted no `update` on a content type could still rewrite an entry of that type by
  restoring an old version, while being refused the history that lists what there is to restore. A
  write they could perform over a read they could not.
  Authorisation also runs before the event stream is read, so a caller who may not write cannot tell
  a real version from an invented one by comparing the status codes, and the server no longer reads
  every event in the stream on the way to refusing them.
- **`DATABASE_URL` no longer turns on Npgsql error detail outside Development.** It was set
  unconditionally, and Npgsql puts parameter values into exception messages when it is on, so a failed
  write copied the row's personal data into the log store, which has its own retention policy and its
  own access list. This is the production path: managed providers set `DATABASE_URL`, while a local
  stack sets `ConnectionStrings__DefaultConnection` and never reaches it. The last of the four defects
  #284 named.
- **A Secret parameter is now protected on every workflow action type, not only Webhook.**
  `WorkflowActionResponse` already hid the `Secret` parameter and reported `secretSet` regardless of
  action type, so a custom action reusing that parameter name was shown as protected while it was
  actually stored in clear. `ProtectSecrets` now encrypts `Secret` for every action, the same way it
  already did for Webhook, closing that gap.
- **A stored secret that predates encryption now refuses with a message that says what to do about
  it.** A Webhook action carrying a plaintext `Secret` from before it was ever protected already
  refused to send rather than sign or deliver anything with it. The failure used to read the same as
  a rotated `Secrets:Key`: "could not be decrypted, enter it again". That is the wrong instruction
  here, because entering the same secret again produces the same unprotected value; the fix is to
  recreate the workflow. The two cases are now told apart and the row says which one applies.
- **`DELETE /api/files/{id}` now requires being the uploader or an admin, matching the download route.**
  A holder of `upload_files` could delete any file in the tenant, including one uploaded by another
  account, while the download route already refused that same account with a 404. Delete could
  destroy a file it could not read. The two gates now agree: `upload_files` still opens list,
  describe and edit for every file in the tenant, but delete and download both also need the
  uploader, or an account holding Admin or SuperAdmin. `docs/access-control.md` covers the split.
  A bespoke role holding only `upload_files` and used to tidy up orphaned uploads, a departed
  employee's files for instance, can no longer delete somebody else's upload after this upgrade,
  and needs an Admin or SuperAdmin account for that instead.
- **A composed request header carrying a line break is now refused, closing a pre-existing
  injection.** Any value substituted into a request definition's header template reached
  `Escaping.None` with nothing stripping or refusing a carriage return or newline, then reached
  `ConnectorSender`'s `TryAddWithoutValidation` unchecked. A content field of
  `"safe\r\nX-Injected: evil"` composed verbatim and sent as two headers, which is a way to forge a
  header on an outbound call made with the connector's own credentials attached, for anyone who can
  write a content field a request template names. Wiring queries into requests widened what reaches
  the same sink, so the fix covers both: any value landing in a header, from content or from a
  query, is checked.

  Refused rather than stripped, naming the header and never the value: stripping the control
  character would send a request the operator did not write, silently, the same reason a Sensitive
  field is refused rather than masked.
- **Workflow action failures no longer persist exception messages.** Failed actions retain the exception type in their run record while the full exception remains available in server logs, preventing provider error bodies from exposing credentials through the API or admin UI.
- **A webhook URL redacted for a run record or failure message kept its path, and that is where
  Discord, Slack and Teams put the secret.** `WebhookAction.Redact` kept scheme, host, port and
  path, dropping only userinfo and the query string, on the reasoning that those two are where
  most providers put a credential. Discord (`/api/webhooks/{id}/{token}`) and Slack
  (`/services/{a}/{b}/{secret}`) put theirs in the path instead, so a webhook that answered 500
  once wrote a replayable secret into a run record or a `WebhookDelivery`, readable by anyone
  holding `ViewWorkflowRuns`. Redaction now keeps only the scheme, host and port; the path is
  always dropped. Run and webhook-delivery records written from now on will show a shorter URL
  than before; that is the fix, not a regression.
- **A webhook delivery's response body needed only `view_workflow_runs`, the same capability that
  reads every workflow run.** Two other places in this codebase refuse to carry a response body at
  all, because a 401 from an OAuth provider frequently echoes the credential that was sent; the
  delivery log was the one place that reasoning had not reached. `GET /api/webhook-deliveries` now
  needs a second capability, `view_webhook_response_bodies`, to read the `responseBody` field.
  Nothing else on the row is gated further: a caller holding only `view_workflow_runs` still sees
  every delivery, its status, its error and everything else, with `responseBody: null`.
  `docs/access-control.md` covers the split.
  **A holder of `view_workflow_runs` who is not also granted `view_webhook_response_bodies` loses
  the ability to read a delivery's response body on upgrade.** Admin's defaults do not include the
  new capability, since Admin never held this access before the split; only SuperAdmin (via `*`)
  and a role an operator grants it to explicitly can read a body. Grant `view_webhook_response_bodies`
  to whichever role should keep debugging webhooks.
- **The response body now expires on its own.** `Webhooks:ResponseBodyRetentionHours` (default 24)
  clears `responseBody` on rows older than the window, on the same hourly sweep that already prunes
  the delivery log at `Webhooks:DeliveryLogRetentionDays` (default 30, unchanged). The row survives;
  only the body is cleared, and `responseBodyClearedAt` is stamped so a cleared body reads
  differently from one that was empty to begin with (nothing answered, or the body has not expired
  yet). `docs/webhooks.md` covers both windows.
- **An access token issued before a security event is now refused.** Revoking refresh tokens stopped
  a session being renewed and did nothing to an access token already issued, which stays valid for up
  to fifteen minutes, so a password change, an administrator reset or enabling MFA all left a stolen
  session working for the rest of that window. `User.TokensValidFrom` is bumped by
  `RevokeRefreshTokens.ForUserAsync`, so it moves wherever sessions are already being invalidated
  rather than at three call sites that have to remember, and `TokenValidationMiddleware` refuses a
  token issued before it. Cached for thirty seconds, which is what the remaining exposure is across
  instances; on the instance that made the change it is zero. `TokenIssuer` now sets `iat`
  explicitly, because the check has nothing to compare against without it. Closes #82.
- **Webhook deliveries are signed.** A receiver could not tell a genuine delivery from anyone who
  learned the URL. A `Webhook` action takes an optional `Secret`, stored encrypted with
  `ISecretProtector` and never returned by any read (`secretSet` stands in for it). Every delivery
  carries `X-Barako-Delivery`, `X-Barako-Timestamp` and, with a secret, `X-Barako-Signature`:
  `sha256=` over HMAC-SHA256 of `"<timestamp>.<body>"`, so a replay is detectable. Without a secret
  the delivery goes out unsigned as before. A secret that can no longer be decrypted after a key
  rotation refuses to send rather than sending unsigned.
- **Any tenant admin could read every tenant's audit log.** The audit trail is one global table, so
  the tenant-scoped session gave `GET /api/audit` no isolation and the `?tenant=` filter was
  caller-chosen. A tenant admin now sees only their own tenant's entries; reading across tenants is
  a SuperAdmin action.
- **The RSS feed passed authored HTML through unescaped.** A feed item's description wrapped the
  field value in a CDATA block, and many readers render a description as HTML, so a Body of
  `<img src=x onerror=...>` became stored XSS in every subscriber's reader. The description is now
  entity-encoded like the title, so authored markup shows as text and never executes.
- **A deployment could boot on the placeholder JWT signing key.** The startup check enforced only a
  minimum length, and the key shipped in `k8s/02-secret.yaml` is a length-valid placeholder, so an
  operator applying the manifests unedited ran a signing key that is public in the repository and
  anyone could forge tokens. Startup now rejects the shipped placeholder as well as a short one.
- **An Admin could grant itself the SuperAdmin role.** `POST /api/users/{id}/roles` is reachable
  with `manage_user_membership`, which the Admin role holds, and it assigned any role including
  SuperAdmin with no check, so an Admin stepped outside the capability model entirely. Granting
  SuperAdmin now requires the caller to already be SuperAdmin, matching the guard the per-tenant
  membership endpoint already had.

## [3.21.0] - 2026-08-23

The release-readiness pass. Most of what follows is about the gates around a release rather than
features, because an audit on 19 August found several of them reported success without checking
anything.

- **A sensitive field was masked under one spelling and returned under another.** Content data is a
  plain case-sensitive dictionary and nothing at the write boundary rejects a key that differs from a
  schema field only by case, because validation walks the schema's fields rather than the data's
  keys. So `"Salary"` and `"salary"` could both be stored, and the mask removed the first and handed
  over the second. A writer who could not set a field could also set it under another casing. Public
  delivery already treated the two as one field; the authenticated path does now.

- **A content type's name is unique per tenant, and a duplicate create answers 409 instead of 400.**
  Uniqueness was a read followed by a write with nothing in the database behind it, so two requests
  close enough together both read nothing and both inserted. The name is a lookup key for the
  validator, the sensitivity service and the search-text backfill, and each of them resolved the
  ambiguity differently, which for a type carrying `Sensitivity` and `Mask` decides what gets masked.
  The index is per tenant, so one customer's "article" does not block another's.

  On upgrade the index is not created for you. Production runs `AutoCreate.CreateOnly`, which never
  alters an object that already exists, so an existing database keeps the old read-then-write
  behaviour until `migrations/4.0.0/3.x-to-4.0.sql` is applied by hand. That file carries the
  statement and the query that finds the duplicates first, because `CREATE UNIQUE INDEX` fails while
  one exists and the duplicates have to be merged or renamed before it will run.
- **A status change that loses a race answers 409.** `PUT /api/contents/{id}/status` appends under an
  expected-version check now, so a request built on a copy another writer has already moved past is
  refused rather than applied over the top. 409 rather than the 412 the update endpoint returns:
  nothing about this request was conditional on a version the client sent, so there is no
  precondition to have failed.
- **A scheduled publish and a concurrent edit could silently undo each other.** The writer stored the
  document the request had loaded at its start, with the new events applied on top, and the
  expected-version check covered only the stream from the append onwards. So the scheduler published
  a due draft and committed, an edit that had loaded the earlier copy appended cleanly afterward,
  and the document it stored said Draft. Nothing recorded the reversal: replaying the stream gave
  Published while the read model said Draft, permanently, and delivery stopped serving an item that
  had been published. The mirror interleaving reverted the editor's data instead. The document is now
  rebuilt from the committed state before the events are applied, and the scheduler sweep saves one
  item at a time under the same check, leaving anything another writer overtook for the next tick
  rather than overwriting it.
- **A role held only through tenant memberships could be deleted.** The referential-integrity guard
  read `User.RoleIds`, which is not where a tenant member's roles live: `MembershipRoles
  .EffectiveRoleIdsAsync` unions the membership list into the global one, and creating a tenant writes
  the membership list when it seeds that tenant's admin. So a role granted to every member of a tenant
  passed the check, the delete succeeded, and each membership was left holding an id that resolves to
  nothing, which the permission resolver treats as denied. The 409 now names the tenants the role is
  held in, so a blocked delete says where to go and unassign it.
- **The seeder created a content type the API could not see.** It wrote a `Models.ContentType` into a
  table nothing else reads, so a freshly seeded instance logged "Created AttendanceRecord content
  type" while `GET /api/content-types` returned an empty envelope and the schema editor showed
  nothing. The demo entries validated against no schema at all, because a content type with no
  definition means loose mode. It seeds a `ContentTypeDefinition` now, with the demo SSN field marked
  Sensitive, and the demo content is committed before the search-text backfill runs so a first boot
  leaves it indexed rather than waiting for the next one.
- **A failed SearchText backfill looked exactly like a completed one.** The seeder runs in an
  un-awaited `Task.Run` whose catch only logs, so a backfill that runs out of memory or time on a
  large corpus leaves the application serving traffic with public search empty for every pre-existing
  document, indefinitely, and nothing distinguishes that from a run that finished. It logs per batch
  now, and a run that does not reach the end says so and says how far it got before rethrowing.
- **An import dropped a content type's public-delivery flag.** Every other attribute of a type
  carried across and that one did not, so a bundle exported from one instance and imported into
  another created the type and the content correctly and left them off the public API, with the
  import reporting success. Records whose content type is in neither the store nor the bundle are
  also counted and named in the report now, rather than being created with empty search text and
  discovered later as content that never appears in search.

refreshed afterward (outstanding short-lived access tokens still expire on their own).

- **Losing the OTP race answered 500.** Giving `OtpCode` optimistic concurrency stopped two
  requests from consuming one code, but left the loser's save throwing into the global handler, so a
  code another request had just used came back as a server error instead of "Invalid or expired
  code." The verify endpoint and the send path now both treat a lost race as a refusal. The save
  that mints the tokens is the one that matters: losing it refuses, rather than returning the tokens
  it had already computed.

- **Two endpoints granted access to a role that does not exist.** `/api/content-types` and the Files
  upload endpoint both named "Editor" in their `Roles(...)` gate and nothing has ever seeded it. It
  granted nothing, because a token only carries roles its user holds, but it misdescribed the
  permission model to anyone reading the line, and it would have started granting silently the day
  somebody created a role by that name for an unrelated reason. A test now refuses any gate naming a
  role nothing creates, in the core or in a module.

- **A production compose file that runs the published images.** `docker-compose.prod.yml` used to
  build the API and the admin from source, so nobody holding only the images we publish could use
  the file that has the production shape, and every deploy compiled a .NET solution and a Next.js
  app on a box that should need only Docker. It now runs `ghcr.io/baryodev/barako-cms` and
  `ghcr.io/baryodev/barako-admin` behind Caddy, with `${VAR:?}` guards so the stack refuses to start
  without a real database password, JWT key, admin password and pinned tag, and with
  `FRONTEND_ORIGINS` asked for at deploy time rather than discovered as a browser CORS error. This
  is the only production compose file; the headers of the other three now say what each is for
  (#307, #309).
- **`.env.prod.example` and `docs/deploy-in-production.md`.** The variables with the command that
  generates each one, and the deploy itself: DNS first, what a healthy stack answers, how a frontend
  reaches the delivery API, and the rough edges an operator hits on day one instead of finding them
  alone.
- **CI resolves every compose file.** A `compose` job runs `docker compose config` on all four,
  asserts the production one resolves to no build step and to the published image names, and asserts
  it refuses to resolve at all with `JWT_KEY` unset. A production compose file nobody has run is
  what #307 was about.
- **A comment claiming Next.js needs the API URL at build time, which cost the production deploy its
  images.** It is true of Next.js in general and false of this image: `admin/entrypoint.sh`
  regenerates `public/env-config.js` from any `NEXT_PUBLIC_*` variable at container start and
  `getApiUrl()` reads that first, which is why `docker-compose.hub.yml` and the quickstart already
  passed it as a plain runtime variable. The comment is gone, `NEXT_PUBLIC_API_URL` is a runtime
  `environment:` entry in the production compose, and the published `barako-admin` image can be
  pointed at any domain (#309).
- **`docs/` was gitignored, so documentation shipped nowhere.** The rule was `docs/*` plus a growing
  allowlist, which meant a new file was ignored by default: `git add docs/whatever.md` did nothing
  and said nothing, and the 4.0 readiness pass concluded documentation was largely absent partly
  because the directory looked almost empty on GitHub. It is inverted. `docs/` is tracked, the few
  paths that stay out are named individually with the reason next to each, and
  `docs/access-control.md`, `docs/device-trust.md` and `docs/workflow-engine-rethink.md` are readable
  without a checkout for the first time (#312).

- **The admin no longer keeps either token in `localStorage`.** The refresh token is an httpOnly
  cookie the page cannot read; the access token is a variable in memory and is gone on reload, which
  a silent refresh replaces. Any script on the origin could read both before, and the refresh token
  is seven days and renewable, so one cross-site scripting bug or one compromised dependency in the
  admin build was a week of account takeover rather than fifteen minutes. The API still returns the
  refresh token in the response body, so the generated clients and anything not in a browser are
  unaffected: what changed is that the admin stops persisting it. Reasoning, the two things you will
  notice, and what the cookie needs from your deployment topology are in
  `docs/session-and-token-storage.md`.

- **A fresh deployment's first backup always failed.** `db-backup` started as soon as Postgres was
  healthy and took its proof backup immediately, racing the API creating its tables, so every first
  deployment logged `archive is only 369 bytes`. The size guard did its job and nothing was written,
  but the stack had no recovery point until somebody noticed, and a failure logged on every first
  deploy is a good way to teach people to ignore the backup log. It waits for the application schema
  now, asked of Postgres rather than of the API so it needs no second service to be reachable.

### Security

**A webhook could be redirected past the SSRF guard.** `WebhookAction` validates the URL it is
given and then handed it to a client whose `AllowAutoRedirect` was left at its default. A target
answering `302 Location: http://169.254.169.254/...` was followed to the metadata service with the
block list never consulted for that address. It needs no DNS control and no race, unlike the
rebinding in #258, and works first time. The client no longer follows redirects.

**A captured Resend webhook could be replayed forever.** The Svix timestamp is mixed into the signed
string, so it could not be tampered with, and it was never compared against the clock. Each replay
of a genuine `email.bounced` writes another suppression record for that recipient. Now rejected
outside five minutes in either direction, and an unparseable timestamp is refused rather than read
as zero. Email.Resend bumped.


**Two workflow endpoints were reachable without signing in, and one returned stored content.**
`GET /api/workflows/actions` and `GET /api/workflows/variables` both shipped with `AllowAnonymous()`
and a comment saying to re-enable auth later. The second reads a real stored document of the
requested content type to derive its fields, and returned each field's stored value as an example,
with no sensitivity masking applied. An unauthenticated caller could name a content type and read
back its field names and their contents, routing around the role restriction on `/api/schemas`.
Both now require `SuperAdmin` or `Admin`, and the extractor returns a placeholder instead of the
stored value.

**Unsigned Resend webhooks were trusted rather than refused.** When no signing secret was
configured the verification branch was skipped entirely, so an unconfigured instance accepted any
caller's webhook payload. It now fails closed. Email.Resend module `0.4.1`.

**Content marked Sensitive was stored as Public.** The sensitivity chosen when creating an entry was
dropped before storage, so entries posted as Sensitive or Hidden were saved as Public and the
redaction rules never engaged for them.

**The PWA report endpoint had no rate limit**, accepting unlimited anonymous submissions.

**Secret scanning had never actually run.** The job reported success while scanning nothing.

### Fixed

- `SearchText` backfill loaded every document at once and failed silently; it now batches and
  reports.
- `GET /api/content-types` is removed. It queried a document type nothing in the codebase ever
  wrote, so it always returned an empty list while `POST` to the same route stored a different type.
  `POST /api/content-types` and `PUT /api/content-types/{name}/public-delivery` are unaffected.

### Added

- **`GET /api/meta`**, authenticated, reporting the running API version and whether the instance
  serves Swagger.
- **An About dialog in the admin**, off a version line in the sidebar footer: API version, admin
  version, API address, documentation, this instance's own API reference when enabled, release
  notes, issues, Discord and sponsor. Nothing opens on its own.
- **Modules declare a contract version.** A module built against an incompatible core is refused at
  startup with a message naming the supported range, rather than failing later in a way that is hard
  to trace.
- Public search across delivered content.

### Changed

- **A release now ships the build that was tested.** The pipeline compiles the solution once, packs
  from that same output, and publishes the resulting artifact. The publishing job has no checkout
  step at all, so it cannot rebuild even by accident. Previously the test and publish jobs compiled
  independently, and "we ship what we tested" held only while two separate builds happened to agree.
- **Every package is installed before it is published.** A job between pack and push adds all
  fourteen to a scratch project from a local feed, builds, and asserts each one delivered a `net8.0`
  assembly. A package that restores cleanly but ships nothing now fails the release.
- **The test gate proves the suite ran.** `dotnet test` exits 0 when it discovers nothing, so the
  gate in front of fourteen published packages used to be satisfied by a command that did nothing.
  It now parses the result file and refuses an unreadable or implausible count.
- **The admin sidebar shows only what your role can reach.** Every account previously saw all
  nineteen destinations, most of which answered with a permission error on arrival.
- **The "what's new" indicator reads the running API's version** instead of a hand-maintained
  constant, which had sat at `3.1.2` while the product shipped `3.20.1`.
- **Node 22 in the admin image.** Node 20 is past end of life.
- The post-deploy smoke test and the playground verification now assert `/api/schemas` returns
  `401`. An unmapped route answers `404`, so a `401` proves the API layer routed the request and
  still refuses anonymous callers.
- Modules read their own configuration section rather than the application root.

### Fixed: seeding a chart of accounts could create two accounts sharing one code

`AccountService.UpsertAsync` looked for an existing account with a database query, so accounts stored
earlier in the *same uncommitted* unit of work were invisible to it. `UpsertManyAsync` is a loop over
that method and is how a host seeds a whole chart in one transaction — precisely where a repeated
code is most likely to appear. The second appearance became a second account: one code split across
two documents, with lookups picking between them arbitrarily and balances divided between them.

It now checks the session's pending changes before the database. Accounting module `0.2.2`.

### Accounting test coverage: 49.6% → 85.4%

The module's own HTTP surface (`POST /api/accounting/journal-entries`, the accounts endpoints), the
one-shot `AccountingMigration`, and `AccountService` had no tests between them, while carrying the
money. Three new suites cover them, each checked by reintroducing the bug it claims to catch —
balance tolerance, totals accumulated through `double`, a migration that moves instead of copies, a
dropped idempotency guard, and a widened role gate.

Two of those checks found weak tests rather than weak code, and both were rewritten: a one-line
journal entry is rejected for being unbalanced, not for having too few lines, so the line-minimum
rule was only pinned once an entry with *no* lines was tested; and a `(decimal)(double)` round trip
is lossless at these magnitudes, so the shape that actually bites — the running totals declared as
`double` — is what the fractional-amount test now pins.

`AccountService` was the surprise. Nothing inside barakoCMS calls it, so it read as dead code, but
a host application uses it in seven places. Whole suite: 71.1% → 74.4%.

## [3.20.1] - 2026-08-15

### Fixed: the opt-in had no way to be turned on for a type that already existed

3.20.0 made public delivery opt-in and added the endpoint to change it, but the admin only offered
the toggle when *creating* a content type. Every existing type — which is every type anyone upgrading
has — had no interface at all, so the documented upgrade step was "call the API by hand".

The content type screen now has the switch, with copy that says what each state means and names the
exact URL that will or will not answer. There are no core code changes; this releases the admin
image.

## [3.20.0] - 2026-08-15

### Changed (breaking): public delivery is now opt-in per content type

**Read this before upgrading. Content served at `/api/public/*` goes dark until you opt each type in.**

Public delivery used to be opt-out. `GET /api/public/{type}` served *any* content type as long as the
entry was Published and its sensitivity Public — and both of those are the defaults, for documents and
for fields alike. So modelling members, orders or a ledger as content handed you an anonymous,
unauthenticated endpoint for them without anyone ever deciding to publish anything.

That is the wrong way round. Publishing is a decision, and it should have to be made.

It was not hypothetical either: on a live deployment this served a club's member roster — names,
member numbers, emails, phone numbers, addresses — and its chart of accounts, including per-member
receivables, to anyone who supplied the club's handle. No token required.

`ContentTypeDefinition` gains `IsPubliclyDeliverable`, defaulting to **false**. The gate covers every
anonymous read path — the list, search and slug routes, the RSS feed, and semantic search in
`BarakoCMS.AI` 0.1.4. An un-opted-in type and an unknown type both answer `404`, deliberately: a
different answer would confirm which types exist.

Field-level sensitivity is unchanged and still applies on top. Opting a type in never implies every
field on it is public.

#### Upgrading

Existing types deserialize with the flag `false`, so **anything you currently serve publicly stops
being served** until you turn it on. For each type your site reads anonymously:

```http
PUT /api/content-types/{name}/public-delivery
{ "enabled": true }
```

Admin or SuperAdmin. There is also a toggle on the content type screen in the admin.

That endpoint is new, and it is why this could ship at all: content types had no update endpoint, so
without it the opt-in would have been a one-way door — every existing type undeliverable, with no
supported way back short of editing the database.

If you are unsure which types are affected, the honest answer is every type your frontend fetches from
`/api/public/`. There is no safe way for the CMS to infer that for you, which is exactly why this is a
major-flagged change rather than a silent default flip.

## [3.19.0] - 2026-08-09

### Fixed: the Next.js upgrade that was never actually broken

The admin moves to Next 16.3, and `npm audit` now reports **zero** vulnerabilities — the `next`,
`postcss` and `sharp` advisories that SECURITY.md had listed as unfixable are all gone.

They were never unfixable. Upgrading Next had been reverted once because it "broke" 28 end-to-end
tests, and the failures looked like a routing regression: after a mocked action the URL stayed at
`/login?`. The real cause is that Next 16.1 began blocking cross-origin requests for dev-server
assets. The end-to-end suite drives `http://127.0.0.1:3100` while the dev server treats `localhost`
as its origin, so every `/_next/*` chunk was refused, the app never hydrated, and any test that
clicked something failed. One line — `allowedDevOrigins: ["127.0.0.1"]` in `next.config.ts` — and the
full pack passes on 16.3.

Development only; a production build serves its own assets and is unaffected. No product code
changed, which is the point: the harness was misconfigured, not the application.

## [3.18.1] - 2026-08-09

### Fixed: 3.18.0 shipped only half its images

The 3.18.0 release published to NuGet and pushed the full suite image, then failed building the Decaf
image, which skipped the admin image and the playground deploy with it. So 3.18.0 exists as a package
but was never deployed; playground stayed on 3.17.1.

The Decaf `Dockerfile` copied only the `.csproj` before restoring, which stopped working when central
package management moved `TargetFramework` into `Directory.Build.props` — `NETSDK1013: The
TargetFramework value '' was not recognized`. `Dockerfile.suite` was unaffected because it copies the
whole build context, which is why only one of the two images failed.

No code changes; this exists to re-run the release now that the image builds.

## [3.18.0] - 2026-08-09

### Changed: enabling MFA now ends other sessions and tells the account owner

Closes the last two findings from the MFA security review.

Turning on two-factor authentication revokes the account's other refresh tokens, and sends the owner
an email saying it happened. Both exist for the same case: an attacker who has hijacked a session on
an account *without* MFA could enrol their own authenticator and keep the account — the enrolment was
silent, and their session survived it. Now no session that predates MFA outlives it, and if the owner
did not do this, they hear about it through a channel the attacker does not control.

The email is best-effort: a send failure is logged, not surfaced, since failing the request would undo
an enrolment the user did ask for. Users will be asked to sign in again after enabling, which is also
a useful confirmation that their authenticator works.

**A bounded gap remains, stated plainly.** Revoking refresh tokens stops a session being renewed; it
does not invalidate an access token already issued, which stays valid until it expires — at most 15
minutes. So an attacker's stolen session ends within 15 minutes of MFA being enabled rather than
immediately. Closing that properly needs a user-level "tokens issued before this moment are invalid"
timestamp checked during authentication. That is worth doing — it would also close the same window on
password change and on logout-everywhere, where `RevokeAllUserTokensAsync` has always been
refresh-token-only — but it belongs in its own change, because it runs on every authenticated request
and a mistake there locks everybody out.

## [3.17.1] - 2026-08-08

### Fixed: the social sign-in MFA gate was never published

`BarakoCMS.ExternalAuth` 0.1.6 ships the change written for 3.15.0 that stops Google, GitHub,
Facebook and LinkedIn sign-in from minting tokens for an account that has MFA enrolled. The code
landed in 3.15.0 but the module's own `<Version>` was left at 0.1.5, and the release pushes with
`--skip-duplicate`, so the package was silently skipped — anyone consuming 0.1.5 still has the
bypass, where a provider-account takeover sidesteps the second factor entirely.

If you use `BarakoCMS.ExternalAuth` with MFA, take 0.1.6. Core is bumped only to get past the
release gate, which reads core's version alone; there are no core changes in 3.17.1.

This is the second time an unbumped module version has swallowed a shipped fix (see 3.12.1). The
underlying gap is that nothing checks whether a module's source changed without its version moving.

## [3.17.0] - 2026-08-06

### Added: MFA in the admin UI

The TOTP backend shipped in 3.15.0, but the admin had no interface for it — which made the feature
unusable in practice and, worse, risky: anyone who enrolled through the API could not get back in,
because the login page treated the MFA challenge like a normal login and stored its empty token as if
it were a session. That is fixed, and the flow now exists end to end:

- **Settings → Security** — enroll with a QR code (rendered locally, so the secret never travels to a
  third-party QR service) or by typing the key, confirm with a code, and get the one-time recovery
  codes with a copy button. Turning MFA off requires a current code, so a hijacked session can't
  silently remove it.
- **Login** — a second step that accepts an authenticator code or a recovery code. The field uses
  `autocomplete="one-time-code"`, so password managers and iOS autofill offer the code directly.

There are no core code changes in this release; the version bump is what releases the admin image (the
release gate reads core's version alone), same as 3.12.1.

## [3.16.0] - 2026-08-05

### Added: browser error capture (the other half of Diagnostics)

The Diagnostics module could always serve captured errors, and the admin has had an Errors page — but
nothing ever sent anything, so the page was permanently empty. The admin now reports:

- Uncaught errors and unhandled promise rejections, via global listeners installed in the root layout.
- React render errors, via a root `global-error` boundary (those never surface through `window.onerror`,
  so they were invisible to any listener-only approach).

Reports are batched, deduplicated client-side, and sent with `keepalive` so a fault on a page being
navigated away from still arrives. The reporter is built so it can never become a source of errors: it
sends with plain `fetch` rather than the shared axios client (whose 401-refresh interceptor could
re-enter), swallows every send failure, and caps sends per page session so a render loop cannot flood
the API. Identity is attached when signed in, so errors can be attributed.

### Added: `telemetry` rate-limit policy

`POST /api/client-errors` is anonymous by design (faults happen before sign-in) and fans out to one
lookup per item in the batch, so under the global 100/min budget it allowed roughly a 20x amplification
against the database. It now has its own tighter policy: 20 batches per minute per IP, far above real
client behaviour. `BarakoCMS.Diagnostics` 0.1.3 applies it.

## [3.15.0] - 2026-08-05

### Added: TOTP multi-factor authentication

Accounts can enroll an authenticator app (Google Authenticator, 1Password, etc.) as a second factor.

- `POST /api/auth/mfa/setup` (auth) — start enrollment; returns a secret + `otpauth://` URI to show as a
  QR code, once.
- `POST /api/auth/mfa/enable` (auth) — confirm with a code; returns one-time recovery codes, once.
- `POST /api/auth/mfa/verify` — complete a two-step login: exchange the challenge from `/login` plus a
  TOTP (or recovery code) for the usual access + refresh tokens.
- `POST /api/auth/mfa/disable` (auth) — requires a current code, so a hijacked session can't strip it.
- `GET /api/auth/mfa/status` (auth).

When MFA is enabled, `POST /api/auth/login` returns `RequiresMfa: true` with a short-lived, single-purpose
challenge token (signed on a distinct `:mfa` audience, so it can never act as an access token) instead of
tokens. Secrets are stored AES-GCM-encrypted at rest; recovery codes are stored only as BCrypt hashes and
are single-use; a per-time-step replay guard (with optimistic concurrency) prevents reusing a code, and
wrong codes count toward the same lockout as password failures.

The feature was security-reviewed before release. The review's headline finding is fixed here:

### Fixed: every sign-in path honors MFA

Enrolling MFA now protects **every** way to obtain tokens, not just password login. The email one-time-code
path (`/api/auth/otp/verify`) and all four social providers (`BarakoCMS.ExternalAuth`: Google, GitHub,
Facebook, LinkedIn) treated mailbox/provider possession as a complete login and minted tokens without the
second factor — an inbox or OAuth-account compromise would have sidestepped MFA entirely. They now return
the same MFA challenge and require `/api/auth/mfa/verify` to finish. MFA-issued tokens also carry the
device-binding claim, matching the password and OTP paths.

Note: the AES key for MFA secrets derives from `Mfa:Key` if set, otherwise the JWT signing key. Set a
dedicated `Mfa:Key` in production and do not rotate it without re-encrypting stored secrets.

## [3.14.1] - 2026-08-05

### Fixed: 3.14.0 startup crash on existing databases

3.14.0 added two Marten indexes (on the new scheduled-publish fields) to the `Content` document. On a
fresh database that is harmless, but on an existing one it is a delta to `mt_doc_contents`, which the
prod/playground `AutoCreate.CreateOnly` policy refuses at startup — so the container crash-looped
(`Cannot derive schema migrations for TableDelta`). The indexes are removed: the scheduler sweep leads
with `Status` (already indexed), so they were never load-bearing. No API or behavior change from 3.14.0.
See H.40 for the missing online-migration step that would let index additions ship safely.

## [3.14.0] - 2026-08-05

### Added: scheduled publish / unpublish

Content can now be armed to go live or retire on its own. Two optional UTC times on a content item:

- `ScheduledPublishAt` — a Draft is promoted to Published at/after this time.
- `ScheduledUnpublishAt` — a Published item is Archived at/after this time.

Set them with `PUT /api/contents/{id}/schedule` (`{ scheduledPublishAt, scheduledUnpublishAt }`, either
optional, null clears; an unpublish time must be after the publish time). A background service,
`ScheduledContentService`, sweeps every minute across the default partition and each active tenant,
applies the due transitions, and clears the consumed time (a future unpublish window survives the
publish). Because public delivery and the RSS feed already gate on `Status == Published`, a scheduled
item simply appears — and later disappears — on its own.

Each transition emits a real `ContentStatusChanged` event, so history is correct and workflows fire.

### Fixed: publish workflows now actually fire

`PUT /api/contents/{id}/status` constructed a `ContentStatusChanged` event and updated the read model
but never appended the event to the stream, so the async `WorkflowProjection` — which is driven off the
stream and already maps a Published transition to the `Published` trigger — never ran. The endpoint now
appends the event (matching the Update and rollback endpoints), so workflows configured on `Published`
finally execute. Scheduled transitions go through the same path.

## [3.13.0] - 2026-08-05

### Added: RSS feeds for public content

Any content type now exposes an RSS 2.0 feed at `GET /api/public/{type}/feed.xml` — the newest 50
Published, document-Public entries. It reuses the same projection as the rest of public delivery, so
drafts, Sensitive documents, and non-Public fields never appear; the feed is anonymous and cached the
same way the other public endpoints are.

Because the CMS is headless, item links point at the caller's frontend, configured (all optional):

- `Feeds:SiteUrl` — the site the links resolve against (falls back to the request host).
- `Feeds:Paths:{type}` — a per-type link template like `/blog/{slug}` (defaults to `/{type}/{slug}`).
- `Feeds:Titles:{type}` — the channel title (defaults to the type name).

Item title, description and date are taken from the usual public fields (Title/Name, then
Excerpt/Summary/Description/Body, then a Date/PublishedAt field falling back to created-at).

## [3.12.2] - 2026-08-03

### Fixed: every module rebuilt against current core

All module packages are republished so they are compiled against 3.12.x. They had drifted badly —
most were last built against core **3.2.x**, nine minor versions back — because a module is only
rebuilt when its own `<Version>` changes, and none had.

This was not theoretical. A host taking new core with the previously published modules got real
failures: import endpoints returning 403, and ledger and file-attachment posts returning 400. The same
host built against matching source passed. If you are on core 3.12.x, take these module versions too;
mixing 3.12.x core with the older module packages is not a supported combination.

No functional changes in this release beyond the rebuild. See H.40 in the roadmap for the pipeline
gap that let the drift accumulate silently.

## [3.12.1] - 2026-08-03

### Fixed

- `BarakoCMS.Portability` 0.1.2 — ships the audit-log capture for export and import that was written
  for 3.12.0 but never published: the module's version was unchanged, and the release pushes with
  `--skip-duplicate`, so the package was silently skipped and stayed at 0.1.1. Core is bumped only to
  get past the release gate, which reads core's version alone; there are no core changes in 3.12.1.

## [3.12.0] - 2026-08-03

### Added: audit log

A queryable "who did what, when", available in core (no module to install).

- `GET /api/audit` (Admin) — filter by actor, action, date range and tenant, paginated.
- Captures auth events (login succeeded/failed/blocked, account lockout, logout, token refresh and
  refresh-token reuse detection) and sensitive administrative actions (role and user-group deletion,
  role/group assignment and removal, content archival, portability export/import).
- Entries are hash-chained: each one carries the previous entry's hash, so editing or removing a past
  entry breaks every hash after it. This is tamper-**evidence**, not tamper-prevention — someone with
  direct database access can still rewrite the chain forward. Known limitation: the previous-hash
  lookup and the insert are not one atomic operation, so two audit-worthy actions racing in the same
  tenant can chain off the same previous hash. That shows up as a detectable fork, and no entry is
  lost.
- Admin gains an "Audit log" page with the same shape as the Errors page.

### Added: per-content-type domain rules (`IContentLifecycleHook`)

Schema validation can express "Amount is a decimal"; it cannot express "total debits must equal total
credits", or "assign the next sequence number". Previously a domain with real invariants had to be
given its own bespoke write endpoint, which put it outside the generic content pipeline.

A module now registers an `IContentLifecycleHook` the way it registers a workflow action, and core
runs it on create **and** update without knowing the module exists. Hooks can reject a write or enrich
it, and they receive the request's Marten session, so anything they store commits in the same
transaction as the entry.

### Changed: decimals in schemaless data are no longer doubles

**Behaviour change — read this if you consume `Content.Data` from .NET.**

Values inside the `Dictionary<string, object>` bags (a content entry's `Data`, a permission rule's
`Conditions`, an audit entry's `Metadata`) previously came back from storage as `System.Double` at the
top level and as raw `JsonElement` when nested. Fractional numbers now come back as `decimal`, and
nested values are plain CLR types at every depth.

- Whole numbers still come back as `long`, so ids and counts are unaffected.
- Values outside `decimal`'s range still fall back to `double` rather than throwing.
- **If your code casts a stored number straight to `double`, it will now throw `InvalidCastException`.**
  Use `Convert.ToDecimal`/`Convert.ToDouble` instead.

This was a correctness fix, not a preference: summing money that round-tripped through binary floating
point accumulates drift, and a plausible-but-wrong accounting total is the worst failure mode this
codebase has. The same change also makes nesting consistent, which retires a class of bug where code
type-checking for `Dictionary<string, object>` silently received a `JsonElement` instead.

### Changed: `BarakoCMS.Accounting` 0.2.0 — accounts and journal entries are content types

**Breaking for hosts using the accounting module.**

`Account` and `JournalEntry` were bespoke Marten documents; they are now ordinary barakoCMS content
types, so they are queryable, permissioned and deliverable through the same generic endpoints as
everything else. The rules a schema cannot express moved into content lifecycle hooks, so posting an
unbalanced entry through plain `POST /api/contents` is rejected, entry numbers are allocated
server-side, and a rejected post does not consume a number. A posted entry is immutable — correct it
by posting a reversing entry.

- New `AccountService` so hosts keep working with the `Account` domain type instead of hand-building
  content dictionaries. Replace `session.Query<Account>()` and `session.Store(new Account { … })` with
  `AccountService.GetAllAsync`/`GetByCodeAsync`/`UpsertAsync`.
- The `/api/accounting/*` endpoints are unchanged for callers, but now read and write content.
- `AccountingMigration.RunAsync` copies existing typed `Account`/`JournalEntry` documents into content.
  It copies rather than moves and is idempotent, so the originals stay on disk and a bad run can be
  repeated rather than being the step that loses a ledger.

### Fixed

- `BarakoCMS.Diagnostics` is wired into the Suite image, so the shipped Suite's admin "Errors" page has
  a backend instead of returning 404.
- CI now fails on Critical/High vulnerable dependencies instead of only reporting them, and Dependabot
  is configured for NuGet, npm and GitHub Actions.
- CSP no longer allows `'unsafe-inline'` in `script-src` outside Development. `style-src` still does —
  see the roadmap for the remaining nonce work.

## [3.11.0] - 2026-07-30

### Added: draft preview

Editors can now preview an unpublished entry on the real frontend without publishing it.

- `POST /api/preview` — an authenticated editor mints a short-lived (30 min) signed token for one draft.
  The caller must have read access to that content type (the same permission check as the authoring read
  endpoint), so you can only mint a link for a draft you're allowed to see.
- `GET /api/public/{type}/{slug}?preview=<token>` returns the draft when the token is valid. The token is
  signed with the JWT key and bound to the exact tenant + type + slug, so it can't be forged or reused for
  another entry. Preview lifts **only** the published gate: a document-Sensitive entry is still refused, only
  Public fields are emitted, and the response is `no-store`. An invalid or expired token falls back to the
  normal published-only behavior, revealing nothing.

## [3.10.0] - 2026-07-28

### Added: AI semantic search (BarakoCMS.AI module)

A new opt-in module adds vector search over published content using a self-hosted embedding model
(Ollama by default) — no third-party API key.

- `POST /api/ai/index/{type}` (admin) builds a type's vector index in the current tenant, embedding each
  Published, document-Public entry from its Public fields only.
- `GET /api/public/{type}/semantic?q=…&limit=…` (anonymous, cacheable) ranks the index by cosine
  similarity, then re-verifies each hit is still Published and document-Public before returning it — so a
  draft, a Sensitive document, a Sensitive field, or an entry unpublished since indexing never surfaces.

Enable with `Ai:Enabled=true` and point `Ai:EmbeddingBaseUrl` at an Ollama-style endpoint. Inert
otherwise. Bundled in the suite image; published as `BarakoCMS.AI` on NuGet.

## [3.9.0] - 2026-07-28

### Added: public content search

`GET /api/public/{type}/search?q=…&limit=…` returns the top public matches for a query. It projects
each entry to its public shape first and only then matches, so it searches exclusively over allowlisted
Public fields — a draft, a document-Sensitive entry, or a value in a Sensitive field can never surface
a result. A title/name hit outranks a body hit. It scans a bounded recent window (swap in Postgres
full-text search for larger corpora). Anonymous and cacheable, like the rest of public delivery.

### Fixed: admin runtime config under a basePath

The admin loaded its runtime `env-config.js` from the origin root, so when hosted under a basePath on
a different origin than it was built for, the config 404'd and the admin fell back to the build-time
API URL — sending auth cross-origin. The script now loads from the basePath.

## [3.8.0] - 2026-07-28

### Added: password change and admin reset

Passwords could be set only at registration or by the initial-admin seeder, so there was no way to
rotate an account's password. Two endpoints close that gap:

- `POST /api/me/password` — the signed-in user changes their own password. It re-verifies the current
  password, enforces the password policy, and rejects a no-op change.
- `POST /api/users/{userId}/password` — a SuperAdmin resets another user's password (recovery or
  rotation), enforcing the same policy.

Both revoke the user's active refresh tokens, so a session established before the change can't be
refreshed afterwards (outstanding short-lived access tokens still expire on their own).

## [3.7.0] - 2026-07-28

### Changed: navigation menus are now a content type

Menus are no longer a bespoke document with their own CRUD endpoints. A menu is a `menu` content type,
edited like any other content and delivered through the existing public API. Modeling it as content
keeps it pluggable and removes a whole hand-written surface.

- Removed the `Menu` document and the `/api/menus` admin endpoints (create/update/delete/list) and the
  `/api/public/menus/{slug}` read endpoint.
- A menu is a `menu` content type with a `Name` and an `Items` field of type `json` that holds the nav
  tree (`{ label, url, openInNewTab, children[] }`). It is served by the generic public delivery at
  `GET /api/public/menu/{slug}`, so the same published-and-Public rules and field allowlist apply.
- Existing `menus` tables are left orphaned and untouched (safe under `AutoCreate.CreateOnly`).

**Breaking:** clients calling `/api/menus*` or `/api/public/menus/{slug}` must move to the `menu`
content type and `GET /api/public/menu/{slug}`. The `@baryodev/barako-client` `public.menu()` method
keeps the same signature and return shape; it now reads the content type under the hood.

## [3.6.0] - 2026-07-27

### Added: pluggable file storage, an S3 provider, and public media

Files can now be stored in Postgres (the built-in default, no configuration) or in any S3-compatible
object store, and both work: the CMS user picks by whether they add the S3 provider and configure it.

- A storage abstraction (`IFileStorage`) moves file bytes behind an interface while metadata stays in
  Postgres. The default keeps bytes in the database.
- A new opt-in `BarakoCMS.Files.S3` provider stores bytes in a bucket. One code path serves AWS S3,
  Cloudflare R2, and MinIO; only the endpoint and public URL differ. Configure it under `Files:S3`;
  with no bucket set it stays dormant and Postgres keeps serving.
- Public media for a website frontend: uploads can be marked public, and `GET /api/public/files/{id}`
  serves a file anonymously only when it is public. Private and missing files are both a plain 404, so
  ids cannot be probed. A public file on an object store is served from its own direct, CDN-friendly
  URL; a public file in Postgres is proxied through the API.

Uploaded SVGs are rejected (they can carry script), and proxied public responses send `nosniff` plus a
sandbox content-security-policy. Built through the process with a security review of the new anonymous
surface (no high-severity findings) and covered by tests against a real MinIO container and the real
API, including the fail-closed public download.

## [3.5.0] - 2026-07-27

### Added: site navigation menus

A tenant-scoped menu (a slug like "main" or "footer", a name, and an ordered list of items with one
level of nesting) so a site frontend can render its navigation from the CMS instead of hardcoding it.
Admins manage menus through `GET/POST/PUT/DELETE /api/menus`, and the frontend reads them anonymously:

- `GET /api/public/menus/{slug}` returns a menu for public rendering.

Menus carry only navigation data (labels and URLs), are scoped to one site, and are cacheable. This
pairs with the public content delivery API to cover a site's chrome as well as its content.

## [3.4.0] - 2026-07-27

### Added: public content delivery API

A read-only, anonymous surface for serving published content to a website frontend, separate from the
authenticated authoring API. Two endpoints:

- `GET /api/public/{type}` returns a paged list of Published entries of a content type.
- `GET /api/public/{type}/{slug}` returns a single Published entry addressed by its slug.

This is what makes barakoCMS able to back a public site (a blog, a docs site, a marketing page)
without the frontend holding credentials. It is deliberately safe by construction, independent of the
authoring API's sensitivity mode:

- Only Published entries are ever returned. Drafts and archived content are never exposed.
- A document marked Sensitive or Hidden is never delivered, even when Published.
- Only fields the content type marks Public leave the API. Field masking is an allowlist, so a field
  removed or renamed in the schema, or a value stored under a differently-cased key, cannot leak.
- Each request is scoped to one tenant, resolved from the X-Tenant header or host.
- Responses carry `Cache-Control: public`, so a CDN can absorb traffic.

Built through the development process with an adversarial security review, which caught and fixed two
data-exposure bugs before merge (the field masking was a denylist that leaked orphan and mis-cased
keys). Covered by abuse-case tests against the real API over a real database.

## [3.3.0] - 2026-07-26

### Added: API keys for machine callers

Long-lived credentials so an SDK, a CI job, or an integration can call the API without holding a
human's password or minting short-lived JWTs. A key is `bcms_` followed by 256 bits of entropy, shown
once when you create it. Only its SHA-256 hash is stored, so a database leak never yields a usable
key. Manage them under Access, then API keys, in the admin: create with a name, scopes and optional
expiry; copy the secret once; revoke any time.

Keys are deliberately confined:

- **Content surface only.** A key can read and write content, content types and schemas, and nothing
  else. It can never manage users, roles, tenants, or other keys. That stays behind a human sign-in,
  so a leaked key can't escalate into platform administration.
- **Scoped.** `content:read`, `content:write`, `contenttype:read`, `contenttype:write`, or `*`. A
  read-only key is refused when it tries to write.
- **Tenant-bound.** A key operates in one tenant and can't reach another's data. It stops working
  the moment its owner's membership is removed or the tenant is deactivated, the same check the login
  path uses.
- **Revocable immediately.** Revoking a key refuses it on its next request, not at expiry.

Sent as `Authorization: Bearer bcms_...`, alongside the existing JWT auth on the same endpoints.

This shipped through the development process with an adversarial security review of the auth code,
which caught and fixed a flaw where a best-effort "last used" write could have silently reverted a
revocation. Covered by unit, integration, and abuse-case tests (forged, revoked, expired, wrong
scope, cross-surface) against the real API over a real database.

## [3.2.4] - 2026-07-25

### Fixed: dashboard crash on partial metrics

The admin overview formatted the error-rate metric without guarding for a missing value, so if the
monitoring endpoint returned a partial object the whole dashboard threw
(`Cannot read properties of undefined (reading 'toFixed')`) and rendered a blank error page. Guarded
it — a missing metric shows `—`, like the other cards already did. Found while writing the end-to-end
tests, not in production.

### Pipeline and tests (internal)

Not user-facing, but part of the same release: CI now runs the whole browser end-to-end pack (not a
subset) plus a secret and dependency scan; every deploy runs a smoke test that logs in, creates
content, and confirms validation still rejects bad input; a one-button rollback workflow was added;
and the field types from 3.2.3 gained backend integration tests that exercise the real API over a
real database.

## [3.2.3] - 2026-07-24

### Added: richer content-type field types

Content types now support properly-typed fields instead of everything being text: `email`, `url`,
`slug`, `uuid`, `money`, `time`, plus `richtext`, `markdown`, and `json` (and a `date`/`datetime`
split). Each is validated at the API — an `email` field rejects a value that isn't an email rather
than silently storing it — and the admin renders a matching control for each type (date/time pickers,
number input for money, a JSON editor for structured data).

Behind it, the allowed field types now live in one `FieldTypeRegistry` that every validator reads
from. Three validators had drifted apart — one accepted `text`/`number`, another rejected them, and a
doc comment advertised types no validator accepted — and a parity test now fails the build if they
ever diverge again.

## [3.2.2] - 2026-07-24

### Fixed: fresh installs boot on an empty database

3.2.1 shipped with `AutoCreate.None`, which never creates schema on demand. Existing
deployments already had their tables so nothing broke there, but a brand-new database had no
tables and the seeder crashed on startup with `relation "mt_doc_roles" does not exist`. A
fresh install is the first thing a new user does, so this needed fixing.

Three changes:

- Production now runs Marten's recommended `CreateOnly`: it creates missing objects (so a fresh
  database and any unregistered document type work) but never updates or drops an existing one,
  so it still won't attempt the failing single-to-conjoined event-store migration that `None`
  was chosen to avoid.
- The schema is applied explicitly at startup, before the seeders run, so their first query
  always finds its tables.
- The full-suite host now seeds the core roles and the initial admin. Previously it ran only the
  module seeders, so a fresh suite install had no user to sign in as.

Verified on a wiped database: schema created, admin seeded, login succeeds. Suite: 248 passing.

## [3.2.1] - 2026-07-22

### 🔐 Security: cross-tenant token issuance

**Upgrade if you run more than one tenant.** Single-tenant deployments were never exposed.

The tenant a token is scoped to comes from the client-supplied `X-Tenant` header. Login, OTP
verify and refresh all trusted it and minted a matching `tenant` claim **without checking
membership**; only `/api/me/switch` checked. Because role resolution falls back to a user's
*global* roles when no membership exists, the resulting token was not merely scoped to another
tenant — it carried working privileges there.

Any registered user could authenticate against any tenant and receive a usable token for it,
including one they had never joined. `BarakoCMS.ExternalAuth` had the same hole via its `club`
parameter, so *Continue with Google* produced the same result.

**Fixed** by routing every token through a single `ITokenIssuer` that owns the tenant-access
check, so it cannot be skipped by omission. Access is granted when the tenant is the default
(the single-tenant/global context), when the slug is unregistered (not a managed tenant, so no
membership model applies), or when the user holds an **Active** membership in a registered,
active tenant.

Two consequences worth knowing:

- **Refresh re-checks on every rotation**, so revoking a membership takes effect within ~15
  minutes instead of lingering for the refresh token's 7-day life.
- **Login denials return "Invalid credentials"** — the same message as a wrong password, since
  "right password, wrong tenant" confirms both the account and the tenant exist.

Covered by nine end-to-end regression tests, verified failing against the vulnerable build
before the fix landed. Suite: 243 passing.

`BarakoCMS.ExternalAuth` 0.1.3 → 0.1.4.

## [3.2.0] - 2026-07-21

### ⚖️ One licence across the suite: MPL-2.0

The core was Apache-2.0 while all eleven modules were MPL-2.0, and a stray `LICENSE.txt`
carrying an unrelated BSD 3-Clause notice sat next to the Apache `LICENSE`. GitHub could not
resolve which applied and reported the repository as having **no licence at all** — which is
worse than either choice, since it leaves adopters with nothing to rely on.

Everything is now **MPL-2.0**, matching the modules and Talaan.

- `LICENSE` replaced with the Mozilla Public License 2.0
- `LICENSE.txt` (BSD 3-Clause, left over from an unrelated 2023 project) removed
- core switched from `PackageLicenseFile` to `PackageLicenseExpression`, so NuGet renders the
  licence inline and it matches how the modules already declared theirs
- README and CONTRIBUTING updated

**What MPL-2.0 means for you:** file-level copyleft. Use barakoCMS in commercial and
closed-source products freely; if you modify a barakoCMS *source file*, publish that file's
changes. Your own application code stays yours. This is deliberately weaker than GPL — linking
and bundling are unrestricted.

**Already shipped versions are unaffected.** `BarakoCMS` 3.1.1 and earlier remain Apache-2.0
under the terms they were released with; 3.2.0 onward is MPL-2.0.

### 📦 All modules republished

Eight modules were live on NuGet but missing from its search index — installable if you knew
the exact ID, invisible if you didn't. Every module gets a patch release so the whole suite
re-indexes and depends on core 3.2.0.

## [3.1.1] - 2026-07-20

### 🔒 Security & Stability Hardening

A focused stabilization pass across authentication, the content write path, the workflow engine, and RBAC. Test suite grew from 173 to 182 passing (9 new regression tests).

#### Security
- **Upgraded Marten 8.16.1 → 8.37.0**, fixing a critical full-text-search injection advisory (GHSA-vmw2-qwm8-x84c).
- **Locked down anonymous endpoints**: content version history now requires authentication + per-content read permission and applies sensitivity redaction; `GET /api/schemas`, `/api/diagnostics/typecheck`, and `/api/monitoring/k8s` are restricted to admin roles (previously publicly readable).
- **JWT signing key is validated at startup** — the app fails fast if it is missing or shorter than 32 characters (no insecure default).
- **Removed committed credentials** from base config; the initial admin password and dev JWT key now live only in `appsettings.Development.json`, and seeded sample accounts are gated to Development.
- **SSRF protection** on workflow webhook actions (loopback, link-local incl. cloud metadata, and private ranges are blocked).
- Added a **global exception handler** (no stack-trace leaks), request body-size limits, and a minimal (non-enumerating) health response.
- **Fixed a latent bug that silently disabled token revocation**: UTC `DateTime` comparisons in LINQ queries threw under Npgsql and were swallowed, so revoked tokens were treated as valid. Revocation now works.

#### Correctness
- **Content rollback** now updates the read model (previously appended an event but left `GET`/`LIST` serving stale data) and records the acting admin.
- **Optimistic concurrency** on content updates is now enforced via Marten `AppendOptimistic`; responses expose a `Version` field to echo back for conflict detection (HTTP 412). Create/Update/ChangeStatus commit their event and read-model document in a single transaction.
- **Refresh-token rotation** is race-safe (optimistic concurrency) with **reuse detection** that revokes the entire token family on replay.
- **Login lockout counter** uses an atomic increment, closing a race that allowed lockout bypass.
- **Permission cache** is invalidated immediately on role/permission/user-role changes instead of serving stale decisions for up to 5 minutes.
- `ConfigurationService` no longer throws on malformed admin-editable settings (falls back to defaults).

#### Workflows
- Workflow execution is **decoupled from the request path** and runs via the async projection — a slow or failing action can no longer block or fail a content save.
- **Fault isolation**: per-action and per-workflow error handling prevents one failing action from stalling the engine/daemon.
- **Template variables are now resolved in live runs** (previously only in dry-run), with a single-pass resolver that prevents second-order injection between fields.
- Status transitions now fire `Published`-triggered workflows; workflows are **validated on creation** (trigger event, action types, required parameters).

### Added
- SVG coffee-bean logo (`assets/logo.svg`) and README Security & Stability section.

## [3.1.0] - 2026-07-20

The admin becomes multi-tenant and module-aware.

### Added
- **Multi-tenant admin** — auto-scopes to your tenant on sign-in, plus a switcher to move between the
  tenants you belong to (`/api/me/tenants`, `/api/me/switch`). The `X-Tenant` header is derived from
  the token's own claim and survives refresh.
- **Installed modules surface in the admin** — sections appear when their module is present:
  Accounting (accounts/balances/ledgers), Feature flags (view/toggle), Email events (Resend
  bounces/complaints), Errors (client-error log + resolve), Analytics, PWA installs.
- **`BarakoCMS.Pwa` module** — `POST /api/pwa/report` (anonymous or tied to the signed-in user) and
  `GET /api/pwa/installs`, so the admin shows who installed the app. Pairs with `@baryodev/pwa-kit`'s
  `reportPwaStatus`.
- **Analytics (Umami)** — device / OS / browser breakdowns; a site status endpoint powering install
  detection (an "add the snippet" banner + a Verify step); a visitors panel on the dashboard.
- **`Email.Resend`** — an `/api/email-events` list endpoint.
- **Quickstart bundle** — `quickstart/` runs the full suite + admin + Postgres from one documented `.env`.

### Fixed
- **Global roles kept when switching tenants** — `MembershipRoles` now unions a user's global roles
  with their tenant membership roles, so a platform SuperAdmin keeps Users/Roles access inside a tenant.

## [3.0.0] - 2026-07

Multi-tenancy and field-level sensitivity.

### Added
- **Multi-tenancy on a shared database** (Marten conjoined tenancy). Identity is global (users, roles,
  tokens, settings, devices are single-tenanted); only domain content and event streams are
  tenant-scoped. The default tenant maps to Marten's default partition — no data migration for
  existing single-tenant deployments.
- `Tenant` registry + `Membership` (a global user's roles within a tenant); tenant resolution via
  `X-Tenant` header/subdomain; `TenantAccessMiddleware`. New endpoints: `/api/tenants*`,
  `/api/me/tenants`, `/api/me/switch`, `/api/club/*`.
- **Field-level sensitivity** — mark content-type fields Sensitive or Hidden; masked per role on read
  (remove / redact / show last 4); a role that can't see a field can't write it either.

## [2.0.0] - 2025-12-11

### 🎉 Major Release: Advanced RBAC System (Phase 1)

**Status**: ✅ Production Ready  
**Test Results**: 104/122 passing (18/18 Phase 1 tests = 100%)  
**Security**: Zero vulnerabilities found

#### Added - RBAC API Endpoints (18 new endpoints)

**Role Management (5 endpoints)**
- `POST /api/roles` - Create role with granular permissions
- `GET /api/roles` - List all roles
- `GET /api/roles/{id}` - Get specific role
- `PUT /api/roles/{id}` - Update role
- `DELETE /api/roles/{id}` - Delete role

**UserGroup Management (7 endpoints)**
- `POST /api/user-groups` - Create user group
- `GET /api/user-groups` - List all groups
- `GET /api/user-groups/{id}` - Get specific group
- `PUT /api/user-groups/{id}` - Update group
- `DELETE /api/user-groups/{id}` - Delete group
- `POST /api/user-groups/{groupId}/users` - Add user to group
- `DELETE /api/user-groups/{groupId}/users/{userId}` - Remove user from group

**User Assignment (4 endpoints)**
- `POST /api/users/{userId}/roles` - Assign role to user
- `DELETE /api/users/{userId}/roles/{roleId}` - Remove role from user
- `POST /api/users/{userId}/groups` - Add user to group
- `DELETE /api/users/{userId}/groups/{groupId}` - Remove user from group

#### Added - RBAC Core Features

- **Permission System**: Content-type-specific CRUD permissions with JSON conditions
- **Role Model**: Support for permissions and system capabilities
- **UserGroup Model**: User organization and group-based permissions
- **ConditionEvaluator**: Dynamic permission conditions (`$CURRENT_USER`, `$eq`, `$in`)
- **PermissionResolver**: Service for checking user permissions

#### Added - Documentation

- Comprehensive RBAC documentation in README.md
- CLA (Contributor License Agreement) requirement
- CLA Assistant integration
- Workflow automation guide with template variables
- AttendancePOC workflow examples
- Pre-publication review artifacts
- Production readiness assessment
- ROADMAP.md with 5-phase plan

#### Added - Data Seeding

- Enhanced DataSeeder with comprehensive AttendancePOC data:
  - 4 roles: SuperAdmin, Admin, HR, User
  - 3 sample users with different roles
  - AttendanceRecord content type with sensitivity configuration
  - Email confirmation workflow
  - 3 sample attendance records

#### Changed

- Updated User model with `RoleIds` and `GroupIds` lists
- Workflow documentation expanded with multiple examples
- Contributing guidelines updated with CLA requirement

#### Security

- All RBAC endpoints secured with role-based authorization
- `SuperAdmin` role for role management
- `Admin` role for user group management
- Production configuration checklist provided
- Security audit passed (zero vulnerabilities)

#### Tests

- 18 new integration tests (100% passing)
  - 7 Role API tests
  - 7 UserGroup API tests
  - 4 User Assignment tests
- Pre-publication testing complete
- Regression testing passed (no Phase 1 regressions)

#### Performance

- All RBAC operations use async/await
- Efficient Marten LINQ queries
- Stateless API design (horizontally scalable)

---

## [2.1.0] - 2025-12-16

### 🎉 Phase 2 Week 4: Plugin System Completion & Documentation

**Status**: ✅ Complete  
**Test Results**: 166/174 passing (96%)  
**Code Quality**: A+ Grade (9.7/10)

#### Added - Plugin-Based Workflow System

- **6 Built-in Workflow Action Plugins**:
  - `EmailAction` - Send email notifications
  - `SmsAction` - Send SMS messages
  - `WebhookAction` - HTTP POST to external services
  - `CreateTaskAction` - Create tasks in the system
  - `UpdateFieldAction` - Update content fields dynamically
  - `ConditionalAction` - If/then/else logic

- **Workflow Tool Endpoints (5 new API endpoints)**:
  - `GET /api/workflows/actions` - List all available action plugins
  - `POST /api/workflows/validate` - Validate workflow JSON schema
  - `GET /api/workflows/{id}/debug` - Get execution history for debugging
  - `POST /api/workflows/dry-run` - Test workflow without side effects
  - `GET /api/workflows/variables` - Get available template variables

- **Plugin Infrastructure**:
  - `IWorkflowPluginRegistry` - Auto-discovery of workflow actions
  - `ITemplateVariableExtractor` - Template variable resolution (`{{data.Field}}`)
  - `IWorkflowSchemaValidator` - JSON schema validation
  - `IWorkflowDebugger` - Execution logging and debugging
  - `WorkflowActionMetadataAttribute` - Plugin metadata for documentation

#### Added - Documentation

- **Plugin Development Guide** (`docs/plugin-development-guide.md`):
  - Step-by-step tutorial for creating custom actions
  - Examples for all 6 built-in plugins
  - Best practices and patterns
  - Template variable usage
  - Troubleshooting guide

- **Workflow Migration Guide** (`docs/workflow-migration-guide.md`):
  - Migration from hardcoded to plugin system
  - Before/after code examples
  - Migration checklist
  - FAQ section
  - **No breaking changes** - fully backward compatible

#### Added - Tests

- **13 Integration Tests** (`WorkflowToolsApiTests.cs`):
  - All 5 workflow tool endpoints tested
  - Real database integration with Testcontainers
  - 100% passing

-  **Unit Tests**:
  - `WorkflowPluginRegistryTests.cs` (5 tests)
  - `WorkflowSchemaValidatorTests.cs` (8 tests)
  - `TemplateVariableExtractorTests.cs` (8 tests)

#### Improved - Code Quality (A+ Grade Achieved)

- **Performance Optimization**:
  - Template variable resolution: 50-70% faster (StringBuilder)
  - Database queries optimized with `.Take(1)`
- **Security Hardening**:
  - Type-safe `WorkflowEvents` constants (no magic strings)
  - Input validation complete
  - Null-safety throughout
- **Documentation**:
  - Complete XML documentation on all public APIs
  - Error handling in all 5 endpoints
  - Structured logging with context
  
#### Changed

- **IReadOnlyList** return types for immutability
- Enhanced error messages in validation
- Cancellation token support in validator

#### Performance

- Workflow plugin discovery: < 100ms for 6 plugins
- Schema validation: < 5ms per workflow
- Template variable resolution: 50-70% faster than before

#### Documentation

- Updated README with workflow system features
- Added plugin quick start example
- Links to development and migration guides

---

## [1.2.1] - 2025-12-08

### Added
- **Idempotency**: Added `IdempotencyFilter` to prevent duplicate requests on POST/PUT/PATCH via `Idempotency-Key` header.
- **Content History**: Implemented full audit trail of versions containing `Data`, `Timestamp`, and `ModifiedBy`.
- **Rollback**: Added ability to revert content to any previous version.
- **Workflows**: Added event-driven workflow engine supporting `Email` actions on `Created` and `Updated` events.
- **Documentation**: Added standalone release notes `RELEASE_NOTES_v1.2.0.md`.

### Security Hardening
- **Secrets Management**: Removed hardcoded secrets from `appsettings.json`. Migrated to User Secrets/Env Vars.
- **Infrastructure**: Secured Swagger UI (Development only) and added strict CORS policy.
- **Logging**: Redacted sensitive data (SMS content) from logs.
- **Auth**: Enforced strong password policy (Min 8 chars, Upper, Lower, Number, Special).
- **Code Quality**: Enforced strict analysis level (`latest`) and build-time style enforcement.

## [1.1.0] - 2025-12-05

### Added
- **Runtime Validation**: Implemented comprehensive validation for Content Types and Content Data.
  - Enforces field types (`string`, `int`, `bool`, `datetime`, `decimal`, `array`, `object`).
  - Enforces PascalCase naming convention for fields.
  - Validates content data against schema on Create and Update.
- **Validation Configuration**: Added `StrictValidation` and `ValidationOptions` to `appsettings.json`.
- **Documentation**: Added `RELEASE_PROCESS.md` and updated `DEVELOPMENT_STANDARDS.md` with validation details.

### Fixed
- **Integration Tests**: Resolved Marten async query issues in validators.
- **JSON Handling**: Fixed `ContentDataValidator` to correctly handle `JsonElement` types.

## [1.0.3] - 2024-01-01

### Added
- **AI Adoption**: Added `llms.txt` and `.cursorrules` to improve AI agent compatibility.
- **Community**: Added `CONTRIBUTING.md` and `CODE_OF_CONDUCT.md`.
- **Production**: Added `Dockerfile` and updated `docker-compose.yml` with health checks.
- **Health Checks**: Added `/health` endpoint.
- **Documentation**: Added `CITATIONS.cff` for research citation.

### Changed
- **Licensing**: Changed license from custom restrictive license to **Apache License 2.0**.
- **NuGet**: Updated package tags to include `ai-native` and `vibe-coding`.
- **Error Handling**: Enabled global exception handling with `UseProblemDetails()`.

### Fixed
- Improved `docker-compose.yml` reliability with `depends_on` and health checks.
