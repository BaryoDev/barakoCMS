-- Undoes migrations/4.8.0/external-auth-used-nonces.sql, for a rollback to a release before the
-- OpenID Connect id token grant.
--
-- An earlier release does not declare this table. It would still boot beside it, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping it puts the
-- database back to what that release built.
--
-- WHAT IS LOST: which nonces have been used. The earlier release has no grant route, so nothing
-- can use one there. After upgrading again, an id token that was already exchanged and has not yet
-- expired could be exchanged once more; provider id tokens live for an hour or less.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.8.0/rollback-external-auth-used-nonces.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_oidc_used_nonces;
