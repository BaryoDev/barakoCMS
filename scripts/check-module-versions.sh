#!/usr/bin/env bash
# Fails when a module's source has changed since the commit that set its current <Version>.
#
# Why this exists: releases push with --skip-duplicate, so a module whose version was not bumped is
# silently skipped and its changes never reach anyone. That has swallowed a shipped fix twice — an
# audit-log capture (3.12.1) and, worse, the social sign-in MFA gate (3.17.1), which left a security
# fix sitting in source while every consumer still had the bypass. Neither was noticed at release
# time, because nothing looked.
#
# The check: for each module, find the commit that last set the version currently in its .csproj,
# then look for later commits touching that module's code. Any, and the version needs bumping.
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

  # Code changes after that point. Exclude the .csproj itself: editing dependencies or metadata
  # there is not a reason to republish on its own, and including it makes every bump self-trigger.
  # packages.lock.json is excluded for the same reason: it follows Directory.Packages.props, which
  # already sits outside every module directory, so a dependency bump keeps not forcing a version
  # bump on every module at once.
  changes=$(git log --format=%h "$version_commit"..HEAD -- "$module" ':!*.csproj' ':!*/packages.lock.json' | wc -l | tr -d ' ')

  if [ "$changes" -gt 0 ]; then
    failed=1
    echo "::error::$module is at $version but has $changes commit(s) of source changes since that version was set. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    git log --oneline "$version_commit"..HEAD -- "$module" ':!*.csproj' ':!*/packages.lock.json' | sed 's/^/    /'
  fi
done

if [ "$failed" -ne 0 ]; then
  echo ""
  echo "One or more modules changed without a version bump. See CHANGELOG 3.12.1 and 3.17.1 for what happens when this ships."
  exit 1
fi

echo "All module versions account for their source changes."
