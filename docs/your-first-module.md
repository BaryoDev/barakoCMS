# Your first module

This walks you from a fresh clone to a module that loads, refuses a bad write, answers a request,
and has passing tests. Follow it in order and type what it says. It takes about an hour, most of it
waiting for the first build.

[MODULES.md](../MODULES.md) is the contract and stays the reference. This page is one path through
it, and links there instead of repeating the rules.

## What a module is

A module is a class library that implements `IBarakoModule` and adds services, document types,
endpoints and seed data to a barakoCMS host without the host's code changing. Modules depend on the
core and the core never depends on a module, so core cannot name your types, and anything your
module needs from core has to be something core already exposes. A module is not a content type: a
content type is data an admin defines at runtime, and a module is compiled code that can enforce
rules a content type's schema cannot express.

You will build **Glossary**: a `glossaryTerm` content type with a `Term` and a `Definition`, a rule
that refuses a definition which uses the word it defines, and `GET /api/glossary/terms`, gated on a
capability the module declares.

## What you need

- The .NET 10 SDK. `global.json` asks for 10.0.103 or a later feature band.
- Docker, running. The tests start PostgreSQL with Testcontainers, and step 8 runs one for the host.
- git and curl.

## 1. Clone and create the project

```sh
git clone https://github.com/BaryoDev/barakoCMS.git
cd barakoCMS
dotnet new classlib -n BarakoCMS.Glossary -o BarakoCMS.Glossary
rm BarakoCMS.Glossary/Class1.cs
dotnet sln barakoCMS.sln add BarakoCMS.Glossary/BarakoCMS.Glossary.csproj
```

Every command from here on runs from the repository root.

`dotnet new classlib` writes a `.csproj` with a target framework, nullable and implicit usings in
it. This repository sets all of those once, in `Directory.Build.props`, so replace the whole file.

Create `BarakoCMS.Glossary/BarakoCMS.Glossary.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>BarakoCMS.Glossary</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\barakoCMS\barakoCMS.csproj" />
  </ItemGroup>

</Project>
```

What is missing on purpose:

- **No `TargetFramework`.** It comes from `Directory.Build.props`, so every project in the
  repository builds for the same framework.
- **No package versions.** This module needs no packages beyond what core brings. When yours does,
  add a `<PackageVersion Include="Name" Version="x.y.z" />` line to `Directory.Packages.props` and
  reference it here as `<PackageReference Include="Name" />`, with no `Version`. That one file is
  what stops two modules resolving different versions of the same dependency. Then run
  `dotnet restore` and commit the `packages.lock.json` it writes. See
  [CLAUDE.md section 3](../CLAUDE.md#3-build-and-dependency-rules).
- **`IsPackable` is false** because this is a practice module. A module you intend to publish needs
  more, covered under [Before it is a real module](#before-it-is-a-real-module).

The project reference is to `barakoCMS`, the core. That is the only direction a reference goes.

## 2. The module class

A module declares its capabilities as constants, because core does not know your module exists and
cannot declare them for you.

Create `BarakoCMS.Glossary/GlossaryCapabilities.cs`:

```csharp
namespace BarakoCMS.Glossary;

public static class GlossaryCapabilities
{
    public const string ReadTerms = "read_glossary_terms";

    // SuperAdmin holds the wildcard and passes any gate without being named.
    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [ReadTerms];
}
```

The content type is ordinary barakoCMS content. The module seeds its definition so it exists on
every host that runs the module.

Create `BarakoCMS.Glossary/GlossaryContentTypes.cs`:

```csharp
using barakoCMS.Models;

namespace BarakoCMS.Glossary;

public static class GlossaryContentTypes
{
    public const string Term = "glossaryTerm";

    public static ContentTypeDefinition TermDefinition() => new()
    {
        Id = Guid.Parse("5a1f0c3e-7d2b-4c1a-9e8f-0b6d4a2c7e11"),
        Name = Term,
        DisplayName = "Glossary term",
        Description = "One word and what it means.",
        Fields =
        [
            new() { Name = "Term", Type = "string", IsRequired = true },
            new() { Name = "Definition", Type = "string", IsRequired = true },
        ],
    };
}
```

Now the module.

Create `BarakoCMS.Glossary/GlossaryModule.cs`:

```csharp
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Glossary;

public sealed class GlossaryModule : IBarakoModule
{
    public string Name => "Glossary";

    public int ContractVersion => ModuleContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IContentLifecycleHook, GlossaryTermHook>();
    }

    public async Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
        var existing = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(t => t.Name == GlossaryContentTypes.Term, ct);
        if (existing is null)
            session.Store(GlossaryContentTypes.TermDefinition());

        await ModuleCapabilities.GrantAsync(
            session, GlossaryCapabilities.SeededRoles, GlossaryCapabilities.All, ct);
    }
}
```

What each member does, briefly, with the rule behind it linked:

- `Name` is how configuration, `BarakoCMS:Modules:Enabled` and `DependsOn` refer to the module.
- `ContractVersion` tells core which module contract you built against, so a future incompatible
  core refuses your module by name at startup instead of failing somewhere later. See
  [Contract version](../MODULES.md#contract-version).
- `ConfigureServices` registers the hook you write in the next step. `configuration` is your own
  `Modules:Glossary` section, never the application root. See
  [Configuration](../MODULES.md#configuration).
- `SeedAsync` runs on every start, so it checks before it stores. It does not call
  `SaveChangesAsync`; the host commits your seed. `ModuleCapabilities.GrantAsync` gives Admin the
  capability your endpoint will ask for. See [Seeding](../MODULES.md#seeding).

**Registering it.** There is nothing to call. The host finds every public `IBarakoModule` in a
library that references core, so a project or package reference is the registration. See
[Using modules](../MODULES.md#using-modules). This module stores no document types of its own, so it
does not override `ConfigureSchema`; when yours does, see [Schema](../MODULES.md#schema).

## 3. A hook that refuses a write

This is what a module can do that a content type cannot. The schema can say `Definition` is a
required string. It cannot say "a definition must not use the word it defines", because that
compares two fields by a rule somebody wrote. A lifecycle hook runs inside the core's content write,
after schema validation and before anything is saved. Any message it returns rejects the write with
a 400, and the message goes back to the caller.

Create `BarakoCMS.Glossary/GlossaryTermHook.cs`:

```csharp
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Glossary;

public sealed class GlossaryTermHook : IContentLifecycleHook
{
    public string ContentType => GlossaryContentTypes.Term;

    public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        var term = Read(context.Data, "Term");
        var definition = Read(context.Data, "Definition");

        if (term.Length > 0 && definition.Contains(term, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IReadOnlyList<string>>([$"The definition of '{term}' uses the word it defines."]);

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    // A JSON client usually sends camelCase keys, and the data keeps whatever arrived, so match the
    // field name the way the schema validator does: without regard to case.
    private static string Read(IReadOnlyDictionary<string, object> data, string field) =>
        data.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
            .Value?.ToString()?.Trim() ?? string.Empty;
}
```

`context.Session` is the same Marten session the write commits through, so a hook can also read
other documents or store something atomically with the entry. The real example of that is
[`BarakoCMS.Accounting/JournalEntryHook.cs`](../BarakoCMS.Accounting/JournalEntryHook.cs): it checks
every referenced account exists and allocates the next entry number inside the write transaction,
so a rejected entry never uses a number.

One rule ships ready made. A reference to the entry's own type is not a parent just because it
points at its own type (a related post pointing back is fine), so core cannot refuse loops for every
such field. If one of your fields is a parent, register `barakoCMS.Core.Hooks.ParentReferenceHook`
for it:

```csharp
services.AddScoped<IContentLifecycleHook>(_ => new ParentReferenceHook("page", "ParentPage"));
```

An update that points the field at the entry itself, closes a loop, or puts the entry more than
`MaxDepth` levels deep (64 unless you pass another number) is then refused with 400. The chain is
walked under a transaction advisory lock, so two saves racing to close a loop cannot both land.

## 4. One endpoint

Endpoints are vertical slices: a folder per action holding its request, response and endpoint. See
[CLAUDE.md section 1a](../CLAUDE.md#1a-architecture). The classes stay `internal`; FastEndpoints
finds them anyway, and the JSON they produce is the contract, not the C# types.

Create `BarakoCMS.Glossary/Features/Terms/List/Models.cs`:

```csharp
using barakoCMS.Models;

namespace BarakoCMS.Glossary.Features.Terms.List;

internal sealed class Request : PaginatedRequest;

internal sealed record Response(IReadOnlyList<TermSummary> Items, int Page, int PageSize, int TotalItems);

internal sealed record TermSummary(Guid Id, string Term, string Definition);
```

The request is a `PaginatedRequest`, which clamps the page size to 100. A list endpoint is always
bounded, whoever calls it.

Create `BarakoCMS.Glossary/Features/Terms/List/Endpoint.cs`:

```csharp
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using ContentEntry = barakoCMS.Models.Content;

namespace BarakoCMS.Glossary.Features.Terms.List;

internal sealed class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/glossary/terms");
        Definition.RequireCapability(GlossaryCapabilities.ReadTerms);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var terms = _session.Query<ContentEntry>()
            .Where(c => c.ContentType == GlossaryContentTypes.Term);

        var total = await terms.CountAsync(ct);
        var page = await terms
            .OrderBy(c => c.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        var items = page
            .Select(c => new TermSummary(c.Id, Text(c.Data, "Term"), Text(c.Data, "Definition")))
            .ToList();

        await Send.OkAsync(new Response(items, req.Page, req.PageSize, total), ct);
    }

    private static string Text(Dictionary<string, object> data, string field) =>
        data.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
            .Value?.ToString() ?? string.Empty;
}
```

`RequireCapability` is the gate, in place of a role name. An anonymous caller gets 401 before the
handler runs, and a signed-in caller whose roles do not hold `read_glossary_terms` gets 403. The
session is opened for the request's tenant, so the query cannot see another tenant's entries.

The content type is matched exactly as `glossaryTerm`. Core stores the type name as the caller
spelled it, so a client that creates `GlossaryTerm` produces entries this query does not return.

Build it:

```sh
dotnet build BarakoCMS.Glossary/BarakoCMS.Glossary.csproj
```

The build enforces `.editorconfig`, so a style problem is a build error here rather than a review
comment later.

## 5. Tests, including the authorisation ones

All backend tests live in `BarakoCMS.Tests`. Add your module to it:

```sh
dotnet add BarakoCMS.Tests/BarakoCMS.Tests.csproj reference BarakoCMS.Glossary/BarakoCMS.Glossary.csproj
```

`BarakoTestHost` is a real barakoCMS over a PostgreSQL that Testcontainers starts, with only the
modules you name, the system roles and admin seeded, and your seeder run. Nothing is faked, so a
module that breaks the contract fails here the way it would on a deployment.

Create `BarakoCMS.Tests/GlossaryModuleTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Glossary;
using BarakoCMS.Testing;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class GlossaryModuleTests : IClassFixture<GlossaryModuleTests.Host>
{
    public sealed class Host : BarakoTestHost
    {
        public Host() : base(o =>
        {
            o.Modules.Add(new GlossaryModule());
            o.Settings["JWT:Key"] = IntegrationTestFixture.JwtKey;
        })
        {
        }
    }

    private readonly Host _host;

    public GlossaryModuleTests(Host host) => _host = host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostTermAsync(HttpClient client, string term, string definition) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = GlossaryContentTypes.Term,
            data = new Dictionary<string, object> { ["Term"] = term, ["Definition"] = definition },
        }, Ct);

    private async Task<List<string>> ListedTermsAsync()
    {
        var admin = await _host.CreateAdminClientAsync(Ct);
        var body = await admin.GetFromJsonAsync<JsonElement>("/api/glossary/terms?pageSize=100", Ct);
        return body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("term").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task The_module_loads_and_is_enabled()
    {
        var admin = await _host.CreateAdminClientAsync(Ct);

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/modules", Ct);

        var glossary = body.GetProperty("items").EnumerateArray()
            .Where(m => m.GetProperty("name").GetString() == "Glossary")
            .ToList();
        glossary.Should().HaveCount(1);
        glossary[0].GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_definition_that_uses_its_own_term_is_refused_and_not_saved()
    {
        var admin = await _host.CreateAdminClientAsync(Ct);
        (await PostTermAsync(admin, "Arabica", "A coffee species grown at altitude."))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await PostTermAsync(admin, "Espresso", "Coffee brewed the espresso way.");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("uses the word it defines");
        var listed = await ListedTermsAsync();
        listed.Should().Contain("Arabica");
        listed.Should().NotContain("Espresso");
    }

    [Fact]
    public async Task An_Admin_reaches_the_list_through_the_capability_the_seeder_granted()
    {
        var client = await _host.CreateClientAsync("Admin", Ct);

        var response = await client.GetAsync("/api/glossary/terms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_in_User_without_the_capability_is_refused()
    {
        var client = await _host.CreateClientAsync("User", Ct);

        var response = await client.GetAsync("/api/glossary/terms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _host.CreateClient().GetAsync("/api/glossary/terms", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

Why the tests look like this:

- **Three authorisation tests, not one.** Authorisation is behaviour. `CreateClientAsync("Admin")`
  is a user holding only the Admin role, so its 200 proves your seeder granted the capability. Do
  not use `CreateAdminClientAsync()` for that: the seeded admin is also SuperAdmin, whose wildcard
  passes every gate, so it would pass even with the grant deleted. The User test proves the gate
  refuses someone signed in, and the anonymous test proves it refuses someone who is not.
- **The refusal test saves a good term first.** "Espresso is not in the list" is also true of an
  empty list, which proves nothing. Asserting Arabica is there makes the absence mean something. See
  [CLAUDE.md section 4](../CLAUDE.md#4-testing).
- **`[Collection("Sequential")]` and the shared `JWT:Key`.** Every class in this project that starts
  a host does both. FastEndpoints keeps some configuration in process-wide statics, so hosts start
  one at a time, and they share the key the rest of the suite signs tokens with.

Referencing your module from the test project also puts it in front of two tests that pin what the
suite can see. Each has a list that you add one line to, in alphabetical order:

- In `BarakoCMS.Tests/SuiteCompositionTests.cs`, add `"Glossary",` to the list in
  `Discovery_finds_every_first_party_module_the_process_references`.
- In `BarakoCMS.Tests/OpenApiTagTests.cs`, add `"Glossary",` to the `expected` array in
  `The_tag_set_is_pinned`. The tag comes from your namespace: what sits between `BarakoCMS.` and
  `.Features`.

Run your tests:

```sh
dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj -- -class BarakoCMS.Tests.GlossaryModuleTests
```

The first run builds every module in the repository and pulls the PostgreSQL image, so it is slow.
The summary line should read `Total: 5, Errors: 0, Failed: 0`. If you see
`DockerUnavailableException`, Docker is not running; that is not your code.

Now prove the refusal test can fail. In `GlossaryModule.cs`, put `//` in front of the
`services.AddScoped<IContentLifecycleHook, GlossaryTermHook>();` line and run the same command. The
refusal test fails, because the core accepts the entry with a 200. Remove the `//` and run it again
to see it pass. A test that passes whether or not the code it guards is there proves nothing.

Then run the two pinned tests you edited:

```sh
dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj -- -class BarakoCMS.Tests.SuiteCompositionTests -class BarakoCMS.Tests.OpenApiTagTests
```

## 6. Run it and call it

The quickstart compose runs the published image, `ghcr.io/baryodev/barako-cms`, which was built
before your module existed, so it cannot load it. Run the same host that image runs,
`BarakoCMS.Suite`, from source instead, with your module referenced.

```sh
dotnet add BarakoCMS.Suite/BarakoCMS.Suite.csproj reference BarakoCMS.Glossary/BarakoCMS.Glossary.csproj
docker run -d --name glossary-postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=barako -p 127.0.0.1:55432:5432 postgres:16-alpine
```

Port 55432 keeps it clear of a PostgreSQL you may already run on 5432. In a second terminal, from
the repository root, start the host and leave it running:

```sh
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=55432;Database=barako;Username=postgres;Password=postgres" \
JWT__Key=local-only-signing-key-at-least-32-characters \
InitialAdmin__Username=admin \
InitialAdmin__Password=local-only-password \
dotnet run --project BarakoCMS.Suite/BarakoCMS.Suite.csproj --urls http://127.0.0.1:5080
```

Every module the Suite references runs, yours included, and the host logs one warning saying the
enabled list is unset. `Development` lets Marten create and update tables freely and turns Swagger on at
`http://127.0.0.1:5080/swagger`. Wait for `Now listening on: http://127.0.0.1:5080`.

If the host stops during its first start, fix the cause and run `docker rm -f glossary-postgres`
and the `docker run` line again before retrying. A start that fails partway leaves some tables
created, and the next start then fails on `already exists`.

Back in the first terminal, sign in and keep the token:

```sh
TOKEN=$(curl -s -X POST http://127.0.0.1:5080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"local-only-password"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')
```

The module loaded:

```sh
curl -s http://127.0.0.1:5080/api/modules -H "Authorization: Bearer $TOKEN"
```

The answer lists `"name":"Glossary"` with `"enabled":true`.

A bad write is refused:

```sh
curl -s -i -X POST http://127.0.0.1:5080/api/contents \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"contentType":"glossaryTerm","data":{"Term":"Espresso","Definition":"Coffee brewed the espresso way."}}'
```

`HTTP/1.1 400 Bad Request`, and the body carries `The definition of 'Espresso' uses the word it
defines.`

A good one is saved:

```sh
curl -s -X POST http://127.0.0.1:5080/api/contents \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"contentType":"glossaryTerm","data":{"Term":"Barako","Definition":"A strong coffee from Batangas."}}'
```

The answer is the new entry's `id` and `version`. Your endpoint lists it:

```sh
curl -s http://127.0.0.1:5080/api/glossary/terms -H "Authorization: Bearer $TOKEN"
```

```json
{"items":[{"id":"...","term":"Barako","definition":"A strong coffee from Batangas."}],"page":1,"pageSize":20,"totalItems":1}
```

And refuses a caller with no token:

```sh
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5080/api/glossary/terms
```

`401`.

When you are done, stop the host with Ctrl+C and remove the database:

```sh
docker rm -f glossary-postgres
```

## Before it is a real module

Glossary is practice, so do not open a pull request with it. When you build a real one, the steps
above are the same, and these are added:

- **Open an issue first** for a new module, per [CONTRIBUTING.md](../CONTRIBUTING.md#finding-something-to-work-on).
- **Packaging.** Set `IsPackable` to true and add `PackageId`, `Version` and `Description` to the
  `.csproj`. Add a `README.md` next to it; `PackagingTests` checks it shows
  `dotnet add package <PackageId>`, mentions `BarakoCMS:Modules:Enabled`, and shows
  `modules.Add(new ...)` as the override. Copy the structure of
  [`BarakoCMS.Pwa/README.md`](../BarakoCMS.Pwa/README.md).
- **The Suite.** Keep the `BarakoCMS.Suite` reference from step 6; that is what puts a first-party
  module into the published image.
- **A changelog fragment** in `changelog.d/`, per [its README](../changelog.d/README.md).
- **Preflight.** `bash scripts/preflight.sh -class BarakoCMS.Tests.YourModuleTests` does the locked
  restore, the Release build, your test class and the repository's file checks, in the order CI does.
- **The whole suite once**, `dotnet test BarakoCMS.Tests/BarakoCMS.Tests.csproj`, because your module
  is now discovered by every test that starts a host.

A module that ships from its own repository instead starts from the `dotnet new barakocms-module`
template. See [Writing a module outside this repository](../MODULES.md#writing-a-module-outside-this-repository).

## What to build next

[#249](https://github.com/BaryoDev/barakoCMS/issues/249) lists the primitive modules nobody has
built yet: contacts, notifications, tagging, comments, full-text search, and scheduling. Each is a
real module with its own issue, and each is this page with a bigger domain. Comment `/take` on one
to claim it.
