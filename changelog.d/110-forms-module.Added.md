- **A form submissions module.** Every client site has a contact form and there was nowhere for a
  submission to land. `BarakoCMS.Forms` adds a per-tenant form definition (name, fields typed from
  `FieldTypeRegistry`, required flags, notify addresses, enabled flag) under `/api/forms`, an
  anonymous `POST /api/public/forms/{name}`, and the submissions behind `view_form_submissions` as a
  paged list, a single read and a CSV export. The public endpoint runs under its own per-address
  rate limit (`Modules:Forms:SubmissionsPerMinute`, 5), drops anything with a value in the honeypot
  field (`Modules:Forms:HoneypotField`, `website`) with a silent 202, refuses a body over 32 KB with
  413 and a field over 4000 characters with 400, and stores each submission as its own Sensitive
  document that no public delivery route can serve. Each submission emails the form's notify
  addresses best effort; a queue with retries is #106. See `docs/forms.md`. Closes #110.
