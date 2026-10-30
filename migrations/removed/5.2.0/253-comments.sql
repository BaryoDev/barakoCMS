-- The Comments module's comments table (#253), for a database that existed before it did.
--
-- One row per comment on a record, keyed to the record by TargetId. Conjoined multi-tenant, so
-- tenant_id leads the primary key. Empty on arrival: a row appears when someone posts a comment.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding. The statements are what db-patch emits, with IF NOT
-- EXISTS added to the index so the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/253-comments.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_comments (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_comments_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS mt_doc_comments_idx_target_id
    ON public.mt_doc_comments USING btree ((CAST(data ->> 'TargetId' as uuid)));

-- Row level security is off for this table, the same as every other conjoined document type here.
-- Tenancy at the database is opt in (see migrations/tenancy/001-app-role.sql) and db-assert reports
-- a policy this build does not declare, so these three lines are what make the assert pass.
DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_comments;
ALTER TABLE public.mt_doc_comments NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_comments DISABLE ROW LEVEL SECURITY;
