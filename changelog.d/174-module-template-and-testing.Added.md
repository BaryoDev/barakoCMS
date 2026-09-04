- **A module author starts from a template and tests on a packable host.** `dotnet new install
  BarakoCMS.Templates` then `dotnet new barakocms-module -n Acme.Notes` produces a module that builds,
  registers and passes its own tests: one endpoint gated on a capability the module declares and
  grants to Admin at seed, one document type, options bound from `Modules:Notes`, a README in the
  house structure, an icon placeholder, packaging metadata inherited from a shared props file with
  the `barakocms-module` tag, and a test project. The tests run on `BarakoCMS.Testing`, a new
  package holding `BarakoTestHost`: the real host over a Testcontainers PostgreSQL with the modules
  you name registered, the system roles and the admin seeded, every module's seeder run, a client
  signed in as the admin, a client for a named role, a tenant helper and a Marten session. Both
  packages are proved from outside the solution by `scripts/check-module-template.sh`, which CI runs
  and the release runs against the artifact it is about to publish. `MODULES.md` gains the section a
  third party needs: what the host checks at startup and what it does not, that a module is trusted
  in-process code, and how to name, version and describe a published one. Fixes #174.
