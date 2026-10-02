-- Undoes migrations/4.6.0/sensitivity-by-capability.sql, for a rollback to a release before 4.6.0.
--
-- Earlier releases decide a field's visibleToRoles by role name, read from the token, so an id in
-- that list matches nobody there and the field would be readable by SuperAdmin alone. This file
-- puts the name back for every id that still names a role. An id whose role is gone stays an id,
-- which matches nobody in either release.
--
-- It also takes view_sensitive off the one role the forward file gives it to: the seeded HR role,
-- stored under id 00000000-0000-0000-0000-000000000003 and named exactly HR. That role reads
-- Sensitive fields on an earlier release by its name, as it did before.
--
-- No other role is touched. A role an operator gave view_sensitive or view_hidden on 4.6.0 keeps
-- the name in its list, so the grant is still there when 4.6.0 comes back. An earlier release
-- ignores a capability it does not know on every gate, so the name opens nothing there, and the
-- access it gave on 4.6.0 is gone while the earlier release runs, since that release never had a
-- way to express it. One thing to know: with Roles:RefuseUnknownCapabilities on, an earlier
-- release refuses an edit of a role that carries either name until the name is taken off it.
--
-- A role renamed while 4.6.0 was running comes back under its new name, so the same people read
-- the field.
--
-- Run it with the API stopped, after stopping 4.6.0 and before starting the earlier release. While
-- 4.6.0 serves a rolled back database it still matches the names, so nothing changes for a caller.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/rollback-sensitivity-by-capability.sql
--
-- Safe to run twice.

DO $$
BEGIN
    IF EXISTS (
           SELECT 1
           FROM pg_class c
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relname = 'mt_doc_contenttypedefinition' AND c.relrowsecurity)
       AND NOT (SELECT r.rolsuper OR r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user)
    THEN
        RAISE EXCEPTION 'rollback-sensitivity-by-capability: row level security is on for public.mt_doc_contenttypedefinition and % cannot bypass it, so this would see no content types and change nothing. Run it as a superuser.', current_user;
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
                                 SELECT COALESCE(stored_role.data -> 'Name', listed.entry) AS entry,
                                        min(listed.ordinal) AS first_seen
                                 FROM jsonb_array_elements(field.value -> 'VisibleToRoles')
                                          WITH ORDINALITY AS listed(entry, ordinal)
                                 LEFT JOIN public.mt_doc_roles AS stored_role
                                        ON jsonb_typeof(listed.entry) = 'string'
                                       AND stored_role.id::text = lower(listed.entry #>> '{}')
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

UPDATE public.mt_doc_roles AS stored_role
SET data = jsonb_set(
        stored_role.data,
        '{SystemCapabilities}',
        COALESCE((
            SELECT jsonb_agg(held.name ORDER BY held.ordinal)
            FROM jsonb_array_elements(stored_role.data -> 'SystemCapabilities')
                     WITH ORDINALITY AS held(name, ordinal)
            WHERE COALESCE(lower(held.name #>> '{}'), '') <> 'view_sensitive'),
            '[]'::jsonb))
WHERE stored_role.id = '00000000-0000-0000-0000-000000000003'
  AND stored_role.data ->> 'Name' = 'HR'
  AND jsonb_typeof(stored_role.data -> 'SystemCapabilities') = 'array'
  AND EXISTS (
        SELECT 1
        FROM jsonb_array_elements(stored_role.data -> 'SystemCapabilities') AS held(name)
        WHERE lower(held.name #>> '{}') = 'view_sensitive');
