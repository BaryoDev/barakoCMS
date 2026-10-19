-- Undoes migrations/5.1.0/1158-content-type-name-index-ignore-case.sql, for a rollback to a release before 5.1.0.
--
-- barako:rerunnable
--
-- Puts back the exact-case unique index on a content type's name. Always possible: names that are
-- unique ignoring case are unique exactly.
--
-- WHAT IS LOST: nothing.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-1158-content-type-name-index-ignore-case.sql

DROP INDEX IF EXISTS public.mt_doc_contenttypedefinition_uidx_name;

DO $rollback_1158$
BEGIN
    IF to_regclass('public.mt_doc_contenttypedefinition') IS NOT NULL THEN
        CREATE UNIQUE INDEX mt_doc_contenttypedefinition_uidx_name
            ON public.mt_doc_contenttypedefinition USING btree (((data ->> 'Name'::text)), tenant_id);
    END IF;
END
$rollback_1158$;
