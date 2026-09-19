-- The collection_syncs table (#794), for a database that existed before collection syncs did.
--
-- One row per content type that is filled from an outside source on a schedule: which request
-- definition or feed to read, which response path becomes which field, and which field is the
-- stable key. It holds no credential; the request it names points at the connector, which is where
-- the credential lives, encrypted. Conjoined multi-tenant, so tenant_id leads the primary key, and
-- the slug is unique per tenant so one tenant taking "nuget-downloads" does not stop another using
-- it. Empty on arrival: nothing writes here until an operator configures a sync.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding. scripts/assert-schema-current.sh runs that assert while the old container is
-- still serving, and an operator reading its output has to find the file it names under the release
-- they are deploying. The statements are what db-patch emits, with IF NOT EXISTS added to the index
-- so the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.3.0/collection-syncs.sql
--
-- Safe to run twice.

CREATE TABLE IF NOT EXISTS public.mt_doc_collection_syncs (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_collection_syncs_tenant_id_id PRIMARY KEY (tenant_id, id)
);

-- tenant_id is the second column of the index, which is what TenancyScope.PerTenant generates. A
-- global unique index here would let the first tenant to take a slug stop every other tenant using
-- that name.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_collection_syncs_uidx_slug
    ON public.mt_doc_collection_syncs USING btree ((data ->> 'Slug'), tenant_id);

-- Row level security is off for this table, the same as every other conjoined document type here.
-- Tenancy at the database is opt in (see migrations/tenancy/001-app-role.sql) and db-assert reports
-- a policy this build does not declare, so these three lines are what make the assert pass on a
-- database where something once enabled it.
DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_collection_syncs;
ALTER TABLE public.mt_doc_collection_syncs NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_collection_syncs DISABLE ROW LEVEL SECURITY;
