# Versioning

What a 6.x release may change, and what it may not, so you can read this page before an upgrade
instead of after. Versions are `MAJOR.MINOR.PATCH`.

Each rule below says what holds it today. **Held by** names a check that runs in CI or a test. **Review
rule** means no check stops a break, and a reviewer has to. Where the two differ, the page says so.

## The rules

| | Patch (6.1.x) | Minor (6.x.0) | Major (7.0.0) |
| --- | --- | --- | --- |
| Routes | Fixes only. No route added or removed. | May add a route. Never removes or renames one. | May remove a route that was deprecated in an earlier release. |
| Response fields | Fixes only. | May add an optional field. Never removes, renames or retypes one. | May remove a field that was deprecated. |
| Status codes | Never changes a code an existing request gets. | Same. | May change a code. |
| Config keys and defaults | Fixes only. | May add a key whose default keeps the old behaviour. Never removes or renames a key, and never changes a default. | May remove a deprecated key and change a default. |
| Secure defaults | Never loosened. | Never loosened. | May change, and says so under Breaking in the release notes. |
| Database migrations | Fixes only. A fix may ship a file that adds. | May add migration files that add objects. Never drops or rewrites what a 6.0 database holds. | May drop what was deprecated. |
| Module contract | No change. | May add a member with a default implementation. Does not remove or change one. | May remove or change a member, and raise `MinimumSupported`. |
| Published images | Same image names, tag scheme and platforms. | Same. | May change. |

One exception to the table: a security fix may tighten a default in a patch or a minor. The release
notes say so under Security and name the setting that restores the old behaviour.

Deprecation comes first. Something a major removes was marked deprecated in an earlier release.
On the C# surface that is `[Obsolete]`, with a removal version at least one full major away
(the rules file at the repository root, section 6, "Public API stability"). On the HTTP surface it is a `Deprecation` header on the response, as in
[delivery-api.md](delivery-api.md). Today only `POST /api/preview` sets it.

## What holds each rule today

**Routes and response fields.** Held by the `OpenAPI contract` job in `ci.yml`, and weekly by
`openapi-contract.yml`. It runs `scripts/openapi-contract-check.sh`, which diffs the OpenAPI
document the Suite serves against `openapi/baseline.json`. It fails on a removed or renamed path, a
removed response property, a response enum value removed, a removed request property and a tightened
pattern. Removing something after it was deprecated fails too: a deprecation notice is not a
version move. Additions pass. The gate has fixtures in `scripts/test-openapi-gate.sh`.

The gate lets a break through if the same change moves an HTTP contract number (see below). That
suits a major. It does not tell a minor from a major, so "a minor never breaks" is a review rule
that the gate backs up by making every break visible in the diff.

**Status codes.** Review rule only. The OpenAPI document does not declare every code a route can
answer with, so do not count on the gate here.

**Config keys and defaults.** Partly held. `ConfigurationReferenceTests` fails when the source reads
a key that [configuration.md](configuration.md) has no row for, and when a row names a key nothing
reads. It does not stop a key being removed (the row goes with it) or a default changing. Those are
review rules. [troubleshooting.md](troubleshooting.md) is held the same way: `TroubleshootingPageTests`
fails when a startup message the page quotes changes in the code.

**Secure defaults.** Review rule, with parts held. The startup guards refuse an example JWT key, an
example database password, an open `AllowedHosts` and a database role that bypasses row level
security. `JwtKeyGuardTests`, `DatabasePasswordGuardTests`, `DatabaseRoleGuardTests` and `SecureDefaultsTests`
(which pins the `AllowedHosts` guard) pin them.
`ComposeDefaultsTests` and `scripts/check-compose-hardening.sh` hold the compose files: no
literal admin password, no host mounts of credentials, Postgres not published off host, and the
memory, process and capability limits on every service. A secure default that no test pins is held by review.

**Database migrations.** Held in part. `ShippedMigrationTests` pins the checksum of every file in a
released version, so a released migration file cannot be edited (see
[migrations.md](migrations.md)). The `Upgrade from 4.x` job in `ci.yml` runs `scripts/upgrade-check.sh`
against a 4.9.0 and a 4.1.0 database, and proves the upgrade and the rollback. It does not start
from a 6.x release. That a minor's files only add is a review rule.

**HTTP contract numbers.** `ApiContract.Version` covers every core route that is not a delivery
route. `ApiContract.DeliveryVersion` covers the routes under `/api/public/`. A module's
`HttpContractVersion` covers its own endpoints. They show in the `X-Api-Contract-Version` and
`X-Delivery-Contract-Version` response headers and in `/api/meta`. A baseline edit that is itself
breaking fails the gate unless one of them goes up in the same change
(`scripts/contract-versions.py` reads them). `ContractSurfaceVersionTests`,
`ApiContractVersionHeaderTests` and `ModuleHttpContractVersionTests` pin the numbers and the headers.
Read these numbers before you upgrade: if one moved, something on that surface broke.

**The C# surface.** Review rule only. There is no package compatibility check in CI. The rules file at the
repository root, section 6, "Public API stability", is the rule.

**The module contract.** `ModuleContract.Version` and `ModuleContract.MinimumSupported` are in
[MODULES.md](../MODULES.md#contract-version). Held by `ModuleContractTests`: the host refuses a
module that declares a contract outside the range, by name, and accepts one that declares nothing.
That a minor only adds to the contract is a review rule.

**Published images.** Held in part. Release images are signed and carry provenance
(`scripts/verify-image-signature.sh`, see [deploy-in-production.md](deploy-in-production.md)). The
`Image platform gate` job and `scripts/check-image-platforms.sh` refuse a tag that does not serve
`linux/amd64` and `linux/arm64`. The `Performance budget` job compares the image size and the running API's
measured numbers with `perf-budget.json`, and the image scans run on every build. The image names, the port, the user it runs as and the
compose variable names are review rules.

## Modules and the core they run on

A module is a NuGet package, and two separate things decide whether it runs on a core.

1. The **dependency range** in the module's `.csproj`. NuGet reads it when the module is added. It
   is the module author's to set and widen as they test. A range with only a lower bound lets a
   later core in.
2. The **contract version** the module declares. The host reads it at startup and refuses the
   module, by name, when it is outside the range this core supports.

Neither tells you whether the module's own calls into core still work. A change in the core's own
types is covered by the C# rules above. Both checks, and what the host does not check, are in
[MODULES.md](../MODULES.md#contract-version) under "Contract version" and "What the host checks at
startup, and what it does not", and the version a module author sets is under "Publishing". Do not
read them as repeated here.

For the first-party modules, `scripts/check-module-versions.sh` fails a change when a module's
source changed since the release that published its version and the version was not bumped. It also
counts a move of the core `major.minor` a module is compiled against as a change. Its test is
`scripts/test-check-module-versions.sh`. `scripts/check-release-packages.sh` fails when a package is
missing from the release list.

## Support windows

How long a major, and the modules built for it, get fixes is in
[SECURITY.md](../SECURITY.md#supported-versions). It is a rule, not a date, so it is not repeated
here.

## Before you upgrade

1. Read `CHANGELOG.md` for every release between yours and the target. A `Breaking` entry in a patch
   or a minor is a mistake: raise it.
2. Compare the HTTP contract numbers before and after. A moved number names the surface that broke.
3. Run the migration files the release ships before the new image starts
   ([migrations.md](migrations.md)). The host refuses to start until they have run.
4. Run `docker compose config` against your `.env`. A new required variable fails there, by name
   ([troubleshooting.md](troubleshooting.md)).
