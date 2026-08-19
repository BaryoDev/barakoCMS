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

Pass modules when registering the CMS:

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Accounting.AccountingModule());
    modules.Add(new BarakoCMS.Import.ImportModule());
    modules.Add(new BarakoCMS.Files.FilesModule());
    modules.Add(new BarakoCMS.Email.Resend.ResendEmailModule());
});

var app = builder.Build();
app.UseBarakoCMS();
await app.RunBarakoModuleSeedersAsync();  // runs each module's SeedAsync
```

Calling `AddBarakoCMS(config)` with no modules behaves exactly as before, because modules are purely additive
and backward-compatible.

You can also discover modules by reflection:

```csharp
modules.DiscoverFrom(typeof(SomeModule).Assembly);
```

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

The core also ships passwordless **email OTP sign-in** (`POST /api/auth/otp/request` + `/verify`),
which uses whatever `IEmailService` is registered.
