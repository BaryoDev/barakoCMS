- **A caller holding `manage_roles` could grant itself a full authorisation bypass.**
  `PermissionResolver` granted every capability to any role whose `Name` was `SuperAdmin`, and role
  create put no guard on the name a caller supplied, so `POST /api/roles {"name":"SuperAdmin"}`
  followed by assigning it bypassed every capability gate, `erase_content` included, which is
  deliberately withheld from Admin. The resolver now identifies the seeded role by its id, which
  `SystemRoles` already documents as the key. That alone was not enough: `TokenIssuer` puts role
  *names* into the JWT and `SensitivityService` reads one back with `IsInRole("SuperAdmin")` to skip
  field scrubbing, and a claim carries no id, so the same fake role also switched off sensitivity
  masking for its holder. Reserving the four seeded names on both role write paths closes that
  second route, which is the only point both paths pass through.
