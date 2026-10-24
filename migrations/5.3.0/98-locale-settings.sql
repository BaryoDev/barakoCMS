-- The locale settings table (#98), for a database that existed before it did.
--
-- One row per tenant, holding the languages the tenant publishes in, so tenant_id leads the primary
-- key. Empty on arrival: a row appears when an administrator saves locale settings. Core ships
-- this file.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the
-- table as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added so the
-- file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/98-locale-settings.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_locale_settings (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_locale_settings_tenant_id_id PRIMARY KEY (tenant_id, id)
);

-- Row level security follows the database. Tenancy at the database is opt in (see
-- migrations/tenancy/001-app-role.sql), and this file is rerunnable, so db-migrate can run it on a
-- table that already exists. It must never take the tenant policy off a database that enforces
-- tenancy. Enforcement is read off the database: another table in public carries
-- marten_tenant_isolation. When one does, each table here gets the same policy, copied exactly, with
-- row level security enabled and forced when that table forces it, and a policy already there is
-- left alone. When none does, the policy is dropped and row level security turned off, which is
-- what db-assert expects from a host with Tenancy:DatabaseEnforcement off.

DO $policy$
DECLARE
    target constant text := 'mt_doc_locale_settings';
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
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_locale_settings;
        ALTER TABLE public.mt_doc_locale_settings NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_locale_settings DISABLE ROW LEVEL SECURITY;
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
