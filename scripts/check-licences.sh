#!/usr/bin/env bash
# Fails when a package's licence does not follow D24 (DECISIONS.md): the engine stays MPL-2.0 and
# everything built on it is MIT (#816).
#
# The licence is set once, by rule, in Directory.Build.props, so reading a .csproj says nothing.
# This asks MSBuild what each project evaluates to, which is what `dotnet pack` writes into the
# package. It also checks the LICENSE file a package folder carries, since that is what someone
# reading the source sees, and the module template's default.
#
#   engine (MPL-2.0): barakoCMS, BarakoCMS.Abstractions. They carry no LICENSE of their own; the
#                     root LICENSE is MPL-2.0 and covers them.
#   everything else:  MIT, with an MIT LICENSE in its folder. BarakoCMS.Suite is not packed but is
#                     checked too, since the image is built from it.
#
# Run locally with: bash scripts/check-licences.sh

set -uo pipefail

cd "$(dirname "$0")/.."

failures=0
checked=0
bad() { echo "  FAIL  $1"; failures=$((failures + 1)); }

engine() { case "$1" in barakoCMS|BarakoCMS.Abstractions) return 0 ;; *) return 1 ;; esac; }

# BarakoCMS.Templates and BarakoCMS.Testing are versioned with the core (check-pinned-versions.sh,
# check-module-versions.sh), and the core's version moves only in a release. A file added to their
# folders before then is a source change that check-module-versions.sh asks a version bump for,
# which they cannot have. So their LICENSE lands with the release that moves all three, and until
# then only their evaluated licence is checked. Once the file is there it is checked like any other.
licence_file_waits_for_release() {
    case "$1" in BarakoCMS.Templates|BarakoCMS.Testing) [ ! -f "$1/LICENSE" ] ;; *) return 1 ;; esac
}

head -1 LICENSE | grep -q '^Mozilla Public License Version 2.0' \
    || bad "the root LICENSE is not MPL-2.0, and it is the one that covers the engine"

# Tracked projects only: a nested worktree or a scratch copy is not this checkout. The template's
# own content is a project for somebody else's repository and is checked by its default below.
mapfile -t projects < <(git ls-files '*.csproj' | grep -v '/templates/' | grep -v '^BarakoCMS.Tests/')

for csproj in "${projects[@]}"; do
    name=$(basename "$csproj" .csproj)
    dir=$(dirname "$csproj")
    packable=$(dotnet msbuild "$csproj" -getProperty:IsPackable 2>/dev/null | tr -d '\r')
    [ "$packable" = "true" ] || [ "$name" = "BarakoCMS.Suite" ] || continue

    licence=$(dotnet msbuild "$csproj" -getProperty:PackageLicenseExpression 2>/dev/null | tr -d '\r')
    checked=$((checked + 1))
    if engine "$name"; then
        [ "$licence" = "MPL-2.0" ] || bad "$name evaluates to '$licence', and as the engine it must be MPL-2.0"
        [ ! -f "$dir/LICENSE" ] || head -1 "$dir/LICENSE" | grep -q '^Mozilla Public License' \
            || bad "$dir/LICENSE is not MPL-2.0, and $name is the engine"
    else
        [ "$licence" = "MIT" ] || bad "$name evaluates to '$licence', and everything built on the engine is MIT"
        if licence_file_waits_for_release "$name"; then
            echo "  note  $name: its LICENSE lands with the release that moves its version"
        elif [ ! -f "$dir/LICENSE" ]; then
            bad "$dir has no LICENSE; $name is MIT and its folder needs the MIT text"
        elif ! head -1 "$dir/LICENSE" | grep -q '^MIT License'; then
            bad "$dir/LICENSE is not the MIT text, and $name is MIT"
        fi
    fi
    echo "  ok    $name: ${licence:-<none>}"
done

template=BarakoCMS.Templates/templates/barakocms-module/.template.config/template.json
default=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["symbols"]["License"]["defaultValue"])' "$template" 2>/dev/null)
[ "$default" = "MIT" ] || bad "the module template's License defaults to '$default', not MIT"

# A scan that matched nothing would pass having checked nothing.
[ "$checked" -ge 10 ] || bad "only $checked project(s) were checked, so the scan missed the packages it exists for"

if [ "$failures" -gt 0 ]; then
    echo "check-licences: $failures problem(s). D24 in DECISIONS.md says which licence each package carries."
    exit 1
fi
echo "check-licences: $checked package(s) follow D24, and the module template defaults to MIT."
