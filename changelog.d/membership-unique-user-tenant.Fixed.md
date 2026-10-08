- **One person holds one membership per tenant.** Memberships carry a unique index on the user and
  the tenant, and `migrations/4.7.0/membership-unique-user-tenant.sql` adds it to an existing
  database. Where two rows already share a user and a tenant the file refuses, changes nothing and
  says how many pairs there are; its header has the query that lists them. Two requests adding the
  same person to a tenant at once could store two rows, and the roles the person held then depended
  on which row a read found first. Now the second request is answered 409 and stores nothing.
