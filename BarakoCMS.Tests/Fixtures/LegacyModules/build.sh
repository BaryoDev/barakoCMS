#!/usr/bin/env bash
# Rebuilds the fixture assemblies LegacyModuleHostTests and ModuleLoadFailureTests use. They are
# checked in because what they prove is how a module compiled against an older barakoCMS loads on this one, and the
# test project cannot compile against an older barakoCMS itself.
#
#   Barako.Fixture.Legacy42.dll  compiled against the BarakoCMS 4.2.0 package from nuget.org
#   Barako.Fixture.Broken.dll    compiled against StubCore, a barakoCMS that names a type no real
#                                barakoCMS has
#   Barako.Fixture.Library.dll   the same, with no module in it
#
# Built in a temporary copy, so this repository's Directory.Build.props, central package versions
# and locked restore do not apply to them. Run: bash BarakoCMS.Tests/Fixtures/LegacyModules/build.sh

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp -r "$here/Legacy42" "$here/StubCore" "$here/Broken" "$here/Library" "$work/"
printf '<Project />\n' > "$work/Directory.Build.props"
printf '<Project />\n' > "$work/Directory.Build.targets"
printf '<Project />\n' > "$work/Directory.Packages.props"

dotnet build "$work/Legacy42/Legacy42.csproj" -c Release -o "$work/out/legacy42" --nologo -v q
dotnet build "$work/Broken/Broken.csproj" -c Release -o "$work/out/broken" --nologo -v q
dotnet build "$work/Library/Library.csproj" -c Release -o "$work/out/library" --nologo -v q

cp "$work/out/legacy42/Barako.Fixture.Legacy42.dll" "$here/"
cp "$work/out/broken/Barako.Fixture.Broken.dll" "$here/"
cp "$work/out/library/Barako.Fixture.Library.dll" "$here/"

# barakoCMS-4.x-public-types.txt is every public type in barakoCMS.dll across the 4.0.0, 4.0.1,
# 4.1.0, 4.2.0, 4.2.1, 4.3.0 and 4.4.0 packages, by metadata name (Outer+Nested, Generic`1), read
# with System.Reflection.Metadata. Add each new 4.x release's public types to it.
