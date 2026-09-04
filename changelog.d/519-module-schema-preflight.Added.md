- **The schema a module wants is checked before it runs.** On boot, before the schema is applied
  and before anything seeds, the host asks Marten for the migration it would apply, attributes every
  object in it to a module by the assembly its document type ships in (or to core), and logs one
  line per module saying which objects are new and which existing ones would change. When the store
  is `AutoCreate.CreateOnly` and a module wants a change to an existing object, startup stops with a
  message naming the module, the object, the policy that refuses it and what would allow it, instead
  of Marten's error several layers down. A change to a core object is attributed to every enabled
  module that overrides the deprecated `ConfigureMarten`, the only hook that can reach one.
  `BarakoCMS:Modules:SchemaPreflight` switches it: unset is on for a `CreateOnly` store and off
  otherwise, `false` keeps the old behaviour. `GET /api/modules` gains `schemaState` (`ready`,
  `needs-migration`, `unknown`) and `schemaChanges` per module from the same check. Fixes #519.
