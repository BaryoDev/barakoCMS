#!/usr/bin/env bash
# Post-deploy smoke test. Confirms a deployed barakoCMS is actually working, not just
# returning 200 on one path. Run after a deploy against the public API base URL.
#
#   scripts/smoke-test.sh https://dev-playground.baryo.dev/barakocms-api
#
# Tiers (each runs only if the previous can):
#   1. always      — /health returns 200 (app up + DB reachable) and /api/schemas returns 401
#                    (API layer routing, and still refusing anonymous callers)
#   2. SMOKE_USER/SMOKE_PASS set — login returns a token (auth works)
#   3. SMOKE_WRITE=1 (+ creds)   — create a content type with an email field, post a valid
#                                  entry (200) and a malformed one (400). Only enable where
#                                  writing test data is fine (dev-playground), never on the
#                                  public demo.
#
# Exits non-zero on the first failed check so a pipeline can gate on it.
set -euo pipefail

BASE="${1:?usage: smoke-test.sh <api-base-url>}"
FAIL=0
note() { echo "  -> $1"; }
check() { # description expected actual
  if [ "$2" = "$3" ]; then note "OK   $1 ($3)"; else note "FAIL $1 (expected $2, got $3)"; FAIL=1; fi
}

echo "== smoke: $BASE =="

# --- Tier 1: liveness + DB -------------------------------------------------
# /health runs every registered check, including the NpgSql one, so a 200 here covers both app-up
# and database-reachable on its own.
check "health 200"        200 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE/health" || echo 000)"

# 401, not 200, and on purpose. This asserts the API layer is routing: an unmapped route answers
# 404, so a 401 means FastEndpoints mapped it and the auth pipeline ran. It also fails if
# /api/schemas ever loses its role check, since that would answer 200.
check "api routes + refuses anon" 401 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE/api/schemas" || echo 000)"

# --- Tier 2: auth ----------------------------------------------------------
TOKEN=""
if [ -n "${SMOKE_USER:-}" ] && [ -n "${SMOKE_PASS:-}" ]; then
  TOKEN=$(curl -s --max-time 10 -X POST "$BASE/api/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"username\":\"${SMOKE_USER}\",\"password\":\"${SMOKE_PASS}\"}" \
    | python3 -c "import sys,json;print(json.load(sys.stdin).get('token',''))" 2>/dev/null || true)
  if [ -n "$TOKEN" ]; then note "OK   login returned a token"; else note "FAIL login returned no token"; FAIL=1; fi
fi

# --- Tier 3: write + validate (only where safe) ----------------------------
if [ "${SMOKE_WRITE:-0}" = "1" ] && [ -n "$TOKEN" ]; then
  TYPE="smoke$(date +%s)"
  auth=(-H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json')

  ct=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "${auth[@]}" -X POST "$BASE/api/content-types" \
    -d "{\"name\":\"$TYPE\",\"displayName\":\"Smoke $TYPE\",\"fields\":[{\"name\":\"Email\",\"displayName\":\"Email\",\"type\":\"email\",\"isRequired\":true,\"validationRules\":{}}]}")
  check "create content type" 200 "$ct"

  good=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "${auth[@]}" -X POST "$BASE/api/contents" \
    -d "{\"contentType\":\"$TYPE\",\"status\":1,\"sensitivity\":0,\"data\":{\"Email\":\"smoke@baryo.dev\"}}")
  check "valid entry accepted" 200 "$good"

  bad=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "${auth[@]}" -X POST "$BASE/api/contents" \
    -d "{\"contentType\":\"$TYPE\",\"status\":1,\"sensitivity\":0,\"data\":{\"Email\":\"not-an-email\"}}")
  check "malformed value rejected" 400 "$bad"
fi

if [ "$FAIL" = "0" ]; then echo "== smoke passed =="; else echo "== smoke FAILED ==" >&2; fi
exit "$FAIL"
