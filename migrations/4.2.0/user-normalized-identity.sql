-- Moves user uniqueness from the stored Username and Email to their normalised forms (#638).
--
-- Every lookup compares the trimmed, lowercased value, and the unique indexes were on the value as
-- entered. So "A@example.com" and "a@example.com" were two values to the index and one to the
-- query, and two registrations racing past the query could both insert.
--
-- This file, in order:
--   1. refuses to run if two existing accounts would collide once normalised, and names them
--   2. writes NormalizedUsername and NormalizedEmail into every user document
--   3. drops the unique indexes on the raw values and creates them on the normalised ones
--
-- A collision is reported, never resolved. Merging two accounts or picking one to keep is a
-- decision about real people, so it is left to the operator: rename or remove one of the named
-- accounts, then run this again.
--
-- Run it on its own transaction so a refusal leaves the database as it was:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/user-normalized-identity.sql
--
-- Safe to run twice. The normalisation here is lower(btrim(...)), which is close to
-- User.NormalizeIdentity but not the same: btrim strips spaces only, where .NET trims tabs and
-- every other whitespace character too, and lower follows the database's locale, which under
-- lc_ctype C leaves every non-ASCII letter as it is. So "alice" and "alice" followed by a tab pass
-- the check below as two names, and so do "Émile" and "émile" on a C locale. The application does
-- not trust what this writes: at every start, UserIdentityBackfill recomputes both fields in .NET,
-- rewrites the ones that differ, and refuses to start, naming both accounts, when that collides.

DO $$
DECLARE
    collisions text;
BEGIN
    SELECT string_agg(format('%s %L is held by %s', kind, normalized, holders), E'\n' ORDER BY kind, normalized)
    INTO collisions
    FROM (
        SELECT 'username' AS kind,
               lower(btrim(coalesce(data ->> 'Username', ''))) AS normalized,
               string_agg(format('%s (%s)', id, data ->> 'Username'), ', ' ORDER BY id) AS holders
        FROM public.mt_doc_users
        GROUP BY 2
        HAVING count(*) > 1
        UNION ALL
        SELECT 'email',
               lower(btrim(coalesce(data ->> 'Email', ''))),
               string_agg(format('%s (%s)', id, data ->> 'Username'), ', ' ORDER BY id)
        FROM public.mt_doc_users
        GROUP BY 2
        HAVING count(*) > 1
    ) found;

    IF collisions IS NOT NULL THEN
        RAISE EXCEPTION 'user-normalized-identity: some accounts share a username or email once case and surrounding spaces are ignored. Nothing was changed. Rename or remove one account in each pair, then run this again.'
            USING DETAIL = collisions;
    END IF;
END
$$;

UPDATE public.mt_doc_users
SET data = data || jsonb_build_object(
    'NormalizedUsername', lower(btrim(coalesce(data ->> 'Username', ''))),
    'NormalizedEmail', lower(btrim(coalesce(data ->> 'Email', ''))));

DROP INDEX IF EXISTS public.mt_doc_users_uidx_username;
DROP INDEX IF EXISTS public.mt_doc_users_uidx_email;

-- The expressions are Marten's, verbatim. UserIdentityMigrationTests compares them to the indexes
-- Marten builds, so a name or expression written from memory fails there instead of at start-up.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_users_uidx_normalized_username
    ON public.mt_doc_users USING btree (((data ->> 'NormalizedUsername'::text)));
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_users_uidx_normalized_email
    ON public.mt_doc_users USING btree (((data ->> 'NormalizedEmail'::text)));
