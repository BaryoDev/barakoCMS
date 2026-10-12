-- Gives every stored role an owner: a tenant, or the platform (#913).
--
-- Until 5.0.0 roles were one list for the deployment, so a role made for one client was listed,
-- assigned and edited in every tenant. From 5.0.0 a role belongs to a tenant (TenantSlug) or is a
-- platform role (IsPlatform) that every tenant can assign and only manage_platform_roles changes.
--
-- What this file does, for each role that has neither yet:
--
--   1. A role stored under a fixed seeded id, one beginning 00000000-0000-0000-0000- (SuperAdmin,
--      Admin, User, the demo HR role, and a first-party module's role such as Accountant), becomes a
--      platform role. Core and its modules seed every role under an id of that form, and a role made
--      through the API never has one.
--   2. Any other role goes to the tenants whose memberships hold it, a membership of any status:
--        - held by no membership, or held as a global role on a user account: it stays in place and
--          belongs to the default tenant, where a single-tenant deployment keeps everything;
--        - otherwise it belongs to the first of those tenants by slug.
--      Each other tenant whose memberships hold it gets a copy: the same name, description,
--      permissions, capabilities and MFA rule, under an id derived from the role's id and the
--      tenant's slug, so running this again finds the same copy. That tenant's memberships, and the
--      role lists on that tenant's content type fields (VisibleToRoles), are rewritten from the
--      role's id to the copy's. The role keeps its id everywhere else.
--   3. The unique index on a role's name alone is replaced by one on the tenant and the name, with
--      the platform roles counted as one group, under the name the application declares. Copies
--      share their role's name, so the old index has to go before any copy is written.
--
-- Nobody gains or loses a permission. A member holds the same permissions after as before, through
-- a role with the same content, and a field readable by a role stays readable by whoever held it in
-- that tenant. A role a third-party module seeds under an id of another form is treated as a role
-- of the operator's own; the module's seed marks its role a platform role again on the next start,
-- and the copies stay with the tenants that held them.
--
-- This file reads the content types of every tenant. Run it as a role that row level security does
-- not hide them from: it refuses to run otherwise.
--
-- ONE ROLE. Set barako.only_role to a role id and only that role is looked at in step 2. Step 1 and
-- step 3 run as usual:
--   PGOPTIONS='-c barako.only_role=<id>' psql "$DATABASE_URL" ... -f this file
--
-- Run it with the API stopped, like every upgrade file, then start 5.0.0:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/role-tenancy.sql
--
-- No rollback file. An earlier release ignores TenantSlug and IsPlatform and reads every role as
-- global, which is what it did. A copy is a second role with the same name, which an earlier
-- release's unique index on the name refuses, so before going back delete the copies (roles with
-- the same name as another) after moving their holders back, and recreate mt_doc_roles_uidx_name.
--
-- Safe to run twice: a role with an owner is skipped, and a copy is found under its derived id.
-- barako:rerunnable

DO $role_tenancy$
DECLARE
    only_role   constant text := nullif(btrim(coalesce(current_setting('barako.only_role', true), '')), '');
    role_row    record;
    tenants     text[];
    owner       text;
    held_global boolean;
    other       text;
    copy_id     uuid;
    assigned    integer := 0;
    copied      integer := 0;
    written     integer;
BEGIN
    IF to_regclass('public.mt_doc_roles') IS NULL
       OR to_regclass('public.mt_doc_memberships') IS NULL
       OR to_regclass('public.mt_doc_users') IS NULL THEN
        RAISE NOTICE 'role-tenancy: the roles, memberships or users table is missing, so no host has started against this database and there is nothing to assign.';
        RETURN;
    END IF;

    IF EXISTS (
           SELECT 1
           FROM pg_class c
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relname = 'mt_doc_contenttypedefinition' AND c.relrowsecurity)
       AND NOT (SELECT r.rolsuper OR r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user)
    THEN
        RAISE EXCEPTION 'role-tenancy: row level security is on for public.mt_doc_contenttypedefinition and % cannot bypass it, so this would miss the role lists on content type fields. Run it as a superuser.', current_user;
    END IF;

    DROP INDEX IF EXISTS public.mt_doc_roles_uidx_name;

    -- Step 1.
    UPDATE public.mt_doc_roles
    SET data = data || jsonb_build_object('IsPlatform', true, 'TenantSlug', NULL)
    WHERE id::text LIKE '00000000-0000-0000-0000-%'
      AND NOT (coalesce(data ->> 'IsPlatform', 'false') = 'true' AND coalesce(data ->> 'TenantSlug', '') = '');

    -- Step 2.
    FOR role_row IN
        SELECT r.id, r.data, r.mt_dotnet_type
        FROM public.mt_doc_roles r
        WHERE r.id::text NOT LIKE '00000000-0000-0000-0000-%'
          AND coalesce(r.data ->> 'IsPlatform', 'false') <> 'true'
          AND coalesce(r.data ->> 'TenantSlug', '') = ''
          AND (only_role IS NULL OR r.id::text = only_role)
        ORDER BY r.id
    LOOP
        SELECT coalesce(array_agg(DISTINCT m.data ->> 'TenantSlug' ORDER BY m.data ->> 'TenantSlug'), '{}')
        INTO tenants
        FROM public.mt_doc_memberships m
        WHERE m.data -> 'RoleIds' ? role_row.id::text
          AND coalesce(m.data ->> 'TenantSlug', '') <> '';

        held_global := EXISTS (
            SELECT 1 FROM public.mt_doc_users u WHERE u.data -> 'RoleIds' ? role_row.id::text);

        owner := CASE WHEN held_global OR cardinality(tenants) = 0 THEN 'default' ELSE tenants[1] END;

        UPDATE public.mt_doc_roles
        SET data = data || jsonb_build_object('TenantSlug', owner, 'IsPlatform', false)
        WHERE id = role_row.id;
        assigned := assigned + 1;

        FOREACH other IN ARRAY tenants
        LOOP
            CONTINUE WHEN other = owner;

            copy_id := md5(role_row.id::text || ':' || other)::uuid;

            INSERT INTO public.mt_doc_roles (id, data, mt_dotnet_type)
            VALUES (
                copy_id,
                role_row.data || jsonb_build_object('Id', copy_id, 'TenantSlug', other, 'IsPlatform', false),
                role_row.mt_dotnet_type)
            ON CONFLICT (id) DO NOTHING;
            GET DIAGNOSTICS written = ROW_COUNT;
            copied := copied + written;

            UPDATE public.mt_doc_memberships m
            SET data = jsonb_set(m.data, '{RoleIds}', (
                    SELECT jsonb_agg(CASE WHEN held #>> '{}' = role_row.id::text THEN to_jsonb(copy_id::text) ELSE held END ORDER BY position)
                    FROM jsonb_array_elements(m.data -> 'RoleIds') WITH ORDINALITY AS listed(held, position)))
            WHERE m.data ->> 'TenantSlug' = other
              AND m.data -> 'RoleIds' ? role_row.id::text;

            IF to_regclass('public.mt_doc_contenttypedefinition') IS NOT NULL THEN
                UPDATE public.mt_doc_contenttypedefinition d
                SET data = jsonb_set(d.data, '{Fields}', (
                        SELECT jsonb_agg(
                                   CASE WHEN jsonb_typeof(field -> 'VisibleToRoles') = 'array'
                                             AND field -> 'VisibleToRoles' ? role_row.id::text
                                        THEN jsonb_set(field, '{VisibleToRoles}', (
                                                 SELECT jsonb_agg(CASE WHEN entry #>> '{}' = role_row.id::text THEN to_jsonb(copy_id::text) ELSE entry END ORDER BY slot)
                                                 FROM jsonb_array_elements(field -> 'VisibleToRoles') WITH ORDINALITY AS visible(entry, slot)))
                                        ELSE field
                                   END ORDER BY position)
                        FROM jsonb_array_elements(d.data -> 'Fields') WITH ORDINALITY AS declared(field, position)))
                WHERE d.tenant_id = other
                  AND jsonb_typeof(d.data -> 'Fields') = 'array'
                  AND EXISTS (
                        SELECT 1 FROM jsonb_array_elements(d.data -> 'Fields') AS f(field)
                        WHERE jsonb_typeof(f.field -> 'VisibleToRoles') = 'array'
                          AND f.field -> 'VisibleToRoles' ? role_row.id::text);
            END IF;
        END LOOP;
    END LOOP;

    -- Step 3. The statement db-assert reports for the index the application declares.
    CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_roles_uidx_tenant_slug_name
        ON public.mt_doc_roles USING btree ((data ->> 'TenantSlug'), (data ->> 'Name')) NULLS NOT DISTINCT;

    RAISE NOTICE 'role-tenancy: gave % role(s) an owner and wrote % copy(ies)%.', assigned, copied,
        CASE WHEN only_role IS NULL THEN '' ELSE '. Only ' || only_role || ' was looked at' END;
END
$role_tenancy$;
