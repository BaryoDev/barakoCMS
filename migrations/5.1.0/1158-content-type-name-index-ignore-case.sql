-- Makes the unique index on a content type's name ignore case (#1158).
--
-- Every lookup of a content type by name compares ignoring case, and the index was on the raw
-- value, so a module seed or older data could store "Article" beside "article" and every later
-- lookup of either spelling failed as ambiguous. From 5.1.0 core declares the index on the
-- lowercased name, per tenant. Marten creates it only where the table does not yet exist: the app
-- runs AutoCreate.CreateOnly, which never alters an object that is there. Without this file an
-- upgraded database keeps the exact-case index.
--
-- The index cannot be built while two names in one tenant differ only by case, and this file does
-- not pick which one to keep: each can have fields and content hanging off it. So it refuses,
-- names how many names are affected, and changes nothing. To see them:
--
--   select tenant_id, lower(data ->> 'Name') as name, count(*)
--   from public.mt_doc_contenttypedefinition
--   group by 1, 2 having count(*) > 1;
--
-- For each, decide which definition to keep, delete the others by id, and run this file again.
--
-- Run it with the API stopped, like every upgrade file. A plain build, not CONCURRENTLY, for the
-- reason migrations/4.5.0/refresh-token-hash-index.sql gives. The table has one row per content
-- type, so the build takes moments.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/1158-content-type-name-index-ignore-case.sql
--
-- Safe to run twice: when the index is already on the lowercased name it is left alone. The
-- expression is Marten's, verbatim, and ContentTypeNameIndexTests compares it with the index
-- Marten builds.

-- For db-migrate (docs/migrations.md). It records this file without running it when the table is not there yet, in which case the first start creates the right index, or the index is already on the lowercased name.
-- barako:skip-when: select to_regclass('public.mt_doc_contenttypedefinition') is null or exists (select 1 from pg_indexes where schemaname = 'public' and tablename = 'mt_doc_contenttypedefinition' and indexname = 'mt_doc_contenttypedefinition_uidx_name' and indexdef like '%lower(%')

DO $$
DECLARE
    clashes integer;
BEGIN
    SELECT count(*) INTO clashes
    FROM (
        SELECT 1
        FROM public.mt_doc_contenttypedefinition
        GROUP BY lower((data ->> 'Name'::text)), tenant_id
        HAVING count(*) > 1
    ) AS duplicated;

    IF clashes > 0 THEN
        RAISE EXCEPTION '1158-content-type-name-index-ignore-case: % name(s) are stored more than once in one tenant with only the case differing, so the unique index cannot be built. Nothing was changed. The header of this file has the query that lists them and what to do.', clashes;
    END IF;
END
$$;

DROP INDEX IF EXISTS public.mt_doc_contenttypedefinition_uidx_name;

CREATE UNIQUE INDEX mt_doc_contenttypedefinition_uidx_name
    ON public.mt_doc_contenttypedefinition USING btree (lower((data ->> 'Name'::text)), tenant_id);
