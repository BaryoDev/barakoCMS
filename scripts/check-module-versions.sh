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
# With no tag declaring the version, nothing in the repository says what the package holding that
# number contains, so the commit range decides nothing and NuGet decides alone (see below).
#
# Run locally with: bash scripts/check-module-versions.sh
# Tested by:        bash scripts/test-check-module-versions.sh

set -uo pipefail

# The pathspecs below use exclude magic, and these variables switch magic off or change how a
# pattern matches. With one of them set by the caller, the commit count quietly comes back empty.
unset GIT_LITERAL_PATHSPECS GIT_GLOB_PATHSPECS GIT_NOGLOB_PATHSPECS GIT_ICASE_PATHSPECS

# One reader for the working tree, for a commit and for every tag: the first line that holds a
# whole <Version> element, and what sits between the last <Version> on it and the first </Version>
# after that. <PackageVersion> is not a <Version>. Two readers that disagree on an odd line would
# make the working tree's number one that no tag can ever be seen to declare.
version_line='/<Version>.*<\/Version>/'
version_text='sub(/.*<Version>/, "", text); sub(/<\/Version>.*/, "", text)'
read_version() { awk "$version_line { text = \$0; $version_text; print text; exit }"; }

# Asks NuGet whether a module's version is published, and leaves the answer in $nuget:
#
#   never      404, the package has never been published. The safest case, not an error.
#   absent     200, and this version is not in the list.
#   published  200, and it is.
#   unknown    anything else (no network, 5xx). Unknown enforces the check: a gate that passes
#              because it could not ask is the one outcome worth avoiding.
#
# CHECK_MODULE_VERSIONS_NUGET_DIR replaces the network with a directory of canned answers, one
# <lowercased package id>.json per published package, in the flat-container index shape. A missing
# file is a 404 and a missing directory is an unreachable NuGet. It exists for the test script, so a
# test never depends on nuget.org or on what has been published since it was written.
nuget=""
ask_nuget() { # $1 = csproj, $2 = module, $3 = version
  local package id body status
  package=$(sed -n 's/.*<PackageId>\(.*\)<\/PackageId>.*/\1/p' "$1" | head -1)
  [ -z "$package" ] && package="$2"
  id=$(echo "$package" | tr 'A-Z' 'a-z')
  body=$(mktemp)
  if [ -n "${CHECK_MODULE_VERSIONS_NUGET_DIR:-}" ]; then
    if [ ! -d "$CHECK_MODULE_VERSIONS_NUGET_DIR" ]; then
      status=000
    elif [ -f "$CHECK_MODULE_VERSIONS_NUGET_DIR/$id.json" ]; then
      cat "$CHECK_MODULE_VERSIONS_NUGET_DIR/$id.json" > "$body"
      status=200
    else
      status=404
    fi
  else
    status=$(curl -s --max-time 20 -o "$body" -w '%{http_code}' \
      "https://api.nuget.org/v3-flatcontainer/$id/index.json" 2>/dev/null || echo 000)
  fi
  if [ "$status" = "404" ]; then
    nuget="never"
    echo "::notice::$2: $package has never been published, so $3 publishes fresh and nothing is skipped."
  elif [ "$status" = "200" ] && ! grep -qF "\"$3\"" "$body"; then
    nuget="absent"
    echo "::notice::$2: $3 is not on NuGet yet, so the release publishes it fresh and nothing is skipped."
  elif [ "$status" = "200" ]; then
    nuget="published"
  else
    nuget="unknown, HTTP $status"
  fi
  rm -f "$body"
}

# Commits that touched a module's code in a range. Excludes the .csproj itself: editing dependencies
# or metadata there is not a reason to republish on its own, and including it makes every bump
# self-trigger. packages.lock.json is excluded for the same reason: it follows
# Directory.Packages.props, which already sits outside every module directory, so a dependency bump
# keeps not forcing a version bump on every module at once.
module_commits() { # $1 = range, $2 = module
  git log --no-color --oneline "$1" -- "$2" ':!*.csproj' ':!*/packages.lock.json'
}

if [ -n "${CHECK_MODULE_VERSIONS_NUGET_DIR:-}" ]; then
  echo "::notice::NuGet answers are read from $CHECK_MODULE_VERSIONS_NUGET_DIR, not from nuget.org."
fi

csprojs=()
for csproj in BarakoCMS.*/BarakoCMS.*.csproj; do
  [ -f "$csproj" ] || continue
  [ "$(dirname "$csproj")" = "BarakoCMS.Tests" ] && continue
  csprojs+=("$csproj")
done
if [ "${#csprojs[@]}" -eq 0 ]; then
  echo "::error::No BarakoCMS.*/BarakoCMS.*.csproj here. Run this from the repository root."
  exit 1
fi

# Release tags, oldest first, so the first one that declares a version is the release that
# published it. Every v* tag counts, not only the ones merged into HEAD: a branch cut before the
# latest release still has to measure from that release, and <tag>..HEAD is then the branch's own
# commits, which is the right answer.
#
# Any v* tag is taken to be a release. The release workflow is the only thing that makes them, and
# whatever tree it tags it has also published, a release candidate included.
#
# Oldest means the order of the tagged commits in history: a release comes after every release it
# is built on, and commit time settles the rest. Not the tag's own date, which for a tag deleted and
# made again as an annotated one is the day it was remade. Not its name: v4.10.0 comes before
# v4.9.0 as text, and a patch to an older line is tagged after newer ones. Not commit time alone
# either, which ties when two commits share a second and inverts when a clock was wrong.
release_tags=()
tagged=$(git for-each-ref --format='%(objecttype)|%(objectname)|%(*objecttype)|%(*objectname)|%(refname:short)' 'refs/tags/v*' |
  awk -F'|' '$1 == "commit" { print $2, $5; next } $3 == "commit" { print $4, $5 }')
if [ -n "$tagged" ]; then
  # shellcheck disable=SC2046
  history=$(git rev-list --date-order --reverse $(cut -d' ' -f1 <<< "$tagged" | sort -u)) || {
    echo "::error::Could not read the history behind the release tags, so they cannot be put in order."
    exit 1
  }
  while IFS= read -r tag; do
    [ -n "$tag" ] && release_tags+=("$tag")
  done < <(awk 'NR == FNR { at[$1] = NR; next } { print at[$1], $2 }' <(printf '%s\n' "$history") <(printf '%s\n' "$tagged") |
    sort -s -n -k1,1 | cut -d' ' -f2-)
fi
tag_count=${#release_tags[@]}

# The version every release tag declares for every module, read in one pass: one line per tag and
# .csproj, in the order above, as rank, tag, path, version. Asking git once per module per tag cost
# a process each, which grew with every release.
#
# This parses git grep's output, so everything a caller's configuration could do to that output is
# pinned here, and the paths are passed literally. If the scan fails or finds nothing at all, stop:
# an empty answer would read as "no tag declares anything", for every module at once.
declared=""
if [ "$tag_count" -eq 0 ]; then
  # CI fetches tags by name for this reason. Without them every module falls back to the commit
  # that set its version, and a failure here may be a published change reported as an unpublished one.
  echo "::warning::No release tags (v*) in this clone, so no module can be matched to the release that published it. Fetch them with: git fetch --tags"
else
  scan=$(git -c color.ui=false -c grep.patternType=basic -c grep.lineNumber=false -c grep.column=false -c grep.fullName=false \
    --literal-pathspecs grep --no-color -G -e '<Version>.*</Version>' "${release_tags[@]}" -- "${csprojs[@]}")
  scan_status=$?
  if [ "$scan_status" -le 1 ] && [ -n "$scan" ]; then
    declared=$(awk -v order="${release_tags[*]}" '
      BEGIN { n = split(order, t, " "); for (i = 1; i <= n; i++) rank[t[i]] = i }
      {
        tag = $0; sub(/:.*/, "", tag)
        rest = substr($0, length(tag) + 2)
        path = rest; sub(/:.*/, "", path)
        if (!(tag in rank) || (tag, path) in seen) next
        seen[tag, path] = 1
        text = substr(rest, length(path) + 2)
        '"$version_text"'
        printf "%d\t%s\t%s\t%s\n", rank[tag], tag, path, text
      }' <<< "$scan" | sort -s -n -k1,1)
  fi
  if [ -z "$declared" ]; then
    echo "::error::There are $tag_count release tags and no module version could be read from any of them (git grep exited $scan_status). Nothing was checked."
    exit 1
  fi
fi

failed=0

for csproj in "${csprojs[@]}"; do
  module=$(dirname "$csproj")

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
  # module only shares it when somebody set it that way. Compared as text, for this module's own
  # .csproj: as numbers 4.10 equals 4.1, and another module's 4.4.0 says nothing about this one.
  release_tag=$(awk -F'\t' -v p="$csproj" -v v="$version" '($3 "") == (p "") && ($4 "") == (v "") { print $2; exit }' <<< "$declared")

  if [ -n "$release_tag" ]; then
    # A release published this version. Anything after it is not in the package.
    #
    # The harm this check exists to prevent is --skip-duplicate silently dropping the push, and that
    # can only happen to a version already on NuGet. NuGet is asked only about a module with
    # changes, so a clean tree makes no calls.
    range="$release_tag..HEAD"
    changes=$(module_commits "$range" "$module" | wc -l | tr -d ' ')
    [ "$changes" -gt 0 ] || continue
    ask_nuget "$csproj" "$module" "$version"
    case "$nuget" in never | absent) continue ;; esac
    failed=1
    echo "::error::$module is at $version but has $changes commit(s) of source changes since $release_tag, the release that published $version. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    case "$nuget" in
      unknown*) echo "    NuGet could not be asked whether $version is published ($nuget), so the check is enforced." ;;
    esac
    module_commits "$range" "$module" | sed 's/^/    /'

  elif [ "$tag_count" -gt 0 ]; then
    # There are releases and none of them declares this version. No range of commits answers the
    # question then. Measured from the bump, code that landed before it is missed, and a commit that
    # touches only the .csproj is exactly what somebody writes when this check says "bump".
    # Measured from the last release, code from before that release is missed, which is the module
    # a release skipped and somebody then bumped to the number it skipped under. So NuGet is asked
    # whatever the commits say. Not published: the release pushes it fresh, with everything in it.
    # Published: the package was not built from any tree we can name, and it fails.
    #
    # This costs one call for each module sitting on a number no release has declared yet, which
    # is the modules bumped since the last release.
    ask_nuget "$csproj" "$module" "$version"
    case "$nuget" in never | absent) continue ;; esac
    failed=1
    first_tag=${release_tags[0]}
    newest_tag=${release_tags[$((tag_count - 1))]}
    if [ "$nuget" = "published" ]; then
      # Two states look like this from here and they want opposite things, so the message names
      # both and leads with the one where bumping is wrong. The release pushes to NuGet first and
      # tags afterwards, so between the two, or when tagging fails as it did for 4.0.0, every module
      # the release just published lands here.
      echo "::error::$module is at $version, which is already on NuGet, and none of the $tag_count release tags ($first_tag to $newest_tag) declares it in $csproj."
      echo "    Either a release has published this commit and not tagged it yet: check the release run and tag the commit it published, do not bump."
      echo "    Or the number is taken by an earlier publish, and what this module has that the package lacks is skipped at publish time (--skip-duplicate): set <Version> to the version of the release that will publish it."
    else
      echo "::error::$module is at $version, none of the $tag_count release tags ($first_tag to $newest_tag) declares it in $csproj, and NuGet could not be asked whether it is published ($nuget), so the check is enforced."
    fi
    if [ "$(module_commits "$newest_tag..HEAD" "$module" | wc -l | tr -d ' ')" -gt 0 ]; then
      echo "    Source changes since $newest_tag, the newest release tag:"
      module_commits "$newest_tag..HEAD" "$module" | sed 's/^/    /'
    fi

  else
    # No release tags in this clone, which the warning above has already said. All that is left to
    # measure from is the commit that set the version, and that commit counts as a change itself: a
    # bump usually arrives with the code that needed it. This is weaker than either branch above.
    if git rev-parse -q --verify "$version_commit^" >/dev/null; then
      range="$version_commit^..HEAD"
      since="$(git rev-parse --short "$version_commit"), the commit that set $version (counted too)"
    else
      range="HEAD"
      since="the first commit, which set $version"
    fi
    changes=$(module_commits "$range" "$module" | wc -l | tr -d ' ')
    [ "$changes" -gt 0 ] || continue
    ask_nuget "$csproj" "$module" "$version"
    case "$nuget" in never | absent) continue ;; esac
    failed=1
    echo "::error::$module is at $version, no release tag declares that version, and it has $changes commit(s) of source changes since $since. NuGet: $nuget. Bump <Version> in $csproj, or those changes will be skipped at publish time (--skip-duplicate)."
    case "$nuget" in
      unknown*) echo "    NuGet could not be asked whether $version is published ($nuget), so the check is enforced." ;;
    esac
    module_commits "$range" "$module" | sed 's/^/    /'
  fi
done

if [ "$failed" -ne 0 ]; then
  echo ""
  echo "One or more modules would be skipped at publish time, or cannot be shown not to be. Each message above says what to do about it. See CHANGELOG 3.12.1 and 3.17.1 for what happens when a skip ships."
  exit 1
fi

echo "All module versions account for their source changes."
