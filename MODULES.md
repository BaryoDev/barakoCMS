# Writing a barakoCMS module

Core is espresso. Modules are what you add to it: water makes an americano, milk a latte, syrup a
flavoured one. You never modify the espresso to make a latte, you add to it, and that is exactly what
this contract lets you do and stops you doing.

If you are deciding whether your idea is a module or a change to core, the tests are in the
[README](README.md#module-or-core).

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
today's behaviour everywhere and leaves the refusal to Marten. `true` on a `CreateOrUpdate` store
runs the check without refusing, which is how a developer sees what production would refuse before
deploying: `GET /api/modules` reports it as `needs-migration` with the object names.
`ModuleSchemaPreflightTests` holds all four cases.

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

**What moves the number.** Removing a member, changing a signature, or changing when core calls a
hook relative to the others. Adding a member with a default implementation does not, because a
module compiled against the previous version keeps working.

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

## Writing a module

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
- **Throwing fails your seed and nobody else's.** It is logged against your module name and rethrown
  to the host once every module has had its turn. A module that fails leaves the others intact.
- **Seeds must be idempotent.** They run on every start.

Modules previously shared one session committed once at the end, so one failure discarded every
module's work and any module could read another's uncommitted data.

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
- calls each `ConfigureSchema` with an `IModuleSchema` restricted to the module's own document types,
- calls each `ConfigureMarten` as well, for modules written before `ConfigureSchema` existed, logging
  a warning naming any module that still uses it,
- registers each module as a singleton `IBarakoModule` so `RunBarakoModuleSeedersAsync` can seed it.

Default services (e.g. the mock `IEmailService`) are registered with `TryAdd`, so a module can
substitute a real implementation.

## First-party modules

| Package | What it adds |
|---|---|
| [BarakoCMS.Accounting](../BarakoCMS.Accounting) | Double-entry ledger: accounts, balanced journal entries, reporting |
| [BarakoCMS.Import](../BarakoCMS.Import) | Bulk import: analyze `.xlsx`/CSV uploads and create content |
| [BarakoCMS.Files](../BarakoCMS.Files) | File attachments (upload/download) stored in Postgres |
| [BarakoCMS.Email.Resend](../BarakoCMS.Email.Resend) | Resend email provider (`IEmailService`) |
| [BarakoCMS.Email.Smtp](../BarakoCMS.Email.Smtp) | SMTP email provider (`IEmailService`), inert until a host is configured |

The core also ships passwordless **email OTP sign-in** (`POST /api/auth/otp/request` + `/verify`),
which uses whatever `IEmailService` is registered.
