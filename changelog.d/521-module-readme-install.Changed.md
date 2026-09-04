- **Module READMEs teach the package reference as the install.** Each `BarakoCMS.*` README and
  `docs/delivering-a-client-project.md` now say that `dotnet add package` plus a restart installs a
  module and `BarakoCMS:Modules:Enabled` decides whether it runs, with `modules.Add(...)` shown once
  as the override. Every module gets a patch bump so the README on nuget.org changes too. #521
