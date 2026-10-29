-- Puts the tenant policy back on the Forms module's tables, on a database that enforces tenancy
-- with row level security. The same file as migrations/4.7.0/tenant-policy-restore.sql, for the
-- module's three tables, and shipped from the module.
--
-- These tables are created by an upgrade file that ends by dropping the marten_tenant_isolation
-- policy and turning row level security off, so the table matches what a host with
-- Tenancy:DatabaseEnforcement off declares:
--
--   mt_doc_public_forms                                              migrations/4.2.0/forms-public-forms.sql
--   mt_doc_form_email_verifications, mt_doc_form_email_budgets       migrations/4.6.0/forms-email-verification.sql
--
-- db-migrate runs those files where a table is missing: on a database the module is new to, and the
-- 4.6.0 file whenever one of its two tables is missing, which then strips the other. On a database
-- with enforcement on that leaves the table with no tenant policy. Run again by hand, as their
-- headers allow, they also take the policy off a table that had it. This file runs after them,
-- since the ledger runs one module's files in version order, and since it ships from the module it
-- runs on a database the module is enabled on later too.
--
-- What this file does. Enforcement on is read off the database itself: another table in public
-- carries marten_tenant_isolation. When none does, enforcement is off, the tables are as they
-- should be, and nothing changes. When one does, each of the three that exists and lacks the policy
-- gets the same policy, copied from that table, and row level security is enabled, and forced when
-- that table forces it. A table that already has the policy is left alone. A table that does not
-- exist is skipped: the host creates it, with its policy, on first start.
--
-- The copy is exact (the USING and WITH CHECK expressions, the command, the roles), so db-assert
-- with enforcement on finds the policy it expects, and so does the next db-apply.
--
-- Run it as the role that owns the tables, which is the role the app connects as, or a superuser.
-- Run it with the API stopped, like every upgrade file:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.7.0/forms-tenant-policy-restore.sql
--
-- Safe to run twice: a table that has the policy is skipped.
-- barako:rerunnable

DO $restore$
DECLARE
    tables constant text[] := ARRAY[
        'mt_doc_public_forms',
        'mt_doc_form_email_verifications',
        'mt_doc_form_email_budgets'
    ];
    donor      record;
    target      text;
    forced      boolean;
    roles       text;
    restored    integer := 0;
BEGIN
    SELECT p.tablename, p.permissive, p.roles, p.cmd, p.qual, p.with_check
    INTO donor
    FROM pg_policies p
    WHERE p.schemaname = 'public'
      AND p.policyname = 'marten_tenant_isolation'
      AND p.tablename <> ALL (tables)
    ORDER BY p.tablename
    LIMIT 1;

    IF NOT FOUND THEN
        RAISE NOTICE 'tenant policy: no table carries marten_tenant_isolation, so tenancy is not enforced at the database and nothing was changed';
        RETURN;
    END IF;

    SELECT c.relforcerowsecurity INTO forced
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relname = donor.tablename;

    SELECT string_agg(CASE WHEN r = 'public' THEN 'PUBLIC' ELSE quote_ident(r) END, ', ')
    INTO roles
    FROM unnest(donor.roles) AS r;

    FOREACH target IN ARRAY tables LOOP
        CONTINUE WHEN to_regclass('public.' || target) IS NULL;
        CONTINUE WHEN EXISTS (
            SELECT 1 FROM pg_policies p
            WHERE p.schemaname = 'public' AND p.tablename = target AND p.policyname = 'marten_tenant_isolation');

        EXECUTE format('CREATE POLICY marten_tenant_isolation ON public.%I AS %s FOR %s TO %s%s%s',
            target,
            donor.permissive,
            donor.cmd,
            roles,
            CASE WHEN donor.qual IS NULL THEN '' ELSE format(' USING (%s)', donor.qual) END,
            CASE WHEN donor.with_check IS NULL THEN '' ELSE format(' WITH CHECK (%s)', donor.with_check) END);
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', target);
        IF forced THEN
            EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', target);
        END IF;

        RAISE NOTICE 'tenant policy: put marten_tenant_isolation back on public.%, copied from public.%', target, donor.tablename;
        restored := restored + 1;
    END LOOP;

    RAISE NOTICE 'tenant policy: % table(s) given the policy back', restored;
END
$restore$;
