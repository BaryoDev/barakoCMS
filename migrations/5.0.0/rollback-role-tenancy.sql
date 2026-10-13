-- Undoes migrations/5.0.0/role-tenancy.sql, for a rollback to a release before 5.0.0 (#913).
--
-- An earlier release declares a unique index on a role's name alone, mt_doc_roles_uidx_name, and
-- starts with AutoCreate.CreateOnly, so it refuses to start while the table carries 5.0.0's index on
-- the tenant and the name instead. Its index also refuses two roles with one name, which 5.0.0
-- allows in different tenants. What this file does:
--
--   1. A copy that role-tenancy.sql wrote (a role under the id derived from another role's id and
--      the copy's tenant) that still has its role's name, permissions, capabilities and MFA rule is
--      folded back into that role. Every membership and account holding the copy, and every
--      content type field listing it in VisibleToRoles, in any tenant, is pointed at the role
--      instead, and then the copy is deleted. A list that ends up naming the role twice keeps it
--      once, where it first appeared.
--   2. Any other role whose name another role still has keeps its id, its holders and its content,
--      and is renamed "<name> (<tenant>)", with a number added if that name is taken too. This is a
--      copy edited since the upgrade, or a role made in 5.0.0 under a name another tenant or a
--      platform role uses. Which role keeps the name: a platform role first, then a role under a
--      seeded id, then the default tenant's, then one that is not a copy, then the oldest. A
--      content type field of that tenant listing the old name gets the new one.
--   3. Every role that is not a platform role loses TenantSlug and IsPlatform, so running
--      role-tenancy.sql again works its owner out afresh from who holds it, and writes the same
--      copies under the same ids. A platform role keeps both, which an earlier release ignores. That
--      includes a role role-tenancy.sql made a platform role because an account held it globally:
--      an earlier release grants it in every tenant either way, and running role-tenancy.sql again
--      skips it, so it stays a platform role.
--   4. The index on the tenant and the name is dropped and mt_doc_roles_uidx_name is created
--      exactly as an earlier release's Marten declares it.
--
-- Nobody gains or loses a permission. A folded copy had the same permissions as the role it is
-- folded into, and a renamed role keeps its own. What a rename can change is a check made by the
-- role's name, which in an earlier release is global: a tenant's role named Admin made in 5.0.0 is
-- not the platform Admin, and after this it is called "Admin (<tenant>)" rather than passing for it.
--
-- This file reads the content types of every tenant. Run it as a role that row level security does
-- not hide them from: it refuses to run otherwise.
--
-- Run it with the API stopped, then start the earlier release:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-role-tenancy.sql
--
-- Safe to run twice: the second run finds no copy, no shared name and no owner to remove.
-- barako:rerunnable

-- A role list with one entry swapped for another, keeping the first position of anything that is
-- then listed twice. Temporary, so it goes when the session ends.
CREATE FUNCTION pg_temp.swap_role_entry(list jsonb, old_entry text, new_entry text) RETURNS jsonb
LANGUAGE sql IMMUTABLE AS $swap$
    SELECT coalesce((
        SELECT jsonb_agg(to_jsonb(entry) ORDER BY first_slot)
        FROM (
            SELECT CASE WHEN item #>> '{}' = old_entry THEN new_entry ELSE item #>> '{}' END AS entry,
                   min(slot) AS first_slot
            FROM jsonb_array_elements(list) WITH ORDINALITY AS listed(item, slot)
            GROUP BY 1) swapped), '[]'::jsonb)
$swap$;

CREATE FUNCTION pg_temp.swap_visible_to(fields jsonb, old_entry text, new_entry text) RETURNS jsonb
LANGUAGE sql IMMUTABLE AS $swap$
    SELECT jsonb_agg(
               CASE WHEN jsonb_typeof(field -> 'VisibleToRoles') = 'array' AND field -> 'VisibleToRoles' ? old_entry
                    THEN jsonb_set(field, '{VisibleToRoles}', pg_temp.swap_role_entry(field -> 'VisibleToRoles', old_entry, new_entry))
                    ELSE field
               END ORDER BY position)
    FROM jsonb_array_elements(fields) WITH ORDINALITY AS declared(field, position)
$swap$;

DO $rollback_role_tenancy$
DECLARE
    has_types   boolean;
    copy_row    record;
    shared      record;
    candidate   text;
    attempt     integer;
    folded      integer := 0;
    renamed     integer := 0;
    released    integer;
BEGIN
    IF to_regclass('public.mt_doc_roles') IS NULL THEN
        RAISE NOTICE 'rollback-role-tenancy: there is no roles table, so there is nothing to undo.';
        RETURN;
    END IF;

    has_types := to_regclass('public.mt_doc_contenttypedefinition') IS NOT NULL;

    IF has_types
       AND EXISTS (
           SELECT 1
           FROM pg_class c
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relname = 'mt_doc_contenttypedefinition' AND c.relrowsecurity)
       AND NOT (SELECT r.rolsuper OR r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user)
    THEN
        RAISE EXCEPTION 'rollback-role-tenancy: row level security is on for public.mt_doc_contenttypedefinition and % cannot bypass it, so this would miss the role lists on content type fields. Run it as a superuser.', current_user;
    END IF;

    DROP INDEX IF EXISTS public.mt_doc_roles_uidx_tenant_slug_name;

    -- Step 1.
    FOR copy_row IN
        SELECT c.id::text AS copy_id, o.id::text AS role_id
        FROM public.mt_doc_roles c
        JOIN public.mt_doc_roles o
          ON c.id = md5(o.id::text || ':' || (c.data ->> 'TenantSlug'))::uuid
         AND c.id <> o.id
        WHERE c.data ->> 'Name' = o.data ->> 'Name'
          AND c.data -> 'Permissions' IS NOT DISTINCT FROM o.data -> 'Permissions'
          AND c.data -> 'SystemCapabilities' IS NOT DISTINCT FROM o.data -> 'SystemCapabilities'
          AND coalesce(c.data ->> 'RequireMfa', 'false') = coalesce(o.data ->> 'RequireMfa', 'false')
        ORDER BY c.id
    LOOP
        IF to_regclass('public.mt_doc_memberships') IS NOT NULL THEN
            UPDATE public.mt_doc_memberships
            SET data = jsonb_set(data, '{RoleIds}', pg_temp.swap_role_entry(data -> 'RoleIds', copy_row.copy_id, copy_row.role_id))
            WHERE jsonb_typeof(data -> 'RoleIds') = 'array' AND data -> 'RoleIds' ? copy_row.copy_id;
        END IF;

        IF to_regclass('public.mt_doc_users') IS NOT NULL THEN
            UPDATE public.mt_doc_users
            SET data = jsonb_set(data, '{RoleIds}', pg_temp.swap_role_entry(data -> 'RoleIds', copy_row.copy_id, copy_row.role_id))
            WHERE jsonb_typeof(data -> 'RoleIds') = 'array' AND data -> 'RoleIds' ? copy_row.copy_id;
        END IF;

        IF has_types THEN
            UPDATE public.mt_doc_contenttypedefinition d
            SET data = jsonb_set(d.data, '{Fields}', pg_temp.swap_visible_to(d.data -> 'Fields', copy_row.copy_id, copy_row.role_id))
            WHERE jsonb_typeof(d.data -> 'Fields') = 'array'
              AND EXISTS (
                    SELECT 1 FROM jsonb_array_elements(d.data -> 'Fields') AS f(field)
                    WHERE jsonb_typeof(f.field -> 'VisibleToRoles') = 'array'
                      AND f.field -> 'VisibleToRoles' ? copy_row.copy_id);
        END IF;

        DELETE FROM public.mt_doc_roles WHERE id = copy_row.copy_id::uuid;
        folded := folded + 1;
    END LOOP;

    -- Step 2.
    FOR shared IN
        SELECT ranked.id, ranked.name, ranked.tenant
        FROM (
            SELECT r.id,
                   r.data ->> 'Name' AS name,
                   nullif(r.data ->> 'TenantSlug', '') AS tenant,
                   row_number() OVER (
                       PARTITION BY r.data ->> 'Name'
                       ORDER BY coalesce(r.data ->> 'IsPlatform', 'false') = 'true' DESC,
                                r.id::text LIKE '00000000-0000-0000-0000-%' DESC,
                                coalesce(r.data ->> 'TenantSlug', '') = 'default' DESC,
                                NOT EXISTS (
                                    SELECT 1 FROM public.mt_doc_roles o
                                    WHERE o.id <> r.id
                                      AND r.id = md5(o.id::text || ':' || (r.data ->> 'TenantSlug'))::uuid) DESC,
                                r.data ->> 'CreatedAt',
                                r.id) AS place
            FROM public.mt_doc_roles r
            WHERE r.data ->> 'Name' IS NOT NULL) ranked
        WHERE ranked.place > 1
        ORDER BY ranked.name, ranked.place
    LOOP
        candidate := shared.name || ' (' || coalesce(shared.tenant, 'platform') || ')';
        attempt := 1;
        WHILE EXISTS (SELECT 1 FROM public.mt_doc_roles WHERE data ->> 'Name' = candidate) LOOP
            attempt := attempt + 1;
            candidate := shared.name || ' (' || coalesce(shared.tenant, 'platform') || ' ' || attempt || ')';
        END LOOP;

        UPDATE public.mt_doc_roles SET data = jsonb_set(data, '{Name}', to_jsonb(candidate)) WHERE id = shared.id;

        IF has_types AND shared.tenant IS NOT NULL THEN
            UPDATE public.mt_doc_contenttypedefinition d
            SET data = jsonb_set(d.data, '{Fields}', pg_temp.swap_visible_to(d.data -> 'Fields', shared.name, candidate))
            WHERE d.tenant_id = shared.tenant
              AND jsonb_typeof(d.data -> 'Fields') = 'array'
              AND EXISTS (
                    SELECT 1 FROM jsonb_array_elements(d.data -> 'Fields') AS f(field)
                    WHERE jsonb_typeof(f.field -> 'VisibleToRoles') = 'array'
                      AND f.field -> 'VisibleToRoles' ? shared.name);
        END IF;

        renamed := renamed + 1;
    END LOOP;

    -- Step 3.
    UPDATE public.mt_doc_roles
    SET data = data - 'TenantSlug' - 'IsPlatform'
    WHERE coalesce(data ->> 'IsPlatform', 'false') <> 'true'
      AND (data ? 'TenantSlug' OR data ? 'IsPlatform');
    GET DIAGNOSTICS released = ROW_COUNT;

    -- Step 4. The statement an earlier release's db-assert reports for the index it declares.
    CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_roles_uidx_name
        ON public.mt_doc_roles USING btree ((data ->> 'Name'));

    RAISE NOTICE 'rollback-role-tenancy: folded % copy(ies) back into their role, renamed % role(s) that shared a name, and took the owner off % role(s).',
        folded, renamed, released;
END
$rollback_role_tenancy$;
