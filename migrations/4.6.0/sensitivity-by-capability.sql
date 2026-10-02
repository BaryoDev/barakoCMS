-- Keeps who can read Sensitive fields the same across the move from role names to capabilities and
-- role ids (#883).
--
-- Before 4.6.0 a Sensitive field with no visibleToRoles of its own was readable by the role named
-- HR, and a field's visibleToRoles held role names. From 4.6.0 the default is the view_sensitive
-- capability, and visibleToRoles holds role ids, so renaming a role changes nothing.
--
-- This file, in order:
--   1. refuses to run as a role that row level security would hide the content types from
--   2. gives view_sensitive to every role named exactly HR that does not hold it already
--   3. rewrites every name in a field's VisibleToRoles to the id of the role carrying that name
--
-- Step 2 is the access the name gave, as a capability. The seeder does the same at start-up for
-- the HR role it seeded (id 00000000-0000-0000-0000-000000000003), so a host that runs the seeder
-- keeps that role's access even if this file is skipped. A role named HR under any other id is
-- reached only by this file.
--
-- Step 3 leaves a name no role carries as it is. The application still matches such an entry
-- against the names of the caller's roles, exactly, so a definition this file did not reach keeps
-- working. Names are matched exactly, case included, the way the role claim was.
--
-- Roles are global and content types are per tenant in one table, so one run covers every tenant.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/sensitivity-by-capability.sql
--
-- Safe to run twice: a role that holds the capability is skipped, and an entry that is already an
-- id matches no role name.

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
WHERE stored_role.data ->> 'Name' = 'HR'
  AND NOT EXISTS (
        SELECT 1
        FROM jsonb_array_elements(
                 CASE WHEN jsonb_typeof(stored_role.data -> 'SystemCapabilities') = 'array'
                      THEN stored_role.data -> 'SystemCapabilities'
                      ELSE '[]'::jsonb
                 END) AS held(name)
        WHERE held.name #>> '{}' = '*' OR lower(held.name #>> '{}') = 'view_sensitive');

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
