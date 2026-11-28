#!/usr/bin/env bash
#
# Proves the production bundle runs with nothing around it (#1436).
#
#   scripts/prod-bundle-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). The bundle is built from this working tree
# with scripts/build-prod-bundle.sh, unpacked in an empty directory outside the checkout, and the
# stack is started from there, so a file the compose file mounts and the bundle misses shows up
# here and not on a customer's host.
#
#   1. the archive matches its SHA256SUMS
#   2. the compose file with no BARAKO_TAG in the environment resolves to the bundle's version
#   2b. the compose project name is barako-cms from .env, so the data volumes do not change name
#      when the next release unpacks into another directory
#   2c. an .env without that line stops compose with a message naming the variable, so an .env
#      copied from an older install cannot start the stack on new, empty volumes
#   3. every bind mount the compose file declares exists in the unpacked directory
#   4. postgres starts, then app and db-backup, so compose runs the app role job through
#      depends_on, and both report healthy
#   5. the role job exited 0 and the backup job wrote a first archive from the mounted script
#   6. the bundle's own caddy service starts from the shipped Caddyfile, and a request over HTTPS
#      reaches the app: the certificate verifies against Caddy's root (no -k), /health/build
#      answers with this image's commit, the response has no Server or Via header, the API's
#      Strict-Transport-Security header comes through, and plain HTTP redirects to https://
#
# Caddy runs with DOMAIN_API=api.localhost. A *.localhost name gets a certificate from Caddy's own
# local CA, so no ACME request leaves the machine. What this does not prove is a real ACME issue
# from Let's Encrypt for a public name. Only the host ports differ from the bundle: a compose
# override written outside the bundle lets Docker pick free ones, because 80 and 443 may be taken
# on the machine running this. The shipped Caddyfile and compose file are not edited.
#
# The image is tagged with the bundle's version for the run, because that is what the bundle asks
# for. The extra tag is removed afterwards.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
VERSION="${BUNDLE_CHECK_VERSION:-0.0.0-ci}"
BUNDLED_IMAGE="ghcr.io/baryodev/barako-cms:$VERSION"
PROJECT="bundle-check-$$"
WORK="$(mktemp -d)"
DIR=""
CADDY_HOST="api.localhost"

step() { printf '\n=== %s\n' "$1"; }
dc() { (cd "$DIR" && docker compose -p "$PROJECT" --env-file "$DIR/.env" -f docker-compose.prod.yml "$@"); }
# The same, with the override that lets Docker choose the host ports for caddy.
dcc() { (cd "$DIR" && docker compose -p "$PROJECT" --env-file "$DIR/.env" -f docker-compose.prod.yml -f "$WORK/caddy-ports.yml" "$@"); }
fail() {
  printf '\nFAILED: %s\n' "$1" >&2
  [ -z "$DIR" ] || dc logs --no-color --tail 60 app db-backup postgres postgres-extensions caddy >&2 || true
  exit 1
}
cleanup() {
  [ -z "$DIR" ] || dc down -v --remove-orphans >/dev/null 2>&1 || true
  [ "$IMAGE" = "$BUNDLED_IMAGE" ] || docker rmi "$BUNDLED_IMAGE" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

docker image inspect "$IMAGE" >/dev/null 2>&1 || { echo "FAILED: image $IMAGE does not exist; build it first" >&2; exit 1; }
[ "$IMAGE" = "$BUNDLED_IMAGE" ] || docker tag "$IMAGE" "$BUNDLED_IMAGE"

step "building the bundle from this working tree"
bash "$REPO/scripts/build-prod-bundle.sh" "$VERSION" "$WORK/release"

step "the archive matches its SHA256SUMS"
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$WORK/release" && sha256sum -c SHA256SUMS)
else
  (cd "$WORK/release" && shasum -a 256 -c SHA256SUMS)
fi

step "unpacking into an empty directory"
mkdir "$WORK/empty"
[ -z "$(ls -A "$WORK/empty")" ] || fail "the target directory is not empty"
tar -xzf "$WORK/release/barako-cms-prod-$VERSION.tar.gz" -C "$WORK/empty"
DIR="$WORK/empty/barako-cms-prod-$VERSION"
[ -d "$DIR" ] || fail "the archive has no barako-cms-prod-$VERSION directory"
# A repository above the directory would let a missing file be found by accident.
if git -C "$DIR" rev-parse --git-dir >/dev/null 2>&1; then
  fail "the unpacked bundle sits inside a git repository, so it is not proof of an empty start"
fi

# The example file with its own values, plus the secrets the stack refuses to start without.
# BARAKO_TAG stays as the bundle wrote it, which is the point. DOMAIN_API is a *.localhost name so
# Caddy can issue its certificate from its own CA without ACME.
grep -v '^DOMAIN_API=' "$DIR/.env.prod.example" > "$DIR/.env"
cat >> "$DIR/.env" <<ENV
DOMAIN_API=$CADDY_HOST
DB_PASSWORD=$(openssl rand -hex 16)
DB_APP_PASSWORD=$(openssl rand -hex 16)
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=Ci-$(openssl rand -hex 8)-Aa1!
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV

step "with no BARAKO_TAG in the environment the app image is the bundle's version"
grep -v '^BARAKO_TAG=' "$DIR/.env" > "$WORK/no-tag.env"
resolved="$(cd "$DIR" && docker compose --env-file "$WORK/no-tag.env" -f docker-compose.prod.yml config --images | grep 'barako-cms')"
[ "$resolved" = "$BUNDLED_IMAGE" ] || fail "the compose file resolves to '$resolved', not $BUNDLED_IMAGE"
echo "$resolved"

step "the compose project name is the bundle's, not the directory's"
project_name="$(cd "$DIR" && docker compose --env-file "$DIR/.env" -f docker-compose.prod.yml config --format json \
  | python3 -c 'import json, sys; print(json.load(sys.stdin)["name"])')"
[ "$project_name" = "barako-cms" ] || fail "the project name is '$project_name', so the data volumes would follow the directory name"
echo "$project_name"

step "an .env without COMPOSE_PROJECT_NAME stops compose before anything starts"
grep -v '^COMPOSE_PROJECT_NAME=' "$DIR/.env" > "$WORK/no-name.env"
if no_name_out="$(cd "$DIR" && env -u COMPOSE_PROJECT_NAME docker compose --env-file "$WORK/no-name.env" -f docker-compose.prod.yml config 2>&1)"; then
  fail "compose resolved the bundle with no COMPOSE_PROJECT_NAME in .env, so a copied older .env would start on empty volumes"
fi
printf '%s\n' "$no_name_out" | grep -qF 'set COMPOSE_PROJECT_NAME in .env' \
  || fail "compose refused the .env without COMPOSE_PROJECT_NAME but did not print the message: $no_name_out"
echo "$no_name_out"

step "every bind mount the compose file declares is in the unpacked directory"
dc config --format json | python3 -c '
import json, os, sys
root = os.path.realpath(sys.argv[1])
bad = 0
for name, svc in sorted(json.load(sys.stdin)["services"].items()):
    for v in svc.get("volumes", []):
        if v.get("type") != "bind":
            continue
        src = os.path.realpath(v["source"])
        ok = src.startswith(root + os.sep) and os.path.exists(src) and (not os.path.isdir(src) or os.listdir(src))
        print(("ok   " if ok else "MISSING ") + name + ": " + os.path.relpath(src, root) + " -> " + v["target"])
        bad += 0 if ok else 1
sys.exit(1 if bad else 0)
' "$DIR" || fail "a bind mount points outside the bundle or at nothing"

step "starting postgres"
dc up -d --wait --wait-timeout 240 postgres || fail "postgres did not become healthy"

step "starting app and db-backup, so compose runs the app role job through depends_on"
limit=""
! command -v timeout >/dev/null || limit="timeout 300"
$limit docker compose -p "$PROJECT" --project-directory "$DIR" --env-file "$DIR/.env" -f "$DIR/docker-compose.prod.yml" up -d app db-backup \
  || fail "compose could not start app and db-backup (or the job did not finish within 300 seconds)"
for svc in app db-backup; do
  healthy=0
  for _ in $(seq 1 80); do
    status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then healthy=1; break; fi
    sleep 3
  done
  [ "$healthy" = 1 ] || fail "$svc did not become healthy"
  echo "$svc is healthy"
done

step "the app role job exited 0"
job_state="$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$(dc ps -aq postgres-extensions)" 2>/dev/null || true)"
[ "$job_state" = "exited 0" ] || fail "the postgres-extensions container is '$job_state', not 'exited 0'"

step "the backup job wrote an archive from the mounted script"
ok=0
for _ in $(seq 1 60); do
  if dc logs --no-color db-backup 2>&1 | grep -q "Backup OK"; then ok=1; break; fi
  sleep 3
done
[ "$ok" = 1 ] || fail "db-backup never logged a successful backup"

step "caddy starts from the bundle's Caddyfile"
# ports is a list that compose appends to, so the override has to replace it (!override, compose
# 2.24 or later). Only the host side changes: both ports stay bound to loopback, chosen by Docker.
cat > "$WORK/caddy-ports.yml" <<'YML'
services:
  caddy:
    ports: !override
      - "127.0.0.1::80"
      - "127.0.0.1::443"
YML
dcc config >/dev/null 2>&1 || fail "compose could not read the caddy ports override (it needs compose 2.24 or later)"
# The app is already up, so leave it alone.
dcc up -d --no-deps --wait --wait-timeout 120 caddy || fail "caddy did not become healthy"
http_port="$(dcc port caddy 80 | sed 's/.*://')"
https_port="$(dcc port caddy 443 | sed 's/.*://')"
case "$http_port$https_port" in
  ''|*[!0-9]*) fail "could not read caddy's host ports (http '$http_port', https '$https_port')" ;;
esac
echo "http $http_port, https $https_port"

step "caddy's local root certificate"
caddy_id="$(dc ps -q caddy)"
for _ in $(seq 1 30); do
  docker cp "$caddy_id:/data/caddy/pki/authorities/local/root.crt" "$WORK/caddy-root.crt" >/dev/null 2>&1 && break
  sleep 2
done
[ -s "$WORK/caddy-root.crt" ] || fail "caddy wrote no local root certificate under /data/caddy/pki/authorities/local"

# Verifies the server certificate against Caddy's root and never skips verification. --resolve keeps
# the name api.localhost whatever the machine's resolver does, so the name on the certificate is
# the one curl checks.
tls_curl() {
  curl -sS --max-time 10 --cacert "$WORK/caddy-root.crt" --resolve "$CADDY_HOST:$https_port:127.0.0.1" "$@"
}

step "a request over HTTPS reaches the app"
expected_sha="$(docker image inspect "$BUNDLED_IMAGE" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^BARAKO_BUILD_SHA=//p')"
[ -n "$expected_sha" ] || expected_sha="unknown"
code=""
for _ in $(seq 1 20); do
  if code="$(tls_curl -D "$WORK/tls-headers.txt" -o "$WORK/tls-body.json" -w '%{http_code}' "https://$CADDY_HOST:$https_port/health/build")"; then
    break
  fi
  code=""
  sleep 2
done
[ -n "$code" ] || fail "no HTTPS answer from caddy that verifies against its own root certificate"
[ "$code" = "200" ] || fail "https://$CADDY_HOST/health/build answered $code, not 200"
tr -d '\r' < "$WORK/tls-headers.txt" > "$WORK/tls-headers.clean"
head -1 "$WORK/tls-headers.clean"
cat "$WORK/tls-body.json"; echo
python3 -c '
import json, sys
body = json.load(open(sys.argv[1]))
sys.exit(0 if body == {"sha": sys.argv[2]} else 1)
' "$WORK/tls-body.json" "$expected_sha" \
  || fail "the body from https://$CADDY_HOST/health/build is not the app's sha $expected_sha, so the request did not reach this image"

step "the response carries no Server or Via header"
! grep -qi '^server:' "$WORK/tls-headers.clean" || fail "the HTTPS response has a Server header, the Caddyfile should strip it"
! grep -qi '^via:' "$WORK/tls-headers.clean" || fail "the HTTPS response has a Via header, the Caddyfile should strip it"
echo "none"

step "the API's Strict-Transport-Security header comes through"
# Production sends it only for a request the app sees as HTTPS, which here depends on Caddy's
# X-Forwarded-Proto from the one proxy address the app trusts. api.localhost is not on the
# framework's excluded host list (localhost itself is), which is why this name is used.
grep -qi '^strict-transport-security: max-age=7776000' "$WORK/tls-headers.clean" \
  || fail "the HTTPS response has no Strict-Transport-Security header with the 90 day policy"
grep -i '^strict-transport-security:' "$WORK/tls-headers.clean"

step "plain HTTP redirects to https://"
code="$(curl -sS --max-time 10 --resolve "$CADDY_HOST:$http_port:127.0.0.1" -D "$WORK/http-headers.txt" -o /dev/null -w '%{http_code}' "http://$CADDY_HOST:$http_port/health/build")" \
  || fail "no answer from caddy over plain HTTP"
case "$code" in
  301|302|303|307|308) ;;
  *) fail "plain HTTP answered $code, not a redirect" ;;
esac
tr -d '\r' < "$WORK/http-headers.txt" > "$WORK/http-headers.clean"
grep -qi '^location: https://' "$WORK/http-headers.clean" \
  || fail "the HTTP redirect does not point at https://"
grep -i '^location:' "$WORK/http-headers.clean"

printf '\nall checks passed\n'
