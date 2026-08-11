#!/usr/bin/env bash
# Tests the vulnerability gates themselves.
#
# A security gate has one failure mode that matters more than the rest: passing when it did not
# actually check. Both gates here had it. `npm audit` returning an error object left the counts as
# `null`, so `[ "null" -gt 0 ]` errored and evaluated false; the .NET gate ran `jq -e` inside an `if`,
# so an unreadable report made the condition false. Either way the build went green having verified
# nothing.
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

# Mirrors the npm gate body in .github/workflows/ci.yml.
npm_gate() {
  local audit=$1 CRIT HIGH
  CRIT=$(jq -r '.metadata.vulnerabilities.critical // empty' "$audit" 2>/dev/null || true)
  HIGH=$(jq -r '.metadata.vulnerabilities.high // empty' "$audit" 2>/dev/null || true)
  case "${CRIT}|${HIGH}" in
    *[!0-9]\|*|*\|*[!0-9]*|\|*|*\|) return 1 ;;
  esac
  { [ "$CRIT" -gt 0 ] || [ "$HIGH" -gt 0 ]; } && return 1
  return 0
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

echo '{"metadata":{"vulnerabilities":{"critical":0,"high":0}}}'          > "$d/npm-clean.json"
echo '{"metadata":{"vulnerabilities":{"critical":0,"high":2}}}'          > "$d/npm-high.json"
echo '{"metadata":{"vulnerabilities":{"critical":1,"high":0}}}'          > "$d/npm-crit.json"
echo '{"error":{"code":"ENETUNREACH","summary":"registry unreachable"}}' > "$d/npm-error.json"
echo 'not json at all'                                                   > "$d/npm-garbage.json"
: > "$d/npm-empty.json"

echo '{"projects":[]}'                                                   > "$d/net-clean.json"
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[{"severity":"High"}]}]}]}]}'      > "$d/net-high.json"
echo '{"projects":[{"frameworks":[{"transitivePackages":[{"vulnerabilities":[{"severity":"Critical"}]}]}]}]}' > "$d/net-crit.json"
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[{"severity":"Moderate"}]}]}]}]}'  > "$d/net-moderate.json"
echo '{"projects":{"project-a":{"frameworks":[]}}}'                       > "$d/net-objshape.json"
echo '{"projects":[{"frameworks":{"net8.0":{}}}]}'                       > "$d/net-objframeworks.json"
echo 'MSBuild error, no json here'                                       > "$d/net-garbage.json"
: > "$d/net-empty.json"

echo "npm gate:"
npm_gate "$d/npm-clean.json";   check "clean report"          pass $?
npm_gate "$d/npm-high.json";    check "high findings"         fail $?
npm_gate "$d/npm-crit.json";    check "critical findings"     fail $?
npm_gate "$d/npm-error.json";   check "audit error object"    fail $?
npm_gate "$d/npm-garbage.json"; check "unparseable output"    fail $?
npm_gate "$d/npm-empty.json";   check "empty report"          fail $?

echo ".NET gate:"
dotnet_gate "$d/net-clean.json";    check "clean report"       pass $?
dotnet_gate "$d/net-high.json";     check "high findings"      fail $?
dotnet_gate "$d/net-crit.json";     check "critical, nested"   fail $?
dotnet_gate "$d/net-moderate.json"; check "moderate only"      pass $?
dotnet_gate "$d/net-objshape.json"; check "projects is an object"      fail $?
dotnet_gate "$d/net-objframeworks.json"; check "frameworks is an object" fail $?
dotnet_gate "$d/net-garbage.json";  check "unparseable output" fail $?
dotnet_gate "$d/net-empty.json";    check "empty report"       fail $?

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
