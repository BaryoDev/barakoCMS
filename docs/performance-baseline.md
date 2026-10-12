# Performance baseline: memory, threads, GC and latency levers

A measured survey of where the API's runtime cost goes and which levers the stack (.NET 10,
FastEndpoints, Marten over PostgreSQL) offers, ending in a ranked list of follow-ups (#949). No
production code changed for it. Every number below comes from a run whose method is written down,
so it can be repeated and compared.

## Method

One process, one machine, one change at a time.

- **What ran:** the Suite image (`Dockerfile.suite`, what `ghcr.io/baryodev/barako-cms` ships),
  built from release/5.0.0 at the 4.9.0 release commit, beside `postgres:16-alpine` with the
  quickstart's settings (`shared_buffers=512MB`, `max_connections=100`, `jit=off`).
- **Limits:** the API container held to `--cpus 2 --memory 1g`. The runtime sizes the garbage
  collector from the CPUs it can see, so an unpinned container measures the machine, not the code.
  The Kubernetes manifest's limit is lower again (`cpu: 500m`, `memory: 512Mi`).
- **Settings:** Production, Swagger off, demo content off, and the global rate limit raised so one
  client address can load it. Nothing else differs from a quickstart install.
- **Sequence per run:** start, time to the first 200 from `/health/ready`; idle 60 seconds; read
  resident memory and thread count from `/proc/1/status`, and 20 seconds of `System.Runtime`
  counters (`dotnet-counters`, run inside the container); create the blog and site blueprints, 20
  published posts of about 1.5 KB and a site entry through the API; then 60 seconds of fixed-rate
  load at 40 requests a second (open loop, latency measured from the due time) rotating through
  five requests: the public list (`GET /api/public/post`), an entry by slug, the site entry, an
  authenticated get-by-id (`GET /api/contents/{id}`) and an authenticated write
  (`POST /api/contents`). Resident memory and threads were sampled every second during the load,
  and counters were collected across it.
- **Host:** a 4-core x86_64 VM with 15 GB of RAM. Absolute latencies are this machine's; the
  comparisons between runs are the point.
- **Repeat it:** `scripts/perf-measure.sh` is the per-pull-request subset of the same sequence
  (#853). The lever runs were the same container and load with one environment variable added.

## Baseline

| Measure | Value |
| --- | ---: |
| Startup to first ready 200 | 9.6 s (later runs of the same image: 7.9 to 8.5 s) |
| Resident memory after 60 s idle | 237.2 MB |
| Resident memory, peak under load | 293.6 MB |
| Threads idle / peak | 20 / 27 |
| GC heap after idle / committed | 10.2 MB / 37.0 MB |
| Assemblies loaded | 329 |
| CPU at rest (of 2 CPUs) | 4.6% |
| CPU under load (of 2 CPUs) | 50.4% |
| Allocation under load | 5.53 MB/s |
| Collections under load | 34.9 a minute, 127.7 ms paused a minute |
| Postgres connections after load | 6 |

| Request (p50 / p95, ms) | Baseline |
| --- | ---: |
| Public list, 20 posts | 8.4 / 14.2 |
| Public entry by slug | 6.8 / 11.0 |
| Public site entry | 7.8 / 12.1 |
| Get by id, signed in | 14.3 / 22.8 |
| Write, signed in | 22.9 / 34.9 |

Every request in every run answered 2xx (480 of each kind per run).

Where the resident memory is, read from `/proc/1/smaps` after 30 seconds idle (233.7 MB in that
run): 113.4 MB is mapped assemblies and native libraries, 59.5 MB anonymous memory (the GC heap,
JIT-compiled code, runtime data), 53.3 MB other mappings, and 7.6 MB is `Microsoft.CodeAnalysis`.

What the baseline says before any lever is pulled:

- **The managed heap is a small part of the process.** After 60 seconds idle the GC heap holds
  about 10 MB and has about 37 MB committed, out of about 237 MB resident. Mapped assemblies and
  native libraries are the largest share (113 MB), so GC settings move the smaller part and
  assembly weight moves the larger one.
- **Roslyn is loaded at idle.** `Microsoft.CodeAnalysis` is mapped into the process before any
  request arrives, 7.6 MB of it resident. Marten generates and compiles its storage code at runtime unless code is
  generated ahead of time, and no `TypeLoadMode` is set (`ServiceCollectionExtensions.AddMartenStore`).
  This is the Roslyn stack #746 counted, with Entity Framework, in 19.5 MB on disk.
- **The idle process is not idle.** About 5% of the two CPUs at rest, from a dozen polling hosted
  services (retention sweeps, the workflow runner, scheduled content, collection syncs, the job
  worker) and the HotCold projection daemon.
- **Under load the cost is allocation, not threads.** 40 requests a second allocated about
  5.5 MB/s, collected about 35 times a minute, and the thread count rose from 20 to 27. The
  connection pool settled at 6 connections.
- **The published image serves no `/metrics`.** `UseHttpMetrics` and `MapMetrics` are called in
  `barakoCMS/Program.cs`, the core host, and not in `BarakoCMS.Suite/Program.cs`, which is what the
  image runs. The scrape-key gate (`Metrics:ScrapeKey`) is configured on a route that does not
  exist there.

## Levers tried

Each run is the baseline with one change. Lower is better in every column.

| Run | Startup s | Idle RSS MB | Peak RSS MB | Threads idle/peak | GCs/min | GC pause ms/min | CPU % under load | List p95 | By id p95 | Write p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline (Server GC, DATAS on) | 9.6 | 237.2 | 293.6 | 20/27 | 34.9 | 127.7 | 50.4 | 14.2 | 22.8 | 34.9 |
| Workstation GC (`DOTNET_gcServer=0`) | 7.9 | 229.3 | 260.5 | 18/22 | 24.0 | 84.0 | 47.5 | 14.0 | 22.3 | 34.1 |
| Workstation GC, non-concurrent (`DOTNET_gcConcurrent=0` too) | 8.5 | 230.5 | 261.0 | 19/21 | 25.1 | 135.0 | 45.1 | 13.0 | 22.6 | 31.8 |
| Server GC with DATAS off (`DOTNET_GCDynamicAdaptationMode=0`) | 8.3 | 246.9 | 501.4 | 20/25 | 1.1 | 45.9 | 50.3 | 13.4 | 20.7 | 32.8 |
| Heap limit 50% of the container (`DOTNET_GCHeapHardLimitPercent=0x32`) | 8.0 | 237.2 | 283.9 | 20/24 | 62.2 | 215.5 | 49.0 | 13.0 | 21.8 | 32.6 |
| TieredPGO off (`DOTNET_TieredPGO=0`) | 7.9 | 234.0 | 267.0 | 21/24 | 39.3 | 160.5 | 37.6 | 12.7 | 22.4 | 35.0 |
| Invariant globalization (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) | 7.9 | 229.4 | 281.1 | 21/26 | 48.0 | 208.7 | 52.0 | 14.2 | 23.1 | 33.9 |
| ReadyToRun publish (separate image) | 6.7 | 269.2 | 311.8 | 20/24 | 40.4 | 180.9 | 49.2 | 14.3 | 22.9 | 33.9 |

Each row is one run, so differences of a few percent are inside the noise: startup moved between
7.9 and 9.6 seconds across runs of the same image, and p95 latency by a millisecond or two. What
stands out of that noise:

- **Workstation GC uses less memory under load and costs nothing measurable here.** Peak resident
  memory 260.5 MB against 293.6 MB (11% lower), idle 229.3 against 237.2, fewer and shorter pauses,
  and latency unchanged at 40 requests a second on 2 CPUs. Non-concurrent adds nothing over it.
  Server GC's advantage is throughput at many cores and high concurrency, which a small install
  does not have; a large one can set it back.
- **DATAS is doing real work and must stay on.** Without it Server GC's peak went to 501.4 MB:
  it collected almost never (1.1 a minute) and let the heap grow instead.
- **A heap limit doubles the collections for 10 MB less peak.** Not worth a setting.
- **TieredPGO off** used less CPU in the first minute of load (37.6% against 50.4%), which is the
  instrumentation phase being skipped, not a steady-state saving; the run is too short to say
  more. Not recommended without a longer run.
- **Invariant globalization** saves about 8 MB idle and 12 MB peak, but changes culture-aware
  formatting (a site's `Locale`, money and dates), so it is a behavioural change.
- **ReadyToRun** starts about 1.2 seconds faster (6.7 s) but costs 128 MB of image (589 MB against
  461 MB uncompressed) and 32 MB of resident memory, because precompiled code is mapped whether it
  runs or not. For a project budgeting image size and memory (#853), the trade goes the wrong way.

The list response for 20 posts was 32.7 KB and gzips to 6.6 KB (5 times smaller).

## Areas surveyed

### Runtime and GC

- **Server vs Workstation GC.** See the table: Workstation was 11% lower at peak with no latency
  cost at this scale. ASP.NET Core defaults to Server GC, with DATAS (dynamic adaptation to
  application sizes) on by default since .NET 9; DATAS is what keeps Server GC from doubling.
- **`GCHeapHardLimit`.** In a container with a memory limit the runtime already caps the heap at
  75% of it. A lower explicit limit changes when the GC works harder, not how big the process is at
  rest, because the heap is the minority of resident memory.
- **TieredPGO.** On by default. Turning it off is measured above; it trades steady-state code
  quality for slightly less JIT work, so it is not worth a setting.
- **ReadyToRun.** Measured above as a separate image. Not wired into the build: the lock files
  are restored without a runtime identifier, and a RID-specific publish restores in unlocked mode
  (the run above had to), so adopting it needs `RuntimeIdentifiers` in the projects and regenerated
  lock files.
- **Native AOT: not reachable.** Marten generates and compiles code at runtime with Roslyn,
  FastEndpoints discovers endpoints and validators by reflection, and System.Text.Json is used with
  reflection-based contracts throughout. Each would need replacing before AOT could even be tried.
  Rejected.

### Thread pool and concurrency

- No `ThreadPool.SetMinThreads` anywhere, and none needed at these rates: the pool stayed small and
  the process peaked at 27 threads.
- **Sync over async:** two places block on async. `WorkflowActionSet.Dispose` calls
  `DisposeAsync().AsTask().GetAwaiter().GetResult()` for an action that is only async-disposable,
  once per action per run. `ContentBatchRunner` returns `outcome.Result` after awaiting it, which
  does not block. No `.Wait()` and no other `.Result` on a task in `Features/*`.
- **`ConfigureAwait`:** not used, and not needed: ASP.NET Core has no synchronization context.

### Kestrel

- HTTP/1.1 only on the plain-HTTP listener the image exposes; HTTP/2 needs TLS or explicit h2c.
  The deployment guide terminates TLS at a proxy, so HTTP/2 to the client is the proxy's job.
- Request body size is the only limit configured (`ServiceCollectionExtensions`, line 300).
- **No response compression.** The public list response for 20 posts was 32.7 KB and gzips to
  6.6 KB. Delivery is
  normally behind a CDN or a reverse proxy that compresses, and compressing in the process costs
  CPU on every cache miss, so this is a proxy setting before it is an application one.

### Marten and Npgsql

- **Sessions:** 30 `LightweightSession` uses and 26 `QuerySession` uses; the request-scoped
  session comes from `TenantSessionFactory`, also lightweight. No identity-map sessions on the
  request path.
- **Pool:** Npgsql's default of 100 connections per data source, and the quickstart's Postgres
  allows 100. One instance used 6 under this load. Several instances behind a load balancer with the
  async daemon each could reach the server's limit; that is a deployment note, not a code change.
- **Code generation:** see "Roslyn is loaded at idle" above. This is the largest single lever found
  and is the first follow-up.
- **Unbounded caches:** `FieldRules` caps its compiled-pattern cache at 1,000.
  `CachedPermissionResolver` keeps one `CancellationTokenSource` per user it has resolved, removed
  only on invalidation, so it grows with distinct users over the process lifetime; a few hundred
  bytes each. `DeliveryDocumentCache` holds one OpenAPI document per tenant. None is a leak at
  today's scale, and none has a bound.

### FastEndpoints

- Discovery scans the core and every module assembly at startup. It is part of the startup time
  above and was not separated from Marten's code generation in these runs.

### Serilog and prometheus-net

- Console sink, synchronous, at Information with Microsoft, System, FastEndpoints, Marten and
  Npgsql at Warning, so a normal request writes nothing. No enricher reads request data.
- Metric labels are bounded: route templates, not paths, and fixed outcome and action names. The
  only metric whose label comes from data is the workflow action label, which `WorkflowMetrics`
  caps at 50 distinct values.

### Payload discipline

- The rule that a list endpoint takes `PaginatedRequest` is now enforced by
  `ArchitectureTests.Every_list_endpoint_takes_a_PaginatedRequest_or_is_allowlisted_with_a_reason`.
  Two list endpoints are allowlisted: the blueprints list (a fixed set in code) and
  `GET /api/stations`, which returns every station key in a tenant and can grow.

### Container and deployment

- No `DOTNET_*` variable is set in `Dockerfile`, `Dockerfile.suite`, the compose files or `k8s/`.
- The Kubernetes limit is `cpu: 500m`, which the runtime reads as one CPU, so Server GC runs one
  heap there and the CPU-count mismatch #949 asked about does not arise.
- The image is 440 MB uncompressed and 121 MB compressed, now budgeted per pull request (#853).

## Ranked follow-ups

Each is small enough to review on its own. Impact is from the numbers above; risk is
config-only or behavioural.

1. **Run the image on Workstation GC.** `ENV DOTNET_gcServer=0` in `Dockerfile.suite` and
   `Dockerfile`, documented so a large install can set it back. 11% lower peak memory, slightly lower
   idle, no latency change measured. Config only, low risk; the #853 budget will show the effect.
2. **Pre-generate Marten's code** (`TypeLoadMode.Static` with `codegen write` at build) so Roslyn is
   not loaded at runtime. Removes 7.6 MB resident, Roslyn's part of the 19.5 MB on disk #746
   measured (with Entity Framework), and the
   runtime compilation at startup. Behavioural: generated code has to be regenerated when a
   document type or a module's schema changes, and the build has to prove it is current. Medium risk;
   pairs with #746.
3. **Serve `/metrics` from the Suite**, behind the existing scrape key, as the core host does. Not a
   cost lever, but the image cannot be measured in production without it, and this survey could
   not read it there. Additive route; low risk.
4. **Quiet the idle process.** About 5% of 2 CPUs at rest from a dozen pollers. Lengthen or back off
   the intervals of the sweeps that find nothing (retention, notices, scheduled content when none is
   due). Small on one host, real on a shared one. Behavioural (timing of background work); low to
   medium risk.
5. **Bound `CachedPermissionResolver`'s per-user token map**, or drop an entry when its cache entry
   expires. Grows with distinct users for the life of the process. Low impact today, low risk.
6. **Make `WorkflowActionSet` asynchronously disposable**, so the runner stops blocking a thread on
   `DisposeAsync` for async-only actions. Low impact, low risk.
7. **Audit culture use, then decide on invariant globalization.** 8 to 12 MB, but only safe if no
   path formats by culture; the site `Locale` and money fields suggest some do. Investigation first.

None of these changes the HTTP or package surface.

## Investigated and rejected

- **Native AOT:** not reachable with Marten's runtime code generation, FastEndpoints' reflection
  discovery and reflection-based JSON. See above.
- **Response compression in the process:** a proxy or CDN does it for delivery; in-process it adds
  CPU to every cache miss.
- **Thread pool minimums:** the pool never needed more than it had.
- **An explicit GC heap limit:** the container limit already caps the heap, and the heap is the
  minority of resident memory.
- **HTTP/2 in Kestrel:** the image serves plain HTTP behind a TLS-terminating proxy.
- **ReadyToRun:** 1.2 seconds faster to start, for 128 MB more image and 32 MB more memory.
- **TieredPGO off:** the saving measured is the first minute's instrumentation, not steady state.
- **DATAS off:** peak memory nearly doubled.

No change here touches the HTTP or package surface (CLAUDE.md section 6).
