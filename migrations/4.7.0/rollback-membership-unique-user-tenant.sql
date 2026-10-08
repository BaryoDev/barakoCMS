-- Undoes migrations/4.7.0/membership-unique-user-tenant.sql, for a rollback to a release before
-- 4.7.0. An older build does not declare the index, so db-assert there would list it as extra.
--
-- No row changes. A release before 4.7.0 reads memberships the same way with or without the index.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.7.0/rollback-membership-unique-user-tenant.sql
--
-- Safe to run twice.

DROP INDEX IF EXISTS public.mt_doc_memberships_uidx_user_id_tenant_slug;
