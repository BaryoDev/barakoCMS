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
-- and the deploy gate (scripts/assert-schema-current.sh) stops there. The table and its indexes are
-- the statements the 4.0.0 file carries, unchanged. The row level security lines at the end differ:
-- here they are guarded, so they never take away a tenant policy that is already on the table.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/site-share-links.sql
--
-- Safe to run twice, and it changes nothing on a database that already has the table, with
-- Tenancy:DatabaseEnforcement on or off.
--
-- With enforcement on and the table absent (a 4.0 or 4.1 database), this creates the table without
-- the tenant policy. db-assert run with enforcement on then reports the policy as outstanding;
-- db-apply adds it. See "Schema changes after 4.0" in docs/upgrading-to-4.0.md.

-- For db-migrate (docs/migrations.md). It records this file without running it when the table is there.
-- barako:skip-when: select to_regclass('public.mt_doc_site_share_links') is not null

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

-- Tenancy at the database is opt in (see migrations/tenancy/001-app-role.sql). With it off, db-patch
-- ends this table with row level security disabled and not forced, and that is what the two
-- statements below leave. They run only when the table carries no policy: with enforcement on, the
-- app has put marten_tenant_isolation and forced row level security on this table, db-assert under
-- that configuration expects both, and a migration has no business removing them. With no policy
-- there is nothing to drop, so the DROP POLICY the 4.0.0 file has is not needed here.
DO $guard$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policy
                   WHERE polrelid = 'public.mt_doc_site_share_links'::regclass) THEN
        ALTER TABLE public.mt_doc_site_share_links NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_site_share_links DISABLE ROW LEVEL SECURITY;
    END IF;
END
$guard$;
