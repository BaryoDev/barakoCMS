#!/usr/bin/env bash
# Tests the vulnerability gates themselves.
#
# A security gate has one failure mode that matters more than the rest: passing when it did not
# actually check. The .NET gate had it: it ran `jq -e` inside an `if`, so an unreadable report made
# the condition false and the build went green having verified nothing. (The npm gate had the same
# fault; it left with the console, and barakoBrew carries it and its test now.)
#
# These cases pin the behaviour: a clean report passes, findings fail, and anything unreadable fails
# closed. Run by CI, so the gates cannot silently regress to fail-open.
#
#   bash scripts/test-ci-gates.sh

set -uo pipefail

pass=0
fail=0

check() { # $1 = description, $2 = expected (pass|fail), $3 = actual exit code
  local want_zero=0
  [ "$2" = "pass" ] && want_zero=1
  if { [ "$want_zero" = 1 ] && [ "$3" = 0 ]; } || { [ "$want_zero" = 0 ] && [ "$3" != 0 ]; }; then
    echo "  ok    $1 (expected $2)"
    pass=$((pass + 1))
  else
    echo "  FAIL  $1 — expected $2, exit was $3"
    fail=$((fail + 1))
  fi
}

# Mirrors the .NET gate body in .github/workflows/ci.yml.
dotnet_gate() {
  local rep=$1 FOUND
  jq -e '(.projects | type) == "array"
         and all(.projects[]; (.frameworks // []) | type == "array")' "$rep" > /dev/null 2>&1 || return 1
  FOUND=$(jq '[.projects[].frameworks[]? // empty
               | (.topLevelPackages // []) + (.transitivePackages // [])
               | .[] | .vulnerabilities[]?
               | select(.severity == "Critical" or .severity == "High")] | length' "$rep")
  case "$FOUND" in ''|*[!0-9]*) return 1 ;; esac
  [ "$FOUND" -gt 0 ] && return 1
  return 0
}

d=$(mktemp -d)
trap 'rm -rf "$d"' EXIT

echo '{"projects":[]}'                                                   > "$d/net-clean.json"
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[{"severity":"High"}]}]}]}]}'      > "$d/net-high.json"
echo '{"projects":[{"frameworks":[{"transitivePackages":[{"vulnerabilities":[{"severity":"Critical"}]}]}]}]}' > "$d/net-crit.json"
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[{"severity":"Moderate"}]}]}]}]}'  > "$d/net-moderate.json"
echo '{"projects":{"project-a":{"frameworks":[]}}}'                       > "$d/net-objshape.json"
echo '{"projects":[{"frameworks":{"net8.0":{}}}]}'                       > "$d/net-objframeworks.json"
echo 'MSBuild error, no json here'                                       > "$d/net-garbage.json"
: > "$d/net-empty.json"

echo ".NET gate:"
dotnet_gate "$d/net-clean.json";    check "clean report"       pass $?
dotnet_gate "$d/net-high.json";     check "high findings"      fail $?
dotnet_gate "$d/net-crit.json";     check "critical, nested"   fail $?
dotnet_gate "$d/net-moderate.json"; check "moderate only"      pass $?
dotnet_gate "$d/net-objshape.json"; check "projects is an object"      fail $?
dotnet_gate "$d/net-objframeworks.json"; check "frameworks is an object" fail $?
dotnet_gate "$d/net-garbage.json";  check "unparseable output" fail $?
dotnet_gate "$d/net-empty.json";    check "empty report"       fail $?

# The shared gate, scripts/vuln-gate.sh, is run as it is. Reports are planted, and "today" is passed
# in so an expiry case does not depend on the date the suite runs.
here=$(cd "$(dirname "$0")" && pwd)
gate="$here/vuln-gate.sh"
today=2026-11-22

# One High with a fix, one Critical with none, one Low with a fix.
cat > "$d/img-high.json" <<'JSON'
{"SchemaVersion":2,"Results":[{"Target":"image","Vulnerabilities":[
  {"VulnerabilityID":"CVE-2099-0001","PkgName":"libfake","Severity":"HIGH","FixedVersion":"1.2.4"},
  {"VulnerabilityID":"CVE-2099-0002","PkgName":"libnofix","Severity":"CRITICAL"},
  {"VulnerabilityID":"CVE-2099-0003","PkgName":"libminor","Severity":"LOW","FixedVersion":"2.0"}]}]}
JSON
# Only the unfixed Critical and the Low.
cat > "$d/img-unfixed.json" <<'JSON'
{"SchemaVersion":2,"Results":[{"Target":"image","Vulnerabilities":[
  {"VulnerabilityID":"CVE-2099-0002","PkgName":"libnofix","Severity":"CRITICAL"},
  {"VulnerabilityID":"CVE-2099-0003","PkgName":"libminor","Severity":"LOW","FixedVersion":"2.0"}]}]}
JSON
echo '{"SchemaVersion":2}'                      > "$d/img-clean.json"
echo '{"SchemaVersion":2,"Results":{"a":1}}'    > "$d/img-objshape.json"
echo 'trivy crashed, no json here'              > "$d/img-garbage.json"
: > "$d/img-empty.json"

echo '[]' > "$d/allow-none.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"no path to it at run time","expires":"2026-12-31"}]' > "$d/allow-ok.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"no path to it at run time","expires":"2026-11-01"}]' > "$d/allow-expired.json"
# Expired, and nothing in the report matches it.
echo '[{"id":"CVE-2099-7777","package":"libgone","reason":"was accepted once","expires":"2026-11-01"}]' > "$d/allow-expired-unmatched.json"
echo '[{"id":"CVE-2099-0001","package":"otherpkg","reason":"wrong package","expires":"2026-12-31"}]' > "$d/allow-wrong-pkg.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","expires":"2026-12-31"}]' > "$d/allow-no-reason.json"
echo 'not json'                                > "$d/allow-garbage.json"

echo
echo "Image gate (vuln-gate.sh trivy):"
bash "$gate" trivy "$d/img-high.json" "$d/allow-none.json" $today > /dev/null;            check "High with a fix"                    fail $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-ok.json" $today > /dev/null;              check "same finding allowlisted"           pass $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-expired.json" $today > /dev/null;         check "allowlist entry expired"            fail $?
bash "$gate" trivy "$d/img-clean.json" "$d/allow-expired-unmatched.json" $today > /dev/null; check "expired entry, nothing matches it" fail $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-wrong-pkg.json" $today > /dev/null;       check "allowlisted for another package"    fail $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-no-reason.json" $today > /dev/null;       check "entry with no reason"               fail $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-garbage.json" $today > /dev/null;         check "allowlist is not JSON"              fail $?
bash "$gate" trivy "$d/img-high.json" "$d/missing.json" $today > /dev/null;               check "allowlist file missing"             fail $?
bash "$gate" trivy "$d/img-unfixed.json" "$d/allow-none.json" $today > /dev/null;         check "unfixed Critical and a Low"         pass $?
bash "$gate" trivy "$d/img-clean.json" "$d/allow-none.json" $today > /dev/null;           check "clean report"                       pass $?
bash "$gate" trivy "$d/img-objshape.json" "$d/allow-none.json" $today > /dev/null;        check "Results is an object"               fail $?
bash "$gate" trivy "$d/img-garbage.json" "$d/allow-none.json" $today > /dev/null;         check "unparseable output"                 fail $?
bash "$gate" trivy "$d/img-empty.json" "$d/allow-none.json" $today > /dev/null;           check "empty report"                       fail $?

# Writes SARIF for the findings that failed, and only those.
VULN_GATE_SARIF="$d/out.sarif" bash "$gate" trivy "$d/img-high.json" "$d/allow-none.json" $today > /dev/null
jq -e '(.runs[0].results | length) == 1 and .runs[0].results[0].ruleId == "CVE-2099-0001"' "$d/out.sarif" > /dev/null 2>&1
check "SARIF holds the failing finding only" pass $?

echo
echo "NuGet findings through the shared gate and allowlist:"
cat > "$d/net-ghsa.json" <<'JSON'
{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Fake.Package","vulnerabilities":[
  {"severity":"High","advisoryurl":"https://github.com/advisories/GHSA-fake-fake-fake"}]}]}]}]}
JSON
echo '[{"id":"GHSA-fake-fake-fake","package":"Fake.Package","reason":"not reachable","expires":"2026-12-31"}]' > "$d/allow-net.json"
bash "$gate" dotnet "$d/net-ghsa.json" "$d/allow-none.json" $today > /dev/null;  check "High advisory"                    fail $?
bash "$gate" dotnet "$d/net-ghsa.json" "$d/allow-net.json" $today > /dev/null;   check "same advisory allowlisted"        pass $?
bash "$gate" dotnet "$d/net-ghsa.json" "$d/allow-net.json" 2027-01-15 > /dev/null; check "same entry after its expiry"    fail $?
bash "$gate" dotnet "$d/net-moderate.json" "$d/allow-none.json" $today > /dev/null; check "Moderate only"                 pass $?
bash "$gate" dotnet "$d/net-garbage.json" "$d/allow-none.json" $today > /dev/null;  check "unparseable output"            fail $?
bash "$gate" nonsense "$d/net-ghsa.json" "$d/allow-none.json" $today > /dev/null 2>&1; check "unknown report kind"        fail $?

echo
echo "The committed allowlist:"
bash "$gate" trivy "$d/img-clean.json" "$here/../.github/vuln-allowlist.json" $today > /dev/null
check "parses, and holds no expired entry" pass $?

echo
echo "Action pins (check-action-pins.sh):"
sha=3d3c42e5aac5ba805825da76410c181273ba90b1
mkdir -p "$d/wf-good" "$d/wf-tag" "$d/wf-branch" "$d/wf-short" "$d/wf-docker" "$d/wf-none"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - uses: ./.github/actions/local\n' "$sha" > "$d/wf-good/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - uses: actions/setup-dotnet@v6\n' "$sha" > "$d/wf-tag/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@main\n' > "$d/wf-branch/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@3d3c42e\n' > "$d/wf-short/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: docker://alpine:3.20\n' > "$d/wf-docker/a.yml"
printf 'name: nothing\n' > "$d/wf-none/a.yml"
bash "$here/check-action-pins.sh" "$d/wf-good" > /dev/null;   check "every action pinned by SHA"      pass $?
bash "$here/check-action-pins.sh" "$d/wf-tag" > /dev/null;    check "one action pinned by a tag"      fail $?
bash "$here/check-action-pins.sh" "$d/wf-branch" > /dev/null; check "action pinned by a branch"       fail $?
bash "$here/check-action-pins.sh" "$d/wf-short" > /dev/null;  check "abbreviated SHA"                 fail $?
bash "$here/check-action-pins.sh" "$d/wf-docker" > /dev/null; check "docker:// without a digest"      fail $?
bash "$here/check-action-pins.sh" "$d/wf-none" > /dev/null;   check "no uses lines at all"            fail $?
bash "$here/check-action-pins.sh" "$here/../.github/workflows" > /dev/null; check "this repository's workflows" pass $?

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
