- **Standing up a client's instance was a runbook of curl calls that came out slightly different
  each time.** `Seed:File` names a config document the seeder applies on the first start: the format
  `GET /api/config/export` writes, plus a `tenants` section with each tenant's name, domains, first
  members (by email, with roles by name) and the sections to apply inside it. It goes through the
  same engine as `POST /api/config/apply`, as the host. A destructive change, and a section written
  for a signed-in caller (`siteSettings`, `contents`), is skipped with a warning naming it. It records
  the file's SHA-256, so a restart with the same file changes nothing; a changed file is applied
  again, every section it names, reverting API edits in those sections. A file that cannot be
  applied stops the seed with every reason and records no hash, keeping what it applied before the
  refusal, and the next start converges. With no
  `Seed:File`, the seeder does exactly what it did before. `docs/delivering-a-client-project.md`
  replaces its curl steps with the file (#905).
