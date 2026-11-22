-- The login role the barakoCMS API connects as: no superuser, owner of everything in schema public.
--
-- Run by the database owner (a superuser) on every start, or by hand:
--
--   DB_APP_USER=barako_app DB_APP_PASSWORD=... psql -d barako -f scripts/db-app-role.sql
--   psql -d barako -v app_role=barako_app -v app_password='...' -f scripts/db-app-role.sql
--
-- Inputs. A -v flag wins when given, otherwise the environment is read through \getenv
-- (DB_APP_USER, DB_APP_PASSWORD). Prefer the environment: -v puts the password on the command line.
-- app_role defaults to barako_app when blank or unset.
--
-- WHY IT EXISTS
--
-- A Postgres superuser bypasses row level security whatever is on the table, so an API that connects
-- as the database owner is not bound by the policies Marten creates. This file leaves a role that is
-- bound, and that owns what Marten needs to own: Marten issues DDL and a non-owner cannot. Ownership
-- rather than grants also means a pg_dump --no-owner --no-privileges restore needs nothing about the
-- role.
--
-- WHAT IT DOES, in one transaction so a refusal changes nothing
--
--   1. Refuses a role name that is not a plain lower case identifier (it ends up in a connection
--      string), an empty or example password, the role running this file, and any role that is a
--      superuser or bypasses row level security. The owner never loses superuser through this file.
--   2. Takes an advisory lock for the database, so two runs at once take turns.
--   3. Installs pg_stat_statements.
--   4. Creates the role with LOGIN and none of the powers, or re-asserts that on an existing role,
--      and sets its password. The password is set on every run, so rotation is a rerun.
--   5. Grants CONNECT and makes the role owner of schema public and of every table, view,
--      materialized view, sequence and function in it, except objects an extension owns.
--
-- The password never reaches a log or a stats view. Statement logging, the statement printed beside
-- an error and utility tracking are off for this transaction, the password statement runs at top
-- level with the psql variable (an error inside a DO block prints its dynamic SQL, password
-- included), and psql prints no context or query text for an error.

\set ON_ERROR_STOP on
\set VERBOSITY terse
\set SHOW_CONTEXT never

\if :{?app_role} \else \getenv app_role DB_APP_USER \endif
\if :{?app_role} \else \set app_role '' \endif
\if :{?app_password} \else \getenv app_password DB_APP_PASSWORD \endif
\if :{?app_password} \else \set app_password '' \endif

BEGIN;

-- Before any statement that carries the password. The owner is a superuser, so it may.
SET LOCAL log_statement = 'none';
SET LOCAL log_min_error_statement = panic;
SET LOCAL pg_stat_statements.track_utility = off;

SELECT coalesce(nullif(btrim(:'app_role'), ''), 'barako_app') AS app_role \gset

-- psql variables do not expand inside a DO block, so the inputs go in as settings the block reads.
-- Local to the transaction, so nothing is left behind.
-- The result is stored rather than printed, because it is the value.
SELECT set_config('barako.app_role', :'app_role', true) IS NOT NULL AS role_stored \gset
SELECT set_config('barako.app_password', :'app_password', true) IS NOT NULL AS password_stored \gset

DO $guard$
DECLARE
    role_name text := current_setting('barako.app_role');
    role_password text := current_setting('barako.app_password');
BEGIN
    IF role_name !~ '^[a-z_][a-z0-9_]{0,62}$' THEN
        RAISE EXCEPTION 'db-app-role: app_role must match ^[a-z_][a-z0-9_]{0,62}$ because it goes into a connection string. Refused, nothing changed.';
    END IF;

    -- The placeholders the docs and compose files show, and what DatabasePasswordGuard refuses.
    IF btrim(role_password) = '' THEN
        RAISE EXCEPTION 'db-app-role: app_password is empty. Set DB_APP_PASSWORD or pass -v app_password. Refused, nothing changed.';
    END IF;
    IF lower(btrim(role_password)) IN ('change-me', 'change-me-to-a-strong-password', 'changeme-in-production',
                                       'local-only-password', 'password', 'postgres')
       OR upper(btrim(role_password)) LIKE 'REPLACE\_THIS%' THEN
        RAISE EXCEPTION 'db-app-role: app_password is a placeholder or example value, not a password. Refused, nothing changed.';
    END IF;

    IF role_name IN (current_user::text, session_user::text) THEN
        RAISE EXCEPTION 'db-app-role: app_role is the role running this file. The owner must stay a superuser. Refused, nothing changed.';
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name AND (rolsuper OR rolbypassrls)) THEN
        RAISE EXCEPTION 'db-app-role: role % is a superuser or bypasses row level security, so it would not be bound by the tenant policies. Refused, nothing changed.', role_name;
    END IF;
END
$guard$;

-- Serialises runs against this database. A run that holds a lock waits here, then finds the role
-- already made. Advisory locks are per database, so this does not cover a second database in the
-- same cluster naming the same role.
SELECT pg_advisory_xact_lock(hashtextextended('barakoCMS.db-app-role', 0));

CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

SELECT format('CREATE ROLE %I', :'app_role')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_role') \gexec

ALTER ROLE :"app_role" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- Top level, with the psql variable. See the note at the top.
ALTER ROLE :"app_role" WITH PASSWORD :'app_password';

GRANT CONNECT ON DATABASE :"DBNAME" TO :"app_role";

ALTER SCHEMA public OWNER TO :"app_role";

DO $owner$
DECLARE
    role_name text := current_setting('barako.app_role');
    changed integer := 0;
    r record;
BEGIN
    -- Relations first, then routines. An index follows its table and a sequence a column owns
    -- follows that column's table, so neither is listed: Postgres refuses to move an owned
    -- sequence by itself, and ALTER TABLE carries it along. Nothing an extension owns is touched.
    FOR r IN
        SELECT c.oid, c.relkind, c.relname, pg_get_userbyid(c.relowner) AS owner,
               EXISTS (SELECT 1 FROM pg_depend d
                       WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
                         AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')) AS owned_sequence
        FROM pg_class c
        WHERE c.relnamespace = 'public'::regnamespace
          AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
        ORDER BY c.relkind = 'S', c.relname
    LOOP
        IF r.owner = role_name THEN
            CONTINUE;
        END IF;

        IF r.relkind = 'S' AND r.owned_sequence THEN
            -- Moves with its table, which is listed first, and is not counted on its own.
            CONTINUE;
        END IF;

        EXECUTE format('ALTER %s public.%I OWNER TO %I',
                       CASE r.relkind
                           WHEN 'v' THEN 'VIEW'
                           WHEN 'm' THEN 'MATERIALIZED VIEW'
                           WHEN 'S' THEN 'SEQUENCE'
                           WHEN 'f' THEN 'FOREIGN TABLE'
                           ELSE 'TABLE'
                       END,
                       r.relname, role_name);
        changed := changed + 1;
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS signature, p.prokind
        FROM pg_proc p
        WHERE p.pronamespace = 'public'::regnamespace
          AND pg_get_userbyid(p.proowner) <> role_name
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER %s %s OWNER TO %I',
                       CASE r.prokind WHEN 'p' THEN 'PROCEDURE' WHEN 'a' THEN 'AGGREGATE' ELSE 'FUNCTION' END,
                       r.signature, role_name);
        changed := changed + 1;
    END LOOP;

    PERFORM set_config('barako.changed', changed::text, true);
END
$owner$;

SELECT current_setting('barako.changed') AS changed,
       (SELECT rolsuper::text FROM pg_roles WHERE rolname = :'app_role') AS rolsuper \gset

COMMIT;

\echo db-app-role: role :app_role, rolsuper :rolsuper, :changed objects changed owner
