- **A host running some of its modules crashed at startup.** With `BarakoCMS:Modules:Enabled`
  naming one module, FastEndpoints' own discovery still scanned every loaded module assembly, so
  the modules left off the list had their endpoints mapped with none of their services registered,
  and `UseBarakoCMS` threw `Unable to resolve service`. Endpoints of a module that is not enabled
  are no longer mapped and answer 404, as `docs/module-inventory.md` already said.
