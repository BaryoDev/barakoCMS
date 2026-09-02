-- Rolls the 4.0.0 schema migration back to what barakoCMS 3.x expects.
--
-- Generated as the drop file of `db-patch`. Apply it with 4.0 stopped and before starting 3.x
-- again. It restores the two mt_streams snapshot columns as NULL, which is what they were.
--
-- One thing it cannot undo: any event appended while 4.0 was running that carries a bdata payload
-- loses that payload when the column goes. barakoCMS does not opt any event into binary
-- serialization, so in this project bdata is NULL for every row, but check before rolling back a
-- database that has run something else:
--   select count(bdata) from mt_events;

drop function if exists public.mt_safe_unaccent(boolean,text) cascade;
CREATE OR REPLACE FUNCTION public.mt_safe_unaccent(use_unaccent boolean, word text)
 RETURNS text
 LANGUAGE plpgsql
 IMMUTABLE STRICT
AS $function$
BEGIN
IF use_unaccent THEN
    RETURN unaccent(word);
ELSE
    RETURN word;
END IF;
END;
$function$;
alter table public.mt_streams add column snapshot jsonb NULL;
alter table public.mt_streams add column snapshot_version integer NULL;
alter table public.mt_events drop column bdata;
drop function if exists public.mt_quick_append_events(uuid, varchar, varchar, uuid[], varchar[], varchar[], jsonb[], bytea[], integer DEFAULT NULL::integer) cascade;
CREATE OR REPLACE FUNCTION public.mt_quick_append_events(stream uuid, stream_type character varying, tenantid character varying, event_ids uuid[], event_types character varying[], dotnet_types character varying[], bodies jsonb[])
 RETURNS integer[]
 LANGUAGE plpgsql
AS $function$
DECLARE
	event_version int;
	event_type varchar;
	event_id uuid;
	body jsonb;
	index int;
	seq int;
    actual_tenant varchar;
	return_value int[];
BEGIN
	select version into event_version from public.mt_streams where id = stream AND tenant_id = tenantid;
	if event_version IS NULL then
		event_version = 0;
		insert into public.mt_streams (id, type, version, timestamp, tenant_id) values (stream, stream_type, 0, now(), tenantid);
    else
        if tenantid IS NOT NULL then
            select tenant_id into actual_tenant from public.mt_streams where id = stream AND tenant_id = tenantid;
            if actual_tenant != tenantid then
                RAISE EXCEPTION 'The tenantid does not match the existing stream';
            end if;
        end if;
	end if;

	index := 1;
	return_value := ARRAY[event_version + array_length(event_ids, 1)];

	foreach event_id in ARRAY event_ids
	loop
	    seq := nextval('public.mt_events_sequence');
		return_value := array_append(return_value, seq);

	    event_version := event_version + 1;
		event_type = event_types[index];
		body = bodies[index];

		insert into public.mt_events
			(seq_id, id, stream_id, version, data, type, tenant_id, timestamp, mt_dotnet_type, is_archived)
		values
			(seq, event_id, stream, event_version, body, event_type, tenantid, (now() at time zone 'utc'), dotnet_types[index], FALSE);

		index := index + 1;
	end loop;

	update public.mt_streams set version = event_version, timestamp = now() where id = stream AND tenant_id = tenantid;

	return return_value;
END
$function$;

-- The content-type name index. 3.x has no such constraint, so dropping it only widens what the
-- database accepts and no data has to move.
DROP INDEX IF EXISTS public.mt_doc_contenttypedefinition_uidx_name;

-- Pending self-registrations. 3.x has no such table and nothing else references it, so dropping it
-- moves no data. Anything still in it is a registration that was never confirmed and, on 3.x, never
-- can be: rolling back means self-registration goes back to creating accounts outright.
DROP TABLE IF EXISTS public.mt_doc_pending_registrations;

-- Email provider settings. 3.x reads its email credentials from configuration only, so dropping
-- this loses whatever was typed into the admin and email falls back to Resend:ApiKey. The key
-- cannot be read out of here first: it is encrypted and nothing decrypts it for display, by design.
-- Have the credential to hand before rolling back.
DROP TABLE IF EXISTS public.mt_doc_email_settings;

-- Connectors and their credentials. 3.x has neither, and nothing else references them, so dropping
-- them moves no data. The secrets cannot be read out first: they are encrypted under Connectors:Key
-- and nothing decrypts one for display, by design. Have the credentials to hand before rolling back.
-- Query definitions. 3.x has no such table and nothing references it, so dropping it moves no
-- content: these are saved recipient lists rather than data, and the entries they select are
-- untouched. Note them down before rolling back, because nothing else records them.
DROP TABLE IF EXISTS public.mt_doc_query_definitions;

DROP TABLE IF EXISTS public.mt_doc_connector_secrets;
DROP TABLE IF EXISTS public.mt_doc_connectors;
