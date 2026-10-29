-- The Contacts module's tables (#250), for a database that existed before the module did.
--
-- mt_doc_contacts holds one row per person or organization, per tenant, so tenant_id leads the
-- primary key. The unique index on EmailKey (the address trimmed and lower cased) is what makes two
-- creates of one address race to a single winner; tenant_id is in it, which is what
-- TenancyScope.PerTenant generates, so the same address can exist once in each tenant. A contact
-- with no address has no key, and NULLs do not collide. mt_doc_contact_links holds the person to
-- organization links, indexed on both ends. Contact uses optimistic concurrency, which is the
-- mt_version column both tables are created with. Empty on arrival.
--
-- CreateOnly would create all of it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the
-- tables as outstanding. Shipped from the Contacts module, so it runs only where the module is
-- enabled.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/contacts.sql
--
-- Safe to run twice. rollback-contacts.sql drops both tables.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_contacts (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_contacts_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_contacts_uidx_email_key
    ON public.mt_doc_contacts USING btree ((data ->> 'EmailKey'), tenant_id);

CREATE INDEX IF NOT EXISTS mt_doc_contacts_idx_kind
    ON public.mt_doc_contacts USING btree ((CAST(data ->> 'Kind' as integer)));

CREATE TABLE IF NOT EXISTS public.mt_doc_contact_links (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_contact_links_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS mt_doc_contact_links_idx_person_id
    ON public.mt_doc_contact_links USING btree ((CAST(data ->> 'PersonId' as uuid)));

CREATE INDEX IF NOT EXISTS mt_doc_contact_links_idx_organization_id
    ON public.mt_doc_contact_links USING btree ((CAST(data ->> 'OrganizationId' as uuid)));

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
    target constant text := 'mt_doc_contacts';
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
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_contacts;
        ALTER TABLE public.mt_doc_contacts NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_contacts DISABLE ROW LEVEL SECURITY;
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
    target constant text := 'mt_doc_contact_links';
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
        DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_contact_links;
        ALTER TABLE public.mt_doc_contact_links NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.mt_doc_contact_links DISABLE ROW LEVEL SECURITY;
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
