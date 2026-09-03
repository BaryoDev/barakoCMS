- **The content-type endpoints ask for a capability instead of a role name.** `/api/content-types`
  (and its `/api/schemas` alias) and the rebuild require `manage_content_types`; setting public
  delivery and setting a field's sensitivity require `manage_public_delivery`. A role created at
  runtime can be granted either.

  Two names, though both gates were the same role pair and one name would have covered them with no
  seeded role noticing. Designing a schema and deciding what an anonymous caller can read are
  different jobs: sensitivity decides whether a value is scrubbed on the way out, public delivery
  decides whether the route answers at all. A role that models content without also choosing what
  leaves the building is an ordinary thing to want, and one name makes it unexpressible.

  Admin holds both by default, because Admin reached all five routes already. Nothing is narrowed,
  and `Auth:LegacyRoleFallback` still honours the old role names while it is on.
