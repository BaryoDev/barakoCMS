#!/usr/bin/env bash
# Post-deploy security smoke test. Confirms the DEPLOYED image still has the hardening the suite
# asserts in-process, so a build or config regression that weakens the running service is caught
# against the real URL, not only in the test project.
#
#   scripts/security-smoke.sh https://dev-playground.baryo.dev/barakocms-api
#
# No writes, no credentials. Every check is a property that must hold for an anonymous caller.
# The deep authorization properties (Admin cannot self-grant SuperAdmin, tenant admins cannot read
# another tenant's audit log, the full capability matrix) are proven against a real stack by the
# integration tests in BarakoCMS.Tests; this script guards the HTTP surface of the shipped image.
#
# Exits non-zero on the first failed check so a pipeline can gate on it.
set -uo pipefail

BASE="${1:?usage: security-smoke.sh <api-base-url>}"
FAIL=0
note() { echo "  -> $1"; }
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
check() { if [ "$2" = "$3" ]; then note "OK   $1 ($3)"; else note "FAIL $1 (expected $2, got $3)"; FAIL=1; fi; }

echo "== security smoke: $BASE =="

# --- security headers on every response, including /health ------------------
hdrs=$(curl -s -D - -o /dev/null "$BASE/health")
for h in "x-content-type-options: nosniff" "x-frame-options: DENY" "content-security-policy" "referrer-policy"; do
  if printf '%s' "$hdrs" | grep -iq "$h"; then note "OK   header present ($h)"; else note "FAIL header missing ($h)"; FAIL=1; fi
done

# --- Swagger / API explorer must be closed in Production --------------------
for p in /swagger /swagger/index.html /swagger/v1/swagger.json /openapi/v1.json; do
  check "swagger closed $p" 404 "$(code "$BASE$p")"
done

# --- config and source paths must not be served ----------------------------
for p in /.env /appsettings.json /appsettings.Production.json /.git/config /web.config; do
  check "config path not served $p" 404 "$(code "$BASE$p")"
done

# --- anonymous callers get 401 on protected endpoints -----------------------
for p in /api/users /api/audit /api/roles /api/capabilities /api/api-keys /api/content-types; do
  check "protected requires auth $p" 401 "$(code "$BASE$p")"
done

# --- a forged alg:none token is rejected ------------------------------------
# Built at runtime rather than hard-coded, so there is no JWT literal in the repo for a secret
# scanner to flag. It is an unsigned token (empty third segment) claiming SuperAdmin; the server
# must refuse it because it validates the signing key.
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
none_hdr=$(printf '%s' '{"alg":"none","typ":"JWT"}' | b64url)
none_claims=$(printf '%s' '{"UserId":"00000000-0000-0000-0000-000000000001","http://schemas.microsoft.com/ws/2008/06/identity/claims/role":"SuperAdmin"}' | b64url)
none_tok="${none_hdr}.${none_claims}."
check "alg:none rejected on /api/users" 401 "$(code -H "Authorization: Bearer $none_tok" "$BASE/api/users")"
check "alg:none rejected on /api/audit" 401 "$(code -H "Authorization: Bearer $none_tok" "$BASE/api/audit")"

echo
if [ "$FAIL" -eq 0 ]; then echo "security smoke: all checks passed"; else echo "security smoke: FAILURES above"; fi
exit "$FAIL"
