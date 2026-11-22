#!/usr/bin/env bash
#
# Proves a 5.9.0 production volume upgrades to this checkout by the steps in "Upgrading" in
# docs/deploy-in-production.md.
#
#   scripts/compose-upgrade-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image (the one CI built, tag :ci) has to exist already. Caddy and db-backup are never started
# on 5.9.0, because their mounts and ACME do not resolve in CI.
#
#   1. docker-compose.prod.yml from tag v5.9.0 and the image ghcr.io/baryodev/barako-cms:5.9.0
#      (pulled by tag, never a fallback) start postgres and the API, which connects as the owner
#   2. one sign-in, a content type and an item go in through the API, and the 5.9.0 job
#      (postgres-extensions, which only installs pg_stat_statements) runs
#   3. this checkout's docker-compose.prod.yml replaces the file, as an operator would copy it, and
#      the "Upgrading" blocks run (the first one stops the API) exactly as written, with two substitutions below
#   4. the volume is the same one, the job exited 0, the migrate branch that ran is the one this
#      checkout's migrations imply, the API is healthy as the app role, owns everything in
#      public, the old item reads back, and a new one is written
#
# The two substitutions, and nothing else, differ from the docs:
#   a. `docker compose pull` is skipped, because the image is the locally built :ci tag
#   b. the last `up -d` is `up -d app db-backup`, because Caddy needs a domain and ACME
# The docs' commands take no -p, so the project is set with COMPOSE_PROJECT_NAME. The stack's .env
# gets BARAKO_TAG=ci and DB_APP_PASSWORD before step 3, which is the operator's own edit and not a
# docs step. Generated values are never printed.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"
# shellcheck source=scripts/lib-db-role-check.sh
. scripts/lib-db-role-check.sh

IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
TAG="${IMAGE##*:}"
OLD_TAG="5.9.0"
OLD_REF="v5.9.0"
OLD_IMAGE="ghcr.io/baryodev/barako-cms:$OLD_TAG"
PROJECT="compose-upgrade-$$"
WORK="$(mktemp -d)"
STACK="$WORK/stack"
LOGS="$(mktemp)"
mkdir "$STACK"

DB_PASSWORD_VALUE="up-db-$(openssl rand -hex 16)"
APP_PASSWORD="up-app-$(openssl rand -hex 16)"
JWT_VALUE="$(openssl rand -hex 32)"
ADMIN_PASSWORD_VALUE="Ci-$(openssl rand -hex 8)-Aa1!"
SECRET_VALUES=("$DB_PASSWORD_VALUE" "$APP_PASSWORD" "$JWT_VALUE" "$ADMIN_PASSWORD_VALUE")

export COMPOSE_PROJECT_NAME="$PROJECT"
DC=(docker compose -p "$PROJECT" --project-directory "$STACK" -f "$STACK/docker-compose.prod.yml")
dc() { "${DC[@]}" "$@"; }
CHECK_DB=barako_cms
CHECK_API=app

cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$WORK"
  rm -f "$LOGS"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() {
  printf '\nFAILED: %s\n' "$1" >&2
  dc logs --no-color --tail 60 app db-backup postgres postgres-extensions 2>&1 | redact >&2 || true
  exit 1
}

# The limit a step gets where timeout exists.
limit=""
! command -v timeout > /dev/null || limit="timeout 600"

write_env() { # <tag> <with the app role password: yes|no>
  cat "$REPO/.env.prod.example" > "$STACK/.env"
  cat >> "$STACK/.env" <<ENV
BARAKO_TAG=$1
DB_PASSWORD=$DB_PASSWORD_VALUE
JWT_KEY=$JWT_VALUE
ADMIN_PASSWORD=$ADMIN_PASSWORD_VALUE
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV
  if [ "$2" = yes ]; then printf 'DB_APP_PASSWORD=%s\n' "$APP_PASSWORD" >> "$STACK/.env"; fi
}

wait_ready() { # <seconds>: polls /health/ready through the curl container, for an image with no probe
  local deadline=$((SECONDS + $1)) resp
  while [ "$SECONDS" -lt "$deadline" ]; do
    resp="$(api_call GET /health/ready 2>/dev/null || true)"
    [ "$(api_status "$resp")" != "200" ] || return 0
    sleep 3
  done
  return 1
}

wait_healthy() { # <seconds> <service>...
  local deadline=$((SECONDS + $1)) svc status
  shift
  for svc in "$@"; do
    while :; do
      status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null || true)"
      [ "$status" != "healthy" ] || break
      [ "$SECONDS" -lt "$deadline" ] || return 1
      sleep 3
    done
  done
}

login() { # prints the access token
  local resp
  resp="$(printf '{"username":"admin","password":"%s"}' "$ADMIN_PASSWORD_VALUE" | api_call POST /api/auth/login)" \
    || fail "could not reach the API to sign in"
  [ "$(api_status "$resp")" = "200" ] || fail "the admin could not sign in (status $(api_status "$resp"))"
  TOKEN="$(api_body "$resp" | json_field accessToken)"
  [ -n "$TOKEN" ] || TOKEN="$(api_body "$resp" | json_field token)"
  [ -n "$TOKEN" ] || fail "the sign-in response holds no token"
}

create_item() { # <title>: prints the new item's id
  local resp id
  resp="$(printf '{"contentType":"upgrade_proof","status":1,"sensitivity":0,"data":{"title":"%s"}}' "$1" \
    | api_call POST /api/contents "$TOKEN")" || fail "could not reach the API to write an item"
  case "$(api_status "$resp")" in 200|201) ;; *) fail "writing an item returned status $(api_status "$resp")" ;; esac
  id="$(api_body "$resp" | json_field id)"
  [ -n "$id" ] || fail "the new item's response holds no id"
  printf '%s' "$id"
}

step "the v5.9.0 tag and image"
if ! git rev-parse -q --verify "refs/tags/$OLD_REF" > /dev/null; then
  git fetch --depth=1 --no-tags origin "+refs/tags/$OLD_REF:refs/tags/$OLD_REF" > /dev/null 2>&1 \
    || fail "tag $OLD_REF is missing here and could not be fetched"
fi
git show "$OLD_REF:docker-compose.prod.yml" > "$STACK/docker-compose.prod.yml" \
  || fail "tag $OLD_REF is missing docker-compose.prod.yml"
docker pull "$OLD_IMAGE" > /dev/null 2>&1 || fail "the image $OLD_IMAGE could not be pulled, so the tag is missing from the registry"
printf 'using %s and %s\n' "$OLD_REF" "$OLD_IMAGE"

step "the migrate branch this checkout implies"
# What --status exits on a database only the 5.9.0 API has started, from the code:
#  - The API's first start on an empty database runs MigrationLedger.BaselineIfFreshAsync
#    (BarakoCommandExtensions.ReconcileMigrationLedgerAsync, before the schema exists). It writes a
#    baselined row for every migration that build ships. 5.9.0 has the same call.
#  - MigrationCommand.StatusAsync exits status.Current ? 0 : 1. Current needs a row for every shipped
#    migration, keyed by owner/version/name. MigrationLedger.StateOf keeps a baselined row's state even
#    when the file's checksum differs, so an edited file does not change the exit code.
#  - So the exit is 1 exactly when this checkout ships a migration path that 5.9.0 did not.
#    Byte identity is stricter than needed, so it is not used. The embedded set leaves out tenancy/,
#    removed/ and rollback-*.sql (barakoCMS.csproj).
EXPECT_STATUS=0
while IFS= read -r f; do
  case "$f" in migrations/tenancy/*|migrations/removed/*|*/rollback-*) continue ;; esac
  case "$f" in *.sql) ;; *) continue ;; esac
  if ! git cat-file -e "$OLD_REF:$f" 2> /dev/null; then
    printf 'ships a migration %s did not: %s\n' "$OLD_REF" "$f"
    EXPECT_STATUS=1
  fi
done < <(git ls-files migrations)
printf 'expected db-migrate --status exit code: %s\n' "$EXPECT_STATUS"

step "starting the 5.9.0 stack: postgres and the API, which connects as the owner"
write_env "$OLD_TAG" no
$limit "${DC[@]}" up -d postgres app > /dev/null 2>&1 || fail "the 5.9.0 stack did not start"
wait_ready 240 || fail "the 5.9.0 API did not answer /health/ready within 240 seconds"

step "5.9.0 content goes in through the API"
login
resp="$(printf '%s' '{"name":"upgrade_proof","displayName":"Upgrade proof","fields":[{"name":"title","displayName":"Title","type":"text","isRequired":true,"validationRules":{}}]}' \
  | api_call POST /api/content-types "$TOKEN")" || fail "could not reach the API to create a content type"
case "$(api_status "$resp")" in 200|201) ;; *) fail "creating the content type returned status $(api_status "$resp")" ;; esac
OLD_ITEM="$(create_item "written on 5.9.0")"

step "5.9.0's job runs, and the API's sessions are the owner's"
$limit "${DC[@]}" run --rm -T postgres-extensions > /dev/null 2>&1 || fail "the 5.9.0 postgres-extensions job failed"
( APP_ROLE=postgres; assert_api_sessions_use_app_role ) || fail "the 5.9.0 API is not connected as the owner"
misowned="$(owner_sql "$MISOWNED_SQL")" || fail "could not read the owners in public"
[ -n "$misowned" ] || fail "public is already owned by the app role, so the upgrade would prove nothing"
VOLUME="${PROJECT}_postgres_prod_data"
volume_before="$(docker volume inspect -f '{{.Name}} {{.CreatedAt}}' "$VOLUME")" || fail "volume $VOLUME not found"

step "bring in this checkout's files, as an operator does; the docs' own first block stops the API"
cp "$REPO/docker-compose.prod.yml" "$STACK/docker-compose.prod.yml"
cp "$REPO/Caddyfile" "$STACK/Caddyfile"
cp -R "$REPO/scripts" "$STACK/scripts"
write_env "$TAG" yes
printf 'copied into the stack folder: docker-compose.prod.yml, Caddyfile, scripts/\n'
printf 'rewrote .env with BARAKO_TAG=%s and DB_APP_PASSWORD (an operator edit, not a docs step)\n' "$TAG"
printf 'COMPOSE_PROJECT_NAME=%s (the docs commands take no -p)\n' "$PROJECT"

step "the Upgrading blocks, from docs/deploy-in-production.md"
python3 -I - "$REPO/docs/deploy-in-production.md" "$WORK" <<'PY' || fail "the Upgrading section does not have the shape this check expects"
import re, sys
doc, out = sys.argv[1], sys.argv[2]
text = open(doc, encoding="utf-8").read()
start = text.index("\n## Upgrading\n")
end = text.index("\n### Upgrading past 4.0.2", start)
blocks = re.findall(r"```bash\n(.*?)```", text[start:end], re.S)
if len(blocks) != 4:
    sys.exit("expected 4 bash blocks, found %d" % len(blocks))
pull = "docker compose -f docker-compose.prod.yml pull\n"
if blocks[0].count(pull) != 1:
    sys.exit("expected one pull line in the first block")
blocks[0] = blocks[0].replace(pull, "")
up = "docker compose -f docker-compose.prod.yml up -d\n"
for i in (2, 3):
    if blocks[i].count(up) != 1:
        sys.exit("expected one up -d line in block %d" % (i + 1))
    blocks[i] = blocks[i].replace(up, "docker compose -f docker-compose.prod.yml up -d app db-backup\n")
for i, b in enumerate(blocks, 1):
    open("%s/doc-block-%d.sh" % (out, i), "w").write(b)
PY
printf 'substitution a: "docker compose pull" is skipped, the image is the locally built :%s tag\n' "$TAG"
printf 'substitution b: the last "up -d" runs as "up -d app db-backup", Caddy needs a domain and ACME\n'

run_block() { # <n>: runs doc-block-<n>.sh in the stack folder, prints its output without secrets
  local rc=0
  (cd "$STACK" && $limit bash "$WORK/doc-block-$1.sh" > "$LOGS" 2>&1) || rc=$?
  redact < "$LOGS"
  return "$rc"
}

step "block 1: stop the API, run the job"
run_block 1 || true
job_rc="$(sed -n 's/^job exit code: //p' "$LOGS" | tail -1)"
[ "$job_rc" = "0" ] || fail "the job exit code is '${job_rc:-missing}', the docs say to carry on only at 0"

step "block 2: db-migrate --status"
status_rc=0
run_block 2 || status_rc=$?
printf 'db-migrate --status exit code: %s\n' "$status_rc"
case "$status_rc" in
  0) branch=3; printf 'branch: --status exited 0, so db-assert and up\n' ;;
  1) branch=4; printf 'branch: --status exited 1, so db-migrate, db-assert and up\n' ;;
  *) fail "db-migrate --status exited $status_rc, which the docs do not cover" ;;
esac
[ "$status_rc" = "$EXPECT_STATUS" ] || fail "--status exited $status_rc but this checkout's migrations imply $EXPECT_STATUS"

step "block $branch"
run_block "$branch" || fail "block $branch of the Upgrading section failed"

step "the volume is the same one"
volume_after="$(docker volume inspect -f '{{.Name}} {{.CreatedAt}}' "$VOLUME")" || fail "volume $VOLUME is gone"
[ "$volume_after" = "$volume_before" ] || fail "volume $VOLUME was replaced during the upgrade"

step "the API and db-backup are healthy on this checkout's image"
wait_healthy 240 app db-backup || fail "app or db-backup did not become healthy within 240 seconds"
running_image="$(docker inspect -f '{{.Config.Image}}' "$(dc ps -q app)")"
[ "$running_image" = "$IMAGE" ] || fail "the app runs '$running_image', not $IMAGE"

step "the app role runs the API and owns everything in public, and the owner job stays idempotent"
check_app_role_runtime

step "the 5.9.0 item reads back and a new one is written"
login
resp="$(api_call GET "/api/contents/$OLD_ITEM" "$TOKEN")" || fail "could not reach the API to read the old item"
[ "$(api_status "$resp")" = "200" ] || fail "the old item returned status $(api_status "$resp")"
api_body "$resp" | grep -qF "written on 5.9.0" || fail "the old item does not hold what 5.9.0 wrote"
NEW_ITEM="$(create_item "written after the upgrade")"
resp="$(api_call GET "/api/contents/$NEW_ITEM" "$TOKEN")" || fail "could not reach the API to read the new item"
api_body "$resp" | grep -qF "written after the upgrade" || fail "the new item does not read back"

printf '\nall checks passed\n'
