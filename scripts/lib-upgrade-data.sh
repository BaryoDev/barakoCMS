# The data checks upgrade-check.sh makes around migrations/4.6.0/sensitivity-by-capability.sql and
# its rollback (#883). Sourced, not run.
#
# They are here and not inline so scripts/test-check-ports.sh can call each one with a query
# function of its own. The rollback check sits after the whole upgrade in upgrade-check.sh, where
# no stubbed run reaches, and a check nothing can make fail is not known to work.
#
# The caller provides:
#   psql_q <sql>     prints the one value the query returns
#   fail <message>   reports and exits
#   FROM_VERSION     the release the database was created by, for the messages
#
# The seeded HR role is the one stored under the seeded id and still named exactly HR. That is the
# role the forward file and the seeder both grant view_sensitive to, and the only one.

SEEDED_HR="id = '00000000-0000-0000-0000-000000000003' and data ->> 'Name' = 'HR'"

seeded_hr_count() {
    psql_q "select count(*) from mt_doc_roles where $SEEDED_HR;"
}

seeded_hr_holding_view_sensitive_count() {
    psql_q "select count(*) from mt_doc_roles where $SEEDED_HR and data -> 'SystemCapabilities' ? 'view_sensitive';"
}

# Before the forward file. Without the role the two checks after it would describe nothing.
require_seeded_hr() {
    [ "$(seeded_hr_count)" = "1" ] \
        || fail "the ${FROM_VERSION} database has no role named HR under the seeded id, so migrations/4.6.0/sensitivity-by-capability.sql proves nothing on this start"
}

# After the forward file, and before the new build boots: its seeder grants the same capability and
# would hide a file that does nothing.
require_seeded_hr_granted() {
    [ "$(seeded_hr_holding_view_sensitive_count)" = "1" ] \
        || fail "migrations/4.6.0/sensitivity-by-capability.sql did not give the HR role view_sensitive, so its holders would stop reading Sensitive fields on upgrade"
}

# After the rollback file.
require_seeded_hr_not_granted() {
    [ "$(seeded_hr_holding_view_sensitive_count)" = "0" ] \
        || fail "migrations/4.6.0/rollback-sensitivity-by-capability.sql left view_sensitive on the seeded HR role, which the forward file put there"
}

# ---------------------------------------------------------------------------------------------
# The data a 5.x start is given, and the read back of every record of it (#1437).
#
# A 4.x start gets the one content entry upgrade-check.sh creates itself. A 5.x start gets that
# entry plus one of each kind of record the upgrade has to carry, made through the FROM_VERSION API
# and so in the shape that release stores it:
#
#   content, event sourced   a type declared event sourced, one entry with three events on its
#                            stream (created, status changed, edited), which is the stream the
#                            document is rebuilt from
#   content, document type   a type that is not, one entry, with a field a workflow set after the
#                            create, so the workflow definition and its run are both exercised
#   content types, role, user group, redirect, connector, workflow, API key, station
#   a second tenant          the tenant row, its CDN purge settings (a 5.9.0 table) and one entry,
#                            all tenant scoped documents
#
# The same read runs before the upgrade, against FROM_VERSION, which proves the seed landed, and
# after it, against the working tree. A migration that drops, truncates or renames something
# fails the second read and not the first.
#
# What cannot be seeded through the 5.9 API: the rows the host writes for itself (refresh tokens,
# revoked tokens, the number counters, outbox rows, pending builds and CDN purges, idempotency
# records) have no route that creates them on demand, and the module tables need the module
# registered, which the Suite image does with its own seed data. The admin sign-in and the event
# progression checks in upgrade-check.sh cover the first group.
#
# The caller provides, besides what is listed above:
#   SEED_FILE        a path in the caller's scratch directory, where ids are kept as kind<TAB>value
#   PG               the postgres container, for psql_q
# Nothing here is a real credential: the passwords, tokens and keys are throwaway test values, and
# the API key secret the server returns goes into SEED_FILE, which is deleted with the scratch
# directory.
# ---------------------------------------------------------------------------------------------

SEED_TENANT="upgrade-seed"

# $1 method, $2 url, $3 bearer token, $4 optional JSON body, $5 optional tenant for X-Tenant.
# Prints the response body. Fails, saying what answered, unless the status is 2xx.
seed_api() {
    local extra=() out code
    if [ -n "${4:-}" ]; then extra+=(-H 'Content-Type: application/json' -d "$4"); fi
    if [ -n "${5:-}" ]; then extra+=(-H "X-Tenant: $5"); fi
    out=$(curl -sS -w '\n%{http_code}' -X "$1" "$2" -H "Authorization: Bearer $3" ${extra[@]+"${extra[@]}"}) || return 1
    code=${out##*$'\n'}
    out=${out%$'\n'*}
    case "$code" in
        2??) printf '%s' "$out" ;;
        *) printf 'HTTP %s from %s %s: %s\n' "$code" "$1" "$2" "$(printf '%s' "$out" | head -c 300)" >&2; return 1 ;;
    esac
}

# $1 a Python expression over the parsed JSON on stdin, called d. Prints its value.
seed_json() { python3 -c "import sys, json; d = json.load(sys.stdin); print($1)"; }

# A list answer is either {"items": [...]} or a bare array.
seed_listed() { # $1 id or name, stdin the list; prints 1 when an item has it as id, name or slug
    python3 -c "
import sys, json
d = json.load(sys.stdin)
items = d['items'] if isinstance(d, dict) else d
want = sys.argv[1]
print(1 if any(want in (i.get('id'), i.get('name'), i.get('slug')) for i in items) else 0)" "$1"
}

seed_keep() { printf '%s\t%s\n' "$1" "$2" >> "$SEED_FILE"; }
seed_get() { awk -F'\t' -v k="$1" '$1 == k { print $2 }' "$SEED_FILE"; }

# $1 what was being read, $2 what it was, $3 what it should be. Reports which side of the upgrade.
seed_expect() {
    [ "$2" = "$3" ] || fail "${SEED_WHEN}: $1 is '$2', expected '$3'"
}

# $1 base url, $2 a token of the default tenant. Creates the records and keeps their ids. The token
# is not spent: tenant work is separate (seed_tenant_data) because switching tenant retires it.
seed_data() {
    local url=$1 tok=$2 id marker=""
    : > "$SEED_FILE"
    SEED_WHEN="seeding ${FROM_VERSION}"

    seed_api POST "$url/api/content-types" "$tok" '{"name":"EsNote","displayName":"Es Note","description":"upgrade seed","eventSourced":true,"fields":[{"name":"Title","type":"Text","required":true}]}' >/dev/null \
        || fail "could not create the event sourced content type on ${FROM_VERSION}"
    seed_api POST "$url/api/content-types" "$tok" '{"name":"DocNote","displayName":"Doc Note","description":"upgrade seed","fields":[{"name":"Title","type":"Text"}]}' >/dev/null \
        || fail "could not create the document content type on ${FROM_VERSION}"

    # The workflow goes in before the document entry, so the entry's create is what it runs on.
    id=$(seed_api POST "$url/api/workflows" "$tok" '{"name":"upgrade-seed-workflow","triggerContentType":"docnote","triggerEvent":"Created","actions":[{"type":"UpdateField","parameters":{"Field":"Marker","Value":"set-by-workflow"}}]}' | seed_json "d['id']") \
        || fail "could not create the workflow on ${FROM_VERSION}"
    seed_keep workflow "$id"

    id=$(seed_api POST "$url/api/contents" "$tok" '{"contentType":"esnote","data":{"Title":"es one"}}' | seed_json "d['id']") \
        || fail "could not create the event sourced entry on ${FROM_VERSION}"
    seed_keep es_entry "$id"
    seed_api PUT "$url/api/contents/$id/status" "$tok" '{"newStatus":1}' >/dev/null \
        || fail "could not publish the event sourced entry on ${FROM_VERSION}"
    # An edit on an event sourced entry names the version it read, and the stream is at 2 now.
    seed_api PUT "$url/api/contents/$id" "$tok" '{"data":{"Title":"es one edited"},"version":2}' >/dev/null \
        || fail "could not edit the event sourced entry on ${FROM_VERSION}"

    id=$(seed_api POST "$url/api/contents" "$tok" '{"contentType":"docnote","data":{"Title":"doc one"}}' | seed_json "d['id']") \
        || fail "could not create the document entry on ${FROM_VERSION}"
    seed_keep doc_entry "$id"
    # The workflow runs on the projection daemon, so wait for what it writes.
    for _ in $(seq 1 30); do
        marker=$(seed_api GET "$url/api/contents/$id" "$tok" | seed_json "d['data'].get('Marker', '')") || marker=""
        [ "$marker" = "set-by-workflow" ] && break
        sleep 2
    done
    [ "$marker" = "set-by-workflow" ] \
        || fail "the workflow seeded on ${FROM_VERSION} never set Marker on the document entry, so there is no workflow run to carry across"

    id=$(seed_api POST "$url/api/roles" "$tok" '{"name":"UpgradeSeedRole","description":"upgrade seed","permissions":[],"systemCapabilities":["view_audit_log"]}' | seed_json "d['id']") \
        || fail "could not create the role on ${FROM_VERSION}"
    seed_keep role "$id"
    id=$(seed_api POST "$url/api/user-groups" "$tok" '{"name":"upgrade-seed-group","description":"upgrade seed"}' | seed_json "d['id']") \
        || fail "could not create the user group on ${FROM_VERSION}"
    seed_keep group "$id"
    id=$(seed_api POST "$url/api/redirects" "$tok" '{"fromPath":"/upgrade-old","toPath":"/upgrade-new","permanent":true}' | seed_json "d['id']") \
        || fail "could not create the redirect on ${FROM_VERSION}"
    seed_keep redirect "$id"
    id=$(seed_api POST "$url/api/connectors" "$tok" '{"slug":"upgrade-seed","name":"Upgrade seed","baseUrl":"https://example.com"}' | seed_json "d['id']") \
        || fail "could not create the connector on ${FROM_VERSION}"
    seed_keep connector "$id"
    id=$(seed_api POST "$url/api/stations" "$tok" '{"name":"upgrade-seed-station","capabilities":["view_audit_log"]}' | seed_json "d['id']") \
        || fail "could not create the station on ${FROM_VERSION}"
    seed_keep station "$id"

    local key
    key=$(seed_api POST "$url/api/api-keys" "$tok" '{"name":"upgrade-seed-key","scopes":["content:read"]}') \
        || fail "could not create the API key on ${FROM_VERSION}"
    seed_keep apikey "$(printf '%s' "$key" | seed_json "d['id']")"
    seed_keep apikey_secret "$(printf '%s' "$key" | seed_json "d['key']")"
}

# $1 base url, $2 a token of the default tenant, $3 when, for the messages ("before the upgrade").
# Reads every record seed_data made. Run it before the upgrade and after, with the same expectations.
verify_seed_data() {
    local url=$1 tok=$2 id got
    SEED_WHEN=$3

    id=$(seed_get es_entry)
    got=$(seed_api GET "$url/api/contents/$id" "$tok") || fail "${SEED_WHEN}: the event sourced entry $id cannot be read"
    seed_expect "the event sourced entry's Title" "$(printf '%s' "$got" | seed_json "d['data'].get('Title', '')")" "es one edited"
    seed_expect "the event sourced entry's status" "$(printf '%s' "$got" | seed_json "d['status']")" "Published"
    seed_expect "the event sourced entry's version" "$(printf '%s' "$got" | seed_json "d['version']")" "3"
    seed_expect "the events on the event sourced entry's stream" "$(psql_q "select count(*) from mt_events where stream_id = '$id';")" "3"
    got=$(seed_api GET "$url/api/contents/$id/history" "$tok") || fail "${SEED_WHEN}: the history of the event sourced entry cannot be read"
    seed_expect "the history entries of the event sourced entry" "$(printf '%s' "$got" | seed_json "len(d['items'])")" "3"

    id=$(seed_get doc_entry)
    got=$(seed_api GET "$url/api/contents/$id" "$tok") || fail "${SEED_WHEN}: the document entry $id cannot be read"
    seed_expect "the document entry's Title" "$(printf '%s' "$got" | seed_json "d['data'].get('Title', '')")" "doc one"
    seed_expect "the document entry's Marker, set by the workflow" "$(printf '%s' "$got" | seed_json "d['data'].get('Marker', '')")" "set-by-workflow"

    got=$(seed_api GET "$url/api/content-types" "$tok") || fail "${SEED_WHEN}: the content types cannot be listed"
    seed_expect "content type esnote listed" "$(printf '%s' "$got" | seed_listed esnote)" "1"
    seed_expect "content type docnote listed" "$(printf '%s' "$got" | seed_listed docnote)" "1"
    seed_expect "content type esnote still event sourced" \
        "$(printf '%s' "$got" | seed_json "[i['eventSourced'] for i in d['items'] if i['name'] == 'esnote'][0]")" "True"

    id=$(seed_get role)
    got=$(seed_api GET "$url/api/roles/$id" "$tok") || fail "${SEED_WHEN}: the role $id cannot be read"
    seed_expect "the role's name" "$(printf '%s' "$got" | seed_json "d['name']")" "UpgradeSeedRole"
    seed_expect "the role's capabilities" "$(printf '%s' "$got" | seed_json "','.join(d['systemCapabilities'])")" "view_audit_log"

    id=$(seed_get group)
    got=$(seed_api GET "$url/api/user-groups/$id" "$tok") || fail "${SEED_WHEN}: the user group $id cannot be read"
    seed_expect "the user group's name" "$(printf '%s' "$got" | seed_json "d['name']")" "upgrade-seed-group"

    seed_expect "the redirect listed" "$(seed_api GET "$url/api/redirects" "$tok" | seed_listed "$(seed_get redirect)")" "1"
    seed_expect "the connector listed" "$(seed_api GET "$url/api/connectors" "$tok" | seed_listed upgrade-seed)" "1"
    seed_expect "the workflow listed" "$(seed_api GET "$url/api/workflows" "$tok" | seed_listed "$(seed_get workflow)")" "1"
    seed_expect "the station listed" "$(seed_api GET "$url/api/stations" "$tok" | seed_listed "$(seed_get station)")" "1"
    seed_expect "the API key listed" "$(seed_api GET "$url/api/api-keys" "$tok" | seed_listed "$(seed_get apikey)")" "1"

    # The key's secret was stored as a hash. It still has to match the one the old build made.
    local code
    code=$(curl -s -o /dev/null -w '%{http_code}' "$url/api/contents/$(seed_get doc_entry)" -H "Authorization: Bearer $(seed_get apikey_secret)")
    seed_expect "the status of a read with the seeded API key" "$code" "200"
}

# $1 base url, $2 a token of the default tenant, which this retires, $3 when, $4 "seed" to create
# the records first. The second tenant, its CDN purge settings and an entry of its own.
# Switching tenant revokes the token it was called with, so it runs last in its phase.
seed_tenant_data() {
    local url=$1 tok=$2 mode=${4:-} got ttok
    SEED_WHEN=$3
    if [ "$mode" = "seed" ]; then
        seed_api POST "$url/api/tenants" "$tok" "{\"handle\":\"$SEED_TENANT\",\"name\":\"Upgrade seed\"}" >/dev/null \
            || fail "could not create the second tenant on ${FROM_VERSION}"
    fi
    ttok=$(seed_api POST "$url/api/me/switch" "$tok" "{\"tenant\":\"$SEED_TENANT\"}" | seed_json "d['token']") \
        || fail "${SEED_WHEN}: cannot switch to the second tenant"
    if [ "$mode" = "seed" ]; then
        seed_api PUT "$url/api/tenant-settings/cdn-purge" "$ttok" '{"provider":"cloudflare","targetId":"upgradeseedzone","token":"not-a-real-token","enabled":false}' "$SEED_TENANT" >/dev/null \
            || fail "could not store CDN purge settings for the second tenant on ${FROM_VERSION}"
        got=$(seed_api POST "$url/api/contents" "$ttok" '{"contentType":"tenantnote","data":{"Title":"tenant one"}}' "$SEED_TENANT") \
            || fail "could not create an entry in the second tenant on ${FROM_VERSION}"
        seed_keep tenant_entry "$(printf '%s' "$got" | seed_json "d['id']")"
    fi
    got=$(seed_api GET "$url/api/tenant-settings/cdn-purge" "$ttok" "" "$SEED_TENANT") \
        || fail "${SEED_WHEN}: the second tenant's CDN purge settings cannot be read"
    seed_expect "the second tenant's CDN purge target" "$(printf '%s' "$got" | seed_json "d['targetId']")" "upgradeseedzone"
    seed_expect "the second tenant's CDN purge token is still stored" "$(printf '%s' "$got" | seed_json "d['tokenSet']")" "True"
    got=$(seed_api GET "$url/api/contents/$(seed_get tenant_entry)" "$ttok" "" "$SEED_TENANT") \
        || fail "${SEED_WHEN}: the second tenant's entry cannot be read"
    seed_expect "the second tenant's entry Title" "$(printf '%s' "$got" | seed_json "d['data'].get('Title', '')")" "tenant one"
}

# After the upgrade: a workflow the old build stored still runs on the new one.
verify_seeded_workflow_runs() { # $1 base url, $2 a token of the default tenant
    local url=$1 tok=$2 id marker=""
    SEED_WHEN="after the upgrade"
    id=$(seed_api POST "$url/api/contents" "$tok" '{"contentType":"docnote","data":{"Title":"doc two"}}' | seed_json "d['id']") \
        || fail "after the upgrade: cannot create a document entry"
    for _ in $(seq 1 30); do
        marker=$(seed_api GET "$url/api/contents/$id" "$tok" | seed_json "d['data'].get('Marker', '')") || marker=""
        [ "$marker" = "set-by-workflow" ] && break
        sleep 2
    done
    [ "$marker" = "set-by-workflow" ] \
        || fail "after the upgrade: the workflow stored by ${FROM_VERSION} did not run on a new entry, so its definition did not survive"
}
