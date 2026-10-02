-- The site_share_links table (#841), for a database that was already on 4.0 or 4.1 when 4.2.0
-- added share links.
--
-- One row per link that lets someone preview a site while it is held back. Only the SHA-256 of the
-- key is stored, never the key. Conjoined multi-tenant, so tenant_id leads the primary key, and the
-- key hash is unique per tenant because that is what redeem looks a key up by. Empty on arrival:
-- nothing writes here until someone creates a link.
--
-- 4.2.0 added this table to migrations/4.0.0/3.x-to-4.0.sql and shipped no file of its own. That
-- covers a database coming from 3.x today. It does not cover one that ran the 4.0.0 file before
-- 4.2.0 existed: nobody runs that file a second time, so db-assert reports the table as outstanding
-- and the deploy gate (scripts/assert-schema-current.sh) stops there. The statements are the ones
-- the 4.0.0 file carries for this table, unchanged, so a database that has run either is the same.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/site-share-links.sql
--
-- Safe to run twice, and it changes nothing on a database that ran the current 4.0.0 file.

CREATE TABLE IF NOT EXISTS public.mt_doc_site_share_links (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_site_share_links_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS mt_doc_site_share_links_idx_expires_at
    ON public.mt_doc_site_share_links USING btree ((public.mt_immutable_timestamptz(data ->> 'ExpiresAt')));

-- tenant_id is the second column of the index, which is what TenancyScope.PerTenant generates.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_site_share_links_uidx_key_hash
    ON public.mt_doc_site_share_links USING btree ((data ->> 'KeyHash'), tenant_id);

-- Row level security is off for this table, the same as every other conjoined document type here.
-- Tenancy at the database is opt in (see migrations/tenancy/001-app-role.sql) and db-assert reports
-- a policy this build does not declare, so these three lines are what make the assert pass on a
-- database where something once enabled it.
DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_site_share_links;
ALTER TABLE public.mt_doc_site_share_links NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_site_share_links DISABLE ROW LEVEL SECURITY;
