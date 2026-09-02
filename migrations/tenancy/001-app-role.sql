-- Hand the barakoCMS tables to a role that row level security can bind.
--
-- Run this as a superuser, once, against the barakoCMS database, BEFORE turning on
-- Tenancy:DatabaseEnforcement and BEFORE pointing the application at the new role.
--
--   psql -v app_role=barako_app -v app_password='...' -f 001-app-role.sql -d barako
--
-- WHY THIS EXISTS
--
-- Marten generates the tenant policies itself. What it cannot do is change who the application
-- connects as, and that is the part that decides whether the policies do anything: a Postgres
-- SUPERUSER bypasses row level security completely, whatever is on the table. Every compose file
-- and k8s config in this repository connects as `postgres`, which is a superuser, so turning the
-- setting on without this script would apply policies to every table and enforce nothing.
--
-- The application refuses to start in that configuration rather than run while appearing to be
-- protected. See DatabaseTenancy.AssertUsableAsync.
--
-- The owner does NOT need exempting. Marten emits FORCE ROW LEVEL SECURITY, which binds the table
-- owner too. Only the superuser attribute escapes a policy.
--
-- WHAT IT DOES
--
-- Creates a login role with NOSUPERUSER and makes it the owner of the schema and of every table,
-- sequence and function barakoCMS uses. Ownership rather than grants, because Marten's schema
-- management issues DDL and a non-owner cannot.

-- Defaults only when nothing was passed. A plain \set here would run as the file is read and
-- silently overwrite the -v flags, which is the second thing running this script taught me: the
-- command line looked honoured and was not.
\if :{?app_role} \else \set app_role barako_app \endif
\if :{?app_password} \else \set app_password 'CHANGE-ME' \endif

-- psql variables do not expand inside a DO block, so they are handed over as session settings the
-- blocks can read. Learned by running this: the first version read :'app_role' inside the block and
-- failed on a syntax error at the colon.
SELECT set_config('barako.app_role', :'app_role', false);
SELECT set_config('barako.app_password', :'app_password', false);

DO $$
DECLARE
    role_name text := current_setting('barako.app_role');
    role_password text := current_setting('barako.app_password');
BEGIN
    IF role_password = 'CHANGE-ME' THEN
        RAISE EXCEPTION 'Set app_password before running this. The placeholder is not a password.';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE',
                       role_name, role_password);
    ELSE
        -- Idempotent, and it re-asserts NOSUPERUSER in case the role was granted it since.
        EXECUTE format('ALTER ROLE %I NOSUPERUSER', role_name);
        RAISE NOTICE 'Role % already existed. Password left alone, NOSUPERUSER re-asserted.', role_name;
    END IF;
END
$$;

DO $$
DECLARE
    role_name text := current_setting('barako.app_role');
    r record;
BEGIN
    EXECUTE format('GRANT ALL ON SCHEMA public TO %I', role_name);
    EXECUTE format('ALTER SCHEMA public OWNER TO %I', role_name);

    -- Everything already in the schema. Objects the application creates afterwards are created by
    -- this role, so they arrive owned correctly.
    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
        EXECUTE format('ALTER TABLE public.%I OWNER TO %I', r.tablename, role_name);
    END LOOP;

    FOR r IN SELECT sequencename FROM pg_sequences WHERE schemaname = 'public' LOOP
        EXECUTE format('ALTER SEQUENCE public.%I OWNER TO %I', r.sequencename, role_name);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS signature
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO %I', r.signature, role_name);
    END LOOP;
END
$$;

-- The password is dropped from the session rather than left where the next statement can read it.
SELECT set_config('barako.app_password', '', false);

-- What to check before pointing the application at it. The first must be false, the second must
-- show the new role as owner of every table.
--
--   SELECT rolname, rolsuper FROM pg_roles WHERE rolname = 'barako_app';
--   SELECT tablename, tableowner FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;
--
-- rowsecurity stays false until the application starts with Tenancy:DatabaseEnforcement on, because
-- Marten creates the policies as part of its schema management. That order is deliberate: the role
-- exists first, the application connects as it, and the policies it creates are owned by it.
