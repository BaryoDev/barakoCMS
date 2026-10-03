-- The Email.Resend module's sent_emails table, for a database that existed before it did.
--
-- One row per email sent on a tenant's behalf, keyed by the id Resend gave it, so the delivery
-- webhook can put a later bounce or complaint back on that tenant. Global, like email_events: the
-- webhook carries no tenant, so there is no tenant_id column and the id alone is the primary key.
-- Empty on arrival: rows appear as tenants send mail. Nothing else changes for email_events; its new
-- Tenant field lives in the JSON document and is not indexed, so it needs no DDL.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding. The statements are what db-patch emits, with IF NOT EXISTS added to the index so
-- the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.5.0/email-sent-emails.sql
--
-- Safe to run twice.

-- For db-migrate (docs/migrations.md). It records this file without running it when the table is there.
-- barako:skip-when: select to_regclass('public.mt_doc_sent_emails') is not null

CREATE TABLE IF NOT EXISTS public.mt_doc_sent_emails (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_sent_emails_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_sent_emails_idx_at
    ON public.mt_doc_sent_emails USING btree ((public.mt_immutable_timestamp(data ->> 'At')));
