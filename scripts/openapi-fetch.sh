#!/usr/bin/env bash
#
# Boots the Suite against an empty PostgreSQL with Swagger on and writes the OpenAPI document it
# serves, normalised, to the given file.
#
# The document is served by the running app, not built from source: the delivery paths are
# generated per content type at runtime and every module adds its own endpoints. So this starts
# the host the published image runs, not the core host, and asks it.
#
# Normalised means: keys sorted, and "servers" removed, since it names the port this run happened
# to bind. Nothing else is touched, so the file is what a consumer reads.
#
# Usage: scripts/openapi-fetch.sh <output file>
#
# To accept an API change, write the baseline with it and commit the diff for review:
#   bash scripts/openapi-fetch.sh openapi/baseline.json

set -euo pipefail

cd "$(dirname "$0")/.."

. scripts/lib-ports.sh

OUT="${1:?usage: scripts/openapi-fetch.sh <output file>}"
PG_PORT="${PG_PORT:-}"
DB="barako_openapi"
JWT_KEY='openapi-contract-key-that-is-at-least-32-chars-long'
WORK="$(mktemp -d)"
HOST_PID=""

cleanup() {
    [ -n "$HOST_PID" ] && kill "$HOST_PID" 2>/dev/null || true
    remove_started "$WORK/pg.cid"
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1" >&2; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

step "publishing the Suite from the working tree"
dotnet publish BarakoCMS.Suite/BarakoCMS.Suite.csproj -c Release -o "$WORK/publish" --nologo -v q \
    -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false >&2

step "starting postgres"
PG_ID=$(docker run -d --cidfile "$WORK/pg.cid" -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p "$(publish_spec "$PG_PORT" 5432)" postgres:16-alpine 2>"$WORK/docker-run.err") \
    || fail "$(publish_failure "$WORK/docker-run.err" "$PG_PORT" postgres)"
PG_PORT=$(published_port "$PG_ID" 5432) || fail "cannot tell which host port postgres was published on"
for _ in $(seq 1 60); do docker exec "$PG_ID" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 2; done
docker exec "$PG_ID" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || fail "postgres never became ready"

step "starting the Suite with Swagger on"
# The seeder stays off so the document describes the API rather than the demo data: a seeded
# content type would add delivery paths that come and go with the seed, not with the code.
env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://127.0.0.1:0" \
    "$LISTEN_LOG_ENV" \
    ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=${PG_PORT};Database=${DB};Username=postgres;Password=postgres" \
    JWT__Key="$JWT_KEY" \
    Swagger__Enabled=true \
    SKIP_SEEDER=true \
    Kubernetes__Enabled=false \
    dotnet exec "$WORK/publish/BarakoCMS.Suite.dll" >"$WORK/host.log" 2>&1 &
HOST_PID=$!

APP_PORT=$(listen_port "$WORK/host.log" "$HOST_PID" 180) || { tail -80 "$WORK/host.log" >&2; fail "the Suite never started listening"; }

step "fetching /swagger/v1/swagger.json"
code=""
for _ in $(seq 1 60); do
    code=$(curl -s -o "$WORK/raw.json" -w '%{http_code}' "http://127.0.0.1:${APP_PORT}/swagger/v1/swagger.json" || true)
    [ "$code" = "200" ] && break
    kill -0 "$HOST_PID" 2>/dev/null || { tail -40 "$WORK/host.log" >&2; fail "the Suite exited before serving the document"; }
    sleep 2
done
[ "$code" = "200" ] || { tail -40 "$WORK/host.log" >&2; fail "the document never answered 200 (last status: ${code:-none})"; }

python3 - "$WORK/raw.json" "$OUT" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
doc.pop("servers", None)
paths = doc.get("paths") or {}
if len(paths) < 50:
    sys.exit(f"the document describes {len(paths)} paths; an API this size has well over 50, so this is not the real document")
with open(sys.argv[2], "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2, sort_keys=True, ensure_ascii=True)
    f.write("\n")
print(f"wrote {sys.argv[2]}: {len(paths)} paths", file=sys.stderr)
PY
