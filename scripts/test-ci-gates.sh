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

# shellcheck disable=SC2016
# (literal ${VAR} strings are the point of the compose list cases)
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
echo '{"SchemaVersion":2,"Results":[{"Target":"image","Vulnerabilities":["CVE-2099-0001"]}]}' > "$d/img-oddfinding.json"
bash "$gate" trivy "$d/img-oddfinding.json" "$d/allow-none.json" $today > /dev/null 2>&1; check "a finding jq cannot read"           fail $?

# Expiry is inclusive: an entry dated today still accepts, the day after it does not. Dates that fit
# the pattern but are not real fail the gate, and the error names the entry.
bash "$gate" trivy "$d/img-high.json" "$d/allow-ok.json" 2026-12-31 > /dev/null;          check "entry accepts on its expiry date"   pass $?
bash "$gate" trivy "$d/img-high.json" "$d/allow-ok.json" 2027-01-01 > /dev/null;          check "entry stops the day after"          fail $?
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"typo","expires":"2026-13-45"}]' > "$d/allow-baddate.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"no such day","expires":"2026-02-30"}]' > "$d/allow-baddate2.json"
bash "$gate" trivy "$d/img-high.json" "$d/allow-baddate.json" $today > /dev/null 2>&1;    check "expiry 2026-13-45"                  fail $?
bash "$gate" trivy "$d/img-clean.json" "$d/allow-baddate2.json" $today > /dev/null 2>&1;  check "expiry 2026-02-30, nothing matches" fail $?
err=$(bash "$gate" trivy "$d/img-clean.json" "$d/allow-baddate.json" $today 2>&1)
printf '%s\n' "$err" | grep -q 'CVE-2099-0001 (libfake) has the impossible expiry date 2026-13-45'
check "impossible date names the entry" pass $?

# A High with a fix whose id or package is empty, or holds a newline or tab, must fail the gate
# and not shift columns into the "unfixed, reported only" branch.
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":"CVE-2099-0009","PkgName":"","Severity":"HIGH","FixedVersion":"1.0"}]}]}' > "$d/img-nopkg.json"
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":"","PkgName":"libfake","Severity":"HIGH","FixedVersion":"1.0"}]}]}' > "$d/img-noid.json"
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":"CVE-2099-0009\nlibfake","PkgName":"libfake","Severity":"HIGH","FixedVersion":"1.0"}]}]}' > "$d/img-nlid.json"
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":"CVE-2099-0009","PkgName":"lib\tfake","Severity":"HIGH","FixedVersion":"1.0"}]}]}' > "$d/img-tabpkg.json"
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":null,"PkgName":"libfake","Severity":"CRITICAL","FixedVersion":"1.0"}]}]}' > "$d/img-nullid.json"
bash "$gate" trivy "$d/img-nopkg.json" "$d/allow-none.json" $today > /dev/null 2>&1;      check "High with an empty package name"    fail $?
bash "$gate" trivy "$d/img-noid.json" "$d/allow-none.json" $today > /dev/null 2>&1;       check "High with an empty id"              fail $?
bash "$gate" trivy "$d/img-nlid.json" "$d/allow-none.json" $today > /dev/null 2>&1;       check "High with a newline in its id"      fail $?
bash "$gate" trivy "$d/img-tabpkg.json" "$d/allow-none.json" $today > /dev/null 2>&1;     check "High with a tab in its package"     fail $?
bash "$gate" trivy "$d/img-nullid.json" "$d/allow-none.json" $today > /dev/null 2>&1;     check "Critical with a null id"            fail $?
# A Low with an empty id is not gated, so it must not fail.
printf '%s\n' '{"SchemaVersion":2,"Results":[{"Vulnerabilities":[{"VulnerabilityID":"","PkgName":"","Severity":"LOW","FixedVersion":"1.0"}]}]}' > "$d/img-lowempty.json"
bash "$gate" trivy "$d/img-lowempty.json" "$d/allow-none.json" $today > /dev/null 2>&1;   check "Low with empty fields is ignored"   pass $?
# An entry with "images" accepts only in a listed repository (tag and digest removed).
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"gosu in postgres","expires":"2026-12-31","images":["postgres"]}]' > "$d/allow-scoped.json"
img_gate() { VULN_GATE_IMAGE=$1 bash "$gate" trivy "$d/img-high.json" "$2" $today > /dev/null 2>&1; }
img_gate "postgres:16-alpine@sha256:aaaa" "$d/allow-scoped.json"; check "scoped entry, tag and digest"       pass $?
img_gate "postgres@sha256:aaaa" "$d/allow-scoped.json";           check "scoped entry, digest only"          pass $?
img_gate "postgres:16-alpine" "$d/allow-scoped.json";             check "scoped entry, tag only"             pass $?
img_gate "postgres" "$d/allow-scoped.json";                       check "scoped entry, bare repository"      pass $?
img_gate "caddy@sha256:aaaa" "$d/allow-scoped.json";              check "scoped entry, other image"          fail $?
img_gate "docker.io/postgres:16" "$d/allow-scoped.json";          check "scoped entry, registry host differs" fail $?
img_gate "" "$d/allow-scoped.json";                               check "scoped entry, no image name"        fail $?
img_gate "caddy@sha256:aaaa" "$d/allow-ok.json";                  check "unscoped entry, any image"          pass $?
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"x","expires":"2026-12-31","images":[]}]' > "$d/allow-img-empty.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"x","expires":"2026-12-31","images":"postgres"}]' > "$d/allow-img-string.json"
echo '[{"id":"CVE-2099-0001","package":"libfake","reason":"x","expires":"2026-12-31","images":[""]}]' > "$d/allow-img-blank.json"
img_gate "postgres:16" "$d/allow-img-empty.json";                 check "images is an empty array"           fail $?
img_gate "postgres:16" "$d/allow-img-string.json";                check "images is a string"                 fail $?
img_gate "postgres:16" "$d/allow-img-blank.json";                 check "images holds an empty string"       fail $?
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Some.Pkg","vulnerabilities":[{"severity":"High","advisoryurl":"https://github.com/advisories/GHSA-aaaa"}]}]}]}]}' > "$d/net-pkg.json"
echo '[{"id":"GHSA-aaaa","package":"Some.Pkg","reason":"x","expires":"2026-12-31","images":["postgres"]}]' > "$d/allow-net-scoped.json"
bash "$gate" dotnet "$d/net-pkg.json" "$d/allow-net-scoped.json" $today > /dev/null 2>&1; check "scoped entry, dotnet report" fail $?

# Writes SARIF for the findings that failed, and only those.
VULN_GATE_SARIF="$d/out.sarif" bash "$gate" trivy "$d/img-high.json" "$d/allow-none.json" $today > /dev/null
jq -e '(.runs[0].results | length) == 1 and .runs[0].results[0].ruleId == "CVE-2099-0001"' "$d/out.sarif" > /dev/null 2>&1
check "SARIF holds the failing finding only" pass $?

# The same CVE in two images stays two alerts: the image is in the message and the fingerprint.
VULN_GATE_IMAGE=postgres:16 VULN_GATE_SARIF="$d/pg.sarif" bash "$gate" trivy "$d/img-high.json" "$d/allow-none.json" $today > /dev/null
VULN_GATE_IMAGE=caddy:2 VULN_GATE_SARIF="$d/caddy.sarif" bash "$gate" trivy "$d/img-high.json" "$d/allow-none.json" $today > /dev/null
jq -s -e '.[0].runs[0].results[0].partialFingerprints != .[1].runs[0].results[0].partialFingerprints
          and (.[0].runs[0].results[0].message.text | contains("postgres:16"))' "$d/pg.sarif" "$d/caddy.sarif" > /dev/null 2>&1
check "SARIF tells two images apart" pass $?

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
echo '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Fake.Package","vulnerabilities":[{"severity":"High","advisoryurl":7}]}]}]}]}' > "$d/net-oddurl.json"
bash "$gate" dotnet "$d/net-oddurl.json" "$d/allow-none.json" $today > /dev/null 2>&1; check "an advisory url jq cannot read" fail $?
printf '%s\n' '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"","vulnerabilities":[{"severity":"High","advisoryurl":"https://github.com/advisories/GHSA-fake-fake-fake"}]}]}]}]}' > "$d/net-nopkg.json"
printf '%s\n' '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Fake.Package","vulnerabilities":[{"severity":"High","advisoryurl":"https://github.com/advisories/"}]}]}]}]}' > "$d/net-noid.json"
bash "$gate" dotnet "$d/net-nopkg.json" "$d/allow-none.json" $today > /dev/null 2>&1;   check "advisory on an empty package id"   fail $?
bash "$gate" dotnet "$d/net-noid.json" "$d/allow-none.json" $today > /dev/null 2>&1;    check "advisory url with an empty id"     fail $?
bash "$gate" nonsense "$d/net-ghsa.json" "$d/allow-none.json" $today > /dev/null 2>&1; check "unknown report kind"        fail $?

echo
echo "The committed allowlist:"
bash "$gate" trivy "$d/img-clean.json" "$here/../.github/vuln-allowlist.json" $today > /dev/null
check "parses, and holds no expired entry" pass $?

echo
echo "Action pins (check-action-pins.sh):"
sha=3d3c42e5aac5ba805825da76410c181273ba90b1
mkdir -p "$d/wf-good" "$d/wf-tag" "$d/wf-branch" "$d/wf-short" "$d/wf-docker" "$d/wf-none" "$d/wf-no-eol"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - uses: ./.github/actions/local\n' "$sha" > "$d/wf-good/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - uses: actions/setup-dotnet@v6\n' "$sha" > "$d/wf-tag/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@main\n' > "$d/wf-branch/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@3d3c42e\n' > "$d/wf-short/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: docker://alpine:3.20\n' > "$d/wf-docker/a.yml"
printf 'name: nothing\n' > "$d/wf-none/a.yml"
# No newline after the last line, which a plain read loop never returns.
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - uses: actions/setup-dotnet@v6' "$sha" > "$d/wf-no-eol/a.yml"
bash "$here/check-action-pins.sh" "$d/wf-good" > /dev/null;   check "every action pinned by SHA"      pass $?
bash "$here/check-action-pins.sh" "$d/wf-tag" > /dev/null;    check "one action pinned by a tag"      fail $?
bash "$here/check-action-pins.sh" "$d/wf-branch" > /dev/null; check "action pinned by a branch"       fail $?
bash "$here/check-action-pins.sh" "$d/wf-short" > /dev/null;  check "abbreviated SHA"                 fail $?
bash "$here/check-action-pins.sh" "$d/wf-docker" > /dev/null; check "docker:// without a digest"      fail $?
bash "$here/check-action-pins.sh" "$d/wf-none" > /dev/null;   check "no uses lines at all"            fail $?
bash "$here/check-action-pins.sh" "$d/wf-no-eol" > /dev/null; check "a tag on a last line with no newline" fail $?
bash "$here/check-action-pins.sh" "$here/../.github/workflows" > /dev/null; check "this repository's workflows" pass $?

echo
echo "Image scan wrapper (scan-image.sh, with a stand-in trivy):"
mkdir -p "$d/fakebin"
# Fails while the counter file says so, then writes the report named by --output. Records its arguments.
cat > "$d/fakebin/trivy" <<'FAKE'
#!/usr/bin/env bash
n=$(cat "$FAKE_COUNT" 2>/dev/null || echo 0)
echo $((n + 1)) > "$FAKE_COUNT"
echo "$@" > "$FAKE_ARGS"
if [ "$n" -lt "${FAKE_FAILS:-0}" ]; then echo "registry: 429 too many requests" >&2; exit 1; fi
while [ $# -gt 0 ]; do [ "$1" = "--output" ] && { cp "$FAKE_REPORT" "$2"; break; }; shift; done
FAKE
chmod +x "$d/fakebin/trivy"
eq() { [ "$1" = "$2" ]; }
scan() { # $1 = report to produce, $2 = failures before success, $3 = image
  rm -f "$d/count"
  FAKE_REPORT="$1" FAKE_FAILS="$2" FAKE_COUNT="$d/count" FAKE_ARGS="$d/args" SCAN_RETRY_PAUSE=0 \
    PATH="$d/fakebin:$PATH" REPORT_DIR="$d/reports" \
    bash -c 'cd "$1" && bash scripts/scan-image.sh "$2" t' _ "$here/.." "$3" > /dev/null 2>&1
}
# scan-image.sh runs the gate on the committed allowlist, which holds nothing that matches these reports.
scan "$d/img-clean.json" 0 postgres:16;  check "clean image"                                    pass $?
scan "$d/img-clean.json" 1 postgres:16;  check "trivy fails once, then runs (retried)"          pass $?
eq "$(cat "$d/count")" 2;                check "retry ran trivy exactly twice" pass $?
scan "$d/img-clean.json" 2 postgres:16;  check "trivy fails twice"                              fail $?
eq "$(cat "$d/count")" 2;                check "no third attempt" pass $?
scan "$d/img-high.json" 0 postgres:16;   check "findings with a fix are not retried and fail"   fail $?
eq "$(cat "$d/count")" 1;                check "findings ran trivy once" pass $?
scan "$d/img-clean.json" 0 -oops;        check "image starting with a dash"                     pass $?
grep -q -- '-- -oops$' "$d/args";        check "image is passed after --" pass $?

echo
echo "Compose image list (list-compose-images.sh):"
lst="$here/list-compose-images.sh"
mkdir -p "$d/lc1/k8s/sub" "$d/lc1/quickstart" "$d/lc2" "$d/lc3" "$d/lc4"
cat > "$d/lc1/docker-compose.yml" <<'YML'
services:
  db:
    image: postgres:${PG_VERSION:-16}
  cache:
    image: "redis:7@sha256:aaaa"
  app:
    image: ghcr.io/baryodev/barako-cms:${BARAKO_TAG}
  app2:
    image: ${REGISTRY:-ghcr.io}/baryodev/barako-cms-decaf:latest
YML
printf 'services:\n  a:\n    image: caddy:2\n' > "$d/lc1/docker-compose.override.yaml"
printf 'services:\n  a:\n    image: nginx:1\n' > "$d/lc1/quickstart/docker-compose.yml"
printf 'spec:\n  containers:\n    - image: busybox:1\n' > "$d/lc1/k8s/sub/job.yml"
out=$(bash "$lst" "$d/lc1" 2>/dev/null); rc=$?
eq "$rc" 0;                                                         check "defaults resolve, own images skipped" pass $?
printf '%s\n' "$out" | grep -qx 'postgres:16';                      check "variable with a default is scanned"   pass $?
printf '%s\n' "$out" | grep -qx 'redis@sha256:aaaa';                check "digest reference"                     pass $?
printf '%s\n' "$out" | grep -qx 'caddy:2';                          check "docker-compose*.yaml is read"         pass $?
printf '%s\n' "$out" | grep -qx 'busybox:1';                        check "k8s .yml in a subfolder is read"      pass $?
! printf '%s\n' "$out" | grep -qi 'baryodev';                       check "own images are not listed"            pass $?
printf 'services:\n  a:\n    image: foo/bar:%s\n  b:\n    image: postgres:16\n' '${TAG}' > "$d/lc2/docker-compose.yml"
err=$(bash "$lst" "$d/lc2" 2>&1 >/dev/null); rc=$?
! eq "$rc" 0;                                                       check "variable with no default fails"       pass $?
printf '%s' "$err" | grep -q 'docker-compose.yml' && printf '%s' "$err" | grep -qF 'foo/bar:${TAG}'
check "the error names the file and the image" pass $?
out=$(bash "$lst" "$d/lc2" 2>/dev/null)
printf '%s\n' "$out" | grep -qx 'postgres:16';                      check "the rest is still listed"             pass $?
bash "$lst" "$d/lc3" > /dev/null 2>&1;                               check "no files at all"                      fail $?
bash "$lst" "$here/.." > /dev/null 2>&1;                             check "this repository's compose files"      pass $?

echo
echo "Action pins in composite actions:"
mkdir -p "$d/gh1/workflows" "$d/gh1/actions/mine" "$d/gh2/workflows" "$d/gh2/actions/mine"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n' "$sha" > "$d/gh1/workflows/a.yml"
cp "$d/gh1/workflows/a.yml" "$d/gh2/workflows/a.yml"
printf 'runs:\n  using: composite\n  steps:\n    - uses: actions/checkout@%s # v7\n' "$sha" > "$d/gh1/actions/mine/action.yml"
printf 'runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@v4\n' > "$d/gh2/actions/mine/action.yaml"
bash "$here/check-action-pins.sh" "$d/gh1/workflows" > /dev/null; check "composite action pinned by SHA"     pass $?
bash "$here/check-action-pins.sh" "$d/gh2/workflows" > /dev/null; check "composite action pinned by a tag"   fail $?

echo
echo "Compose hardening (check-compose-hardening.sh):"
chk="$here/check-compose-hardening.sh"
fx="$here/testdata/compose-hardening"
bash "$chk" "$fx/good.yml" > /dev/null 2>&1;                         check "every option present"                  pass $?
err=$(bash "$chk" "$fx/bad.yml" 2>&1 > /dev/null); rc=$?
eq "$rc" 1;                                                          check "a service missing options fails"       pass $?
for want in "no-read-only: missing read_only" "no-cap-drop: missing cap_drop ALL" \
            "no-new-privileges: missing no-new-privileges" "root-user: missing a non-root user" \
            "no-mem-limit: missing mem_limit" "no-pids-limit: missing pids_limit" \
            "no-healthcheck: missing a healthcheck" "one-shot-still-needs-read-only: missing read_only" \
            "restart-no-without-label: missing a healthcheck" \
            "tmpfs-no-size: missing a tmpfs size" "tmpfs-size-zero: missing a tmpfs size" \
            "tmpfs-size-negative: missing a tmpfs size" \
            "privileged: undoes hardening with privileged" "host-network: undoes hardening with network_mode" \
            "host-pid: undoes hardening with pid" "cap-add-all: undoes hardening with cap_add ALL" \
            "cap-add-sys-admin: undoes hardening with cap_add SYS_ADMIN" \
            "seccomp-unconfined: undoes hardening with security_opt" \
            "apparmor-unconfined: undoes hardening with security_opt" "docker-socket: mounts the Docker socket"; do
  printf '%s' "$err" | grep -qF "bad.yml, service $want";           check "names the file, service and option: ${want%%:*}" pass $?
done
! printf '%s' "$err" | grep -q 'service one-shot-still-needs-read-only: missing a healthcheck'; check "the one-shot label exempts the healthcheck only" pass $?
! printf '%s' "$err" | grep -qE 'service (with-tmpfs|plain|one-shot):'; check "good services are not reported" pass $?
# The operator's own env file: a limit at 0 or -1 removes the limit, and compose cannot refuse it.
base_env="$d/ch-base.env"
cp "$here/../.env.prod.example" "$base_env"
printf 'BARAKO_TAG=check\nDB_PASSWORD=x-check-value\nDB_APP_PASSWORD=x-app-check-value\nJWT_KEY=x-check-value-0123456789-0123456789\nADMIN_PASSWORD=x-check-value\n' >> "$base_env"
prod="$here/../docker-compose.prod.yml"
bash "$chk" --env-file "$base_env" "$prod" > /dev/null 2>&1;         check "the operator env as shipped passes"    pass $?
grep -v '^DB_APP_PASSWORD=' "$base_env" > "$d/ch-noapp.env"
bash "$chk" --env-file "$d/ch-noapp.env" "$prod" > /dev/null 2>&1;    check "no DB_APP_PASSWORD does not resolve"   fail $?
for var in API_MEM_LIMIT API_PIDS_LIMIT DB_MEM_LIMIT DB_PIDS_LIMIT EXTENSIONS_MEM_LIMIT EXTENSIONS_PIDS_LIMIT \
           BACKUP_MEM_LIMIT BACKUP_PIDS_LIMIT PROXY_MEM_LIMIT PROXY_PIDS_LIMIT API_TMP_SIZE; do
  for bad in 0 -1; do
    { cat "$base_env"; printf '%s=%s\n' "$var" "$bad"; } > "$d/ch-limit.env"
    bash "$chk" --env-file "$d/ch-limit.env" "$prod" > /dev/null 2>&1; check "$var=$bad removes a limit" fail $?
  done
  { cat "$base_env"; printf '%s=\n' "$var"; } > "$d/ch-limit.env"
  bash "$chk" --env-file "$d/ch-limit.env" "$prod" > /dev/null 2>&1;  check "$var left blank keeps the default" pass $?
done
bash "$chk" --env-file "$d/ch-missing.env" "$prod" > /dev/null 2>&1; check "an env file that does not exist"       fail $?
printf 'services:\n  a:\n    image: x:${NOT_SET_ANYWHERE:?must be set}\n' > "$d/ch-unresolved.yml"
bash "$chk" "$d/ch-unresolved.yml" > /dev/null 2>&1;                  check "a file compose cannot resolve"         fail $?
printf 'services: {}\n' > "$d/ch-empty.yml"
bash "$chk" "$d/ch-empty.yml" > /dev/null 2>&1;                       check "a file with no services"               fail $?
bash "$chk" "$d/ch-missing.yml" > /dev/null 2>&1;                     check "a file that does not exist"            fail $?
bash "$chk" > /dev/null 2>&1;                                        check "no file given"                         fail $?
bash "$chk" "$here/../docker-compose.yml" "$here/../docker-compose.hub.yml" \
  "$here/../docker-compose.prod.yml" "$here/../quickstart/docker-compose.yml" > /dev/null 2>&1; check "the four shipped compose files" pass $?
bash "$chk" "$fx/good.yml" "$fx/bad.yml" > /dev/null 2>&1;            check "one bad file among good ones"          fail $?

echo
echo "Action pins, other YAML forms and nested actions:"
mkdir -p "$d/gf/workflows" "$d/gq/workflows" "$d/gn/workflows" "$d/gn/actions/a/b" "$d/gm/workflows" "$d/gm/actions/a/b" "$d/gc/workflows"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - {uses: actions/checkout@v4}\n' "$sha" > "$d/gf/workflows/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      - "uses": actions/checkout@v4\n' "$sha" > "$d/gq/workflows/a.yml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n' "$sha" > "$d/gn/workflows/a.yml"
cp "$d/gn/workflows/a.yml" "$d/gm/workflows/a.yml"
printf 'runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@v4\n' > "$d/gn/actions/a/b/action.yml"
printf 'runs:\n  using: composite\n  steps:\n    - uses: actions/setup-node@v4\n' > "$d/gm/actions/a/b/action.yaml"
printf 'jobs:\n  a:\n    steps:\n      - uses: actions/checkout@%s # v7\n      # uses: actions/setup-node@v4\n      - run: echo "- uses: x/y@v1"\n' "$sha" > "$d/gc/workflows/a.yml"
bash "$here/check-action-pins.sh" "$d/gf/workflows" > /dev/null; check "flow-style step with a tag"          fail $?
bash "$here/check-action-pins.sh" "$d/gq/workflows" > /dev/null; check "quoted uses key with a tag"           fail $?
bash "$here/check-action-pins.sh" "$d/gn/workflows" > /dev/null; check "nested action.yml with a tag"         fail $?
bash "$here/check-action-pins.sh" "$d/gm/workflows" > /dev/null; check "nested action.yaml with a tag"        fail $?
bash "$here/check-action-pins.sh" "$d/gc/workflows" > /dev/null; check "comments and run text are not steps"  pass $?

# Every uses key on a line is checked, not only the first.
mkdir -p "$d/gx/workflows" "$d/gy/workflows"
printf 'jobs:\n  a:\n    steps: [{uses: actions/checkout@%s}, {uses: actions/setup-node@v7}]\n' "$sha" > "$d/gx/workflows/a.yml"
printf 'jobs:\n  a:\n    steps: [{uses: actions/checkout@%s}, {uses: actions/setup-node@%s}]\n' "$sha" "$sha" > "$d/gy/workflows/a.yml"
out=$(bash "$here/check-action-pins.sh" "$d/gx/workflows"); check "later tag on a flow line fails"            fail $?
case "$out" in *actions/setup-node@v7*) check "the later tag is named" pass 0 ;; *) check "the later tag is named" pass 1 ;; esac
out=$(bash "$here/check-action-pins.sh" "$d/gy/workflows"); check "two SHA-pinned flow maps pass"             pass $?
case "$out" in *"2 checked, 0 not pinned"*) check "two flow maps count 2 checked" pass 0 ;; *) check "two flow maps count 2 checked" pass 1 ;; esac

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
