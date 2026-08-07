# BarakoCMS

Headless CMS for .NET 8. A core web application plus optional modules shipped as NuGet packages,
with a Next.js admin UI.

Human-facing contribution rules live in `CONTRIBUTING.md`. This file is the working agreement for
anyone (person or agent) changing code here.

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

## 2. Stack

- **.NET 8**, one target framework for every project (set in `Directory.Build.props`)
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
modules resolving different versions of the same dependency.

**No floating versions.** `3.7.*` makes two builds of the same commit non-reproducible. Pin it.

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

BarakoCMS ships as NuGet packages, so external code compiles against these types. Within a major
version, do not remove or change the signature of a public member. Instead:

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
