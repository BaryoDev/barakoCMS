-- The Forms module's public_forms table (#720), for a database that existed before the module did.
--
-- One row per content type that accepts anonymous submissions. Conjoined multi-tenant, so tenant_id
-- leads the primary key, and the id is the content type's name. Empty on arrival: nothing writes
-- here until an operator marks a type as a form. CreateOnly would create it on first boot, since a
-- missing table is a creation and not an alteration; this file exists so db-assert passes before
-- the deploy instead of reporting the table as outstanding. The statements are what db-patch emits,
-- so the assertion finds exactly what the module declares.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/forms-public-forms.sql
--
-- Safe to run twice.

CREATE TABLE IF NOT EXISTS public.mt_doc_public_forms (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_public_forms_tenant_id_id PRIMARY KEY (tenant_id, id)
);

DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_public_forms;
ALTER TABLE public.mt_doc_public_forms NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_public_forms DISABLE ROW LEVEL SECURITY;
