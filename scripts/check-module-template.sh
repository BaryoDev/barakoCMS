#!/usr/bin/env bash
# Proves the module template from the outside: packs core, BarakoCMS.Testing and BarakoCMS.Templates
# into a local feed, installs the template from that feed, creates a module with it, builds the
# module and runs its tests against the packages in the feed.
#
#   scripts/check-module-template.sh [work-dir]
#
# BARAKO_TEMPLATE_FEED   a directory already holding the packed .nupkg files (the release workflow
#                        hands over the artifact it is about to publish); unset, the script packs.
# BARAKO_SKIP_BUILD=1    the solution is already built in Release (CI builds it once); pack only.
# BARAKO_TEMPLATE_SKIP_TESTS=1
#                        build the generated module but do not run its tests (they need Docker).
#
# The restore uses its own packages directory. A warm ~/.nuget cache holds a BarakoCMS of the same
# version from nuget.org, and NuGet serves a cached package without asking any source, so a shared
# cache would prove nothing about the package just built. Source mapping sends BarakoCMS* to the
# feed and everything else to nuget.org for the same reason.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=${1:-$(mktemp -d)}
mkdir -p "$WORK"
WORK=$(cd "$WORK" && pwd)
FEED=${BARAKO_TEMPLATE_FEED:-$WORK/feed}
SAMPLE=Acme.BarakoCMS.Sample

if [ -z "${BARAKO_TEMPLATE_FEED:-}" ]; then
  mkdir -p "$FEED"
  for p in barakoCMS BarakoCMS.Testing BarakoCMS.Templates; do
    # Build, then pack --no-build, the way the release does. Core sets GeneratePackageOnBuild, and
    # a pack that builds as it goes reaches the packing step before the runtime config it packs
    # has been written (NU5026).
    if [ "${BARAKO_SKIP_BUILD:-0}" != "1" ]; then
      dotnet build "$ROOT/$p/$p.csproj" --configuration Release
    fi
    dotnet pack "$ROOT/$p/$p.csproj" --no-build --configuration Release -o "$FEED"
  done
fi

TEMPLATE_PKG=$(ls "$FEED"/BarakoCMS.Templates.*.nupkg | head -1)
[ -n "$TEMPLATE_PKG" ] || { echo "::error::No BarakoCMS.Templates package in $FEED."; exit 1; }
ls "$FEED"/BarakoCMS.Testing.*.nupkg >/dev/null 2>&1 || { echo "::error::No BarakoCMS.Testing package in $FEED."; exit 1; }

export NUGET_PACKAGES="$WORK/packages"

dotnet new uninstall BarakoCMS.Templates >/dev/null 2>&1 || true
trap 'dotnet new uninstall BarakoCMS.Templates >/dev/null 2>&1 || true' EXIT
dotnet new install "$TEMPLATE_PKG"

OUT="$WORK/out"
rm -rf "$OUT"
mkdir -p "$OUT"
cat > "$OUT/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-feed" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-feed">
      <package pattern="BarakoCMS*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

cd "$OUT"
dotnet new barakocms-module -n "$SAMPLE"
cd "$SAMPLE"

echo "--- generated files"
find . -type f | sort

# The short name has to reach every place the template derives it, or the module registers under
# one name and reads configuration under another.
grep -q 'public string Name => "Sample";' "src/$SAMPLE/SampleModule.cs" \
  || { echo "::error::The module's Name was not derived from the project name."; exit 1; }
grep -q 'Get("/api/sample/notes");' "src/$SAMPLE/Features/Notes/List/Endpoint.cs" \
  || { echo "::error::The route was not derived from the module name."; exit 1; }
grep -q 'barakocms-module' Directory.Build.props \
  || { echo "::error::The generated props file lost the barakocms-module tag."; exit 1; }
if grep -rq 'MyBarakoModule\|ModuleName\|BARAKOCMS_\|TEMPLATE_' --include='*.cs' --include='*.csproj' --include='*.props' --include='*.md' --include='*.slnx' .; then
  echo "::error::A template placeholder survived into the generated module:"
  grep -rn 'MyBarakoModule\|ModuleName\|BARAKOCMS_\|TEMPLATE_' --include='*.cs' --include='*.csproj' --include='*.props' --include='*.md' --include='*.slnx' .
  exit 1
fi

dotnet restore
dotnet build --no-restore --configuration Release

# The module packs the way the packaging tests expect a module to pack: with a README and an icon.
dotnet pack "src/$SAMPLE/$SAMPLE.csproj" --no-build --configuration Release -o "$WORK/sample-out"
unzip -Z1 "$WORK/sample-out/$SAMPLE.0.1.0.nupkg" | grep -qx 'README.md' \
  || { echo "::error::The generated module's package has no README."; exit 1; }
unzip -Z1 "$WORK/sample-out/$SAMPLE.0.1.0.nupkg" | grep -qx 'icon.png' \
  || { echo "::error::The generated module's package has no icon."; exit 1; }
unzip -p "$WORK/sample-out/$SAMPLE.0.1.0.nupkg" "$SAMPLE.nuspec" | grep -q 'barakocms-module' \
  || { echo "::error::The generated module's package does not carry the barakocms-module tag."; exit 1; }

if [ "${BARAKO_TEMPLATE_SKIP_TESTS:-0}" = "1" ]; then
  echo "Generated module builds and packs. Tests skipped (BARAKO_TEMPLATE_SKIP_TESTS=1)."
  exit 0
fi

# --project, the new dotnet test on the .NET 10 SDK: the generated global.json opts the module into
# Microsoft.Testing.Platform, which is the only runner xunit.v3 supports there.
dotnet test --project "tests/$SAMPLE.Tests/$SAMPLE.Tests.csproj" --no-build --configuration Release
echo "Generated module builds, packs and passes its tests."
