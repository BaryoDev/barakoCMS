- **`preflight.sh` no longer passes having tested nothing.** Run with no `-class`, it did a restore,
  a build and the file scans, then printed "all checks passed" without executing a single test. That
  is the hole the script already refuses one level down, where a `-class` matching zero tests is a
  failure rather than a clean finish. It now refuses the empty argument list for the same reason,
  with `--no-tests` for the case where only the file scans are wanted.
- **A new check asserts the three pinned versions agree.** `barakoCMS.csproj`, the template pack's
  own version and the core version a scaffolded module targets all move together on a release, and
  nothing compared them. On 4.1.0 the core moved and the other two did not, which surfaced as
  `ModuleTemplateTests` failing eighteen minutes into CI. `scripts/check-pinned-versions.sh` now
  says so before the build.
