-- Undoes migrations/5.5.0/906-service-principals.sql, for a rollback to a release before service principals.
--
-- WHAT IS LOST: every service principal. An earlier release does not know the ServicePrincipalId
-- field on an API key and reads such a key as acting as its creator, so a key that acted as a
-- principal starts acting as the person who made it, and stops working if that person has left.
-- Revoke those keys before rolling back if that is not what you want.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-906-service-principals.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_service_principals;
