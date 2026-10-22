#!/usr/bin/env bash
#
# Measures what perf-budget.json budgets (#853, #969) and writes the numbers as JSON:
#
#   build_seconds            docker build of Dockerfile.suite, no cache
#   image_uncompressed_mb    the image as Docker stores it
#   image_compressed_mb      the image saved and gzipped, about what a registry pull transfers
#   startup_seconds          container start to the first 200 from /health/ready
#   memory_at_rest_mb        the API's resident memory after 60 seconds idle
#   delivery_<target>_p50_ms, delivery_<target>_p95_ms
#                            scripts/delivery-load.py at its default rate, for list, slug and site
#
# The API runs as the quickstart runs it: the published image's entry point, Production, Postgres
# with the quickstart's settings, Swagger off. It is held to 2 CPUs and 1 GB, a small server, because
# the runtime sizes its garbage collector from the CPUs it sees: unpinned, memory at rest measures
# the runner rather than the change.
#
# Beyond the CPU and memory limits, what differs from a quickstart install: demo content and Swagger
# stay off (their defaults there too), and the global rate limit is raised so the seeding and the
# load run, all from one address, measure delivery rather than the limiter's 429s.
#
# The delivery data (the blog and site blueprints, 20 published posts, a published site entry) is
# created through the API after memory at rest is read, so the idle number is an empty install's.
#
# Usage: scripts/perf-measure.sh <output json>
#   IDLE_SECONDS, LOAD_SECONDS  override the 60 second idle wait and load run (for a local try)
#   DOCKERFILE                  build another Dockerfile (default Dockerfile.suite)

set -euo pipefail

OUT="$(realpath -m "${1:?usage: scripts/perf-measure.sh <output json>}")"

cd "$(dirname "$0")/.."

. scripts/lib-ports.sh

IDLE_SECONDS="${IDLE_SECONDS:-60}"
LOAD_SECONDS="${LOAD_SECONDS:-60}"
DOCKERFILE="${DOCKERFILE:-Dockerfile.suite}"
TAG="barako-perf:$$"
NET="barako-perf-$$"
DB_PASSWORD='perf-not-a-real-password'
ADMIN_PASSWORD='Perf-not-a-real-password-1'
WORK="$(mktemp -d)"

cleanup() {
    remove_started "$WORK/api.cid" "$WORK/pg.cid"
    docker network rm "$NET" >/dev/null 2>&1 || true
    docker image rm -f "$TAG" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1" >&2; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }
now() { date +%s.%N; }
elapsed() { python3 -c "import sys; print(round(float(sys.argv[2]) - float(sys.argv[1]), 1))" "$1" "$2"; }

step "building $DOCKERFILE with no cache"
start=$(now)
docker build --no-cache -q -f "$DOCKERFILE" -t "$TAG" . >/dev/null || fail "the image did not build"
build_seconds=$(elapsed "$start" "$(now)")
echo "built in ${build_seconds}s" >&2

step "measuring the image"
uncompressed=$(docker image inspect -f '{{.Size}}' "$TAG")
compressed=$(docker save "$TAG" | gzip -c | wc -c)
echo "uncompressed $uncompressed bytes, compressed $compressed bytes" >&2

step "starting postgres with the quickstart's settings"
docker network create "$NET" >/dev/null
docker run -d --cidfile "$WORK/pg.cid" --network "$NET" --network-alias postgres \
    -e POSTGRES_DB=barakocms -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$DB_PASSWORD" \
    postgres:16-alpine postgres -c max_connections=100 -c shared_buffers=512MB \
    -c effective_cache_size=1GB -c work_mem=4MB -c random_page_cost=1.1 -c jit=off >/dev/null
PG_ID=$(cat "$WORK/pg.cid")
for _ in $(seq 1 60); do docker exec "$PG_ID" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 1; done
docker exec "$PG_ID" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || fail "postgres never became ready"

step "starting the API"
start=$(now)
docker run -d --cidfile "$WORK/api.cid" --network "$NET" --cpus 2 --memory 1g \
    -p "$(publish_spec "" 8080)" \
    -e ASPNETCORE_ENVIRONMENT=Production \
    -e ConnectionStrings__DefaultConnection="Host=postgres;Database=barakocms;Username=postgres;Password=$DB_PASSWORD" \
    -e JWT__Key='perf-not-a-real-key-at-least-32-characters' \
    -e InitialAdmin__Username=admin -e InitialAdmin__Password="$ADMIN_PASSWORD" \
    -e Seed__DemoContent=false -e Swagger__Enabled=false \
    -e RateLimiting__Global__PermitLimit=100000 \
    "$TAG" >/dev/null
API_ID=$(cat "$WORK/api.cid")
API_PORT=$(published_port "$API_ID" 8080) || fail "cannot tell which host port the API was published on"
BASE="http://127.0.0.1:$API_PORT"
ready=""
for _ in $(seq 1 600); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health/ready" || true)" = "200" ]; then ready=1; break; fi
    [ "$(docker inspect -f '{{.State.Running}}' "$API_ID")" = "true" ] || { docker logs "$API_ID" 2>&1 | tail -40 >&2; fail "the API exited during startup"; }
    sleep 0.2
done
[ -n "$ready" ] || { docker logs "$API_ID" 2>&1 | tail -40 >&2; fail "/health/ready never answered 200"; }
startup_seconds=$(elapsed "$start" "$(now)")
echo "ready in ${startup_seconds}s" >&2

step "idle for ${IDLE_SECONDS}s, then reading resident memory"
sleep "$IDLE_SECONDS"
# The image is chiseled and has no shell, so docker exec cannot run grep in it (exit 127). The
# container's process is an ordinary process on the Docker host, and its status file is readable there.
api_pid=$(docker inspect -f '{{.State.Pid}}' "$API_ID")
rss_kb=$(awk '/^VmRSS:/ {print $2}' "/proc/$api_pid/status" 2>/dev/null || true)
case "$rss_kb" in ''|*[!0-9]*) fail "could not read VmRSS from the API process" ;; esac
echo "VmRSS ${rss_kb} kB" >&2

step "creating delivery data through the API"
api() { # method path [json]
    local body="${3:-}"
    [ -n "$body" ] || body='{}'
    curl -sS --fail-with-body -X "$1" "$BASE$2" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$body"
}
TOKEN=$(curl -sS -f -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"$ADMIN_PASSWORD\"}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])') \
    || fail "could not sign in as the initial admin"
api POST /api/content-types/blueprints/blog >/dev/null || fail "could not apply the blog blueprint"
api POST /api/content-types/blueprints/site >/dev/null || fail "could not apply the site blueprint"
for n in $(seq 1 20); do
    api POST /api/contents "{\"contentType\":\"post\",\"status\":\"Published\",\"data\":{\"Title\":\"Post $n\",\"Slug\":\"post-$n\",\"Body\":\"Body of post $n.\"}}" >/dev/null \
        || fail "could not create post $n"
done
api POST /api/contents '{"contentType":"site","status":"Published","data":{"Name":"Budget site"}}' >/dev/null \
    || fail "could not create the site entry"

step "delivery latency for ${LOAD_SECONDS}s"
python3 scripts/delivery-load.py --base-url "$BASE" --type post --slug post-1 \
    --duration "$LOAD_SECONDS" --json "$WORK/load.json" >&2 || fail "the delivery load run had errors"

python3 - "$OUT" "$WORK/load.json" "$build_seconds" "$uncompressed" "$compressed" "$startup_seconds" "$rss_kb" <<'PY'
import json, platform, sys
out, load_path, build, unc, comp, startup, rss = sys.argv[1:8]
load = json.load(open(load_path))
arch = {"x86_64": "amd64", "aarch64": "arm64"}.get(platform.machine(), platform.machine())
metrics = {
    "build_seconds": float(build),
    "image_uncompressed_mb": round(int(unc) / 1048576, 1),
    "image_compressed_mb": round(int(comp) / 1048576, 1),
    "startup_seconds": float(startup),
    "memory_at_rest_mb": round(int(rss) / 1024, 1),
}
for target in ("list", "slug", "site"):
    metrics[f"delivery_{target}_p50_ms"] = load[target]["p50_ms"]
    metrics[f"delivery_{target}_p95_ms"] = load[target]["p95_ms"]
json.dump({"arch": arch, "metrics": metrics}, open(out, "w"), indent=2)
print(json.dumps({"arch": arch, "metrics": metrics}, indent=2), file=sys.stderr)
PY
