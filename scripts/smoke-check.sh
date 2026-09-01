#!/usr/bin/env bash
#
# Runs the real admin against the real API, with no mocking anywhere.
#
# Why this exists, in one sentence: every other admin test mocks the API with page.route, so it
# proves the admin behaves correctly given fixtures the same person wrote, and cannot prove those
# fixtures match the server.
#
# That gap shipped a bug and kept it. The History panel read `versions` from a response that had
# returned `items` since the envelope change. It rendered an empty list rather than failing, which
# reads as "this entry has no history", and every mocked spec stayed green because the mock returned
# `versions` too. Nothing in CI could have caught it.
#
# The sequence:
#
#   1. stand up Postgres
#   2. build and start the API from the working tree, seeder on, so there is an administrator
#   3. seed one content type and one entry, through the API, so there is something to list
#   4. build and start the admin pointed at that API
#   5. run admin/smoke, which contains no page.route and must not
#
# Usage: scripts/smoke-check.sh

set -euo pipefail

PG="smoke-check-pg"
PG_PORT="${PG_PORT:-55434}"
API_PORT="${API_PORT:-5099}"
ADMIN_PORT="${ADMIN_PORT:-3200}"
ADMIN_USERNAME='admin'
ADMIN_PASSWORD='SmokeCheck!123'
JWT_KEY='smoke-check-key-that-is-at-least-32-chars-long'
WORK="$(mktemp -d)"
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=barako_cms;Username=postgres;Password=postgres"
API="http://127.0.0.1:${API_PORT}"

cleanup() {
    if [ -n "${ADMIN_PID:-}" ]; then kill "$ADMIN_PID" 2>/dev/null || true; wait "$ADMIN_PID" 2>/dev/null || true; fi
    if [ -n "${API_PID:-}" ]; then kill "$API_PID" 2>/dev/null || true; wait "$API_PID" 2>/dev/null || true; fi
    docker rm -f "$PG" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

# A port already in use makes every check below pass for the wrong reason: the browser reaches
# whatever is listening and the assertions describe someone else's process. Same guard, and the same
# reasoning, as scripts/upgrade-check.sh.
#
# `if lsof ...` on a missing binary is false, which reads as "port free" and fails open, so pick a
# tool that exists and say so when neither does.
if command -v lsof >/dev/null 2>&1; then
    port_in_use() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
elif command -v ss >/dev/null 2>&1; then
    port_in_use() { ss -ltn "sport = :$1" 2>/dev/null | grep -q LISTEN; }
else
    echo "note: neither lsof nor ss is available, so the port check below is skipped" >&2
    port_in_use() { return 1; }
fi

for port in "$PG_PORT" "$API_PORT" "$ADMIN_PORT"; do
    if port_in_use "$port"; then
        fail "port $port is already in use. Something else would answer below and this run would pass without testing anything."
    fi
done

step "starting postgres"
docker run -d --name "$PG" \
    -e POSTGRES_DB=barako_cms -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
# -h 127.0.0.1 forces TCP. The postgres image runs a temporary initdb server on the Unix socket
# first, so a socket check succeeds during bootstrap and the wait breaks early against a server that
# is about to shut down. Same trap as upgrade-check.sh.
for _ in $(seq 1 60); do docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 2; done
docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || fail "postgres never became ready"

step "building the API from the working tree"
dotnet publish barakoCMS/barakoCMS.csproj -c Release -o "$WORK/api" --nologo -v q -clp:ErrorsOnly

step "starting the API"
# exec, so $! is the dotnet process rather than the subshell. Killing the subshell leaves the host
# running and the cleanup silently does nothing, which is how a later run meets a port that is
# already in use.
#
# env -i for the same reason upgrade-check.sh uses it: a stray DATABASE_URL in the shell would point
# the API at a different database and everything below would pass against the wrong data.
#
# CORS__AllowedOrigins is a comma separated string, not an array: the key is CORS:AllowedOrigins.
# Named CORS__AllowedOrigins__0 the first time, and the browser answered every request with a CORS
# failure rather than anything about the contract. Worth recording, because it is the pack finding a
# configuration mistake, which is a category the mocked pack cannot have.
#
# No comments inside the argument list below. A # line breaks a backslash continuation, and env then
# has no command to run, so it prints its environment and exits 0 while the API never starts.
#
# Development, not Production, and this is a real trade worth stating. The refresh cookie is marked
# Secure everywhere except a Development host, deliberately and for good reasons written down in
# RefreshTokenCookie.cs, so over plain http a Production host sets a cookie the browser refuses to
# store and the admin bounces back to the login page. That is correct behaviour, not a bug, and it
# is what a browser would hit here.
#
# The alternative is terminating TLS with a self-signed certificate, which means teaching both the
# browser and the admin's server-side fetch to accept it. That is a lot of moving parts guarding a
# property this pack is not asking about: the question here is whether the two halves agree on
# shapes, and shapes do not change with the environment. The cookie's Secure flag does, and
# RefreshTokenCookie.IsSecure is unit tested precisely so it does not need a host to verify.
#
# What this pack therefore does NOT cover: the cookie attributes on a Production host. Said out loud
# so nobody reads a green run here as covering it.
(
    exec env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Development \
        ASPNETCORE_URLS="$API" \
        ConnectionStrings__DefaultConnection="$CONN" \
        JWT__Key="$JWT_KEY" \
        InitialAdmin__Username="$ADMIN_USERNAME" \
        InitialAdmin__Password="$ADMIN_PASSWORD" \
        CORS__AllowedOrigins="http://127.0.0.1:${ADMIN_PORT}" \
        Kubernetes__Enabled=false \
        dotnet "$WORK/api/barakoCMS.dll"
) &
API_PID=$!

for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$API/health" 2>/dev/null)" = "200" ] && break
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$API/health" 2>/dev/null)" = "200" ] || fail "the API never became healthy"
# Health answered, but by what? If the process we started is gone, something else is on that port.
kill -0 "$API_PID" 2>/dev/null || fail "the API process exited; whatever answered /health is not it"

step "seeding one content type and one entry"
TOKEN=$(curl -s -X POST "$API/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"${ADMIN_USERNAME}\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("token",""))')
[ -n "$TOKEN" ] || fail "could not log in as the seeded administrator, so the smoke run would test nothing"

# Handed to the pack so a test that needs to call the API directly does not have to spend one of the
# five auth requests the limiter allows per fifteen minutes. The admin keeps its access token in
# memory rather than localStorage, deliberately, so there is nothing for a test to read out of the
# browser.

curl -s -o /dev/null -X POST "$API/api/content-types" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d '{"name":"smokepost","displayName":"Smoke Post","fields":[{"name":"Title","type":"string"}]}'

curl -s -o /dev/null -X POST "$API/api/contents" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d '{"contentType":"smokepost","data":{"Title":"Smoke entry"},"status":"Draft"}'

# The list has to have something in it, or "no rows" and "the client cannot read the envelope" look
# the same from the browser, which is the ambiguity this whole script exists to remove.
COUNT=$(curl -s "$API/api/contents?page=1&pageSize=5" -H "Authorization: Bearer $TOKEN" \
    | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("items",[])))')
[ "${COUNT:-0}" -gt 0 ] || fail "seeding produced no content, so an empty admin list would prove nothing"
echo "seeded $COUNT entr(y|ies)"

# The browser sends X-Tenant. If the seeded entry is invisible under that header, the admin shows an
# empty list while the API has content, and every assertion below about the list would be describing
# the empty state rather than the contract.
TENANTED=$(curl -s "$API/api/contents?page=1&pageSize=5" -H "Authorization: Bearer $TOKEN" -H "X-Tenant: default" \
    | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("items",[])))')
[ "${TENANTED:-0}" -gt 0 ] || fail "the seeded entry is not visible with X-Tenant: default, which is the header the admin sends. Seeding and reading disagree about the tenant."
echo "visible under X-Tenant: default: $TENANTED"

step "building and starting the admin"
(
    cd admin
    NEXT_PUBLIC_API_URL="$API" npm run build --silent
)
# next.config.ts sets output: standalone, and `next start` refuses that build. Running the
# standalone server is also the closer simulation: it is what the published image runs.
cp -R admin/.next/static "admin/.next/standalone/.next/static"
(
    exec env PATH="$PATH" HOME="$HOME" NEXT_PUBLIC_API_URL="$API" \
        HOSTNAME=127.0.0.1 PORT="$ADMIN_PORT" \
        node admin/.next/standalone/server.js
) &
ADMIN_PID=$!

for _ in $(seq 1 60); do
    curl -s -o /dev/null "http://127.0.0.1:${ADMIN_PORT}/login" && break
    sleep 2
done
curl -s -o /dev/null "http://127.0.0.1:${ADMIN_PORT}/login" || fail "the admin never started"
kill -0 "$ADMIN_PID" 2>/dev/null || fail "the admin process exited; whatever answered is not it"

step "running the unmocked pack"
# Refuse to run if a mock has crept in. The value of this pack is entirely that it does not mock,
# and one page.route added in a hurry would quietly turn it back into the thing it replaced.
# A call, not a mention. The first version of this matched the doc comment that explains why
# mocking is banned here, which is the kind of self-inflicted failure that teaches people to
# weaken a guard rather than trust it.
if grep -rnE "^[^*/]*page\\.route\\(" admin/smoke/ >/dev/null 2>&1; then
    grep -rnE "^[^*/]*page\\.route\\(" admin/smoke/ >&2
    fail "admin/smoke contains a route mock. That is the one thing this pack must not do."
fi

cd admin
SMOKE_API_URL="$API" \
SMOKE_TOKEN="$TOKEN" \
SMOKE_ADMIN_URL="http://127.0.0.1:${ADMIN_PORT}" \
SMOKE_ADMIN_USERNAME="$ADMIN_USERNAME" \
SMOKE_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
    npx playwright test --config=playwright.smoke.config.ts --project=chromium ${SMOKE_FILTER:-}

printf '\nthe admin and the API agree on the contract\n'
