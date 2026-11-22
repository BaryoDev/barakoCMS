#!/usr/bin/env bash
# Fails when a scanner report holds a Critical or High finding that is not accepted.
#
#   scripts/vuln-gate.sh <trivy|dotnet> <report.json> [<allowlist.json>] [<today YYYY-MM-DD>]
#
# trivy:  a `trivy image --format json` report. A finding with no fixed version is reported and
#         does not fail the gate, because nobody can act on it yet.
# dotnet: a `dotnet list package --vulnerable --format json --output-version 1` report. Every
#         finding counts, as the NuGet gate always has.
#
# One allowlist serves both (default .github/vuln-allowlist.json). Each entry is
# {"id", "package", "reason", "expires"}. A finding is accepted while an entry names its id and
# package (package "*" matches any) and the expiry date is today or later; it stops accepting
# the day after. An expired entry fails the
# gate even when nothing matches it, so a stale exception cannot sit there unread.
#
# A report or allowlist it cannot read fails, the same as before: a gate that cannot see must not
# wave things through.
#
# Optional: VULN_GATE_SARIF=<file> writes the failing findings as SARIF for code scanning, and
# VULN_GATE_LOCATION names the file those results point at (default Directory.Packages.props).
# VULN_GATE_IMAGE names the image being scanned; it goes in the message and in the fingerprint, so
# one CVE in two images stays two alerts although both point at the same file.
# shellcheck disable=SC2016
# (jq programs are single-quoted on purpose)
set -uo pipefail

kind=${1:?usage: vuln-gate.sh <trivy|dotnet> <report.json> [<allowlist.json>] [<today>]}
report=${2:?usage: vuln-gate.sh <trivy|dotnet> <report.json> [<allowlist.json>] [<today>]}
allowlist=${3:-.github/vuln-allowlist.json}
today=${4:-$(date -u +%F)}
location=${VULN_GATE_LOCATION:-Directory.Packages.props}
image=${VULN_GATE_IMAGE:-}

case "$kind" in
    trivy)
        shape='(.SchemaVersion | type) == "number"
               and ((.Results // []) | type == "array")
               and all((.Results // [])[]; (.Vulnerabilities // []) | type == "array")'
        findings='[(.Results // [])[] | (.Vulnerabilities // [])[]
                   | select(.Severity == "CRITICAL" or .Severity == "HIGH")
                   | {severity: .Severity, id: .VulnerabilityID, package: .PkgName,
                      fixed: ((.FixedVersion // "") != "")}]'
        ;;
    dotnet)
        shape='(.projects | type) == "array"
               and all(.projects[]; (.frameworks // []) | type == "array")'
        findings='[.projects[].frameworks[]? // empty
                   | (.topLevelPackages // []) + (.transitivePackages // []) | .[]
                   | . as $p | .vulnerabilities[]?
                   | select(.severity == "Critical" or .severity == "High")
                   | {severity: (.severity | ascii_upcase),
                      id: ((.advisoryurl // "unknown") | split("/") | last),
                      package: $p.id, fixed: true}]'
        ;;
    *)
        echo "::error::unknown report kind '$kind'"
        exit 2
        ;;
esac

if ! jq -e "$shape" "$report" >/dev/null 2>&1; then
    echo "::error::$kind report missing, unreadable or not the expected shape, failing closed"
    head -c 500 "$report" || true
    exit 1
fi

if ! jq -e 'type == "array" and all(.[];
        (.id | type == "string" and length > 0)
        and (.package | type == "string" and length > 0)
        and (.reason | type == "string" and length > 0)
        and (.expires | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")))' \
        "$allowlist" >/dev/null 2>&1; then
    echo "::error::$allowlist is missing or malformed (each entry needs id, package, reason and a YYYY-MM-DD expires), failing closed"
    exit 1
fi

# A date that fits the pattern can still be impossible (2026-13-45), and a string compare would
# read it as far in the future. Each date has to survive a round trip through a real calendar date.
badnames=$(jq -r '.[] | select((try (.expires | strptime("%Y-%m-%d") | mktime | strftime("%Y-%m-%d")) catch "") != .expires)
                  | "\(.id) (\(.package)) has the impossible expiry date \(.expires)"' "$allowlist")
if [ -n "$badnames" ]; then
    while IFS= read -r line; do
        echo "::error::allowlist entry $line, fix the date"
    done <<<"$badnames"
    exit 1
fi

failing=0

expired=$(jq -r --arg today "$today" '.[] | select(.expires < $today) | "\(.id)\t\(.package)\t\(.expires)"' "$allowlist")
while IFS=$'\t' read -r id package expires; do
    [ -n "$id" ] || continue
    echo "::error::allowlist entry $id ($package) expired on $expires, renew it or remove it"
    failing=$((failing + 1))
done <<<"$expired"

live=$(jq -c --arg today "$today" '[.[] | select(.expires >= $today)]' "$allowlist")

# Every finding once, as "severity<TAB>id<TAB>package<TAB>fixed<TAB>accepted". The shape check above
# does not look inside each finding, so a finding jq cannot read fails here instead of vanishing.
# A High or Critical finding whose id or package is empty, not a string, or holds a tab, newline or
# the row separator is an error, so it fails the gate instead of shifting columns in the rows below.
if ! rows=$(jq -r --argjson live "$live" "$findings"' | unique
    | map(if (.id | type == "string" and length > 0 and (test("[\t\n\r\u001f]") | not))
             and (.package | type == "string" and length > 0 and (test("[\t\n\r\u001f]") | not))
          then . else error("finding with an empty or unreadable id or package") end)
    | .[]
    | . as $f
    | "\(.severity)\u001f\(.id)\u001f\(.package)\u001f\(.fixed)\u001f\(
        [$live[] | select(.id == $f.id and (.package == $f.package or .package == "*"))] | length > 0)"' \
    "$report"); then
    echo "::error::could not read the findings in the $kind report (an id or package is empty or holds a tab or newline), failing closed"
    exit 1
fi

sarif_rows=""
reported=0
while IFS=$'\x1f' read -r severity id package fixed accepted; do
    [ -n "$id" ] || continue
    if [ "$accepted" = "true" ]; then
        echo "accepted until its expiry: $severity $id ($package)"
    elif [ "$fixed" != "true" ]; then
        echo "unfixed, reported only: $severity $id ($package)"
        reported=$((reported + 1))
    else
        echo "::error::$severity $id ($package) has a fix available"
        sarif_rows+="$severity"$'\t'"$id"$'\t'"$package"$'\n'
        failing=$((failing + 1))
    fi
done <<<"$rows"

if [ -n "${VULN_GATE_SARIF:-}" ]; then
    printf '%s' "$sarif_rows" | jq -R -s --arg loc "$location" --arg image "$image" '
        [split("\n")[] | select(length > 0) | split("\t")
         | {ruleId: .[1], level: "error",
            message: {text: "\(.[0]) \(.[1]) in \(.[2])\(if $image == "" then "" else " (image \($image))" end) has a fix available"},
            partialFingerprints: {"vulnGate/v1": "\(.[1]):\(.[2]):\($image)"},
            locations: [{physicalLocation: {artifactLocation: {uri: $loc},
                                            region: {startLine: 1}}}]}] as $results
        | {version: "2.1.0",
           "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
           runs: [{tool: {driver: {name: "vuln-gate",
                                   rules: ([$results[] | {id: .ruleId}] | unique_by(.id))}},
                   results: $results}]}' > "$VULN_GATE_SARIF"
fi

echo "$kind gate: $failing failing, $reported unfixed and reported"
[ "$failing" -eq 0 ]
