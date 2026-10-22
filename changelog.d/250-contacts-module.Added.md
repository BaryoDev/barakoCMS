- **A Contacts module: one record for a person or an organization.** `BarakoCMS.Contacts` stores
  contacts with typed custom fields (declared on the `contact-record` content type), person to
  organization links with a role, and a unique email per tenant. A duplicate email answers 409
  naming the existing contact, and `POST /api/contacts/{id}/merge` folds a duplicate into it.
  Reads need `read_contacts` and writes `manage_contacts`, both granted to Admin. Published
  contacts are served at `/api/public/contacts` with Public fields only, never email, phone or
  address. The module ships its own contract number (`HttpContractVersion` 1) and moves no core
  number. See `BarakoCMS.Contacts/README.md`.
