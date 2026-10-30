-- The Documents module's tables (#585), for a database that existed before the module did.
--
-- document_templates holds the templates a RenderDocument workflow action names by slug, one row per
-- template per tenant. document_renders remembers which run already produced which document (the
-- result) and which run is producing one now (a claim), so a retry returns the same file. Both are
-- empty on arrival. Conjoined multi-tenant, so tenant_id leads each primary key.
--
-- The slug is unique within a tenant. The action takes the first template with the slug, so two rows
-- would make the document depend on which one a read found first. The index is the one the module
-- declares (a PerTenant unique index, under Marten's own name), so db-assert passes. A database that
-- already holds two templates with one slug cannot have the index built; this file stops and names
-- the slugs instead of failing on the CREATE.
--
-- CreateOnly would create the tables on first boot; this file exists so db-assert passes before the
-- deploy instead of reporting them as outstanding.
--
-- Plain DDL plus a block, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/585-documents.sql
--
-- Safe to run twice. rollback-585-documents.sql drops both tables.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_document_templates (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_document_templates_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE TABLE IF NOT EXISTS public.mt_doc_document_renders (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_document_renders_tenant_id_id PRIMARY KEY (tenant_id, id)
);

DO $$
DECLARE
    clashes text;
BEGIN
    SELECT string_agg(format('%L (tenant %L, %s rows)', slug, tenant_id, n), ', ')
    INTO clashes
    FROM (
        SELECT tenant_id, data ->> 'Slug' AS slug, count(*) AS n
        FROM public.mt_doc_document_templates
        GROUP BY tenant_id, data ->> 'Slug'
        HAVING count(*) > 1
    ) AS duplicated;

    IF clashes IS NOT NULL THEN
        RAISE EXCEPTION '585-documents: more than one document template shares a slug, so the unique index cannot be built. Nothing was changed. Delete or rename all but one of: %', clashes;
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_document_templates_uidx_slug
    ON public.mt_doc_document_templates USING btree (((data ->> 'Slug'::text)), tenant_id);

-- Row level security follows the database. Tenancy at the database is opt in (see
-- migrations/tenancy/001-app-role.sql), and this file is rerunnable, so db-migrate can run it on
-- tables that already exist. It must never take the tenant policy off a database that enforces
-- tenancy. Enforcement is read off the database: another table in public carries
-- marten_tenant_isolation. When one does, each table here gets the same policy, copied exactly, with
-- row level security enabled and forced when that table forces it, and a policy already there is
-- left alone. When none does, the policy is dropped and row level security turned off, which is
-- what db-assert expects from a host with Tenancy:DatabaseEnforcement off.

DO $policy$
DECLARE
    target constant text := 'mt_doc_document_templates';
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
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_document_templates;
        ALTER TABLE public.mt_doc_document_templates NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_document_templates DISABLE ROW LEVEL SECURITY;
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

DO $policy$
DECLARE
    target constant text := 'mt_doc_document_renders';
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
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_document_renders;
        ALTER TABLE public.mt_doc_document_renders NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_document_renders DISABLE ROW LEVEL SECURITY;
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
