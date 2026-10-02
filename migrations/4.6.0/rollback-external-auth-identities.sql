-- Undoes migrations/4.6.0/external-auth-identities.sql, for a rollback to a release before OpenID
-- Connect providers could be configured.
--
-- An earlier release does not declare this table. It would still boot beside it, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping it puts the
-- database back to what that release built.
--
-- WHAT IS LOST: which provider account belongs to which user. The users themselves stay, with their
-- email and roles. After upgrading again, a person who signs in through the same provider is linked
-- again by email, which needs the provider to vouch for the address as it did the first time.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/rollback-external-auth-identities.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_external_identities;
