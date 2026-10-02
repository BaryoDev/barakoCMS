# Writing a barakoCMS module

Core is espresso. Modules are what you add to it: water makes an americano, milk a latte, syrup a
flavoured one. You never modify the espresso to make a latte, you add to it, and that is exactly what
this contract lets you do and stops you doing.

If you are deciding whether your idea is a module or a change to core, the tests are in the
[README](README.md#module-or-core).

Never built one? [docs/your-first-module.md](docs/your-first-module.md) walks from a fresh clone to a
working module with tests, and links back here for each rule.

barakoCMS has an optional **module system** for layering self-contained features (accounting, import,
files, email providers, …) on top of the generic core, without forking it. Core stays lean; a host
opts into exactly the modules it wants.

## Using modules

Reference the package and restart. That is the whole install:

```sh
dotnet add package BarakoCMS.Accounting
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
await app.RunBarakoModuleSeedersAsync();  // runs each module's SeedAsync
```

`AddBarakoCMS` reads the application's dependency context (`DependencyContext.Default`, the
`deps.json` next to the host) and loads every library that reaches `BarakoCMS` through its
dependencies, directly or through another module (`BarakoCMS.Files.S3` references only
`BarakoCMS.Files`), so an unrelated package is never loaded on the chance it holds a module. In each
one it looks for
public, top-level, concrete `IBarakoModule` types with a parameterless constructor, and registers
them ordered by type name so a build is reproducible. A private or nested implementation is not a
module anyone ships and is left alone. `AppDomain.CurrentDomain.GetAssemblies()` is not used,
because assemblies load lazily and a referenced module nothing has touched yet is absent from it.

A module that needs constructor arguments, or a host that wants only the modules it names, adds
them by hand. What the callback adds keeps its place ahead of anything discovered, and discovery
skips a type the host already added:

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Discover = false;                          // explicit list only; the default is true
    modules.Add(new BarakoCMS.Accounting.AccountingModule());
    modules.Add(new BarakoCMS.Files.FilesModule());
});
```

`BarakoCMS:Modules:Discover=false` in configuration does the same for a host with no callback; what
the callback sets wins. A host published without a `deps.json` finds nothing and adds its modules
by hand. `modules.DiscoverFrom(typeof(SomeModule).Assembly)` still scans a named assembly.

Calling `AddBarakoCMS(config)` in a project that references no modules behaves exactly as before,
because modules are purely additive.

### Choosing which modules run

Discovery finds modules; configuration decides which run. `BarakoCMS:Modules:Enabled` is read as
an array or as one comma-separated string, matched against `IBarakoModule.Name` without regard to
case:

```json
{ "BarakoCMS": { "Modules": { "Enabled": ["Accounting", "Files"] } } }
```

```sh
BarakoCMS__Modules__Enabled=Accounting,Files
```

Three states, and they are different on purpose:

- **Unset.** Every module found runs, and the host logs one warning naming them and saying how to
  set the list. This is how every deployment behaved before the list existed, so an upgrade
  changes nothing.
- **Set to an empty string.** Core only. (An empty JSON array reads as unset, because the JSON
  provider produces no key for it; use `""`.)
- **Set to names.** Exactly those. A name that matches nothing refuses startup with a message
  listing the names available, because a typo that silently leaves a module off is worse than a
  boot that says what it knows. A module whose `DependsOn` names one you left off is refused the
  same way, by `DependsOn`'s own check.

A module enabled for the first time seeds on that boot: the seed runner reads the same
registrations the list filtered, seeds run on every start, and seeds are idempotent by contract.
`ModuleEnablementTests` holds this.

**Disabling leaves data.** Taking a module off the list stops its endpoints, services, schema
registration and seeding. Its tables and documents stay in the database untouched, and come back
as they were when it is enabled again. Nothing here drops anything.

`GET /api/modules` lists every module the host added or discovery found, each with `enabled`, so
"installed but off" and "not installed" can be told apart. See
[docs/module-inventory.md](docs/module-inventory.md).

### Schema preflight

Production runs `AutoCreate.CreateOnly`, which creates a missing table with its indexes and never
alters an existing one. A module whose `ConfigureSchema` only adds its own tables therefore boots on
any database, and every first-party module is in that position today. A module that adds an index
to a table that already exists used to fail at startup inside Marten, several layers down and
without the module's name.

On boot, before the schema is applied and before anything seeds, the host now asks Marten for the
migration it would apply and attributes every object in it to a module by the assembly its document
type ships in (the module's own, plus `SchemaAssemblies`), or to core. It logs one line per module
saying which objects are new and which existing ones would change. When the store is `CreateOnly`
and a module wants a change to an existing object, startup stops with a message naming the module,
the object, the policy that refuses it and the two ways out: apply the change first with `db-patch`
(see [docs/upgrading-to-4.0.md](docs/upgrading-to-4.0.md)), or run the store with
`AutoCreate.CreateOrUpdate`, which this host uses when `ASPNETCORE_ENVIRONMENT` is `Development`.
Core's own deltas are logged and then left to Marten, whose message that document already covers.

The one way a module reaches an object it does not own is the deprecated `ConfigureMarten`, which
hands over the raw `StoreOptions`. A change to a core object is therefore attributed to every
enabled module that overrides that hook; Marten stores schema alterations on deferred builders, so
two such modules cannot be told apart, and both are named.

`BarakoCMS:Modules:SchemaPreflight` switches it. Unset means on for a `CreateOnly` store and off
otherwise, so a development store that applies the change anyway behaves as before. `false` keeps
today's behaviour everywhere and leaves the refusal to Marten. `true` on a `CreateOrUpdate` or
`All` store runs the check and refuses only what that policy refuses too, so a change to an
existing object is reported instead of refused. That is how a developer sees what production would
refuse before deploying: `GET /api/modules` reports it as `needs-migration` with the object names.
`ModuleSchemaPreflightTests` covers these cases.

## Contract version

Core states which version of the module contract it implements:

```csharp
ModuleContract.Version           // 1
ModuleContract.MinimumSupported  // 1
```

Declare what your module was written against:

```csharp
public int ContractVersion => ModuleContract.Version;
```

**What the contract covers.** Every member of `IBarakoModule`, the shape of `IModuleSchema`, and the
order in which core calls them. Nothing else. A module that reaches past those into core's own
services is not using the contract, and the version says nothing about it.

**What moves the number.** Removing a member, changing a signature, changing when core calls a
hook relative to the others, or moving the place in the request pipeline where `ConfigureApp`
middleware runs (see [Middleware](#middleware)). Adding a member with a default implementation does
not, because a module compiled against the previous version keeps working.

**It is not the CMS version, deliberately.** Core can go 3.21 to 4.0 without touching the contract,
and a contract change can land in a minor. Tying them together would mean either a major release
every time a hook gained a parameter, or a silent contract change inside a patch.

**Unstated is accepted.** The default is `0`, meaning the module did not say. Every module written
before this existed declares nothing, and refusing them to enforce a field they could not have known
about would break the ecosystem to make a point. What core will not do is load a module that states
a version core cannot honour: that is refused at startup, by name, before anything is registered.

**Checking what an instance loaded.** `GET /api/modules` lists the modules a running instance
saw, each with the contract version it declared and whether it is enabled, so an author can confirm
a deployment picked up their module and which version it thinks it is talking to. It is SuperAdmin
or Admin, and it reports the name, the contract version, the enabled flag and the schema preflight
state and nothing else. A
discovered module goes through the same contract check as one the host added, and the refusal names
the module, the version it declared and the range core accepts. See
[docs/module-inventory.md](docs/module-inventory.md).

### The version of your own endpoints

`ContractVersion` is about what your module compiles against. A module that serves endpoints has a
second, unrelated number: the version of the JSON and status codes those endpoints answer with,
which is what a console or a renderer calling them depends on.

```csharp
public int HttpContractVersion => 1;
```

Move it when you remove or rename a response field, change a field's type or a status code, or
start refusing a request you used to accept. Adding an optional field does not move it. It is
independent of core's own HTTP versions and of your package version, and it covers every endpoint
you ship wherever the route is mounted: a module route under `/api/public/` moves this number, not
core's delivery number. A change core makes to something every route shares, such as the error
body, does not move it either, since the number is compiled into your package.

The default is `0`, meaning unstated. Core does not check the number. It reports it as
`httpContractVersion` on each entry of the `modules` part of `GET /api/meta/describe`, which lists
enabled modules to callers who may read `GET /api/modules`. A module the enabled list left off is
not in that list.

## Writing a module

The contract ships as [`BarakoCMS.Abstractions`](BarakoCMS.Abstractions): `IBarakoModule`,
`IModuleSchema`, `ModuleContract`, the documents and events under `barakoCMS.Models` and
`barakoCMS.Events`, the service interfaces under `barakoCMS.Core.Interfaces`, and the workflow
extension points. It does not reference the core, so it cannot grow a dependency on the host by
accident. Namespaces are unchanged, so a module already built on `BarakoCMS` needs no edit.

Implement `IBarakoModule` (all members but `Name` have default no-op implementations, so implement
only what you need):

```csharp
public sealed class MyModule : IBarakoModule
{
    public string Name => "MyFeature";

    // Register DI services. `config` is YOUR OWN section, Modules:MyFeature, not the app root.
    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        var apiKey = config["ApiKey"];              // reads Modules:MyFeature:ApiKey
        services.Configure<MyOptions>(config);      // or bind the whole section
    }

    // Register your own document types. `schema` accepts only types from assemblies you ship.
    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<MyDocument>().Index(x => x.SomeField);
    }

    // Add middleware. It runs after tenant resolution, authentication and UseAuthorization.
    // A response header is written as the response starts, not before next: see Middleware.
    public void ConfigureApp(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-My-Feature"] = "on";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }

    // Endpoints ship in your assembly and are auto-discovered (defaults to this assembly).
    public IEnumerable<Assembly> EndpointAssemblies => new[] { GetType().Assembly };

    // Seed idempotent baseline data (roles, reference data).
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        => Task.CompletedTask;
}
```

### Ordering

Modules are configured in the order they were registered, and that order decides who wins when two
modules touch the same DI registration. If yours must run after another, say so:

```csharp
public IEnumerable<string> DependsOn => ["Files"];
```

Modules are sorted before anything runs. Independent modules keep their declared order, so a build
is reproducible. A dependency that is not registered is refused by name, and a cycle is refused with
the cycle printed.

`BarakoCMS.Files.S3` is the real example: it replaces the storage `BarakoCMS.Files` registers, and
`RemoveAll` only removes what is already there.

`DependsOn` is ordering only. It does not register the other module for you, and it does not let you
reach into its services.

### Schema

`ConfigureSchema` accepts only document types from assemblies your module ships. Reaching for a
core type, or another module's, throws at registration and names the module, the type and where it
came from.

```text
Module 'Pwa' tried to configure the schema for 'barakoCMS.Models.Content', which ships in
'barakoCMS'. A module may only configure document types from its own assemblies (BarakoCMS.Pwa).
```

If your document types live in a **separate assembly** you ship, declare it:

```csharp
public IEnumerable<Assembly> SchemaAssemblies => new[] { GetType().Assembly, typeof(MyDocument).Assembly };
```

`SchemaAssemblies` is separate from `EndpointAssemblies` deliberately. One list would mean widening
endpoint scanning also widens what you may configure, so listing an assembly to have its endpoints
found would grant permission to re-map that assembly's documents.

The older `ConfigureMarten(StoreOptions)` received the same options object core configured, so a
module could re-map core documents, change tenancy or alter the event store. It still runs so
existing modules keep working, it is `[Obsolete]`, the host logs a warning naming any module using
it, and it is removed in barakoCMS 5.0.

Migrating is two edits:

```diff
- public void ConfigureMarten(StoreOptions options)
+ public void ConfigureSchema(IModuleSchema schema)
  {
-     options.Schema.For<MyDocument>().Index(x => x.SomeField);
+     schema.For<MyDocument>().Index(x => x.SomeField);
  }
```

### Seeding

`SeedAsync` runs in your own scope, your own session and your own transaction. The host commits it
once your seed returns.

- **Do not call `SaveChangesAsync` yourself.** Committing early gives up the all-or-nothing property
  your seed relies on.
- **You cannot see another module's seed data**, committed or not, and it cannot see yours. If your
  module needs data another module seeds, `DependsOn` will run that module first, but the sessions are
  isolated so you still cannot read what it wrote. `DependsOn` orders execution; it does not share data.
- **Throwing fails your seed and nobody else's.** It is logged against your module name; the other
  seeders still run and their committed work stays intact. Once every module has had its turn,
  failures are thrown together as an `AggregateException`, failing startup unless the host catches it.
- **Seeds must be idempotent.** They run on every start.

Modules previously shared one session committed once at the end, so one failure discarded every
module's work and any module could read another's uncommitted data.

### Middleware

`ConfigureApp(IApplicationBuilder app)` adds middleware to the request pipeline. `UseBarakoCMS`
calls it once per enabled module, and whatever a module adds runs at one fixed position. In order,
a request passes through:

1. Exception handling, forwarded headers, HTTPS redirection and HSTS outside Development, the
   security headers, the API contract header and the metrics scrape guard.
2. Core's rate limiter.
3. The correlation id and request logging.
4. Tenant resolution.
5. CORS.
6. Authentication, the token revocation check and the tenant access check.
7. `UseAuthorization`.
8. **Module middleware**, one module after another.
9. Core's output cache.
10. What answers: the health probes, the OpenAPI document and the endpoints. An endpoint's global
    pre-processors, the capability gate among them, run inside the endpoint.

What your middleware can rely on there:

- **The tenant is resolved.** Read it from the request's services:
  `context.RequestServices.GetRequiredService<TenantContext>().Slug`.
- **The caller is known.** `HttpContext.User` is the authenticated caller, or anonymous on an
  endpoint that allows it. A request with no token on an endpoint that needs one never reaches
  your middleware, and the token revocation check and the tenant access check have already run.
- **The endpoint is matched and has not run.** `context.GetEndpoint()` returns it on a host built on
  `WebApplication`, which is how every barakoCMS host is built.

What it cannot rely on:

- **The endpoint's own checks have not happened.** The capability gate, the API key scope check,
  the idempotency filter and the DeviceTrust module's device enforcement are global
  pre-processors. They run inside the endpoint, after you. A request that reaches your middleware can still be answered 403, so
  middleware that answers without calling `next` (a cache, say) has skipped all of them and owns
  them.
- **There may be no endpoint.** A request for a path nothing is mapped to reaches your middleware
  too, anonymous or not, with `context.GetEndpoint()` null, before core answers it 404.
- **What you write before `next` on a cached route is not yours for long.** Your middleware is
  outside core's output cache, so it runs on every request, including one the cache answers. A
  response header written before `next` is stored with the cached response and replayed to every
  later caller over whatever you wrote for that request. Write response headers in
  `context.Response.OnStarting`, as the example above does and as core does for its own: that value
  is the current request's on a cached response too. And a `Set-Cookie` written before `next` stops
  core storing the response at all, which switches the cache off for that route.
- **It does not see the health probes.** Requests under `/health` go around module middleware, the
  same way they go around core's output cache, so a module that throttles or caches cannot decide
  whether a pod is restarted.

**What you are handed.** `app` is a branch of the pipeline that belongs to your module, made with
`IApplicationBuilder.New()`, not the host application. It shares the host's container
(`app.ApplicationServices`). It is not a `WebApplication` and not an `IEndpointRouteBuilder`, and it
does not carry the host's route builder, so `UseEndpoints` on it cannot map onto the host: a
module's endpoints ship in `EndpointAssemblies`. Everything you add to it lands at step 8 and
nowhere else, so a module cannot put middleware ahead of tenant resolution or authentication, and
cannot reorder or remove what core added.

What that does not prevent: a module can end a request by not calling `next`, which is what a
throttle or a cache is for, and it can answer a path of its own from inside its branch, by hand or
with its own `UseRouting` and `UseEndpoints`, for a request core matched to no endpoint. Anything
served that way has none of the endpoint checks listed above. Like the scoped configuration
section, this is a boundary and not a sandbox.

**Order between modules.** The order modules are configured in: `DependsOn` first, then
registration order (what the callback added, then what discovery found, by type name). The first
module is outermost, so it sees a request before the next module does and the response after it.

**Order between hooks.** `ConfigureServices`, then `ConfigureSchema`, then `ConfigureApp`, then
`SeedAsync`. `UseBarakoCMS` builds the Marten store before it calls any `ConfigureApp`, so a module
whose schema is refused fails under its own name before any pipeline hook runs.

**A hook that throws** stops startup. `UseBarakoCMS` throws an `InvalidOperationException` naming
the module, with the module's exception inside it, and every module's hook runs before any module
middleware is added, so a failure leaves none behind. Middleware that cannot be constructed fails
the same way, by name, when the pipeline is built.

**Keep it to adding middleware.** The hook runs on every start of the host, including a start that
only runs a `db-assert`, `db-apply` or `db-patch` command, so it is not the place for work that
needs the database or the network.

**A module left off `BarakoCMS:Modules:Enabled`** does not have the hook called.

**The position is part of the contract.** Moving it would change what every module's middleware can
see, so it moves `ModuleContract.Version`.

`ModuleConfigureAppTests` holds the position over HTTP, on both sides, with the output cache
behaviour and the order of the hooks. `ModuleAppPipelineTests` holds the ordering between modules,
the branch, the health probe exemption and the failure behaviour.

### Configuration

A module receives its own `Modules:{Name}` section, never the application root.

```json
{
  "Modules": {
    "MyFeature": { "ApiKey": "...", "Enabled": true }
  }
}
```

As an environment variable that is `Modules__MyFeature__ApiKey`.

This is deliberate. The root also holds `ConnectionStrings`, `JWT` and `InitialAdmin`, and no module
needs the database password or the token signing key. Handing them to every referenced package was
authority granted by accident rather than on purpose.

It is a boundary, not a sandbox. In-process code can read the environment directly whatever the host
passes it. What the scoping buys is that a module wanting a core secret has to reach around the API
to get it, which is a signal, and something a reviewer can grep for. A module is trusted code; the
trust decision is made when someone references the package.

**Moving an existing module.** If your module read a root-level section before this change, set
`LegacyConfigurationSection` to it. When `Modules:{Name}` is empty and the legacy section is not, the
host passes the legacy one and logs a warning naming both keys, so upgrading does not silently
un-configure a working deployment:

```csharp
public string? LegacyConfigurationSection => "Umami";
```

Remove it once deployments have moved. It will stop being read in a future major version.

A half-finished migration works: if some keys have moved and some have not, both sections are read
and the scoped value wins where both define the same key. Moving one key at a time is safe.

Under the hood `AddBarakoCMS` collects the modules and:

- calls each `ConfigureServices`,
- adds each module's `EndpointAssemblies` to FastEndpoints discovery (additive to the host scan),
- registers each module as a singleton `IBarakoModule` so `RunBarakoModuleSeedersAsync` can seed it.

The Marten store is built on first use, and building it:

- calls each `ConfigureSchema` with an `IModuleSchema` restricted to the module's own document types,
- calls each `ConfigureMarten` as well, for modules written before `ConfigureSchema` existed, logging
  a warning naming any module that still uses it.

`UseBarakoCMS` builds the store first, if nothing has yet, and then calls each `ConfigureApp`, in
the same module order, at the position described under [Middleware](#middleware).

Default services (e.g. the mock `IEmailService`) are registered with `TryAdd`, so a module can
substitute a real implementation.

`IFileStore` is how the core, or a module that must not reference BarakoCMS.Files, stores, reads
and deletes a file in the scope's tenant. BarakoCMS.Files implements it. No member takes a tenant:
the scope decides it.

- `FindPublicAsync`, `OpenPublicAsync` and `PublicUrlAsync` take no caller and hand out public
  files only. A private file reads as absent. These are the members for work with no user, such as
  a workflow.
- `FindAsync` and `OpenAsync` take the signed-in user as a `ClaimsPrincipal` and give that user
  what the two download routes would: any public file, and a private one only to the user it
  belongs to or to an account holding Admin or SuperAdmin. A principal that is not signed in,
  comes from an API key, or carries a `tenant` claim for another tenant gets public files only.
  Pass the principal of the current request. The store does not check again that its token is
  still valid, that its tenant is still active, or device trust; the request pipeline did.
- `SaveAsync` stores a file after the checks an upload gets (allowed type, content matching the
  type, 10 MB, and the virus scan when one is configured) and answers with the file or the reason
  it was refused. It checks nobody's right to store: the module calling it gates its own route.
  `Owner` is the user the file belongs to, and may be left empty. `SuppliedBy` is the user who
  sent the file, named as the actor in the audit entry a scanner refusal leaves; leave it null
  for a file a job produced.
- `DeleteAsync` deletes for a caller who could delete through `DELETE /api/files/{id}`: one
  holding `upload_files` who is the file's owner or an administrator. It answers `InUse` while an
  entry names the file, unless forced.

`SaveAsync` and `DeleteAsync` commit through the scope's session, which the storage shares. Call
them before staging anything else on that session: with work already staged they throw
`InvalidOperationException` and do nothing, so a refused or failed save never commits a caller's
rows. A save stores the bytes and then the record, in two commits, so a crash between them leaves
bytes no record names, as on upload.

Inside a content batch (`IContentBatchRunner`) the session writes into the batch's transaction and
does not commit it. With the Postgres storage a file saved or deleted there is committed or rolled
back with the batch. With an object store the bytes are written or removed at once: a batch that
rolls back after a save leaves bytes no record names, and one that rolls back after a delete
leaves a record whose bytes are gone. Do not delete through the seam inside a batch on an object
store.

With no module that stores files the default throws on every call, naming the module to enable. Every member except `FindPublicAsync` and `OpenPublicAsync` has a
default that throws `NotSupportedException`, so a store written against those two still compiles.

### Durable work

**No host implements this yet.** The interfaces below are in `BarakoCMS.Abstractions` so they can be
reviewed as a contract first. The host registers nothing for them, so resolving one fails until the
implementation lands (#965). What follows is what the interfaces' own documentation promises, and
what an in-memory fake in this repository's tests keeps.

- `IDurableOutbox` queues a message, now (`EnqueueAsync`) or no earlier than a time
  (`ScheduleAsync`). Both return the message's id.
- `IDurableRuns` starts a run once per id (`StartAsync`), parks it until a key is resumed or a
  deadline passes (`WaitAsync`), and resumes it (`ResumeAsync`). Each answers false when its key
  cannot be used, and stages nothing then.
- `IDurableMessageHandler<TMessage>` handles one message type. It is a plain class registered in
  `ConfigureServices`, and it is given the message, the tenant and the message's id.

The rules a module writes against:

- **Staged, not sent.** Every call stages into the session the caller is writing with and commits
  with that session's save, or not at all. Save afterwards.
- **The tenant is the session's.** No call takes a tenant. The handler runs in the tenant the
  message was queued in and is told which.
- **Attempted at least once.** A message can reach its handler more than once, so a handler is
  idempotent on `DurableMessageContext.MessageId`. No order is promised between two messages.
- **A throw is a failed attempt.** It is retried later and, when the attempts run out, kept as a
  dead letter. A handler that knows a failure is permanent records it and returns.
- **A unit of work that loses a key to another fails to commit** with
  `DurableWorkConflictException`, and nothing in it is committed.
- **A message is stored data.** It is written as JSON under its type's name, so a renamed type
  strands what is already stored. It must not carry a credential.
- **Keys start with the module's name** (`forms:reply:...`), because run ids and wait keys share
  one namespace per tenant with every other module.

## Writing a module outside this repository

Everything above applies. This section is what a module that ships as its own package needs on top.

### Start from the template

```sh
dotnet new install BarakoCMS.Templates
dotnet new barakocms-module -n Acme.Notes
cd Acme.Notes
dotnet test
```

`-n Acme.Notes` gives a module named `Notes` in the `Acme.Notes` package: an `IBarakoModule` that
declares `ContractVersion`, binds its options from `Modules:Notes`, registers one document type and
grants its capability to Admin at seed; one endpoint, `GET /api/notes/notes`, gated on that
capability, paged and tenant scoped; a README in the structure below; an icon placeholder; a
`Directory.Build.props` carrying the packaging metadata and the `barakocms-module` tag; and a test
project on `BarakoCMS.Testing` with three tests. The tests need Docker. `--BarakoCMSVersion` and
`--TestingVersion` pin the packages the module builds against; the defaults are the versions the
template shipped beside.

### Test it on a real host

`BarakoCMS.Testing` holds `BarakoTestHost`: the real host pipeline over a PostgreSQL that
Testcontainers starts, with the modules you name registered and discovery off, the system roles
and the initial admin seeded, and every module's seeder run. Derive a fixture that names your
module, then take it as an xunit class fixture:

```csharp
public sealed class NotesHost : BarakoTestHost
{
    public NotesHost() : base(o =>
    {
        o.Modules.Add(new NotesModule());
        o.Settings["Modules:Notes:Greeting"] = "test";
    }) { }
}

public class NotesModuleTests : IClassFixture<NotesHost>
{
    private readonly NotesHost _host;
    public NotesModuleTests(NotesHost host) => _host = host;

    [Fact]
    public async Task An_Admin_reaches_the_endpoint()
    {
        var client = await _host.CreateClientAsync("Admin");   // only what the seeders granted Admin
        var response = await client.GetAsync("/api/notes/notes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

`CreateAdminClientAsync()` signs in as the seeded admin, who is also SuperAdmin and so passes every
gate; `CreateClientAsync("Admin")` is the one that proves your seeder granted the capability.
`CreateTenantAsync()` and `CreateAdminClientAsync(slug)` cover a second tenant, `OpenSession()` is a
Marten session for arranging data, and `CreateClient()` is anonymous. Nothing is faked: a module
that fails the contract check or configures a document type it does not own fails here the way it
fails on a deployment.

### What the host checks at startup, and what it does not

Checked, in this order, before any request is served:

1. **The contract version.** A module stating a `ContractVersion` outside
   `ModuleContract.MinimumSupported` through `ModuleContract.Version` is refused by name. Unstated
   (`0`) is accepted. Only enabled modules are checked.
2. **Registration.** The same module class added twice is refused, and so are two modules sharing a
   `Name`.
3. **Ordering.** A `DependsOn` naming a module that is not registered is refused by name; a cycle is
   refused with the cycle printed.
4. **The enabled list.** A name in `BarakoCMS:Modules:Enabled` that matches no module refuses
   startup and lists the names available.
5. **Schema ownership.** `ConfigureSchema` throws on a document type from an assembly the module
   did not declare in `SchemaAssemblies`.
6. **The pipeline hook.** A module that throws in `ConfigureApp` stops `UseBarakoCMS` with an error
   naming it.
7. **Seeding.** Each seeder runs in its own session and transaction; one throwing is logged against
   the module and does not stop the others. After all seeders run, any failures are thrown together
   as an `AggregateException`, failing startup unless the host catches it.

Not checked, and worth knowing:

- **The core version you compiled against.** The contract version is about `IBarakoModule`, not
  about the `BarakoCMS` package. Whether your module binds against the core it is loaded into is
  NuGet's question, answered by the dependency range in your `.csproj`. A module compiled against
  core 3.21 and loaded into a 5.0 that renamed a type you used fails at the first call, not at
  startup.
- **What your services and endpoints do.** The host registers what `ConfigureServices` adds and
  serves what your assembly declares. It does not inspect either.
- **The capabilities you seed.** A capability name is a string; nothing validates it. Your
  endpoints ask for it and your seeder grants it, and the two agreeing is your test's job.
- **Schema changes to existing tables.** Production runs `CreateOnly`. See the known limitation
  under "Choosing which modules run".

### A module is trusted code

Say it plainly: a module runs in the host's process, as the host's identity, with the host's
database connection. The scoped configuration section keeps core's secrets out of what the host
hands you, and it keeps nothing out of what in-process code can reach for. There is no sandbox, no
permission model and no isolation between modules beyond the schema ownership check and the
per-module seed session.

The trust decision is made when someone references your package, the same way it is made for any
other dependency. For a module author that means: read what you depend on, pin versions, and do not
reach past the contract into core's internals, because nothing stops you and the contract version
says nothing about what you find there.

### Publishing

**Name.** `BarakoCMS.*` is the first-party prefix; do not use it. `<Vendor>.BarakoCMS.<Feature>` or
`<Vendor>.<Feature>` both read well. `IBarakoModule.Name` is the short feature name (`Notes`, not
`Acme.Notes`), because it is the configuration section, the `Enabled` entry and the `DependsOn`
target, and none of those want a vendor in them.

**Tag.** Keep `barakocms-module` in `PackageTags`. One search on nuget.org returns every module that
carries it, first-party or not, and that is the whole listing. The template's `Directory.Build.props`
sets it; append your own tags after it.

**Version.** Your version is yours, semver over your own surface. Two things are versioned against
core and both belong in the README, not the version number: the `BarakoCMS` dependency range you
build against, and the contract version you declare. Widen the range when you have tested against a
newer core; bump your major when a core change forces you to break your own surface. Do not mirror
core's version, since a module that says 4.0 because core said 4.0 tells nobody what changed in it.

**README.** The template's is the house structure: one line on what it adds, how to enable it, the
configuration keys under `Modules:<Name>`, a table of endpoints with the capability each asks for,
and a compatibility line naming the contract version and the core range it was tested on. A package
with no README renders as an empty page on nuget.org, which is where people decide.

**Icon.** `assets/icon.png`, a real PNG under a megabyte, referenced from the props file. The
template ships a placeholder.

## First-party modules

| Package | What it adds |
|---|---|
| [BarakoCMS.Accounting](BarakoCMS.Accounting) | Double-entry ledger: accounts, balanced journal entries, reporting |
| [BarakoCMS.Import](BarakoCMS.Import) | Bulk import: analyze `.xlsx`/CSV uploads and create content |
| [BarakoCMS.Files](BarakoCMS.Files) | File attachments (upload/download) stored in Postgres |
| [BarakoCMS.Forms](BarakoCMS.Forms) | Public form submissions: a content type marked as a form takes anonymous, rate limited submissions stored Sensitive |
| [BarakoCMS.Email.Resend](BarakoCMS.Email.Resend) | Resend email provider (`IEmailService`) |
| [BarakoCMS.Email.Smtp](BarakoCMS.Email.Smtp) | SMTP email provider (`IEmailService`), inert until a host is configured |
| [BarakoCMS.Pages](BarakoCMS.Pages) | Page tree over a content type: parent loop, depth and reserved slug rules, public navigation and path resolution, and an authenticated tree |
| [BarakoCMS.Files.S3](BarakoCMS.Files.S3) | S3-compatible storage for the Files module (AWS S3, Cloudflare R2, SeaweedFS); public files get a direct URL, private files are proxied |
| [BarakoCMS.DeviceTrust](BarakoCMS.DeviceTrust) | Records the device behind each sign-in, binds sessions to devices, and can require OTP approval for a new device |
| [BarakoCMS.ExternalAuth](BarakoCMS.ExternalAuth) | Sign-in with Google, GitHub, Facebook or LinkedIn over OAuth, matched to a user by verified email |
| [BarakoCMS.FeatureFlags](BarakoCMS.FeatureFlags) | Feature flags, toggled and targeted by tenant, user or percentage, evaluated server side |
| [BarakoCMS.Portability](BarakoCMS.Portability) | Export and import content types and their entries as a JSON bundle |
| [BarakoCMS.Diagnostics](BarakoCMS.Diagnostics) | Client error log: browser errors posted to `/api/client-errors`, deduplicated by fingerprint |
| [BarakoCMS.Analytics.Umami](BarakoCMS.Analytics.Umami) | Admin-only proxy over an Umami instance: visitors, pages, referrers, countries, and site registration |
| [BarakoCMS.Pwa](BarakoCMS.Pwa) | Records PWA installs and installed-app launches, anonymous or tied to the signed-in user |
| [BarakoCMS.AI](BarakoCMS.AI) | Semantic search over published, public content with an embedding model run by Ollama by default |

The core also ships passwordless **email OTP sign-in** (`POST /api/auth/otp/request` + `/verify`),
which uses whatever `IEmailService` is registered.
