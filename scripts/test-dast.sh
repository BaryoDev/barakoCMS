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
if grep -q '"after the signed-in pass"' scripts/dast-scan.sh && grep -q 'api_call GET /api/auth/mfa/status' scripts/dast-scan.sh; then
  ok "dast-scan.sh checks the token on /api/auth/mfa/status after the pass"
else bad "dast-scan.sh checks the token on /api/auth/mfa/status after the pass"; fi

echo
echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
