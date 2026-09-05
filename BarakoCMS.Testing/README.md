<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/BarakoCMS.Testing/assets/icon.png" width="96" height="96" alt="BarakoCMS.Testing logo" />
  <h1>BarakoCMS.Testing</h1>
  <p><em>A running barakoCMS for your module's tests.</em></p>
</div>

---

`BarakoTestHost` starts a PostgreSQL through Testcontainers, builds the real barakoCMS host with
the modules you name, applies the schema, seeds the system roles and the admin, and runs every
module's seeder. Your test then talks to it over HTTP. Nothing is faked, so a module that fails
the contract check or reaches for a document type it does not own fails here the way it would
fail on a deployment.

## Use it

```sh
dotnet add package BarakoCMS.Testing
```

Derive a fixture that names your module and its settings, then take it as an xunit class fixture:

```csharp
public sealed class MyHost : BarakoTestHost
{
    public MyHost() : base(o =>
    {
        o.Modules.Add(new MyModule());
        o.Settings["Modules:MyModule:ApiKey"] = "test";
    }) { }
}

public class MyModuleTests : IClassFixture<MyHost>
{
    private readonly MyHost _host;
    public MyModuleTests(MyHost host) => _host = host;

    [Fact]
    public async Task The_endpoint_answers()
    {
        var client = await _host.CreateAdminClientAsync();
        var response = await client.GetAsync("/api/mymodule/things");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

`BarakoTestHost<MyModule>` is the same for a module with a parameterless constructor and no
settings. One container per fixture, shared by every test in the class. Each host validates
tokens with its own key, so several fixtures can run in one test process.

## What it gives you

| Member | What it is |
|---|---|
| `CreateClient()` | An anonymous client |
| `CreateAdminClientAsync()` | Signed in as the seeded admin (SuperAdmin and Admin) on the default tenant |
| `CreateAdminClientAsync(slug)` | The same admin on another tenant, with the membership and the `X-Tenant` header |
| `CreateClientAsync("Admin")` | A new user holding only the named seeded roles, so a capability your seeder grants is tested on its own |
| `CreateTenantAsync()` | An active tenant with a random slug |
| `OpenSession()` | A Marten session for arranging data or reading what an endpoint wrote |
| `Services` | The host's service provider |
| `AdminUsername`, `AdminPassword` | For a test that signs in through `POST /api/auth/login` |
| `JwtKey` | The key this host signs and validates with |

Settings you pass override the host's own. The host sets the connection string, a fresh JWT key,
`InitialAdmin`, `Swagger:Enabled=true` and `Seed:DemoContent=false`.

## Needs

Docker, for the container. `dotnet new barakocms-module` (the `BarakoCMS.Templates` package) produces
a module and a test project already on this package.

## Part of barakoCMS

This package belongs to [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source headless
CMS for .NET 10. [MODULES.md](https://github.com/BaryoDev/barakoCMS/blob/master/MODULES.md) is the
module contract.

Licensed under MPL-2.0.
