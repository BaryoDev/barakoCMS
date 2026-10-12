-- The two numbering tables (#584), for a database that existed before gapless numbering did.
--
-- barako_number_counters holds one row per tenant, series and period: the last number handed out.
-- The allocator takes the next number by updating that row inside the caller's transaction, so a
-- rolled back write gives its number back and the series has no gaps. mt_doc_external_number_uses
-- records a number that came from outside (a paper receipt book, an older system) so the allocator
-- never hands it out again. Both are per tenant. Both are empty on arrival: rows appear when a
-- module first allocates or records a number.
--
-- CreateOnly would create both on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the tables
-- as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added to the indexes
-- so the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.9.0/gapless-numbering.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.barako_number_counters (
    tenant_id     varchar    NOT NULL,
    series        varchar    NOT NULL,
    period        varchar    NOT NULL,
    last_value    bigint     NOT NULL,
    CONSTRAINT pkey_barako_number_counters_tenant_id_series_period PRIMARY KEY (tenant_id, series, period)
);

CREATE TABLE IF NOT EXISTS public.mt_doc_external_number_uses (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_external_number_uses_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS mt_doc_external_number_uses_idx_series
    ON public.mt_doc_external_number_uses USING btree ((data ->> 'Series'));

CREATE INDEX IF NOT EXISTS mt_doc_external_number_uses_idx_number
    ON public.mt_doc_external_number_uses USING btree ((CAST(data ->> 'Number' as bigint)));

-- Row level security follows the database. Tenancy at the database is opt in (see
-- migrations/tenancy/001-app-role.sql), and this file is rerunnable, so db-migrate can run it on a
-- table that already exists. It must never take the tenant policy off a database that enforces
-- tenancy, the gap migrations/4.7.0/tenant-policy-restore.sql had to close for older files.
-- Enforcement is read off the database the same way that file does: another table in public carries
-- marten_tenant_isolation. When one does, this table gets the same policy, copied exactly, with row
-- level security enabled and forced when that table forces it, and a policy already here is left
-- alone. When none does, the policy is dropped and row level security turned off, which is what
-- db-assert expects from a host with Tenancy:DatabaseEnforcement off.
DO $policy$
DECLARE
    target constant text := 'mt_doc_external_number_uses';
    donor  record;
    forced boolean;
    roles  text;
BEGIN
    SELECT p.tablename, p.permissive, p.roles, p.cmd, p.qual, p.with_check
    INTO donor
    FROM pg_policies p
    WHERE p.schemaname = 'public'
      AND p.policyname = 'marten_tenant_isolation'
      AND p.tablename <> target
    ORDER BY p.tablename
    LIMIT 1;

    IF NOT FOUND THEN
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_external_number_uses;
        ALTER TABLE public.mt_doc_external_number_uses NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_external_number_uses DISABLE ROW LEVEL SECURITY;
        RETURN;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies p
        WHERE p.schemaname = 'public' AND p.tablename = target AND p.policyname = 'marten_tenant_isolation') THEN
        SELECT string_agg(CASE WHEN r = 'public' THEN 'PUBLIC' ELSE quote_ident(r) END, ', ')
        INTO roles
        FROM unnest(donor.roles) AS r;

        EXECUTE format('CREATE POLICY marten_tenant_isolation ON public.%I AS %s FOR %s TO %s%s%s',
            target,
            donor.permissive,
            donor.cmd,
            roles,
            CASE WHEN donor.qual IS NULL THEN '' ELSE format(' USING (%s)', donor.qual) END,
            CASE WHEN donor.with_check IS NULL THEN '' ELSE format(' WITH CHECK (%s)', donor.with_check) END);
        RAISE NOTICE 'tenant policy: put marten_tenant_isolation on public.%, copied from public.%', target, donor.tablename;
    END IF;

    SELECT c.relforcerowsecurity INTO forced
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relname = donor.tablename;

    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', target);
    IF forced THEN
        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', target);
    END IF;
END
$policy$;
