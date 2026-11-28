#!/usr/bin/env bash
#
# Scans the API with ZAP, against the hardened production stack started on this machine.
#
#   bash scripts/dast-scan.sh [image]     # default: build this checkout as barako-cms:dast
#
# It runs the same way on a developer's machine and in CI:
#
#   1. starts docker-compose.prod.yml (no caddy) under its own compose project, with secrets made
#      here by openssl and never printed
#   2. runs scripts/security-smoke.sh against it (its output goes to the report folder; the terminal
#      gets a pass line, or the number of failed checks)
#   3. signs in as the seeded admin, registers one user (the only user creation route, and it gives
#      the global User role, the least the API hands out)
#   4. runs the ZAP API scan against openapi/baseline.json with the target set to the stack: once
#      with that user's bearer token, then once anonymous. The signed-in pass goes first because
#      ZAP's attempts on the sign-in route use up its per-address limit. The user signs in right
#      before that pass, and the token is tried on GET /api/me (any signed-in user may read it)
#      before and after the pass. Access tokens last 15 minutes, so a 401 after the pass means the
#      token expired during it and the pass proved nothing: exit 2. The signed-in pass scans a copy of
#      the spec without the operations listed in security/zap/signed-in-skip.tsv (logout, password
#      change and the like), because the token is sent to every operation and those would end it. The
#      anonymous pass still covers them.
#   5. prints counts per risk level and a result line (scripts/dast-summary.py), then tears the
#      stack down with its volumes
#
# This repository is public and so are its CI logs. Nothing ZAP found is printed: ZAP's own output
# goes to a file in the report folder, the summary prints counts only, and in CI a report folder this
# script made is deleted at the end and never uploaded. On a developer's machine the folder stays
# (it is its own temporary folder, outside the one that holds the secrets) and its path is printed.
#
# Environment:
#   DAST_REPORT_DIR           where reports go (default: a new temporary folder). In CI it is deleted
#                             only when this script made it
#   DAST_MAX_MINUTES          active scan cap and start wait for the anonymous pass (default 15)
#   DAST_AUTH_MAX_MINUTES     the same for the signed-in pass (default 8). Smaller on purpose: the
#                             token lasts 15 minutes and the pass has to end inside that
#   DAST_MIN_URLS             floor for the URL count ZAP prints ("Total of N URLs"). Below it, or no
#                             such line, is exit 2 (default 100; the spec has 253 paths)
#   DAST_ACCEPTED             the accepted alerts file (default security/zap/accepted.tsv)
# The caps bound the active scan and ZAP's waits for start and passive scan, so a whole pass can take
# longer than its cap. Only the signed-in pass has a hard limit, the token, which is checked after it.
#
# One run per machine at a time: the stack uses a fixed subnet (10.87.53.0/28, the compose file's
# proxy network). Container names carry the project name, so they do not collide.
#
# Exit: 0 no new High, 1 a new High (or the smoke check failed), 2 the scan could not run. A scan
# that could not run never passes.
#
# The stack start mirrors scripts/compose-runtime-check.sh (same env file, same ordering). The
# requests to the API reuse api_call from scripts/lib-db-role-check.sh, which reaches the API from a
# throwaway container on the project network, so no port is published. The decisions (ZAP exit code,
# URL floor, token state, report folder) are functions in scripts/lib-dast.sh, tested by
# scripts/test-dast.sh.
set -uo pipefail

cd "$(dirname "$0")/.." || exit 2
# shellcheck source=scripts/lib-db-role-check.sh
. scripts/lib-db-role-check.sh
# shellcheck source=scripts/lib-dast.sh
. scripts/lib-dast.sh

# Pinned by version and digest. Look the digest up with
#   docker buildx imagetools inspect ghcr.io/zaproxy/zaproxy:<version>
ZAP_IMAGE="ghcr.io/zaproxy/zaproxy:2.17.0@sha256:9624669db72f54d86c4b46a7064518bd9bf79d8c61a4c31befa9923489f328af"

IN_CI=0
if [ "${CI:-}" = "true" ] || [ -n "${GITHUB_ACTIONS:-}" ]; then IN_CI=1; fi

say() { printf '%s\n' "$*"; }
# A message with no secret and nothing the scan found. Exit 2 means the scan could not run.
die() { say "DAST result: could not run the scan ($1)"; exit 2; }

# Anything secret goes through here first, so a runner masks it before anything can print it.
mask() { if [ -n "${GITHUB_ACTIONS:-}" ] && [ -n "$1" ]; then printf '::add-mask::%s\n' "$1"; fi; }

mask_all() {
  local v
  for v in "$@"; do
    mask "$v"
    SECRET_VALUES+=("$v")
  done
}

DB_PASSWORD="$(openssl rand -hex 16)"
APP_PASSWORD="$(openssl rand -hex 16)"
JWT_KEY="$(openssl rand -hex 32)"
ADMIN_PASSWORD="Dast-$(openssl rand -hex 8)-Aa1!"
USER_NAME="dast-user-$(openssl rand -hex 4)"
USER_PASSWORD="Dast-$(openssl rand -hex 8)-Aa1!"
mask_all "$DB_PASSWORD" "$APP_PASSWORD" "$JWT_KEY" "$ADMIN_PASSWORD" "$USER_PASSWORD"

PROJECT="dast-$$"
TAG="dast-$$"
STACK_IMAGE="ghcr.io/baryodev/barako-cms:$TAG"
ZAP_NAME="dast-zap-$$"
DC=()
TAGGED=0
REPORT_DIR=""
REPORT_CREATED=0
WORK="$(mktemp -d)" || die "no temporary folder"

dc() { "${DC[@]}" "$@"; }

# Safe at any point after WORK exists: what is not set yet is skipped.
# shellcheck disable=SC2329 # runs from the EXIT trap
cleanup() {
  local rc=$?
  trap - EXIT
  docker rm -f "$ZAP_NAME" > /dev/null 2>&1 || true
  if [ "${#DC[@]}" -gt 0 ]; then
    # The app's log holds the requests ZAP sent, so it is kept only where a person reads it.
    if [ "$IN_CI" = 0 ] && [ -n "$REPORT_DIR" ]; then dc logs --no-color app > "$REPORT_DIR/app.log" 2>&1 || true; fi
    dc down -v --remove-orphans > /dev/null 2>&1 || true
  fi
  if [ "$TAGGED" = 1 ]; then docker rmi "$STACK_IMAGE" > /dev/null 2>&1 || true; fi
  if [ -n "$REPORT_DIR" ]; then
    if dast_delete_reports "$IN_CI" "$REPORT_CREATED"; then
      rm -rf "${REPORT_DIR:?}"
      say "reports removed (CI never keeps them)"
    elif [ "$IN_CI" = 1 ]; then
      # A folder the caller made stays, but what this run put in it does not.
      rm -rf "${REPORT_DIR:?}/zap-wrk" "${REPORT_DIR:?}/stack.log" "${REPORT_DIR:?}/smoke.log" \
        "${REPORT_DIR:?}/zap-anonymous.log" "${REPORT_DIR:?}/zap-authenticated.log"
      say "this run's reports removed from the folder you gave (CI never keeps them)"
    else
      say "full reports: $REPORT_DIR"
    fi
  fi
  # The env file holds the generated secrets, so it goes with the rest of the work folder.
  rm -rf "${WORK:?}"
  exit "$rc"
}
trap cleanup EXIT

dast_choose_report_dir "${DAST_REPORT_DIR:-}" "$WORK" || die "the report folder could not be made"
# The ZAP user inside the container is not this user, so the folder it writes to is open. It sits
# inside the report folder, which keeps the access of the folder around it.
ZAP_WRK="$REPORT_DIR/zap-wrk"
{ mkdir -p "$ZAP_WRK" && chmod 777 "$ZAP_WRK"; } || die "the ZAP work folder could not be made"
STACK_LOG="$REPORT_DIR/stack.log"
: > "$STACK_LOG"

IMAGE="${1:-}"
if [ -z "$IMAGE" ]; then
  say "building this checkout as barako-cms:dast"
  docker build -q -f Dockerfile.suite -t barako-cms:dast . > /dev/null || die "the image did not build"
  IMAGE="barako-cms:dast"
fi
# The compose file names ghcr.io/baryodev/barako-cms:<BARAKO_TAG>, so the image under test is
# tagged that way for the run and the tag is removed again at the end.
docker tag "$IMAGE" "$STACK_IMAGE" || die "the image does not exist"
TAGGED=1

ENVFILE="$WORK/stack.env"
OVERRIDE="$WORK/dast-override.yml"
cat .env.prod.example > "$ENVFILE"
cat >> "$ENVFILE" <<ENV
BARAKO_TAG=$TAG
DB_PASSWORD=$DB_PASSWORD
DB_APP_PASSWORD=$APP_PASSWORD
JWT_KEY=$JWT_KEY
ADMIN_USER=dast-admin
ADMIN_PASSWORD=$ADMIN_PASSWORD
PROXY_SUBNET=10.87.53.0/28
PROXY_ADDRESS=10.87.53.14
ENV
# Registration asks for an emailed link by default, and there is no mailbox here. The scan stack
# turns that off, with the acknowledgement the API demands, so the low-privilege user can exist.
# The global rate limit (100 requests a minute per client address by default) would answer most of
# ZAP's requests with 429, so the scan stack raises it. The other limits stay as shipped.
# The container names carry the project name, because the production file fixes them and a second
# stack on the machine would collide. Nothing else about the production file changes.
cat > "$OVERRIDE" <<YML
services:
  app:
    container_name: $PROJECT-app
    environment:
      - Auth__RequireEmailVerification=false
      - Auth__AcknowledgeUnverifiedRegistration=true
      - RateLimiting__Global__PermitLimit=100000
  postgres:
    container_name: $PROJECT-postgres
  postgres-extensions:
    container_name: $PROJECT-postgres-extensions
  db-backup:
    container_name: $PROJECT-db-backup
  caddy:
    container_name: $PROJECT-caddy
YML

DC=(docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml -f "$OVERRIDE")
CHECK_API=app

# security-smoke.sh calls curl on the host. The stack publishes no port, so a curl on the PATH for
# that one run forwards to a container on the project network.
mkdir -p "$WORK/bin"
cat > "$WORK/bin/curl" <<SHIM
#!/usr/bin/env bash
exec docker run --rm --network "${PROJECT}_default" "$CURL_IMAGE" "\$@"
SHIM
chmod +x "$WORK/bin/curl"

say "=== starting the stack"
dc up -d --wait --wait-timeout 240 postgres > "$STACK_LOG" 2>&1 || die "postgres did not become healthy"
limit=""
! command -v timeout > /dev/null || limit="timeout 300"
$limit "${DC[@]}" up -d app db-backup >> "$STACK_LOG" 2>&1 || die "the stack did not start"
for svc in app db-backup; do
  healthy=0
  for _ in $(seq 1 80); do
    status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2> /dev/null || true)"
    if [ "$status" = "healthy" ]; then healthy=1; break; fi
    sleep 3
  done
  [ "$healthy" = 1 ] || die "$svc did not become healthy"
done
say "stack is healthy"

# The smoke script names every path and header it checks, so its output stays in the report folder.
say "=== security smoke"
smoke_rc=0
PATH="$WORK/bin:$PATH" bash scripts/security-smoke.sh "http://$CHECK_API:8080" > "$REPORT_DIR/smoke.log" 2>&1 || smoke_rc=$?
smoke_failed="$(dast_smoke_failures "$REPORT_DIR/smoke.log")"
if [ "$smoke_rc" -eq 0 ]; then
  say "security smoke: pass"
else
  say "security smoke: FAIL, ${smoke_failed:-0} checks failed (exit $smoke_rc)"
fi

# sign_in <username> <password>: sets SIGNIN_CODE to the status the API answered and SIGNIN_TOKEN to
# the access token, or to nothing when the sign-in was refused.
SIGNIN_CODE=""
SIGNIN_TOKEN=""
sign_in() {
  local resp
  SIGNIN_TOKEN=""
  resp="$(printf '{"username":"%s","password":"%s"}' "$1" "$2" | api_call POST /api/auth/login)"
  SIGNIN_CODE="$(api_status "$resp")"
  [ "$SIGNIN_CODE" = "200" ] || return 0
  SIGNIN_TOKEN="$(api_body "$resp" | json_field token)"
}

# signin_failed <who>: stops with the status class of the refused sign-in, or "rate limited" for a 429.
signin_failed() {
  local why
  why="status $(dast_status_class "$SIGNIN_CODE")"
  [ "$SIGNIN_CODE" != 429 ] || why="rate limited"
  die "$1 could not sign in ($why)"
}

# token_probe <token>: one GET /api/me with the token, from a throwaway curl container on the project
# network. Sets PROBE_CODE (the status code) and PROBE_HEADERS (the response headers). The route is
# open to any signed-in user, and it is not one that a token which has to enrol in MFA may use, so a
# token that every other route would refuse with 403 does not pass it. The body is thrown away.
PROBE_CODE=""
PROBE_HEADERS=""
token_probe() {
  local out
  out="$(docker run --rm --network "${PROJECT}_default" "$CURL_IMAGE" -s --max-time 30 -o /dev/null \
    -D - -w 'STATUS:%{http_code}' -H "Authorization: Bearer $1" "http://${CHECK_API}:8080/api/me" 2> /dev/null || true)"
  PROBE_CODE="${out##*STATUS:}"
  PROBE_HEADERS="${out%STATUS:*}"
}

# token_state <token> <phase>: tries the token, prints only the status class, and returns 0 for a
# 2xx. The result is kept in TOKEN_STATE (ok, expired, limited or failed). A 429 is tried once more
# after the Retry-After the API gave (1 to 60 seconds): the limit is per client address, and the
# probe's container can be handed the address ZAP just used.
TOKEN_STATE=""
token_state() {
  local wait
  token_probe "$1"
  TOKEN_STATE="$(dast_token_state "$PROBE_CODE")"
  if [ "$TOKEN_STATE" = limited ]; then
    wait="$(printf '%s' "$PROBE_HEADERS" | dast_retry_after)"
    say "token check ($2): status $(dast_status_class "$PROBE_CODE"), trying once more after ${wait}s"
    sleep "$wait"
    token_probe "$1"
    TOKEN_STATE="$(dast_token_state "$PROBE_CODE")"
  fi
  say "token check ($2): status $(dast_status_class "$PROBE_CODE")"
  [ "$TOKEN_STATE" = ok ]
}

say "=== sign-in"
sign_in dast-admin "$ADMIN_PASSWORD"
[ -n "$SIGNIN_TOKEN" ] || signin_failed "the seeded admin"
mask "$SIGNIN_TOKEN"
# The admin token is not used again. Signing in proves the seed ran.
SIGNIN_TOKEN=""

resp="$(printf '{"username":"%s","password":"%s","email":"%s@example.test"}' \
  "$USER_NAME" "$USER_PASSWORD" "$USER_NAME" | api_call POST /api/auth/register)"
[ "$(api_status "$resp")" = "200" ] || die "the low-privilege user could not be registered"
say "signed in as the seeded admin and registered a low-privilege user"

cp openapi/baseline.json "$ZAP_WRK/openapi.json" || die "openapi/baseline.json is missing"
# The signed-in pass scans a copy without the operations that end the token, the session or the user
# (security/zap/signed-in-skip.tsv). The anonymous pass keeps the whole spec. A skip line that no longer
# matches the spec stops both passes, so a renamed route is fixed in the skip file before anything scans.
python3 -I scripts/dast-spec-filter.py openapi/baseline.json security/zap/signed-in-skip.tsv \
  "$ZAP_WRK/openapi-signed-in.json" > /dev/null || die "the signed-in spec could not be made from the skip file"
chmod 644 "$ZAP_WRK/openapi.json" "$ZAP_WRK/openapi-signed-in.json"
MAX="${DAST_MAX_MINUTES:-15}"
AUTH_MAX="${DAST_AUTH_MAX_MINUTES:-8}"
MIN_URLS="${DAST_MIN_URLS:-100}"

# run_zap <pass name> <minutes> <spec file in the ZAP folder> [bearer token]. ZAP prints alert names and URLs, so its stdout and
# stderr go to a file. The bearer token reaches ZAP through its replacer rule, the documented way to
# add a header to every request. After the pass the URL count ZAP printed is read back: no count, or
# one under the floor, is a scan that reached nothing.
run_zap() {
  local name="$1" minutes="$2" spec="$3" token="${4:-}" rc=0 opts urls
  opts="-config scanner.maxScanDurationInMins=$minutes"
  if [ -n "$token" ]; then
    opts="$opts -config replacer.full_list(0).description=dast-auth"
    opts="$opts -config replacer.full_list(0).enabled=true"
    opts="$opts -config replacer.full_list(0).matchtype=REQ_HEADER"
    opts="$opts -config replacer.full_list(0).matchstring=Authorization"
    opts="$opts -config replacer.full_list(0).regex=false"
    opts="$opts -config \"replacer.full_list(0).replacement=Bearer $token\""
  fi
  say "=== ZAP API scan ($name)"
  docker run --rm --name "$ZAP_NAME" --network "${PROJECT}_default" \
    -v "$ZAP_WRK:/zap/wrk:rw" "$ZAP_IMAGE" \
    zap-api-scan.py -t "$spec" -f openapi -O "http://$CHECK_API:8080" \
    -J "$name.json" -T "$minutes" -I -z "$opts" > "$REPORT_DIR/zap-$name.log" 2>&1 || rc=$?
  dast_zap_finished "$rc" || die "ZAP exited with an error on the $name pass"
  urls="$(dast_url_count "$REPORT_DIR/zap-$name.log")" || die "ZAP printed no URL count on the $name pass, so it reached nothing"
  dast_url_floor_ok "$urls" "$MIN_URLS" || die "ZAP held $urls URLs on the $name pass, under the floor of $MIN_URLS"
  say "ZAP finished the $name pass with $urls URLs"
}

# The signed-in pass runs first. The sign-in limit (5 per client address in 15 minutes, as shipped) is
# spent by ZAP's own attempts on the sign-in route, and the next container is often given the address
# ZAP just left, so a sign-in after a pass is refused. The user signs in right before its pass, because
# an access token lasts 15 minutes and the pass has to fit inside them.
sign_in "$USER_NAME" "$USER_PASSWORD"
USER_TOKEN="$SIGNIN_TOKEN"
[ -n "$USER_TOKEN" ] || signin_failed "the low-privilege user"
mask_all "$USER_TOKEN"
token_state "$USER_TOKEN" "before the signed-in pass" || die "the user token was not accepted before the pass"
run_zap authenticated "$AUTH_MAX" openapi-signed-in.json "$USER_TOKEN"
if ! token_state "$USER_TOKEN" "after the signed-in pass"; then
  if [ "$TOKEN_STATE" = expired ]; then
    die "the token expired during the signed-in pass, so the pass proved nothing"
  fi
  die "the token was not accepted after the signed-in pass, so the pass proved nothing"
fi

run_zap anonymous "$MAX" openapi.json

say "=== summary"
accepted="${DAST_ACCEPTED:-security/zap/accepted.tsv}"
summary_rc=0
python3 -I scripts/dast-summary.py "$ZAP_WRK/anonymous.json" "$ZAP_WRK/authenticated.json" \
  --accepted "$accepted" || summary_rc=$?

if [ "$summary_rc" -ne 0 ]; then exit "$summary_rc"; fi
if [ "$smoke_rc" -ne 0 ]; then
  say "DAST result: FAIL, the security smoke check failed"
  exit 1
fi
exit 0
