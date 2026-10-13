- **A tenant's role could take a module's defaults by sharing its name.** Role names are unique only
  within one owner since #913, and a module that found its seeded role by name took the first role of
  that name in any tenant. `CapabilityDefaults.GrantAsync`, the obsolete `ModuleCapabilities.GrantAsync`
  and the Accounting seed now read only ownerless and platform roles, then the default tenant's, so
  a tenant's own `Accountant` is never granted the module's capabilities or adopted in place of the
  platform role. Accounting 4.4.0 (#913).
