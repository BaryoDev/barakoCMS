-- Undoes migrations/5.0.0/tenant-status.sql, for a rollback to a release before 5.0.0.
--
-- barako:rerunnable
--
-- Nothing to undo, so this changes nothing. The forward file only adds Status beside IsActive, and
-- IsActive stays in every tenant document as a copy of Status (true for Active, false otherwise). A
-- release before 5.0.0 reads IsActive and ignores Status, so it sees a Suspended or Archived tenant
-- as inactive and an Active one as active. Status is kept rather than removed: a later upgrade then
-- keeps an Archived tenant Archived, where rebuilding Status from IsActive would read it as Suspended.
--
-- WHAT IS LOST: nothing.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-tenant-status.sql

DO $rollback_tenant_status$
BEGIN
    RAISE NOTICE 'rollback-tenant-status: nothing to undo; IsActive already says what a release before 5.0.0 reads.';
END
$rollback_tenant_status$;
