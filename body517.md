`AddBarakoCMS` now discovers every `IBarakoModule` in the application's dependency context and `BarakoCMS:Modules:Enabled` decides which of them run. `BarakoCMS.Suite/Program.cs` names no modules any more. `GET /api/modules` lists every module seen with an `enabled` field.

What changed

- `BarakoModuleBuilder.DiscoverFrom()` with no arguments reads `DependencyContext.Default.RuntimeLibraries`, loads only libraries whose dependency closure reaches `BarakoCMS`, and registers public top-level concrete `IBarakoModule` types with a parameterless constructor, ordered by full type name, skipping a type the host already added. `AddBarakoCMS` calls it after the host's callback unless `modules.Discover = false` (or `BarakoCMS:Modules:Discover=false` for a host with no callback).
- `BarakoCMS:Modules:Enabled` reads as an array or a comma-separated string, matched case-insensitively on `IBarakoModule.Name`. Unset: every module runs and one warning says how to set the list. Empty string: core only. A name that matches nothing refuses startup and lists the names available.
- `ModuleCatalogue` (internal) records every module seen and whether it runs; the endpoint reads it instead of the `IBarakoModule` singletons. `ModuleSummary` gains `Enabled`; the two existing fields are unchanged.
- The contract version check that already ran at startup now runs over the merged list, so a discovered module is refused the same way as an added one; the message names the module, its version and the range core accepts.
- Docs: `MODULES.md` (the enabled list, disabling leaves data, the schema preflight limitation), `docs/module-inventory.md`, `quickstart/README.md`, `SECURITY.md` (a referenced module is trusted in-process code and the site owner is the reviewer).
- `Microsoft.Extensions.DependencyModel` is now a direct reference of core, pinned in `Directory.Packages.props`.

Proof

`dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj --no-build -- -class ...` over ModuleEnablementTests, ModuleDiscoveryTests, SuiteCompositionTests, ModulesEndpointTests, ModuleContractTests, ModuleOrderTests, ModuleConfigurationScopeTests, SmtpEmailModuleTests and PublicSurfaceTests: 60 passed, 0 failed. Each new behaviour was checked by mutating the production code and watching its test go red: the warning removed, an empty string read as unset, the list not applied, an unknown name tolerated, the catalogue and the endpoint reporting enabled for everything, discovery returning nothing, the duplicate guard removed, the sort reversed, the opt-out ignored, and private nested types let through. Eleven mutations, eleven red runs, all restored and rebuilt green.

Seeding: `Only_an_enabled_module_is_seeded` shows the seed runner reads the filtered registrations, so a module enabled for the first time seeds on that boot and a disabled one is never asked.

Dormancy: `SuiteCompositionTests` pins the set discovery finds from the same references the Suite has (the thirteen plus Email.Smtp, which the Suite project referenced all along and the hand-written list never added) and holds that Files.S3, AI and Email.Smtp stay dormant until their own section is configured, with the paired test that each one wakes.

Decisions

- The test assembly holds fake modules (private nested doubles plus one public `DiscoverableProbeModule`). Discovery only sees public top-level types, so the doubles are invisible to it. The integration fixture opts out of discovery for the process (`DiscoveryDefault`, a module initializer setting `BarakoCMS__Modules__Discover=false`) because it wires module services and schema by hand and discovery on top of that registered each schema twice. Tests that build their own in-memory configuration are unaffected and exercise discovery directly.
- "Depends on core" is transitive reach, not a direct reference: `BarakoCMS.Files.S3` references only `BarakoCMS.Files`, and a direct-only rule would miss it.
- A library that reaches core and then fails to load throws with the library name rather than being skipped, because a silently missing module is the failure this exists to prevent. The escape hatch is `BarakoCMS:Modules:Discover=false` plus explicit adds.
- A host with no modules at all gets no unset warning; there is nothing the list would decide.
- An empty JSON array reads as unset because the JSON provider emits no key for it; the docs say to use `""` for core only.
- The module docs live in `MODULES.md` at the repository root, which is where the existing module documentation is; there is no `docs/MODULES.md`.
- `BarakoCMS:Modules:Discover` is a second configuration key, added so a host that calls `AddBarakoCMS(configuration)` with no callback can still turn discovery off. The callback's setting wins over it.

Gaps

- Schema preflight for a newly enabled module under `AutoCreate.CreateOnly` is not implemented; `MODULES.md` documents it as a known limitation. Needs an issue.
- No test covers a discovered module with an unsupported contract version, because a public discoverable type declaring one would refuse startup for every host in the test process. The check runs over the merged list and `ModuleContractTests` covers the refusal.
- Contract check runs over enabled modules only. A disabled module with an unsupported version is not refused, on the reasoning that it configures nothing.

Fixes #170
Fixes #172


Follow-ups filed from review: #519 (schema preflight before a module is enabled) and #521 (module READMEs still teach modules.Add).
