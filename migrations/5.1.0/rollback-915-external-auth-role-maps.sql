-- Undoes migrations/5.1.0/915-external-auth-role-maps.sql, for a rollback to a release before
-- claim-to-role maps.
--
-- WHAT IS LOST: every tenant's map. Roles already given by a map stay on their memberships. After
-- upgrading again, each tenant has to set its map again.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-915-external-auth-role-maps.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_external_role_maps;
