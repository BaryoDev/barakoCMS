#!/usr/bin/env bash
# Scans one image with Trivy and runs the result through the vulnerability gate.
#
#   bash scripts/scan-image.sh <image> <label> [<location>]
#
# Needs trivy on PATH (scripts/install-trivy.sh). Writes trivy-reports/<label>.json, and
# trivy-reports/<label>.sarif when SARIF is set to anything. The gate fails on a High or Critical
# finding that has a fix and is not on .github/vuln-allowlist.json; unfixed findings are listed
# and do not fail. <location> is the file code scanning points a finding at.
set -euo pipefail

image=${1:?usage: scan-image.sh <image> <label> [<location>]}
label=${2:?usage: scan-image.sh <image> <label> [<location>]}
location=${3:-Dockerfile}

out=${REPORT_DIR:-trivy-reports}
mkdir -p "$out"
report="$out/$label.json"

# The gate does the severity and fix filtering, so the report keeps everything High and above.
# --exit-code is left at 0 on purpose: a scan that fails to run must be told apart from one that
# found something, and the gate fails closed on a report it cannot read. With it at 0 a non-zero
# exit from trivy means the run itself failed (a registry rate limit, a hang, a database download),
# never that the image has vulnerabilities, so that case is retried once after a pause. The image
# goes after `--` so a reference that starts with a dash is not read as a flag.
run_trivy() {
    trivy image --quiet --scanners vuln --severity HIGH,CRITICAL \
        --format json --output "$report" -- "$image"
}
rm -f "$report"
if ! run_trivy; then
    echo "::warning::trivy failed to run on $image, retrying once"
    sleep "${SCAN_RETRY_PAUSE:-20}"
    rm -f "$report"
    run_trivy || { echo "::error::trivy failed to run on $image twice"; exit 1; }
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "## Image scan: $image"
        echo '```'
        jq -r '[(.Results // [])[] | (.Vulnerabilities // [])[]]
               | if length == 0 then "no High or Critical findings"
                 else .[] | "\(.Severity)\t\(.VulnerabilityID)\t\(.PkgName)\t\(.InstalledVersion) fixed: \(if (.FixedVersion // "") == "" then "none" else .FixedVersion end)" end' \
            "$report" 2>/dev/null || echo "report unreadable"
        echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
fi

if [ -n "${SARIF:-}" ]; then
    export VULN_GATE_SARIF="$out/$label.sarif"
fi
VULN_GATE_IMAGE="$image" VULN_GATE_LOCATION="$location" bash "$(dirname "$0")/vuln-gate.sh" trivy "$report"
