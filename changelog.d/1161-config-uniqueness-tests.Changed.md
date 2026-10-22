- **Config apply and export carry a content type's uniqueness rules, and a test now says so.** A
  new type in a config document is created with its rules, checked the way `POST /api/content-types`
  checks them. A document without `uniqueness` keeps a stored type's rules, and one with different
  rules is pointed at `PUT /api/content-types/{name}/uniqueness`. The behaviour was already there
  through the shared upsert; the docs and tests did not cover config.
