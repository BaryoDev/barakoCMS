-- The rollback for migrations/4.7.0/tenant-policy-restore.sql, for a rollback to a release before
-- 4.7.0. It changes nothing, on purpose.
--
-- That file only ever adds the tenant policy where the database already enforces tenancy on its
-- other tables. An earlier build with Tenancy:DatabaseEnforcement on declares that same policy on
-- every conjoined table, these included, so it expects what the file left. Taking the
-- policy off again would remove tenant isolation from those tables, and db-assert on the earlier
-- build would then report it as outstanding.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.7.0/rollback-tenant-policy-restore.sql
--
-- Safe to run twice.

SELECT 'tenant-policy-restore needs no rollback' AS note;
