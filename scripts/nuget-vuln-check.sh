#!/usr/bin/env bash
# The NuGet vulnerability gate: lists vulnerable packages, direct and transitive, and fails on a
# Critical or High one that is not on .github/vuln-allowlist.json.
#
#   bash scripts/nuget-vuln-check.sh
#
# CI and the nightly run both call this, so they cannot drift apart. Moderate and Low findings
# show in the summary and do not block. The report is read by scripts/vuln-gate.sh, which fails
# closed on a report it cannot read.
set -euo pipefail

dotnet restore --locked-mode

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo '## Vulnerable dependencies'
        echo '```'
        dotnet list package --vulnerable --include-transitive 2>&1
        echo '```'
    } | tee -a "$GITHUB_STEP_SUMMARY"
fi

# --format json rather than the console table: the JSON schema is stable and the table layout is
# not a contract.
dotnet list package --vulnerable --include-transitive --format json --output-version 1 > vuln-report.json

exec bash "$(dirname "$0")/vuln-gate.sh" dotnet vuln-report.json
