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
#   2. runs scripts/security-smoke.sh against it
#   3. signs in as the seeded admin, registers one user (the only user creation route, and it gives
#      the global User role, the least the API hands out) and signs in as that user
#   4. runs the ZAP API scan twice against openapi/baseline.json with the target set to the stack,
#      once anonymous and once with that user's bearer token
#   5. prints counts per risk level and a result line (scripts/dast-summary.py), then tears the
#      stack down with its volumes
#
# This repository is public and so are its CI logs. Nothing ZAP found is printed: ZAP's own output
# goes to a file in the report folder, the summary prints counts only, and in CI the report folder is
# deleted at the end and never uploaded. On a developer's machine the folder stays and its path is
# printed so the full reports can be read.
#
# Environment:
#   DAST_REPORT_DIR    where reports go (default: a new temporary folder)
#   DAST_MAX_MINUTES   cap for each ZAP pass, active scan and wait for start (default 15)
#   DAST_ACCEPTED      the accepted alerts file (default security/zap/accepted.tsv)
#
# Exit: 0 no new High, 1 a new High (or the smoke check failed), 2 the scan could not run. A scan
# that could not run never passes.
#
# The stack start mirrors scripts/compose-runtime-check.sh (same env file, same ordering). The
# requests to the API reuse api_call from scripts/lib-db-role-check.sh, which reaches the API from a
# throwaway container on the project network, so no port is published.
set -uo pipefail

cd "$(dirname "$0")/.." || exit 2
# shellcheck source=scripts/lib-db-role-check.sh
. scripts/lib-db-role-check.sh

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

IMAGE="${1:-}"
if [ -z "$IMAGE" ]; then
  say "building this checkout as barako-cms:dast"
  docker build -q -f Dockerfile.suite -t barako-cms:dast . > /dev/null || die "the image did not build"
  IMAGE="barako-cms:dast"
fi

PROJECT="dast-$$"
# The compose file names ghcr.io/baryodev/barako-cms:<BARAKO_TAG>, so the image under test is
# tagged that way for the run and the tag is removed again at the end.
TAG="dast-$$"
STACK_IMAGE="ghcr.io/baryodev/barako-cms:$TAG"
docker tag "$IMAGE" "$STACK_IMAGE" || die "the image does not exist"

WORK="$(mktemp -d)"
if [ -n "${DAST_REPORT_DIR:-}" ]; then
  REPORT_DIR="$DAST_REPORT_DIR"
  mkdir -p "$REPORT_DIR" || die "the report folder could not be made"
else
  REPORT_DIR="$WORK/reports"
  mkdir -p "$REPORT_DIR"
fi
# The ZAP user inside the container is not this user, so the folder it writes to is open. It sits
# inside the report folder, which keeps the access of the folder around it.
ZAP_WRK="$REPORT_DIR/zap-wrk"
mkdir -p "$ZAP_WRK" && chmod 777 "$ZAP_WRK"
STACK_LOG="$REPORT_DIR/stack.log"
: > "$STACK_LOG"

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
PROXY_ADDRESS=10.87.53.2
ENV
# Registration asks for an emailed link by default, and there is no mailbox here. The scan stack
# turns that off, with the acknowledgement the API demands, so the low-privilege user can exist.
# Nothing else about the production file changes.
cat > "$OVERRIDE" <<'YML'
services:
  app:
    environment:
      - Auth__RequireEmailVerification=false
      - Auth__AcknowledgeUnverifiedRegistration=true
YML

DC=(docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml -f "$OVERRIDE")
dc() { "${DC[@]}" "$@"; }
CHECK_API=app

# security-smoke.sh calls curl on the host. The stack publishes no port, so a curl on the PATH for
# that one run forwards to a container on the project network.
mkdir -p "$WORK/bin"
cat > "$WORK/bin/curl" <<SHIM
#!/usr/bin/env bash
exec docker run --rm --network "${PROJECT}_default" "$CURL_IMAGE" "\$@"
SHIM
chmod +x "$WORK/bin/curl"

# shellcheck disable=SC2329 # runs from the EXIT trap
cleanup() {
  local rc=$?
  docker rm -f "dast-zap-$$" > /dev/null 2>&1 || true
  # The app's log holds the requests ZAP sent, so it is kept only where a person reads it.
  if [ "$IN_CI" = 0 ]; then dc logs --no-color app > "$REPORT_DIR/app.log" 2>&1 || true; fi
  dc down -v --remove-orphans > /dev/null 2>&1 || true
  docker rmi "$STACK_IMAGE" > /dev/null 2>&1 || true
  if [ "$IN_CI" = 1 ]; then
    rm -rf "${REPORT_DIR:?}"
    say "reports removed (CI never keeps them)"
  else
    say "full reports: $REPORT_DIR"
  fi
  # The env file holds the generated secrets, so it goes with the rest of the work folder.
  rm -rf "${WORK:?}"
  exit "$rc"
}
trap cleanup EXIT

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

say "=== security smoke"
smoke_rc=0
PATH="$WORK/bin:$PATH" bash scripts/security-smoke.sh "http://$CHECK_API:8080" || smoke_rc=$?

# token_for <username> <password>: prints an access token, or nothing.
token_for() {
  local resp
  resp="$(printf '{"username":"%s","password":"%s"}' "$1" "$2" | api_call POST /api/auth/login)"
  [ "$(api_status "$resp")" = "200" ] || return 0
  api_body "$resp" | json_field token
}

say "=== sign-in"
ADMIN_TOKEN="$(token_for dast-admin "$ADMIN_PASSWORD")"
[ -n "$ADMIN_TOKEN" ] || die "the seeded admin could not sign in"
mask "$ADMIN_TOKEN"
# The admin token is not used again. Signing in proves the seed ran.
unset ADMIN_TOKEN

resp="$(printf '{"username":"%s","password":"%s","email":"%s@example.test"}' \
  "$USER_NAME" "$USER_PASSWORD" "$USER_NAME" | api_call POST /api/auth/register)"
[ "$(api_status "$resp")" = "200" ] || die "the low-privilege user could not be registered"
USER_TOKEN="$(token_for "$USER_NAME" "$USER_PASSWORD")"
[ -n "$USER_TOKEN" ] || die "the low-privilege user could not sign in"
mask_all "$USER_TOKEN"
say "signed in as the seeded admin and as a new low-privilege user"

cp openapi/baseline.json "$ZAP_WRK/openapi.json" || die "openapi/baseline.json is missing"
chmod 644 "$ZAP_WRK/openapi.json"
MAX="${DAST_MAX_MINUTES:-15}"

# run_zap <pass name> [bearer token]. ZAP prints alert names and URLs, so its stdout and stderr go
# to a file. Exit 0, 1 and 2 are a finished scan (alerts are judged from the JSON, not from this
# code); 3 and anything else are a scan that could not run. The bearer token reaches ZAP through its
# replacer rule, the documented way to add a header to every request.
run_zap() {
  local name="$1" token="${2:-}" rc=0 opts
  opts="-config scanner.maxScanDurationInMins=$MAX"
  if [ -n "$token" ]; then
    opts="$opts -config replacer.full_list(0).description=dast-auth"
    opts="$opts -config replacer.full_list(0).enabled=true"
    opts="$opts -config replacer.full_list(0).matchtype=REQ_HEADER"
    opts="$opts -config replacer.full_list(0).matchstring=Authorization"
    opts="$opts -config replacer.full_list(0).regex=false"
    opts="$opts -config \"replacer.full_list(0).replacement=Bearer $token\""
  fi
  say "=== ZAP API scan ($name)"
  docker run --rm --name "dast-zap-$$" --network "${PROJECT}_default" \
    -v "$ZAP_WRK:/zap/wrk:rw" "$ZAP_IMAGE" \
    zap-api-scan.py -t openapi.json -f openapi -O "http://$CHECK_API:8080" \
    -J "$name.json" -T "$MAX" -I -z "$opts" > "$REPORT_DIR/zap-$name.log" 2>&1 || rc=$?
  case "$rc" in
    0 | 1 | 2) say "ZAP finished the $name pass" ;;
    *) die "ZAP exited with an error on the $name pass" ;;
  esac
}

run_zap anonymous
run_zap authenticated "$USER_TOKEN"

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
