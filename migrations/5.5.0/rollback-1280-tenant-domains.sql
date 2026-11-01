-- Undoes migrations/5.5.0/1280-tenant-domains.sql, for a rollback to a release before verified domains.
--
-- An earlier release does not declare this table and routes every domain in a tenant's Domains list,
-- which this release left in place. Dropping the table puts the database back to what that release
-- built.
--
-- WHAT IS LOST: which domains were pending, and every verification token. A domain that was pending
-- routes again under the earlier release, because that release does not know about verification.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-1280-tenant-domains.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_tenant_domains;
