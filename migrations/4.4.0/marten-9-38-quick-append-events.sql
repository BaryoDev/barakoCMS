-- Marten 9.37.0 to 9.38.0 replaces the body of mt_quick_append_events. The signature is unchanged.
--
-- 9.38 routes StartStream through this function, where it used to take a per-event INSERT whose
-- sequence numbers nothing read back, so a failed batch could leave a permanent gap that stalled
-- the async daemon (Marten #5456). The new body also inserts a new stream's mt_streams row at its
-- final version instead of inserting 0 and updating it in the same transaction (Marten #5464).
--
-- Why this file has to exist: the app runs AutoCreate.CreateOnly in production and on playground,
-- which never replaces a function that is already there. An upgraded install keeps the 9.37 body,
-- the start-up assertion reports the difference, and the host crash-loops. Apply this while the
-- old build is still serving, then deploy. The 9.37 build calls the function with the same
-- arguments and gets the same result, so running it early is safe.
--
-- Cost: a function replacement, no data touched.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.4.0/marten-9-38-quick-append-events.sql
--
-- Safe to run twice. The statements are what db-patch emits for this upgrade, verbatim: the body
-- is compared as text, so an edited comment inside it is a difference the assertion refuses.

DROP FUNCTION IF EXISTS public.mt_quick_append_events(stream uuid, stream_type character varying, tenantid character varying, event_ids uuid[], event_types character varying[], dotnet_types character varying[], bodies jsonb[], bdatas bytea[], expected_version integer) cascade;

CREATE OR REPLACE FUNCTION public.mt_quick_append_events(stream uuid, stream_type varchar, tenantid varchar, event_ids uuid[], event_types varchar[], dotnet_types varchar[], bodies jsonb[], bdatas bytea[], expected_version integer DEFAULT NULL::integer) RETURNS int[] AS $$
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
			(seq_id, id, stream_id, version, data, bdata, type, tenant_id, timestamp, mt_dotnet_type, is_archived)
		values
			(seq, event_id, stream, event_version, body, bdatas[index], event_type, tenantid, (now() at time zone 'utc'), dotnet_types[index], FALSE);

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
