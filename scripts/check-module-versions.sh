#!/usr/bin/env bash
# Fails when a module's source has changed since the commit that set its current <Version>.
#
# Why this exists: releases push with --skip-duplicate, so a module whose version was not bumped is
# silently skipped and its changes never reach anyone. That has swallowed a shipped fix twice — an
# audit-log capture (3.12.1) and, worse, the social sign-in MFA gate (3.17.1), which left a security
# fix sitting in source while every consumer still had the bypass. Neither was noticed at release
# time, because nothing looked.
#
# The check: for each module, find where its declared version was released, then look for later
# commits touching that module's code. Any, and the version needs bumping.
#
# "Where it was released" is the tag v<version> when that exists, not the commit that set the number
# in the .csproj. Those are different commits and the gap between them is the rest of the release,
# which is published. Comparing against the version-setting commit reported every one of those as a
# change that would be skipped, which is backwards: they are in the package. It fired on clean master
# after 4.0.0 (#645 set the version, the release commit then touched the module) and blocked every
# pull request until somebody bumped a module for no reason. See #675.
#
# With no such tag the version has not been released under this scheme, so it falls back to the
# version-setting commit and the NuGet check below decides.
#
# Run locally with: bash scripts/check-module-versions.sh

set -uo pipefail

failed=0

for csproj in BarakoCMS.*/BarakoCMS.*.csproj; do
  module=$(dirname "$csproj")
  [ "$module" = "BarakoCMS.Tests" ] && continue

  version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$csproj" | head -1)
  if [ -z "$version" ]; then
    echo "::warning::$module has no <Version>; skipping"
    continue
  fi

  # Find the commit that introduced the version currently declared, by walking the .csproj's history
  # newest-first and keeping the oldest consecutive commit that still declares this version.
  #
  # Do not reach for `git log -S` here: it matches any commit that changes how often the string
  # appears, which includes the commit that *removed* the previous version. The newest such hit is
  # then the bump itself, the range below is empty, and the check silently passes — which is exactly
  # how an earlier version of this script failed to catch the case it was written for.
  version_commit=""
  for commit in $(git log --format=%H -- "$csproj"); do
    commit_version=$(git show "$commit:$csproj" 2>/dev/null | sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' | head -1)
    if [ "$commit_version" = "$version" ]; then
      version_commit=$commit
    else
      break
    fi
  done

  if [ -z "$version_commit" ]; then
    # The working tree declares a version that HEAD does not — an in-progress bump, nothing to check.
    continue
  fi

  # What --skip-duplicate can drop is a change that is not in the published package, and the
  # published package is whatever the release built, which the tag names. The commit that set the
  # number in the .csproj is earlier than that, usually by the rest of the release, so measuring from
  # it counts published changes as unpublished ones.
  #
  # No tag means this version was never released under this scheme. Fall back to the version-setting
  # commit, which is the older, stricter reference, and let the NuGet check below decide.
  if git rev-parse -q --verify "refs/tags/v$version^{commit}" >/dev/null; then
    reference="v$version"
  else
    reference="$version_commit"
  fi

  # Code changes after that point. Exclude the .csproj itself: editing dependencies or metadata
  # there is not a reason to republish on its own, and including it makes every bump self-trigger.
  # packages.lock.json is excluded for the same reason: it follows Directory.Packages.props, which
  # already sits outside every module directory, so a dependency bump keeps not forcing a version
  # bump on every module at once.
  changes=$(git log --format=%h "$reference"..HEAD -- "$module" ':!*.csproj' ':!*/packages.lock.json' | wc -l | tr -d ' ')

  # The harm this check exists to prevent is --skip-duplicate silently dropping the push, and that
  # can only happen to a version already on NuGet. When the declared version is not published, the
  # release pushes it fresh and carries every change with it, so there is nothing to skip. Without
  # this, the first release of any version fails as soon as a module is touched after its version
  # was set, which is the whole pre-release window.
  #
  # The three answers are treated differently on purpose. 404 means the package has never been
  # published, which is the safest case, not an error. 200 lets us ask whether this version is in
  # the list. Anything else (no network, 5xx) is unknown, and unknown enforces the check: a gate
  # that passes because it could not ask is the one outcome worth avoiding.
  if [ "$changes" -gt 0 ]; then
    package=$(sed -n 's/.*<PackageId>\(.*\)<\/PackageId>.*/\1/p' "$csproj" | head -1)
    [ -z "$package" ] && package="$module"
    body=$(mktemp)
    status=$(curl -s --max-time 20 -o "$body" -w '%{http_code}' \
      "https://api.nuget.org/v3-flatcontainer/$(echo "$package" | tr 'A-Z' 'a-z')/index.json" 2>/dev/null || echo 000)
    if [ "$status" = "404" ]; then
      echo "::notice::$module: $package has never been published, so $version publishes fresh and nothing is skipped."
      changes=0
    elif [ "$status" = "200" ] && ! grep -q "\"$version\"" "$body"; then
      echo "::notice::$module: $version is not on NuGet yet, so the release publishes it fresh and nothing is skipped."
      changes=0
    fi
    rm -f "$body"
  fi

  if [ "$changes" -gt 0 ]; then
    failed=1
    echo "::error::$module is at $version but has $changes commit(s) of source changes since $reference. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    git log --oneline "$reference"..HEAD -- "$module" ':!*.csproj' ':!*/packages.lock.json' | sed 's/^/    /'
  fi
done

if [ "$failed" -ne 0 ]; then
  echo ""
  echo "One or more modules changed without a version bump. See CHANGELOG 3.12.1 and 3.17.1 for what happens when this ships."
  exit 1
fi

echo "All module versions account for their source changes."
