- **Modules are found by reference and chosen by configuration.** `AddBarakoCMS` now discovers
  every `IBarakoModule` in the application's dependency context, so `dotnet add package` plus a
  restart is the whole install and `BarakoCMS.Suite/Program.cs` names no modules at all. Only
  libraries that reach `BarakoCMS` through their dependencies are loaded, only public top-level types with a parameterless
  constructor count, discovered modules are ordered by type name, and a type the host already added
  is skipped. `modules.Discover = false` on the builder, or `BarakoCMS:Modules:Discover=false` in
  configuration, keeps the explicit list only.

  `BarakoCMS:Modules:Enabled`, an array or a comma-separated string
  (`BarakoCMS__Modules__Enabled=Accounting,Files`), decides which of the modules found run. Unset
  runs all of them and logs one warning saying how to set it, so an existing deployment changes
  nothing on upgrade; an empty string is core only; a name that matches nothing refuses startup and
  lists the names available. Disabling a module leaves its data in place. `GET /api/modules` now
  lists every module seen with an `enabled` field, so "installed but off" and "not installed" can
  be told apart. Fixes #170 and #172.
