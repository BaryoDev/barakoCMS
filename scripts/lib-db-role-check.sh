# shellcheck shell=bash
# Sourced by compose-runtime-check.sh, compose-secrets-runtime-check.sh, quickstart-runtime-check.sh
# and compose-upgrade-check.sh. Not run on its own.
#
# What they share is the proof that a compose deployment runs as the app role: every API session
# uses it, it is not a superuser and does not bypass row level security, it owns everything in
# schema public, a second run of the owner job changes nothing, and a table made by hand as the
# owner belongs to it after one more run of the job.
#
# The caller provides, before it calls anything here:
#
#   DC             an array: the docker compose command with its project, env file and -f files
#   dc <args>      runs "${DC[@]}" "$@"
#   PROJECT        the compose project name (the network is ${PROJECT}_default)
#   CHECK_DB       the database name (barako_cms for production, barakocms for the quickstart)
#   CHECK_API      the API's service name (app for production, api for the quickstart)
#   APP_PASSWORD   the app role's password, never printed
#   step <text>    prints a heading
#   fail <text>    reports and exits; it must not print a secret
#
# Optional: APP_ROLE (default barako_app), DB_OWNER (default postgres) and API_HOST_HEADER (the Host
# header api_call sends, when the API only answers to one).
#
# Nothing here prints a password, a connection string or a log line. Queries go in on stdin because
# psql does not substitute :'name' inside a -c string.

APP_ROLE="${APP_ROLE:-barako_app}"
DB_OWNER="${DB_OWNER:-postgres}"
HAND_MADE_TABLE="hand_made_by_owner"

# Throwaway curl container for requests to an API that publishes no port, pinned by digest like the
# images in the compose files.
CURL_IMAGE="curlimages/curl:8.11.1@sha256:c1fe1679c34d9784c1b0d1e5f62ac0a79fca01fb6377cdd33e90473c6f9f9a69"

# The values a log must never hold. A caller fills SECRET_VALUES; redact reads text on stdin and
# prints it with each value replaced, for the log tail its fail prints to a public CI log.
SECRET_VALUES=()
redact() {
  local text v
  text="$(cat)"
  for v in "${SECRET_VALUES[@]+"${SECRET_VALUES[@]}"}"; do
    [ -z "$v" ] || text="${text//"$v"/<redacted>}"
  done
  printf '%s\n' "$text"
}

# Runs psql as the database owner inside the postgres container. The SQL comes in on stdin.
owner_psql() {
  dc exec -T postgres psql -X -At -U "$DB_OWNER" -d "$CHECK_DB" -v ON_ERROR_STOP=1 -v app_role="$APP_ROLE" "$@"
}

# owner_sql <sql>: prints what the query returns.
owner_sql() { printf '%s\n' "$1" | owner_psql "${@:2}"; }

# Objects in public the app role does not own. An extension's own members are not counted: the
# owner script leaves them alone, with the same pg_depend deptype 'e' test. Covers relations of kind
# r, p, S, v, m and f, routines, and the schema itself. Any row is a failure.
read -r -d '' MISOWNED_SQL <<'SQL' || true
select 'relation ' || c.relkind::text || ' ' || c.relname || ' owned by ' || pg_get_userbyid(c.relowner)
from pg_class c
where c.relnamespace = 'public'::regnamespace
  and c.relkind in ('r', 'p', 'S', 'v', 'm', 'f')
  and pg_get_userbyid(c.relowner) <> :'app_role'
  and not exists (select 1 from pg_depend d
                  where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype = 'e')
union all
select 'routine ' || p.oid::regprocedure::text || ' owned by ' || pg_get_userbyid(p.proowner)
from pg_proc p
where p.pronamespace = 'public'::regnamespace
  and pg_get_userbyid(p.proowner) <> :'app_role'
  and not exists (select 1 from pg_depend d
                  where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e')
union all
select 'schema public owned by ' || pg_get_userbyid(n.nspowner)
from pg_namespace n
where n.nspname = 'public' and pg_get_userbyid(n.nspowner) <> :'app_role'
order by 1;
SQL

# The role's attributes and the owners of every relation and routine in public, and of the schema,
# one line each. pg_roles, never pg_authid: the password hash gets a new salt on every run. Relations
# are keyed by name, routines by oid::regprocedure.
read -r -d '' SNAPSHOT_SQL <<'SQL' || true
select 'role ' || r::text from pg_roles r where r.rolname = :'app_role'
union all
select 'schema public ' || pg_get_userbyid(nspowner) from pg_namespace where nspname = 'public'
union all
select 'relation ' || c.relkind::text || ' ' || c.relname || ' ' || pg_get_userbyid(c.relowner)
from pg_class c where c.relnamespace = 'public'::regnamespace
union all
select 'routine ' || p.oid::regprocedure::text || ' ' || pg_get_userbyid(p.proowner)
from pg_proc p where p.pronamespace = 'public'::regnamespace;
SQL

# The API's sessions, found by the container's address on the project network so the other
# services' sessions are not counted. Polled up to 30 seconds for at least one; a role other than the
# app role fails at once.
assert_api_sessions_use_app_role() {
  local api_ips roles deadline
  api_ips="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$(dc ps -q "$CHECK_API")")" \
    || fail "could not inspect the $CHECK_API container"
  [ -n "${api_ips// /}" ] || fail "could not find the API's address"
  deadline=$((SECONDS + 30))
  while :; do
    roles="$(printf '%s\n' "select distinct usename from pg_stat_activity where client_addr = any(string_to_array(btrim(:'ips'), ' ')::inet[]) order by 1;" \
      | dc exec -T postgres psql -X -At -U "$DB_OWNER" -d "$CHECK_DB" -v ON_ERROR_STOP=1 -v ips="$api_ips")" \
      || fail "could not read pg_stat_activity"
    if [ -n "$roles" ]; then
      [ "$roles" = "$APP_ROLE" ] || fail "the API's sessions use '$(printf '%s' "$roles" | tr '\n' ' ')', not only $APP_ROLE"
      return 0
    fi
    [ "$SECONDS" -lt "$deadline" ] || fail "no API session showed up in pg_stat_activity within 30 seconds"
    sleep 2
  done
}

# The role is not a superuser and does not bypass row level security.
assert_role_has_no_powers() {
  local facts
  facts="$(owner_sql "select rolsuper::text || ' ' || rolbypassrls::text from pg_roles where rolname = :'app_role';")" \
    || fail "could not read pg_roles"
  [ "$facts" = "false false" ] || fail "role $APP_ROLE has rolsuper and rolbypassrls '${facts:-<role missing>}', expected 'false false'"
}

# Everything in public belongs to the app role. A count first, so a query that sees nothing cannot pass.
assert_public_owned_by_app_role() {
  local count misowned
  count="$(owner_sql "select count(*) from pg_class where relnamespace = 'public'::regnamespace and relkind = 'r';")" \
    || fail "could not count the tables in public"
  [ "${count:-0}" -gt 0 ] || fail "public holds no tables, so the ownership check would prove nothing"
  misowned="$(owner_sql "$MISOWNED_SQL")" || fail "could not read the owners in public"
  [ -z "$misowned" ] || fail "objects in public the app role does not own: $(printf '%s' "$misowned" | tr '\n' ';')"
}

# pg_stat_statements is there, and nothing the API ran asked the app role for an extension.
assert_extensions_and_no_extension_errors() {
  local names logs
  names="$(owner_sql "select extname from pg_extension;")" || fail "could not read pg_extension"
  printf '%s\n' "$names" | grep -qx 'pg_stat_statements' || fail "pg_stat_statements is not installed"
  logs="$(mktemp)"
  dc logs --no-color "$CHECK_API" > "$logs" 2>&1 || true
  if grep -qE 'permission denied for database|permission denied to create extension|must be owner of extension' "$logs"; then
    # Name the extension only. Postgres 13 and later say: permission denied to create extension "NAME".
    ext="$(grep -oE 'permission denied to create extension "[^"]{1,63}"|must be owner of extension [A-Za-z0-9_-]{1,63}' "$logs" \
      | head -1 | sed -e 's/.*extension //' -e 's/"//g')"
    rm -f "$logs"
    fail "the $CHECK_API log shows the app role asked for an extension it may not create (${ext:-name not in the log}); add it to the owner script"
  fi
  rm -f "$logs"
}

# run_owner_job: runs the owner job once more, on the project's own network and env. Sets JOB_LINE to
# the line it prints. The job's other output is not shown, because an error from it can name a
# setting. A hung job is stopped at 180 seconds where timeout exists.
run_owner_job() {
  local out rc=0 limit=""
  ! command -v timeout > /dev/null || limit="timeout 180"
  out="$($limit "${DC[@]}" run --rm -T postgres-extensions 2>&1)" || rc=$?
  [ "$rc" -eq 0 ] || fail "the owner job exited $rc"
  JOB_LINE="$(printf '%s\n' "$out" | grep '^db-app-role:' | tail -1)"
  [ -n "$JOB_LINE" ] || fail "the owner job printed no result line"
  printf '%s\n' "$JOB_LINE"
}

# assert_job_changed <n>: the last run says exactly n objects changed owner.
assert_job_changed() {
  case "$JOB_LINE" in
    *", $1 objects changed owner") ;;
    *) fail "the owner job said '$JOB_LINE', expected '$1 objects changed owner'" ;;
  esac
}

# A second run of the owner job on a settled stack changes nothing: no owner, and none of the role's
# attributes. A new line in the second snapshot is allowed (the API may have made a table meanwhile,
# and it owns what it makes); a line that is gone or different is not.
assert_second_run_changes_nothing() {
  local before after
  before="$(mktemp)"; after="$(mktemp)"
  owner_sql "$SNAPSHOT_SQL" | LC_ALL=C sort > "$before" || fail "could not take the owner snapshot"
  [ -s "$before" ] || fail "the owner snapshot is empty"
  run_owner_job
  assert_job_changed 0
  owner_sql "$SNAPSHOT_SQL" | LC_ALL=C sort > "$after" || fail "could not take the owner snapshot after the rerun"
  local changed
  changed="$(LC_ALL=C comm -23 "$before" "$after")"
  rm -f "$before" "$after"
  [ -z "$changed" ] || fail "a second run of the owner job changed these: $(printf '%s' "$changed" | tr '\n' ';')"
}

# app_psql <password>: runs psql as the app role from a second container on the project network,
# SQL on stdin. The password has to be right: the image's pg_hba trusts the local socket, so a psql
# inside the postgres container proves nothing about it. The image is the one the owner job runs.
app_psql() {
  local image
  image="$(docker inspect -f '{{.Config.Image}}' "$(dc ps -aq postgres-extensions | head -1)")" \
    || fail "could not find the owner job's image"
  PGPASSWORD="$1" docker run --rm -i --network "${PROJECT}_default" -e PGPASSWORD -e PGHOST=postgres \
    -e PGCONNECT_TIMEOUT=10 "$image" psql -X -q -At -U "$APP_ROLE" -d "$CHECK_DB" -v ON_ERROR_STOP=1
}

# A table the owner makes after start belongs to the app role once the job has run again, and the
# app role then reads and writes it with its password.
assert_hand_made_table_reaches_app_role() {
  local note out wrong
  owner_sql "create table public.$HAND_MADE_TABLE (id int primary key, note text);" > /dev/null \
    || fail "could not create a table as the owner"
  run_owner_job
  assert_job_changed 1
  assert_public_owned_by_app_role
  note="written-by-the-app-role"
  out="$(printf '%s\n' "insert into public.$HAND_MADE_TABLE values (1, '$note'); select note from public.$HAND_MADE_TABLE where id = 1;" \
    | app_psql "$APP_PASSWORD" 2>&1)" || fail "the app role could not write and read the table the owner made"
  [ "$out" = "$note" ] || fail "the app role read '$out' from the table, expected what it wrote"
  wrong="not-$(openssl rand -hex 8)"
  local refusal rc=0
  refusal="$(printf '%s\n' "select 1;" | app_psql "$wrong" 2>&1 > /dev/null)" || rc=$?
  [ "$rc" -ne 0 ] || fail "the app role signed in over the network with a wrong password, so the check above proves nothing"
  # Only the expected refusal counts: a docker or network failure would also exit non-zero.
  if printf '%s' "$refusal" | grep -q 'password authentication failed'; then
    printf 'a wrong password was refused with the expected authentication failure\n'
  else
    fail "the wrong password failed, but not with 'password authentication failed' (docker or network error?)"
  fi
}

# Everything above, in order, once the API is healthy and idle.
check_app_role_runtime() {
  step "the API's sessions use the app role"
  assert_api_sessions_use_app_role
  step "the app role is not a superuser and does not bypass row level security"
  assert_role_has_no_powers
  step "everything in public belongs to the app role"
  assert_public_owned_by_app_role
  step "pg_stat_statements is installed and the API asked for no extension"
  assert_extensions_and_no_extension_errors
  step "a second run of the owner job changes nothing"
  assert_second_run_changes_nothing
  step "a table the owner made by hand moves to the app role on the next run"
  assert_hand_made_table_reaches_app_role
}

# api_call <method> <path> [token]: one request to the API from a curl container on the project
# network. A body, if there is one, comes in on stdin. Prints the response body, then a last line
# holding only the status code.
api_call() {
  local method="$1" path="$2" token="${3:-}"
  local args=(-s --max-time 30 -w '\n%{http_code}' -X "$method" -H 'Content-Type: application/json')
  [ -z "$token" ] || args+=(-H "Authorization: Bearer $token")
  [ -z "${API_HOST_HEADER:-}" ] || args+=(-H "Host: $API_HOST_HEADER")
  if [ "$method" = GET ]; then
    docker run --rm --network "${PROJECT}_default" "$CURL_IMAGE" "${args[@]}" "http://${CHECK_API}:8080${path}"
  else
    docker run --rm -i --network "${PROJECT}_default" "$CURL_IMAGE" "${args[@]}" --data-binary @- "http://${CHECK_API}:8080${path}"
  fi
}

# api_status <response from api_call>, api_body <response>.
api_status() { printf '%s' "${1##*$'\n'}"; }
api_body() { printf '%s' "${1%$'\n'*}"; }

# json_field <name>: reads JSON on stdin, prints the field, or nothing.
json_field() {
  python3 -I -c 'import sys, json
try:
    d = json.load(sys.stdin)
    v = d.get(sys.argv[1]) if isinstance(d, dict) else None
    print("" if v is None else v)
except Exception:
    print("")' "$1"
}
