-- The rollback for migrations/4.7.0/forms-tenant-policy-restore.sql, for a rollback to a release
-- before 4.7.0. It changes nothing, on purpose, for the reason
-- migrations/4.7.0/rollback-tenant-policy-restore.sql gives: an earlier build with
-- Tenancy:DatabaseEnforcement on declares the same policy on these tables, and taking it off again
-- would remove tenant isolation from them.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.7.0/rollback-forms-tenant-policy-restore.sql
--
-- Safe to run twice.

SELECT 'forms-tenant-policy-restore needs no rollback' AS note;
