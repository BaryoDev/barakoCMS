-- Rolls back migrations/5.1.0/event-headers.sql: drops mt_events.headers and puts back the
-- mt_quick_append_events that 5.0 declares. Event headers stored since the upgrade are lost.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-event-headers.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP FUNCTION IF EXISTS public.mt_quick_append_events(stream uuid, stream_type character varying, tenantid character varying, event_ids uuid[], event_types character varying[], dotnet_types character varying[], bodies jsonb[], bdatas bytea[], causation_ids character varying[], correlation_ids character varying[], headers jsonb[], expected_version integer) cascade;

ALTER TABLE public.mt_events DROP COLUMN IF EXISTS headers;

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
	--
	-- #5539: and only when we actually appended something. With ZERO events this was a BLIND write-back
	-- of the version read at the top of the function, and under READ COMMITTED that is a lost update: the
	-- UPDATE waits on a concurrent appender's row lock, the winner commits, the WHERE clause is
	-- re-evaluated against the new row, and `set version = <the stale value>` lands -- leaving
	-- mt_streams.version BELOW the stream's own events. Every later append then computes a version that
	-- already exists and dies on pk_mt_events_stream_and_version, forever, until the row is repaired by
	-- hand. A non-empty call cannot do this: its first INSERT collides with the winner on that same
	-- primary key and rolls the whole thing back, which is why only the empty case was exposed.
	--
	-- Two independent guards, because either alone would have been enough and the cost of both is nil:
	--   * the append count -- appending nothing has nothing to write, so the correct number of statements
	--     is zero rather than one that happens to be harmless;
	--   * greatest(), so the write-back can never LOWER a stream's version whoever calls it. An append
	--     only ever moves a version forward, so this is a no-op on every correct path and a backstop on
	--     any future caller that reads the version and then writes it back.
	if not is_new_stream and COALESCE(array_length(event_ids, 1), 0) > 0 then
		update public.mt_streams set version = greatest(version, event_version), timestamp = now() where id = stream AND tenant_id = tenantid;
	end if;

	return return_value;
END
$$ LANGUAGE plpgsql;

