-- Adds the correlation id and the causation id to every stored event (#691).
--
-- Two nullable columns on mt_events, and the two matching arguments on mt_quick_append_events.
-- correlation_id is the id of the request that wrote the event, the same value the response
-- carries in X-Correlation-ID. causation_id is the W3C traceparent of the span that wrote it. An
-- event stored before this file ran keeps both null, and nothing reads them as required.
--
-- Why this file has to exist: the app runs AutoCreate.CreateOnly in production and on playground,
-- which never alters a table or replaces a function that is already there. Without it the new
-- build reaches ApplyMartenSchemaAsync, reports the difference and does not start.
--
-- It can be applied while the old build is still serving, then deploy, like the 4.3.0 and 4.4.0
-- event store files. The app appends in Marten's Rich mode (RestoreV8Defaults), where an event is
-- written by a plain INSERT that names its own columns, so a 4.5 build keeps writing with the two
-- columns there and never calls the function this replaces. scripts/upgrade-check.sh writes
-- through the old build after this file to hold that true. The same caveat as those files: an old
-- instance that restarts after this file fails its own start-up schema assertion, so apply it
-- shortly before the deploy and not days ahead.
--
-- Cost: two ADD COLUMN with no default, which is a catalogue change and not a table rewrite, and
-- one function replacement. No row is touched.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/event-correlation-metadata.sql
--
-- Safe to run twice. The function is Marten 9.40's own text for a store with these two columns
-- on: the body is compared as text, so an edited comment inside it is a difference the start-up
-- assertion refuses. EventCorrelationMigrationTests compares it with the one Marten creates.
-- barako:rerunnable

ALTER TABLE public.mt_events
    ADD COLUMN IF NOT EXISTS correlation_id varchar NULL;
ALTER TABLE public.mt_events
    ADD COLUMN IF NOT EXISTS causation_id varchar NULL;

DROP FUNCTION IF EXISTS public.mt_quick_append_events(stream uuid, stream_type character varying, tenantid character varying, event_ids uuid[], event_types character varying[], dotnet_types character varying[], bodies jsonb[], bdatas bytea[], expected_version integer) cascade;

CREATE OR REPLACE FUNCTION public.mt_quick_append_events(stream uuid, stream_type varchar, tenantid varchar, event_ids uuid[], event_types varchar[], dotnet_types varchar[], bodies jsonb[], bdatas bytea[], causation_ids varchar[], correlation_ids varchar[], expected_version integer DEFAULT NULL::integer) RETURNS int[] AS $$
DECLARE
	event_version int;
	stream_is_archived boolean;
	event_type varchar;
	event_id uuid;
	body jsonb;
	index int;
	seq int;
    actual_tenant varchar;
    is_new_stream boolean := false;
	return_value int[];
BEGIN
    if expected_version IS NOT NULL then
        -- COALESCE turns the NULL we get for a brand-new stream into 0, so a
        -- FetchForWriting against a non-existent stream (which sets
        -- ExpectedVersionOnServer = 0) and a StartStream(id, version: 0) both
        -- land on the new-stream branch instead of mis-firing the guard.
        select version, is_archived into event_version, stream_is_archived from public.mt_streams where id = stream AND tenant_id = tenantid;
        if COALESCE(event_version, 0) != expected_version then
            RAISE EXCEPTION 'Stream version mismatch on ''%'': expected %, actual %', stream, expected_version, COALESCE(event_version, 0) USING ERRCODE = 'MT003';
        end if;
    else
        select version, is_archived into event_version, stream_is_archived from public.mt_streams where id = stream AND tenant_id = tenantid;
    end if;

	if event_version IS NULL then
		event_version = 0;
		is_new_stream := true;
		-- #5456 follow-up: insert the FINAL version straight away rather than 0 plus a
		-- trailing UPDATE of the row we just wrote in this same transaction. That UPDATE
		-- is a second heap tuple + WAL record per stream creation for no observable gain
		-- -- nothing outside this transaction can see the intermediate 0. The trailing
		-- UPDATE below is skipped for this branch via is_new_stream.
		insert into public.mt_streams (id, type, version, timestamp, tenant_id) values (stream, stream_type, COALESCE(array_length(event_ids, 1), 0), now(), tenantid);
    else
        if stream_is_archived then
            RAISE EXCEPTION 'Attempted to append event to archived stream with Id ''%''.', stream USING ERRCODE = 'MT001';
        end if;
        if tenantid IS NOT NULL then
            select tenant_id into actual_tenant from public.mt_streams where id = stream AND tenant_id = tenantid;
            if actual_tenant != tenantid then
                RAISE EXCEPTION 'The tenantid does not match the existing stream';
            end if;
        end if;
	end if;

	index := 1;
	-- #5062: array_length('{}', 1) is NULL in PostgreSQL, not 0, so a call with an
	-- empty event array used to return ARRAY[NULL] -- a bigint[] whose only element
	-- is NULL, which Npgsql cannot read into long[] ('Cannot read a non-nullable
	-- collection of elements because the returned array contains nulls'). COALESCE
	-- makes the empty case mean what it says: zero events appended, so the final
	-- version is the stream's current version.
	return_value := ARRAY[event_version + COALESCE(array_length(event_ids, 1), 0)];

	foreach event_id in ARRAY event_ids
	loop
        seq := nextval('public.mt_events_sequence');
		return_value := array_append(return_value, seq);

	    event_version := event_version + 1;
		event_type = event_types[index];
		body = bodies[index];

		-- #4515 / #4578 / Phase 2: bdatas[index] carries the binary payload
		-- for events opted in to binary serialization (NULL otherwise).
		-- bodies[index] is the {} JSON placeholder for those events so the
		-- existing data jsonb NOT NULL constraint stays intact.
		insert into public.mt_events
			(seq_id, id, stream_id, version, data, bdata, type, tenant_id, timestamp, mt_dotnet_type, is_archived, causation_id, correlation_id)
		values
			(seq, event_id, stream, event_version, body, bdatas[index], event_type, tenantid, (now() at time zone 'utc'), dotnet_types[index], FALSE, causation_ids[index], correlation_ids[index]);

		index := index + 1;
	end loop;

	-- A brand-new stream already carries its final version from the insert above, so the
	-- UPDATE is only needed when we appended onto a stream that already existed.
	if not is_new_stream then
		update public.mt_streams set version = event_version, timestamp = now() where id = stream AND tenant_id = tenantid;
	end if;

	return return_value;
END
$$ LANGUAGE plpgsql;
