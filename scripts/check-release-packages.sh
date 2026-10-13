#!/usr/bin/env bash
# Fails when a packable project is missing from PACKAGE_PROJECTS in .github/workflows/release.yml,
# or that list names a project that does not exist.
#
# The release packs and publishes exactly that list. A new package left off it builds, passes every
# test and is never published, while its README tells people to `dotnet add package` it.
# BarakoCMS.Templates is the one packable project kept off on purpose (release.yml says why) and is
# packed from TEMPLATE_PROJECTS instead.
#
# Run locally with: bash scripts/check-release-packages.sh

set -uo pipefail

cd "$(dirname "$0")/.."

listed=$(python3 - <<'EOF'
import re
s = open(".github/workflows/release.yml").read()
m = re.search(r"^  PACKAGE_PROJECTS: >-\n((?:    .*\n)+)", s, re.M)
print("\n".join(sorted(m.group(1).split())) if m else "")
EOF
)
[ -n "$listed" ] || { echo "check-release-packages: PACKAGE_PROJECTS was not found in release.yml"; exit 1; }

# The same project set PackagingTests treats as packable: an IsPackable element of true, tracked,
# outside the template's own content.
packable=$(git ls-files '*.csproj' | grep -v '/templates/' | while read -r csproj; do
    if grep -qiE '<IsPackable>[[:space:]]*true[[:space:]]*</IsPackable>' "$csproj"; then basename "$csproj" .csproj; fi
done | grep -vx 'BarakoCMS.Templates' | sort)
[ -n "$packable" ] || { echo "check-release-packages: found no packable project, so the comparison would prove nothing"; exit 1; }

missing=$(comm -23 <(printf '%s\n' "$packable") <(printf '%s\n' "$listed"))
unknown=$(comm -13 <(printf '%s\n' "$packable") <(printf '%s\n' "$listed"))
status=0
for p in $missing; do echo "  FAIL  $p is packable and is not in PACKAGE_PROJECTS, so the release never publishes it"; status=1; done
for p in $unknown; do echo "  FAIL  PACKAGE_PROJECTS names $p, which is not a packable project here"; status=1; done
[ "$status" = 0 ] && echo "check-release-packages: all $(printf '%s\n' "$packable" | wc -l) packable projects are in PACKAGE_PROJECTS."
exit $status
