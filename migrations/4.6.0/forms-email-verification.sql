-- The Forms module's two email verification tables (#811), for a database that existed before
-- they did.
--
-- form_email_verifications holds one row per email address a form sent a one-time code to: the
-- code's hash, when it expires, how often it was checked and how many codes the address was sent
-- lately. The id is a SHA-256 of the address, so the address itself is not in the table.
-- form_email_budgets holds one row per form: how many codes it sent in the current window. Both
-- are conjoined multi-tenant, so tenant_id leads the primary key. Both are loaded by id only, so
-- neither has an index. Empty on arrival: nothing writes here until a form turns verification on
-- and a visitor asks for a code.
--
-- CreateOnly would create them on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the
-- tables as outstanding. The statements are the shape db-patch emits for mt_doc_public_forms, which
-- has the same kind of id and no index either.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/forms-email-verification.sql
--
-- Safe to run twice.

CREATE TABLE IF NOT EXISTS public.mt_doc_form_email_verifications (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_form_email_verifications_tenant_id_id PRIMARY KEY (tenant_id, id)
);

DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_form_email_verifications;
ALTER TABLE public.mt_doc_form_email_verifications NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_form_email_verifications DISABLE ROW LEVEL SECURITY;

CREATE TABLE IF NOT EXISTS public.mt_doc_form_email_budgets (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_form_email_budgets_tenant_id_id PRIMARY KEY (tenant_id, id)
);

DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_form_email_budgets;
ALTER TABLE public.mt_doc_form_email_budgets NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_form_email_budgets DISABLE ROW LEVEL SECURITY;
