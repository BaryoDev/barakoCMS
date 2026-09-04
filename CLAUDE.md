# BarakoCMS

Headless CMS for .NET 10. A core web application plus optional modules shipped as NuGet packages,
with a Next.js admin UI.

Human-facing contribution rules live in `CONTRIBUTING.md`. This file is the coding standard, and it
is the working agreement for anyone (person or agent) changing code here. It is named for the tool
that reads it automatically; `CODING_STANDARDS.md` is the signpost that makes it findable by name.

---

## 1. Layout

```
barakoCMS/              Core application: endpoints, auth, content, workflow
BarakoCMS.*/            Optional modules, each its own NuGet package
BarakoCMS.Suite/        Meta-package bundling the modules
BarakoCMS.Tests/        All backend tests, unit and integration
admin/                  Next.js admin UI (lint, vitest, playwright)
docs/                   Feature documentation
k8s/, scripts/          Deployment
```

Modules depend on the core, never the reverse. If a module needs something from the core, the core
exposes it; do not add a reference back from `barakoCMS/` to a module.

## 1a. Architecture

**Vertical slices, not technical layers.** Code is organised by feature:
`Features/<FeatureName>/<Action>/`, for example `Features/Content/Create/`. A slice holds its own
`Endpoint.cs`, `Models.cs` (request and response records) and `Validator.cs`. Resist the urge to
add a shared `Services/` or `Handlers/` folder that every feature reaches into.

**REPR, not controllers.** Endpoints follow Request-Endpoint-Response via FastEndpoints. There are
no MVC controllers here.

**No repository pattern.** Inject Marten's `IDocumentSession` directly in the endpoint. Introduce a
service only when logic is genuinely shared or complex enough to test on its own, not reflexively.

```csharp
session.Store(entity);                    // save
session.Query<T>().Where(...);            // query
await session.SaveChangesAsync(ct);       // commit
```

**Validation** uses FluentValidation through the FastEndpoints `Validator<T>` base class, not
hand-rolled checks inside `HandleAsync`.

**A list endpoint is bounded.** Take `PaginatedRequest` (or cap the result), always. An unbounded
query on an anonymous endpoint is an availability problem anyone can trigger.

**Prefer an existing pattern over a new one.** If a neighbouring endpoint solves the same problem,
match it or say in the pull request why not. Check the pattern first: a pattern being existing is
not evidence it is correct, and if it looks wrong, say so rather than spreading it.

### Adding an endpoint

1. Create `Features/<FeatureName>/<ActionName>/`.
2. Define `Request` and `Response` records.
3. Add an `Endpoint` class inheriting `Endpoint<Request, Response>`.
4. Implement `Configure()` for the route and permissions.
5. Implement `HandleAsync()` for the logic.
6. Add tests. Authorisation is part of the behaviour, so test it.

## 2. Stack

- **.NET 10**, one target framework for every project (set in `Directory.Build.props`)
- **FastEndpoints** for HTTP endpoints
- **Marten** over PostgreSQL for persistence and event sourcing
- **Serilog** for logging, **prometheus-net** for metrics
- **xUnit** with **Testcontainers** for integration tests

## 3. Build and dependency rules

**Shared MSBuild settings belong in `Directory.Build.props`.** `TargetFramework`, `Nullable`,
`ImplicitUsings`, licence and company metadata are set once. A `.csproj` keeps only what is
genuinely its own: `PackageId`, `Version`, `Description`, `RootNamespace`, project references.

**Package versions belong in `Directory.Packages.props`.** Reference packages without a version:

```xml
<PackageReference Include="Marten" />
```

Adding a new package means adding a `<PackageVersion>` entry there first. This is what stops two
modules resolving different versions of the same dependency. Then run `dotnet restore` and commit
the `packages.lock.json` changes: every restore in CI, the Dockerfiles and the scripts runs in
locked mode, and a lock file that does not match fails with NU1004 instead of being regenerated.

**No floating versions.** `3.7.*` makes two builds of the same commit non-reproducible. Pin it.

**Developer-machine files stay out of the repository.** `launchSettings.json` pins ports, a launch
URL and a browser on whoever committed it, and on a test project it does nothing at all.
`.gitignore` covers it; the core app's profile is the one deliberate exception, because it is how
`dotnet run` picks up the Development environment.

**A config default must preserve existing behaviour.** Adding a flag must not turn off something
that used to work. Default it to what happens today and let people opt in. A default that silently
removes a feature from every existing deployment is a breaking change with no signature change to
show for it.

**Formatting is `.editorconfig`'s job**, enforced at build time via `EnforceCodeStyleInBuild`. Do
not reformat code you are not otherwise changing; it buries the real diff.

## 4. Testing

```bash
dotnet test BarakoCMS.Tests/BarakoCMS.Tests.csproj      # backend
npm --prefix admin run test                             # admin unit
npm --prefix admin run test:e2e                         # admin end to end
```

**Integration tests need Docker running.** Testcontainers starts PostgreSQL and MinIO. Without
Docker the suite reports a large number of failures that are environmental, not regressions. Check
the error before assuming you broke something: `DockerUnavailableException` means start Docker.

### Tests for a bug fix must fail before the fix

Either write the failing test first, or revert the production change and confirm the test goes red
before re-applying it. A test that passes both ways proves nothing.

Beware coincidental passes. Default ordering, seed data, or an empty collection can make a broken
path produce the right answer for the input you happened to pick. Construct inputs where broken
and fixed behaviour differ visibly.

**An assertion over a collection must first assert the collection is not empty.**

```csharp
items.Should().OnlyHaveUniqueItems();   // passes on an empty list, proving nothing
```

```csharp
items.Should().HaveCount(3);           // now the uniqueness assertion has something to run on
items.Should().OnlyHaveUniqueItems();
```

This is the specific form of coincidental pass that keeps recurring, and it was got wrong here in a
test whose whole purpose was to catch a bug.

### Naming

Test classes are `{Subject}Tests`. Test methods read as sentences describing the behaviour:
`A_voided_entry_is_excluded_from_balances`. Keep that style; it makes a failure list readable.

## 5. Verification discipline

- **Rebuild before trusting a green run.** `--no-build` can pass against stale output. If you are
  reporting build or test state, build fresh first.
- **Read the exit code, not the last line.** Piping through `tail` or `grep` returns *that*
  command's exit code, so a failed build can look like it succeeded.
- **Confirm which branch you are on** before drawing a conclusion from a search.

## 6. Public API stability

### What section 6 covers

The rule below applies to the package's public surface, and the surface is the boundary rather than
the accident of what happens to be marked `public`. In scope:

- `Modules/*` and `Core/Interfaces/*`, the module contract
- `Models/*` and `Events/*`, the documents and events a consumer stores and reads
- `Features/Workflows/IWorkflowAction`, `IWorkflowEngine` and `WorkflowActionResult`, since custom
  actions are a documented extension point and the result is what one returns
- `AddBarakoCMS` and `UseBarakoCMS`, the entry points
- `DataSeeder`, which a host assembling its own startup calls

Out of scope, and `internal` so that stays true: everything under `Features/*`. The endpoints, their
`Request` and `Response` records, and their validators are how this host implements the API, not
something another assembly compiles against. FastEndpoints discovers internal endpoint classes, and
`InternalsVisibleTo` covers the tests.

If a type outside that list needs to be public, that is a deliberate addition to the contract. Say
so in the pull request.

### The rule

Within a major version, do not remove or change the signature of a public member. Instead:

- add a new overload, mark the old one `[Obsolete]`, and have the old one call the new one;
- add interface members with a default implementation so existing implementors still compile;
- give every `[Obsolete]` a removal version at least one full major away.

Unavoidable breaks get called out explicitly in the pull request.

## 7. Comments

Default to no comment. Names and small methods carry the meaning. A comment earns its place when
it explains a non-obvious *why*, an invariant the types cannot express, or a deliberate edge case.

Linking a tracked issue to explain a surprising decision is welcome and stays useful after the
issue closes.

Do not leave provenance noise: no `// fix for X`, `// added for the Y flow`, `// see PR #123`.
That belongs in commit messages and rots in source.

## 8. Commits and pull requests

- Branch: `{type}/{issue}-{short-description}`, type one of
  `feature | bugfix | improvement | qa | chore`
- PR title: `Area: Description (closes #123)`
- PR body: `Fixes #123` on its own line, since GitHub only auto-closes from the body
- Commit messages: short and human. No AI attribution trailers, no `Co-Authored-By`.

## 9. Security

- Secrets never enter the repository. CI runs Gitleaks; treat a hit as a real incident, since
  rotating is the only fix once a secret is pushed.
- Parameterise every query. Marten handles this, but raw SQL must not be built by concatenation.
- Never log passwords, tokens, API keys or connection strings.
- Authorisation is checked server side on every endpoint. The admin UI hiding a control is not
  access control.
