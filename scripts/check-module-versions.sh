#!/usr/bin/env bash
# Fails when a module's source has changed since the release that published its current <Version>.
#
# Why this exists: releases push with --skip-duplicate, so a module whose version was not bumped is
# silently skipped and its changes never reach anyone. That has swallowed a shipped fix twice: an
# audit-log capture (3.12.1) and, worse, the social sign-in MFA gate (3.17.1), which left a security
# fix sitting in source while every consumer still had the bypass. Neither was noticed at release
# time, because nothing looked.
#
# The check: for each module, find the release that published its declared version, then look for
# later commits touching that module's code. Any, and the version needs bumping.
#
# "The release that published it" is the earliest release tag whose tree declares this version in
# the module's .csproj. The release packs each module at the version its own .csproj carries and
# pushes with --skip-duplicate, so the first tagged tree to carry a number is the one whose package
# NuGet kept, and every later release with the same number was skipped. Two other references were
# tried first and both blocked every pull request from a clean master:
#
#   The commit that set the number in the .csproj. That is earlier than the release, usually by the
#   rest of the release, so it reported published changes as ones that would be skipped. It fired
#   after 4.0.0 (#645 set the version, the release commit then touched the module). See #675.
#
#   The tag named v<version>. A module's number is not always the release's number. Import and
#   Portability were set to 4.4.0 after v4.4.0 was tagged and first published by the 4.5.0 release,
#   so the tag v4.4.0 named a tree where they were still 4.3.0, and the check measured from before
#   the changes were written. It fired after 4.5.0. See #1041. The same thing happened after 4.1.0
#   with 4.0.1, and was read at the time as a skipped package (#749); the 4.0.1 packages on NuGet
#   were built from the v4.1.0 commit and had the changes in them.
#
# With no tag declaring the version it has not been released from a tagged tree, so the reference
# falls back to the newest release behind HEAD, and NuGet decides (see below).
#
# Run locally with: bash scripts/check-module-versions.sh
# Tested by:        bash scripts/test-check-module-versions.sh

set -uo pipefail

read_version() { sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' | head -1; }

# Asks NuGet which versions of a package exist. Writes the answer to $2 and prints the HTTP status.
#
# CHECK_MODULE_VERSIONS_NUGET_DIR replaces the network with a directory of canned answers, one
# <lowercased package id>.json per published package, in the flat-container index shape. A missing
# file is a 404 and a missing directory is an unreachable NuGet. It exists for the test script, so a
# test never depends on nuget.org or on what has been published since it was written.
nuget_index() { # $1 = package id, $2 = file for the body
  local id
  id=$(echo "$1" | tr 'A-Z' 'a-z')
  if [ -n "${CHECK_MODULE_VERSIONS_NUGET_DIR:-}" ]; then
    if [ ! -d "$CHECK_MODULE_VERSIONS_NUGET_DIR" ]; then
      echo 000
    elif [ -f "$CHECK_MODULE_VERSIONS_NUGET_DIR/$id.json" ]; then
      cat "$CHECK_MODULE_VERSIONS_NUGET_DIR/$id.json" > "$2"
      echo 200
    else
      echo 404
    fi
    return
  fi
  curl -s --max-time 20 -o "$2" -w '%{http_code}' \
    "https://api.nuget.org/v3-flatcontainer/$id/index.json" 2>/dev/null || echo 000
}

if [ -n "${CHECK_MODULE_VERSIONS_NUGET_DIR:-}" ]; then
  echo "::notice::NuGet answers are read from $CHECK_MODULE_VERSIONS_NUGET_DIR, not from nuget.org."
fi

# Release tags, oldest first, so the first one that declares a version is the release that
# published it. Every v* tag counts, not only the ones merged into HEAD: a branch cut before the
# latest release still has to measure from that release, and <tag>..HEAD is then the branch's own
# commits, which is the right answer.
#
# Any v* tag is taken to be a release. The release workflow is the only thing that makes them, and
# whatever tree it tags it has also published, a release candidate included.
#
# Oldest means the tagged commit's committer time, not the tag's own date and not its name. A tag
# deleted and made again as an annotated one carries the day it was remade, which would sort an old
# release after the ones that followed it. Names do not sort as releases either: v4.10.0 comes
# before v4.9.0 as text, and a patch to an older line is tagged after newer ones. A lightweight tag
# fills the first of the two fields below and an annotated one the second, never both.
release_tags=()
while IFS= read -r line; do
  tag=${line#* }
  [ -n "$tag" ] && release_tags+=("$tag")
done < <(git for-each-ref --format='%(committerdate:unix)%(*committerdate:unix) %(refname:short)' 'refs/tags/v*' | sort -s -n -k1,1)
tag_count=${#release_tags[@]}

# The version every release tag declares for every module, read in one pass: one line per tag and
# .csproj, in the order above, as rank, tag, path, version. Asking git once per module per tag cost
# a process each, which grew with every release.
declared=""
if [ "$tag_count" -gt 0 ]; then
  declared=$(git grep -e '<Version>.*</Version>' "${release_tags[@]}" -- ':(glob)BarakoCMS.*/BarakoCMS.*.csproj' 2>/dev/null |
    awk -v order="${release_tags[*]}" '
      BEGIN { n = split(order, t, " "); for (i = 1; i <= n; i++) rank[t[i]] = i }
      {
        tag = $0; sub(/:.*/, "", tag)
        rest = substr($0, length(tag) + 2)
        path = rest; sub(/:.*/, "", path)
        if ((tag, path) in seen) next
        seen[tag, path] = 1
        text = substr(rest, length(path) + 2)
        sub(/.*<Version>/, "", text); sub(/<\/Version>.*/, "", text)
        printf "%d\t%s\t%s\t%s\n", rank[tag], tag, path, text
      }' | sort -s -n -k1,1)
fi

# The newest release behind HEAD, for a module whose version no tag declares.
last_release=$(git describe --tags --abbrev=0 --match 'v*' HEAD 2>/dev/null || true)

if [ "$tag_count" -eq 0 ]; then
  # CI fetches tags by name for this reason. Without them every module falls back to the stricter
  # reference below, and a failure here may be a published change reported as an unpublished one.
  echo "::warning::No release tags (v*) in this clone, so no module can be matched to the release that published it. Fetch them with: git fetch --tags"
fi

failed=0

for csproj in BarakoCMS.*/BarakoCMS.*.csproj; do
  module=$(dirname "$csproj")
  [ "$module" = "BarakoCMS.Tests" ] && continue

  version=$(read_version < "$csproj")
  if [ -z "$version" ]; then
    echo "::warning::$module has no <Version>; skipping"
    continue
  fi

  # Find the commit that introduced the version currently declared, by walking the .csproj's history
  # newest-first and keeping the oldest consecutive commit that still declares this version.
  #
  # Do not reach for `git log -S` here: it matches any commit that changes how often the string
  # appears, which includes the commit that *removed* the previous version. The newest such hit is
  # then the bump itself, the range below is empty, and the check silently passes, which is exactly
  # how an earlier version of this script failed to catch the case it was written for.
  version_commit=""
  for commit in $(git log --format=%H -- "$csproj"); do
    commit_version=$(git show "$commit:$csproj" 2>/dev/null | read_version)
    if [ "$commit_version" = "$version" ]; then
      version_commit=$commit
    else
      break
    fi
  done

  if [ -z "$version_commit" ]; then
    # The working tree declares a version that HEAD does not: an in-progress bump, nothing to check.
    continue
  fi

  # What --skip-duplicate can drop is a change that is not in the published package, and the
  # published package is whatever the first release to carry this number built. Read the number out
  # of each tag's tree rather than trusting the tag's name: the name is the core's version, and a
  # module only shares it when somebody set it that way.
  release_tag=$(awk -F'\t' -v p="$csproj" -v v="$version" '$3 == p && $4 == v { print $2; exit }' <<< "$declared")

  # No tag declares it, so nothing says a release built this version from a tree we can name. The
  # reference is then the newest release behind HEAD: whatever holds this number on NuGet, no
  # tagged release put the code written since then into it. Measuring from the commit that set the
  # version is too late. Code can land first and the bump after it, in a commit that touches only
  # the .csproj, and that is exactly what somebody does when this check tells them to bump: the
  # range is then empty and the check passes without asking NuGet.
  #
  # With no release behind HEAD at all, the reference is the commit before the version was set, so
  # the version-setting commit counts. A bump usually arrives with the code that needed it.
  if [ -n "$release_tag" ]; then
    range="$release_tag..HEAD"
    since="$release_tag, the release that published $version"
  elif [ -n "$last_release" ]; then
    range="$last_release..HEAD"
    since="$last_release, the newest release behind HEAD"
  elif git rev-parse -q --verify "$version_commit^" >/dev/null; then
    range="$version_commit^..HEAD"
    since="$(git rev-parse --short "$version_commit"), the commit that set $version (counted too)"
  else
    range="HEAD"
    since="the first commit, which set $version"
  fi

  # Code changes after that point. Exclude the .csproj itself: editing dependencies or metadata
  # there is not a reason to republish on its own, and including it makes every bump self-trigger.
  # packages.lock.json is excluded for the same reason: it follows Directory.Packages.props, which
  # already sits outside every module directory, so a dependency bump keeps not forcing a version
  # bump on every module at once.
  changes=$(git log --format=%h "$range" -- "$module" ':!*.csproj' ':!*/packages.lock.json' | wc -l | tr -d ' ')

  # The harm this check exists to prevent is --skip-duplicate silently dropping the push, and that
  # can only happen to a version already on NuGet. When the declared version is not published, the
  # release pushes it fresh and carries every change with it, so there is nothing to skip. Without
  # this, the first release of any version fails as soon as a module is touched after its version
  # was set, which is the whole pre-release window.
  #
  # NuGet is only asked about a module that is about to fail, so a clean tree makes no calls.
  #
  # The three answers are treated differently on purpose. 404 means the package has never been
  # published, which is the safest case, not an error. 200 lets us ask whether this version is in
  # the list. Anything else (no network, 5xx) is unknown, and unknown enforces the check: a gate
  # that passes because it could not ask is the one outcome worth avoiding.
  nuget="not asked"
  if [ "$changes" -gt 0 ]; then
    package=$(sed -n 's/.*<PackageId>\(.*\)<\/PackageId>.*/\1/p' "$csproj" | head -1)
    [ -z "$package" ] && package="$module"
    body=$(mktemp)
    status=$(nuget_index "$package" "$body")
    if [ "$status" = "404" ]; then
      echo "::notice::$module: $package has never been published, so $version publishes fresh and nothing is skipped."
      changes=0
    elif [ "$status" = "200" ] && ! grep -qF "\"$version\"" "$body"; then
      echo "::notice::$module: $version is not on NuGet yet, so the release publishes it fresh and nothing is skipped."
      changes=0
    elif [ "$status" = "200" ]; then
      nuget="published"
    else
      nuget="unknown, HTTP $status"
    fi
    rm -f "$body"
  fi

  if [ "$changes" -gt 0 ]; then
    failed=1
    if [ -n "$release_tag" ]; then
      echo "::error::$module is at $version but has $changes commit(s) of source changes since $since. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    elif [ "$nuget" = "published" ] && [ "$tag_count" -gt 0 ]; then
      # On NuGet, and no tagged tree declares it. Two states look like this from here and they want
      # opposite things, so the message names both and leads with the one where bumping is wrong.
      # The release pushes to NuGet first and tags afterwards, so between the two, or when tagging
      # fails as it did for 4.0.0, every module the release just published lands here.
      echo "::error::$module is at $version, which is already on NuGet, and none of the $tag_count release tags (${release_tags[0]} to ${release_tags[$((tag_count - 1))]}) declares it in $csproj. $changes commit(s) of source changes since $since."
      echo "    Either a release has published this commit and not tagged it yet: check the release run and tag the commit it published, do not bump."
      echo "    Or the number is taken by an earlier publish and these commits are not in it: set <Version> to the version of the release that will publish them, or they are skipped at publish time (--skip-duplicate)."
    else
      echo "::error::$module is at $version, no release tag declares that version, and it has $changes commit(s) of source changes since $since. NuGet: $nuget. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    fi
    case "$nuget" in
      unknown*) echo "    NuGet could not be asked whether $version is published ($nuget), so the check is enforced." ;;
    esac
    git log --oneline "$range" -- "$module" ':!*.csproj' ':!*/packages.lock.json' | sed 's/^/    /'
  fi
done

if [ "$failed" -ne 0 ]; then
  echo ""
  echo "One or more modules changed without a version bump. See CHANGELOG 3.12.1 and 3.17.1 for what happens when this ships."
  exit 1
fi

echo "All module versions account for their source changes."
