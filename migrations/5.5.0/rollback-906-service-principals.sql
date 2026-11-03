-- Undoes migrations/5.5.0/906-service-principals.sql, for a rollback to a release before service principals.
--
-- WHAT IS LOST: every service principal. An earlier release does not know the ServicePrincipalId
-- field on an API key and reads such a key as acting as its creator, so a key that acted as a
-- principal starts acting as the person who made it, and stops working if that person has left.
-- This script revokes those keys first (see below), so no key changes who it acts as.
--
-- WHY KEYS ARE REVOKED: an earlier release also ignores the ReadOnly flag, so a read-only key
-- would turn into a full-access key of its creator. Revoking every read-only key and every
-- principal key keeps a rollback from widening anyone's access. Issue new keys afterwards.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-906-service-principals.sql
--
-- Safe to run twice.

UPDATE public.mt_doc_api_keys
SET data = jsonb_set(data, '{Revoked}', 'true'::jsonb),
    mt_last_modified = transaction_timestamp(),
    mt_version = md5(random()::text || clock_timestamp()::text)::uuid
WHERE data->>'ReadOnly' = 'true' OR data->>'ServicePrincipalId' IS NOT NULL;

DROP TABLE IF EXISTS public.mt_doc_service_principals;
