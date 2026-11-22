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

echo "== the shipped accepted file parses and has no entries"
run "$D/medium-only.json" --accepted security/zap/accepted.tsv
expect_rc "security/zap/accepted.tsv parses" 0
if grep -vE '^[[:space:]]*(#|$)' security/zap/accepted.tsv | grep -q .; then
  bad "security/zap/accepted.tsv has entries"
else
  ok "security/zap/accepted.tsv has no entries"
fi

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

echo
echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
