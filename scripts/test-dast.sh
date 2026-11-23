#!/usr/bin/env bash
# Tests the DAST summary and the rules the DAST workflow has to keep. Needs no Docker and no ZAP.
#
#   bash scripts/test-dast.sh
#
# The fixtures in scripts/testdata/dast/ carry distinctive strings in the alert name, plugin id, URL,
# parameter and evidence (zq-, 90999). The summary has to print counts and none of them, because the
# CI log of this repository is public.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

D=scripts/testdata/dast
# shellcheck source=scripts/lib-dast.sh
. scripts/lib-dast.sh
SUMMARY=scripts/dast-summary.py
pass=0
fail=0
ok() { echo "  ok    $1"; pass=$((pass + 1)); }
bad() { echo "  FAIL  $1"; fail=$((fail + 1)); }

out="" err="" rc=0
run() { # runs the summary with the arguments; sets out, err, rc
  local o e
  o=$(mktemp) e=$(mktemp)
  python3 -I "$SUMMARY" "$@" > "$o" 2> "$e"
  rc=$?
  out=$(cat "$o") err=$(cat "$e")
  rm -f "$o" "$e"
}
expect_rc() { if [ "$rc" = "$2" ]; then ok "$1"; else bad "$1 (exit $rc, wanted $2)"; fi; }
expect_has() { if printf '%s\n%s' "$out" "$err" | grep -qF -- "$2"; then ok "$1"; else bad "$1 (missing '$2')"; fi; }
expect_lacks() { # description, string
  if printf '%s\n%s' "$out" "$err" | grep -qF -- "$2"; then bad "$1 (printed '$2')"; else ok "$1"; fi
}

echo "== a High fails and prints counts only"
run "$D/high.json"
expect_rc "high: exits 1" 1
expect_has "high: prints the count" "High 1, Medium 2, Low 1, Informational 2"
expect_has "high: prints the fail line" "FAIL, 1 new High"
for secret in "Zq Distinct Alert Name" "90999" "zq-secret-path" "zq_param" "zq-evidence-token" "zq-description-text" "Zq Other Medium"; do
  expect_lacks "high: output has no '$secret'" "$secret"
done

echo "== an accepted High passes"
run "$D/high.json" --accepted "$D/accepted-high.tsv"
expect_rc "accepted: exits 0" 0
expect_has "accepted: the High is dropped from the count" "High 0, Medium 2, Low 1, Informational 2"
expect_has "accepted: says how many were dropped" "(1 accepted)"
expect_lacks "accepted: output has no plugin id" "90999"
run "$D/high.json" --accepted "$D/accepted-other.tsv"
expect_rc "accepted: another plugin id does not hide the High" 1

echo "== Medium and lower pass"
run "$D/medium-only.json"
expect_rc "medium only: exits 0" 0
expect_has "medium only: prints the counts" "medium-only.json: High 0, Medium 2, Low 1, Informational 2"
expect_has "medium only: prints the pass line" "pass, no new High"
run "$D/medium-only.json" "$D/high.json"
expect_rc "one High among two reports fails" 1

echo "== a scan that could not be read never passes"
run "$D/does-not-exist.json"
expect_rc "missing report: exits 2" 2
run "$D/empty.json"
expect_rc "empty report: exits 2" 2
run "$D/no-site.json"
expect_rc "report with no scanned site: exits 2" 2
run "$D/medium-only.json" "$D/does-not-exist.json"
expect_rc "one good and one missing report: exits 2" 2
run "$D/medium-only.json" --accepted "$D/does-not-exist.tsv"
expect_rc "missing accepted file: exits 2" 2
run "$D/medium-only.json" --accepted "$D/accepted-bad.tsv"
expect_rc "accepted file with a bad line: exits 2" 2
run
expect_rc "no report named: exits 2" 2
run "$D/high.json" --accepted "$D/accepted-high.tsv" "$D/empty.json"
expect_rc "an accepted High does not hide an unreadable report" 2

echo "== a report or accepted file that is not what it should be"
run "$D/not-utf8.json"
expect_rc "non-UTF-8 report: exits 2" 2
expect_has "non-UTF-8 report: says it is unreadable" "is missing or unreadable"
expect_lacks "non-UTF-8 report: no traceback" "Traceback"
run "$D/medium-only.json" --accepted "$D/accepted-not-utf8.tsv"
expect_rc "non-UTF-8 accepted file: exits 2" 2
expect_has "non-UTF-8 accepted file: says it could not be read" "could not be read"
expect_lacks "non-UTF-8 accepted file: no traceback" "Traceback"
run "$D/medium-only.json" --accepted "$D/accepted-unicode-digit.tsv"
expect_rc "accepted plugin id with a non-ASCII digit: exits 2" 2
run "$D/no-riskcode.json"
expect_rc "alert with no riskcode: exits 2" 2
run "$D/unknown-riskcode.json"
expect_rc "alert with an unknown riskcode: exits 2" 2
run "$D/text-riskcode.json"
expect_rc "alert with a non-numeric riskcode: exits 2" 2
run "$D/bool-riskcode.json"
expect_rc "alert with a boolean riskcode: exits 2" 2
run "$D/no-alerts-key.json"
expect_rc "site with no alerts key: exits 2" 2
run "$D/not-a-dict-alert.json"
expect_rc "alert that is not an object: exits 2" 2
run "$D/empty-alerts.json"
expect_rc "empty alert list passes the summary (the URL floor in dast-scan.sh guards it): exits 0" 0
expect_has "empty alert list: prints zero counts" "empty-alerts.json: High 0, Medium 0, Low 0, Informational 0"

echo "== the shipped accepted file parses and has no entries"
run "$D/medium-only.json" --accepted security/zap/accepted.tsv
expect_rc "security/zap/accepted.tsv parses" 0
if grep -vE '^[[:space:]]*(#|$)' security/zap/accepted.tsv | grep -q .; then
  bad "security/zap/accepted.tsv has entries"
else
  ok "security/zap/accepted.tsv has no entries"
fi

echo "== the decisions in scripts/lib-dast.sh"
yes_if() { if "${@:2}"; then ok "$1"; else bad "$1"; fi; }
no_if() { if "${@:2}"; then bad "$1"; else ok "$1"; fi; }
for code in 0 1 2; do
  yes_if "ZAP exit $code is a finished scan" dast_zap_finished "$code"
done
for code in 3 4 137 "" abc; do
  no_if "ZAP exit '$code' is a failure" dast_zap_finished "$code"
done
tmp=$(mktemp -d)
printf 'Total of 253 URLs\nsomething else\n' > "$tmp/ok.log"
printf 'Total of 12 URLs\nTotal of 253 URLs\n' > "$tmp/two.log"
printf 'WARN: no URLs found\n' > "$tmp/none.log"
printf 'Total of many URLs\n' > "$tmp/text.log"
if [ "$(dast_url_count "$tmp/ok.log")" = 253 ]; then ok "URL count: reads the number from ZAP's line"; else bad "URL count: reads the number from ZAP's line"; fi
if [ "$(dast_url_count "$tmp/two.log")" = 253 ]; then ok "URL count: takes the last line"; else bad "URL count: takes the last line"; fi
no_if "URL count: a log with no count fails" dast_url_count "$tmp/none.log"
no_if "URL count: a non-numeric count fails" dast_url_count "$tmp/text.log"
no_if "URL count: a missing log fails" dast_url_count "$tmp/missing.log"
yes_if "URL floor: 253 against 100 passes" dast_url_floor_ok 253 100
yes_if "URL floor: equal passes" dast_url_floor_ok 100 100
no_if "URL floor: a scan that reached nothing fails" dast_url_floor_ok 0 100
no_if "URL floor: just under fails" dast_url_floor_ok 99 100
no_if "URL floor: an empty count fails" dast_url_floor_ok "" 100
no_if "URL floor: a non-numeric floor fails" dast_url_floor_ok 5 abc
if [ "$(dast_status_class 200)" = 2xx ] && [ "$(dast_status_class 401)" = 4xx ] && [ "$(dast_status_class 503)" = 5xx ]; then
  ok "status class: prints the class only"
else bad "status class: prints the class only"; fi
if [ "$(dast_status_class abc)" = none ] && [ "$(dast_status_class "")" = none ]; then ok "status class: a non-status is none"; else bad "status class: a non-status is none"; fi
if [ "$(dast_token_state 200)" = ok ] && dast_token_state 204 > /dev/null; then ok "token state: 2xx is ok"; else bad "token state: 2xx is ok"; fi
state=$(dast_token_state 401); trc=$?
if [ "$state" = expired ] && [ "$trc" != 0 ]; then ok "token state: 401 is expired and not ok"; else bad "token state: 401 is expired and not ok"; fi
state=$(dast_token_state 500); trc=$?
if [ "$state" = failed ] && [ "$trc" != 0 ]; then ok "token state: 500 is failed and not ok"; else bad "token state: 500 is failed and not ok"; fi
state=$(dast_token_state 429); trc=$?
if [ "$state" = limited ] && [ "$trc" != 0 ]; then ok "token state: 429 is limited and not ok"; else bad "token state: 429 is limited and not ok"; fi
if [ "$(printf 'HTTP/1.1 429 Too Many Requests\r\nRetry-After: 12\r\n\r\n' | dast_retry_after)" = 12 ]; then ok "retry after: reads the seconds from the header"; else bad "retry after: reads the seconds from the header"; fi
if [ "$(printf 'retry-after: 900\r\n' | dast_retry_after)" = 60 ]; then ok "retry after: caps at 60"; else bad "retry after: caps at 60"; fi
if [ "$(printf 'HTTP/1.1 429 Too Many Requests\r\n\r\n' | dast_retry_after)" = 5 ]; then ok "retry after: a missing header is 5"; else bad "retry after: a missing header is 5"; fi
if [ "$(printf 'Retry-After: Wed, 21 Oct 2026 07:28:00 GMT\r\n' | dast_retry_after)" = 5 ]; then ok "retry after: a date instead of seconds is 5"; else bad "retry after: a date instead of seconds is 5"; fi
if [ "$(printf 'Retry-After: 0\r\n' | dast_retry_after)" = 1 ]; then ok "retry after: 0 is raised to 1"; else bad "retry after: 0 is raised to 1"; fi
if [ "$(printf 'Retry-After: 99999999999999999999\r\n' | dast_retry_after)" = 60 ]; then ok "retry after: a 20-digit value is 60"; else bad "retry after: a 20-digit value is 60"; fi
state=$(dast_token_state ""); trc=$?
if [ "$state" = failed ] && [ "$trc" != 0 ]; then ok "token state: no status is failed"; else bad "token state: no status is failed"; fi
work="$tmp/work"; mkdir -p "$work"
dast_choose_report_dir "" "$work"; crc=$?
if [ "$crc" = 0 ] && [ "$REPORT_CREATED" = 1 ] && [ -d "$REPORT_DIR" ]; then ok "report folder: the default is made here"; else bad "report folder: the default is made here"; fi
case "$REPORT_DIR/" in "$work/"*) bad "report folder: the default is outside the work folder" ;; *) ok "report folder: the default is outside the work folder" ;; esac
rmdir "$REPORT_DIR"
dast_choose_report_dir "$tmp/new" "$work"
if [ "$REPORT_CREATED" = 1 ] && [ -d "$tmp/new" ]; then ok "report folder: a named folder that is new counts as created here"; else bad "report folder: a named folder that is new counts as created here"; fi
mkdir -p "$tmp/theirs"
dast_choose_report_dir "$tmp/theirs" "$work"
if [ "$REPORT_CREATED" = 0 ] && [ "$REPORT_DIR" = "$tmp/theirs" ]; then ok "report folder: a folder the caller made is not counted as created here"; else bad "report folder: a folder the caller made is not counted as created here"; fi
no_if "report folder: a path under the work folder is refused" dast_choose_report_dir "$work/reports" "$work"
yes_if "delete rule: CI deletes a folder it made" dast_delete_reports 1 1
no_if "delete rule: CI keeps a folder the caller made" dast_delete_reports 1 0
no_if "delete rule: a developer machine keeps the reports" dast_delete_reports 0 1
printf 'check one\n  -> FAIL x\ncheck two\n  -> FAIL y\n  -> ok z\n' > "$tmp/smoke.log"
if [ "$(dast_smoke_failures "$tmp/smoke.log")" = 2 ]; then ok "smoke failures: counts the failed checks"; else bad "smoke failures: counts the failed checks"; fi
: > "$tmp/clean.log"
if [ "$(dast_smoke_failures "$tmp/clean.log")" = 0 ]; then ok "smoke failures: none is zero"; else bad "smoke failures: none is zero"; fi
rm -rf "${tmp:?}"

echo "== the signed-in spec filter"
FILTER=scripts/dast-spec-filter.py
ftmp=$(mktemp -d)
filter_run() { # filter_run <spec> <skip> <out>: sets out, rc
  local o
  o=$(mktemp)
  python3 -I "$FILTER" "$1" "$2" "$3" > "$o" 2>&1
  rc=$?
  out=$(cat "$o") err=""
  rm -f "$o"
}
# spec_has <file> <path> [method]: 0 when the spec has the path (and the method on it).
spec_has() {
  python3 -I -c '
import json, sys
paths = json.load(open(sys.argv[1]))["paths"]
sys.exit(0 if sys.argv[2] in paths and (len(sys.argv) < 4 or sys.argv[3] in paths[sys.argv[2]]) else 1)
' "$@"
}
filter_run "$D/filter-spec.json" "$D/filter-skip.tsv" "$ftmp/out.json"
expect_rc "filter: a good skip file exits 0" 0
if spec_has "$ftmp/out.json" /api/zq-a post; then bad "filter: an entry removed"; else ok "filter: an entry removed"; fi
if spec_has "$ftmp/out.json" "/api/zq-b/{id}"; then bad "filter: a path with no operation left is removed"; else ok "filter: a path with no operation left is removed"; fi
if spec_has "$ftmp/out.json" /api/zq-a get; then ok "filter: the other methods on the same path are kept"; else bad "filter: the other methods on the same path are kept"; fi
if spec_has "$ftmp/out.json" /api/zq-c get; then ok "filter: a path that is not named is kept"; else bad "filter: a path that is not named is kept"; fi
expect_has "filter: prints a count" "2 operations left out"
expect_lacks "filter: prints no route" "zq-"
filter_run "$D/filter-spec.json" "$D/filter-skip-stale.tsv" "$ftmp/stale.json"
expect_rc "filter: a stale entry exits 2" 2
expect_has "filter: a stale entry says which line is not in the spec" "is not an operation in the spec"
expect_lacks "filter: a stale entry prints no route" "zq-renamed"
if [ -e "$ftmp/stale.json" ]; then bad "filter: a failed run writes no copy"; else ok "filter: a failed run writes no copy"; fi
filter_run "$D/filter-spec.json" "$D/filter-skip-wrong-method.tsv" "$ftmp/wrong.json"
expect_rc "filter: a method the path does not have exits 2" 2
filter_run "$D/filter-garbage.json" "$D/filter-skip.tsv" "$ftmp/garbage.json"
expect_rc "filter: a spec that is not JSON exits 2" 2
filter_run "$D/filter-spec-not-utf8.json" "$D/filter-skip.tsv" "$ftmp/utf.json"
expect_rc "filter: a non-UTF-8 spec exits 2" 2
expect_lacks "filter: a non-UTF-8 spec gives no traceback" "Traceback"
filter_run "$D/filter-no-paths.json" "$D/filter-skip.tsv" "$ftmp/nopaths.json"
expect_rc "filter: a spec with no paths exits 2" 2
filter_run "$D/does-not-exist.json" "$D/filter-skip.tsv" "$ftmp/missing.json"
expect_rc "filter: a missing spec exits 2" 2
filter_run "$D/filter-spec.json" "$D/does-not-exist.tsv" "$ftmp/missing.json"
expect_rc "filter: a missing skip file exits 2" 2
filter_run "$D/filter-spec.json" "$D/filter-skip-bad.tsv" "$ftmp/bad.json"
expect_rc "filter: a malformed skip line exits 2" 2
filter_run openapi/baseline.json security/zap/signed-in-skip.tsv "$ftmp/shipped.json"
expect_rc "filter: the shipped skip file applies to openapi/baseline.json" 0
if spec_has "$ftmp/shipped.json" /api/auth/logout; then bad "filter: the shipped copy has no logout"; else ok "filter: the shipped copy has no logout"; fi
if spec_has "$ftmp/shipped.json" /api/auth/login post; then ok "filter: the shipped copy keeps login"; else bad "filter: the shipped copy keeps login"; fi
rm -rf "${ftmp:?}"

echo "== every route that ends a token, a session or a user is skipped or listed"
SKIP=security/zap/signed-in-skip.tsv
# Routes that call a revocation function and are not in the skip file, with why they stay in the
# signed-in pass. METHOD path|reason.
NOT_SKIPPED=(
  "POST /api/auth/refresh|anonymous route: reads the refresh cookie, which ZAP does not send, and ignores the bearer"
  "POST /api/me/sessions/revoke-others|ends the user's other sessions and keeps the one the bearer belongs to"
  "POST /api/users/{userId}/block|admin only: needs ManageUsers or SuperAdmin, a User-role token is refused"
  "POST /api/users/{userId}/unblock|admin only: needs ManageUsers or SuperAdmin"
  "POST /api/users/{userId}/mfa/reset|admin only: needs ManageUsers or SuperAdmin"
  "POST /api/users/{userId}/password|admin only: needs ManageUsers or SuperAdmin"
  "DELETE /api/users/{id}/sessions|admin only: needs ManageUsers or SuperAdmin"
  "DELETE /api/users/{id}/sessions/{sessionId}|admin only: needs ManageUsers or SuperAdmin"
)
revocation_pattern='RevokeTokenAsync|RevokeAllUserTokensAsync|SessionRevocation\.EndAsync|RevokeRefreshTokens\.ForUserAsync|SessionRoutes\.(EndAsync|EndAllAsync)|ISessionEpochService'
# declared_routes: METHOD path for every route a file that calls a revocation function declares.
declared_routes() {
  grep -rlE "$revocation_pattern" barakoCMS --include='*.cs' | while read -r f; do
    grep -oE '^[[:space:]]*(Get|Post|Put|Delete|Patch)\("/[^"]*"' "$f" | sed -E 's/^[[:space:]]*//; s/\("/ /; s/"$//'
  done | awk '{print toupper($1) " " $2}' | sort -u
}
skipped_routes() { grep -vE '^[[:space:]]*(#|$)' "$SKIP" | awk -F'\t' '{print $1 " " $2}' | sort -u; }
listed_routes() { for entry in "${NOT_SKIPPED[@]}"; do printf '%s\n' "${entry%%|*}"; done | sort -u; }
declared=$(declared_routes)
# A route file that ends sessions but declares its route some other way (Verbs, a constant, another file)
# would drop out of the search silently, so every such file under Features must yield a route.
unrouted=0
while read -r f; do
  grep -qE '^[[:space:]]*(Get|Post|Put|Delete|Patch)\("/[^"]*"' "$f" || unrouted=$((unrouted + 1))
done < <(grep -rlE "$revocation_pattern" barakoCMS/Features --include='*.cs')
if [ "$unrouted" -eq 0 ]; then
  ok "guard: every Features file that ends sessions declares its route where the search reads it"
else
  bad "guard: every Features file that ends sessions declares its route where the search reads it ($unrouted not)"
fi
if [ -n "$declared" ] && [ "$(printf '%s\n' "$declared" | wc -l)" -ge 10 ]; then
  ok "guard: the search finds the routes that call a revocation function"
else bad "guard: the search finds the routes that call a revocation function"; fi
missing=$(comm -23 <(printf '%s\n' "$declared") <(sort -u <(skipped_routes) <(listed_routes)))
if [ -z "$missing" ]; then
  ok "guard: every route that calls a revocation function is skipped or listed with a reason"
else
  bad "guard: every route that calls a revocation function is skipped or listed with a reason ($(printf '%s\n' "$missing" | wc -l) not)"
fi
stale=$(comm -13 <(printf '%s\n' "$declared") <(listed_routes))
if [ -z "$stale" ]; then ok "guard: every listed route still calls a revocation function"; else bad "guard: every listed route still calls a revocation function"; fi
both=$(comm -12 <(skipped_routes) <(listed_routes))
if [ -z "$both" ]; then ok "guard: no route is both skipped and listed as kept"; else bad "guard: no route is both skipped and listed as kept"; fi

echo "== workflow rules"
# workflow_problems <file>: prints one line per broken rule.
workflow_problems() {
  local f=$1 top tmpdir
  if grep -qE 'upload-artifact' "$f"; then echo "uses upload-artifact"; fi
  if grep -qE 'secrets\.' "$f"; then echo "uses a repository secret"; fi
  # The block after a permissions: at column 0 is exactly contents: read.
  top=$(awk '/^permissions:/ {p=1; next} p && /^[^[:space:]]/ {p=0} p && NF {print}' "$f" | sed 's/^[[:space:]]*//')
  if [ "$top" != "contents: read" ]; then echo "top level permissions are not only contents: read"; fi
  # No job asks for more: any other permissions: key is indented.
  if grep -qE '^[[:space:]]+permissions:' "$f"; then echo "a job sets its own permissions"; fi
  # Every action by full sha.
  tmpdir=$(mktemp -d)
  cp "$f" "$tmpdir/w.yml"
  if ! bash scripts/check-action-pins.sh "$tmpdir" > /dev/null 2>&1; then echo "an action is not pinned by a full sha"; fi
  if ! grep -qE '^[[:space:]-]*uses:' "$f"; then echo "no action found, so the pin check proved nothing"; fi
  rm -rf "${tmpdir:?}"
  # The ZAP image by version and digest wherever the file names it.
  if grep -E 'zaproxy/zaproxy' "$f" | grep -vqE 'zaproxy/zaproxy:[0-9][0-9.]*@sha256:[0-9a-f]{64}'; then
    echo "the ZAP image is not pinned by version and digest"
  fi
  return 0
}
problems=$(workflow_problems .github/workflows/dast.yml)
if [ -z "$problems" ]; then ok "dast.yml keeps every rule"; else bad "dast.yml: $problems"; fi
problems=$(workflow_problems "$D/bad-workflow.yml")
for want in "uses upload-artifact" "top level permissions are not only contents: read" \
  "a job sets its own permissions" "an action is not pinned by a full sha" \
  "the ZAP image is not pinned by version and digest"; do
  if printf '%s\n' "$problems" | grep -qxF "$want"; then ok "bad workflow is caught: $want"; else bad "bad workflow not caught: $want"; fi
done

echo "== the scan script"
if grep -qE '(^|[[:space:]])set[[:space:]]+-[a-zA-Z]*x|xtrace|BASH_XTRACEFD|bash[[:space:]]+-[a-z]*x' scripts/dast-scan.sh; then
  bad "dast-scan.sh enables xtrace"
else
  ok "dast-scan.sh never enables xtrace"
fi
if grep -E 'zaproxy/zaproxy' scripts/dast-scan.sh | grep -vE '^[[:space:]]*#' | grep -vqE 'zaproxy/zaproxy:[0-9][0-9.]*@sha256:[0-9a-f]{64}'; then
  bad "dast-scan.sh names the ZAP image without a digest"
else
  ok "dast-scan.sh pins the ZAP image by version and digest"
fi
if grep -qE 'zaproxy/zaproxy:[0-9][0-9.]*@sha256:[0-9a-f]{64}' scripts/dast-scan.sh; then
  ok "dast-scan.sh names the ZAP image"
else
  bad "dast-scan.sh does not name the ZAP image"
fi
if grep -q '::add-mask::' scripts/dast-scan.sh; then ok "dast-scan.sh masks secrets in Actions"; else bad "dast-scan.sh does not mask secrets"; fi

# The decisions live in lib-dast.sh and the script has to call them, in an order that keeps the rules.
for fn in dast_zap_finished dast_url_count dast_url_floor_ok dast_token_state dast_status_class \
  dast_choose_report_dir dast_delete_reports dast_smoke_failures; do
  if grep -vE '^[[:space:]]*#' scripts/dast-scan.sh | grep -q "$fn"; then ok "dast-scan.sh calls $fn"; else bad "dast-scan.sh does not call $fn"; fi
done
if grep -qE '^[[:space:]]*\. scripts/lib-dast\.sh' scripts/dast-scan.sh; then ok "dast-scan.sh sources lib-dast.sh"; else bad "dast-scan.sh does not source lib-dast.sh"; fi
mask_line=$(grep -nE '^mask_all ' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
docker_line=$(grep -nE '^[^#]*\bdocker ' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
if [ -n "$mask_line" ] && [ -n "$docker_line" ] && [ "$mask_line" -lt "$docker_line" ]; then
  ok "dast-scan.sh masks secrets before the first docker call"
else bad "dast-scan.sh masks secrets before the first docker call"; fi
trap_line=$(grep -nE '^trap cleanup EXIT' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
tag_line=$(grep -nE '^docker tag ' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
env_line=$(grep -nE '^cat .env.prod.example' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
if [ -n "$trap_line" ] && [ -n "$tag_line" ] && [ -n "$env_line" ] && [ "$trap_line" -lt "$tag_line" ] && [ "$trap_line" -lt "$env_line" ]; then
  ok "dast-scan.sh installs the cleanup trap before the tag and the env file"
else bad "dast-scan.sh installs the cleanup trap before the tag and the env file"; fi
# shellcheck disable=SC2016 # the patterns are literal text
if grep -q 'dast_zap_finished "\$rc"' scripts/dast-scan.sh && grep -q '|| rc=\$?' scripts/dast-scan.sh; then
  ok "dast-scan.sh keeps ZAP's exit code and judges it"
else bad "dast-scan.sh keeps ZAP's exit code and judges it"; fi
if grep -q '"after the signed-in pass"' scripts/dast-scan.sh && grep -qF ':8080/api/me"' scripts/dast-scan.sh; then
  ok "dast-scan.sh checks the token on /api/me after the pass"
else bad "dast-scan.sh checks the token on /api/me after the pass"; fi
# shellcheck disable=SC2016 # the patterns are literal text
if grep -q 'dast_retry_after' scripts/dast-scan.sh && grep -qF 'sleep "$wait"' scripts/dast-scan.sh; then
  ok "dast-scan.sh retries a limited token check once"
else bad "dast-scan.sh retries a limited token check once"; fi
# shellcheck disable=SC2016 # the patterns are literal text
if grep -qF 'run_zap authenticated "$AUTH_MAX" openapi-signed-in.json' scripts/dast-scan.sh \
  && grep -qF 'run_zap anonymous "$MAX" openapi.json' scripts/dast-scan.sh; then
  ok "dast-scan.sh runs the signed-in pass on the filtered spec and the anonymous pass on the whole one"
else bad "dast-scan.sh runs the signed-in pass on the filtered spec and the anonymous pass on the whole one"; fi
filter_line=$(grep -nE '^python3 -I scripts/dast-spec-filter.py' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
auth_line=$(grep -nE '^run_zap authenticated' scripts/dast-scan.sh | head -n 1 | cut -d: -f1)
if [ -n "$filter_line" ] && [ -n "$auth_line" ] && [ "$filter_line" -lt "$auth_line" ]; then
  ok "dast-scan.sh builds the signed-in spec before the signed-in pass"
else bad "dast-scan.sh builds the signed-in spec before the signed-in pass"; fi
if grep -qF 'the ZAP work folder could not be made' scripts/dast-scan.sh; then
  ok "dast-scan.sh stops when the ZAP work folder cannot be made"
else bad "dast-scan.sh stops when the ZAP work folder cannot be made"; fi
if grep -qF 'RateLimiting__Global__PermitLimit=' scripts/dast-scan.sh; then
  ok "dast-scan.sh raises the global rate limit for the scan stack"
else bad "dast-scan.sh raises the global rate limit for the scan stack"; fi

echo
echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
