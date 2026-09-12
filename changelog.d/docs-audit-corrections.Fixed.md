- **Six documentation claims that were checkably false.** The 4.0 rollback guide said it restores two
  columns; it drops nine tables, and the email provider key and connector credentials in them cannot
  be read back first because nothing decrypts them for display. It also called the migration safe to
  re-run when four statements carry no guard. `compliance-posture` claimed a CycloneDX SBOM per
  package and per image attached to each release; there is one solution-wide SBOM kept as a 90-day
  workflow artifact. `delivering-a-client-project` had `Auth:LegacyRoleFallback` defaulting to true
  when 4.0 defaults it false, still described the cross-tenant audit read that
  `Features/Audit/List` closed, and listed `DOMAIN_ADMIN` as required after the console moved out.
  `SECURITY.md` still said 4.0 had not shipped.
