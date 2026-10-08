- **The tenant policy is put back where an upgrade file left a table without it.**
  `migrations/4.7.0/tenant-policy-restore.sql` reads whether the database enforces tenancy with row
  level security from its other tables. Where it does, each of the six tables an earlier upgrade
  file creates (share links, sourcing policies, collection syncs and the three Forms tables) that
  lacks `marten_tenant_isolation` gets the same policy, copied from a table that has it, with row
  level security on. With `Tenancy:DatabaseEnforcement` off it changes nothing.
