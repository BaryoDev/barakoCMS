-- Keeps who can read Sensitive fields the same across the move from role names to capabilities and
-- role ids (#883).
--
-- Before 4.6.0 a Sensitive field with no visibleToRoles of its own was readable by the role named
-- HR, and a field's visibleToRoles held role names. From 4.6.0 the default is the view_sensitive
-- capability, and visibleToRoles holds role ids, so renaming a role changes nothing.
--
-- This file, in order:
--   1. refuses to run as a role that row level security would hide the content types from
--   2. gives view_sensitive to the seeded HR role, if it is stored and does not hold it already
--   3. says so, and changes nothing, when a role named HR is stored under any other id without
--      the capability
--   4. rewrites every name in a field's VisibleToRoles to the id of the role carrying that name
--
-- Step 2 is the access the name gave, as a capability. The seeded HR role is the one stored under
-- id 00000000-0000-0000-0000-000000000003 and still named exactly HR, which is the same role the
-- seeder grants the capability to at every start. The two routes reach one role and no other. The
-- name alone is not enough: HR is not a reserved name from 4.6.0, so a role an operator creates
-- under that name afterwards is theirs to grant, and a second run of this file must not grant it.
--
-- Step 3 covers a database first seeded before role ids were fixed (commit cbe50fe, 25 January
-- 2026: v3.2.0 does not have it, v4.0.0 does). There the seeder made HR under a random id, and
-- every later seeder found it by name and left the id alone. This file cannot tell that role from
-- one an operator made, so it grants nothing and raises a notice naming the id. If its holders
-- should keep reading Sensitive fields, add view_sensitive to that role in the role editor after
-- the upgrade, or with the statement the notice gives.
--
-- Step 4 leaves a name no role carries as it is. The application still matches such an entry
-- against the names of the caller's roles, exactly, so a definition this file did not reach keeps
-- working. Names are matched exactly, case included, the way the role claim was.
--
-- Roles are global and content types are per tenant in one table, so one run covers every tenant.
--
-- Run it with the API stopped, like every upgrade file, then start 4.6.0. The two releases read
-- visibleToRoles differently: 4.5 and earlier match its entries against the role names in the
-- token, and after this file the entries are ids. So while an earlier release serves a migrated
-- database, a field with a role list is masked for every role on that list and readable by
-- SuperAdmin alone, until 4.6.0 is running. Nothing is disclosed in that state, and nothing is
-- lost: the values are untouched. The other order is safe to serve: 4.6.0 on a database this file
-- has not reached still matches the names, and the seeder has granted the seeded HR role already.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/sensitivity-by-capability.sql
--
-- Safe to run twice: a role that holds the capability is skipped, a role named HR under another
-- id is never granted, and an entry that is already an id matches no role name.
-- barako:rerunnable

DO $$
BEGIN
    IF EXISTS (
           SELECT 1
           FROM pg_class c
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relname = 'mt_doc_contenttypedefinition' AND c.relrowsecurity)
       AND NOT (SELECT r.rolsuper OR r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user)
    THEN
        RAISE EXCEPTION 'sensitivity-by-capability: row level security is on for public.mt_doc_contenttypedefinition and % cannot bypass it, so this would see no content types and change nothing. Run it as a superuser.', current_user;
    END IF;
END
$$;

UPDATE public.mt_doc_roles AS stored_role
SET data = jsonb_set(
        stored_role.data,
        '{SystemCapabilities}',
        CASE WHEN jsonb_typeof(stored_role.data -> 'SystemCapabilities') = 'array'
             THEN stored_role.data -> 'SystemCapabilities'
             ELSE '[]'::jsonb
        END || '["view_sensitive"]'::jsonb)
WHERE stored_role.id = '00000000-0000-0000-0000-000000000003'
  AND stored_role.data ->> 'Name' = 'HR'
  AND NOT EXISTS (
        SELECT 1
        FROM jsonb_array_elements(
                 CASE WHEN jsonb_typeof(stored_role.data -> 'SystemCapabilities') = 'array'
                      THEN stored_role.data -> 'SystemCapabilities'
                      ELSE '[]'::jsonb
                 END) AS held(name)
        WHERE held.name #>> '{}' = '*' OR lower(held.name #>> '{}') = 'view_sensitive');

DO $$
DECLARE
    other_ids text;
BEGIN
    SELECT string_agg(stored_role.id::text, ', ' ORDER BY stored_role.id)
    INTO other_ids
    FROM public.mt_doc_roles AS stored_role
    WHERE stored_role.data ->> 'Name' = 'HR'
      AND stored_role.id <> '00000000-0000-0000-0000-000000000003'
      AND NOT EXISTS (
            SELECT 1
            FROM jsonb_array_elements(
                     CASE WHEN jsonb_typeof(stored_role.data -> 'SystemCapabilities') = 'array'
                          THEN stored_role.data -> 'SystemCapabilities'
                          ELSE '[]'::jsonb
                     END) AS held(name)
            WHERE held.name #>> '{}' = '*' OR lower(held.name #>> '{}') = 'view_sensitive');

    IF other_ids IS NOT NULL THEN
        RAISE NOTICE 'sensitivity-by-capability: a role named HR is stored under another id (%) and was not given view_sensitive, because this file cannot tell a role seeded before ids were fixed from one an operator made. Its holders read Sensitive fields that list no roles before 4.6.0 and do not from 4.6.0. To keep that, add view_sensitive to the role in the role editor, or run: update public.mt_doc_roles set data = jsonb_set(data, ''{SystemCapabilities}'', coalesce(data -> ''SystemCapabilities'', ''[]''::jsonb) || ''["view_sensitive"]''::jsonb) where id = ''<that id>'';', other_ids;
    END IF;
END
$$;

UPDATE public.mt_doc_contenttypedefinition AS definition
SET data = jsonb_set(definition.data, '{Fields}', rewritten.fields)
FROM (
    SELECT stored_type.ctid AS row_id,
           jsonb_agg(
               CASE WHEN jsonb_typeof(field.value -> 'VisibleToRoles') = 'array'
                         AND jsonb_array_length(field.value -> 'VisibleToRoles') > 0
                    THEN jsonb_set(field.value, '{VisibleToRoles}', (
                             SELECT jsonb_agg(resolved.entry ORDER BY resolved.first_seen)
                             FROM (
                                 SELECT COALESCE(to_jsonb(stored_role.id::text), listed.entry) AS entry,
                                        min(listed.ordinal) AS first_seen
                                 FROM jsonb_array_elements(field.value -> 'VisibleToRoles')
                                          WITH ORDINALITY AS listed(entry, ordinal)
                                 LEFT JOIN public.mt_doc_roles AS stored_role
                                        ON jsonb_typeof(listed.entry) = 'string'
                                       AND stored_role.data ->> 'Name' = listed.entry #>> '{}'
                                 GROUP BY 1
                             ) AS resolved))
                    ELSE field.value
               END
               ORDER BY field.ordinal) AS fields
    FROM public.mt_doc_contenttypedefinition AS stored_type
    CROSS JOIN LATERAL jsonb_array_elements(
                   CASE WHEN jsonb_typeof(stored_type.data -> 'Fields') = 'array'
                        THEN stored_type.data -> 'Fields'
                        ELSE '[]'::jsonb
                   END) WITH ORDINALITY AS field(value, ordinal)
    GROUP BY stored_type.ctid
) AS rewritten
WHERE definition.ctid = rewritten.row_id
  AND definition.data -> 'Fields' IS DISTINCT FROM rewritten.fields;
