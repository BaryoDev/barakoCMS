#!/usr/bin/env bash
# Merges origin/master into the current branch, regenerates lock files when a merge touched a
# csproj or Directory.Packages.props, and stages them. Lists conflicts and exits 1 if any, so an
# agent can pick those up rather than a broken merge landing on the branch.
#
#   bash scripts/sync-master.sh

set -uo pipefail

cd "$(dirname "$0")/.."

fail() { echo "sync-master: $1"; exit 1; }

echo "== fetch =="
git fetch origin || fail "fetch failed"

# Git refuses a merge outright when the working tree is dirty, before it ever writes conflict
# markers. Catch that up front so the failure reads as "dirty tree", not as an empty conflict list.
if [ -n "$(git status --porcelain)" ]; then
  fail "working tree has uncommitted changes; commit or stash before running sync-master"
fi

before=$(git rev-parse HEAD)

echo "== merge origin/master =="
if ! git merge origin/master --no-edit; then
  conflicts=$(git diff --name-only --diff-filter=U)
  if [ -z "$conflicts" ]; then
    fail "merge failed with no conflict markers; check for a dirty working tree or another git error above"
  fi
  echo "sync-master: merge conflicts in:"
  printf '%s\n' "$conflicts"
  exit 1
fi

changed=$(git diff --name-only "$before" HEAD)

if printf '%s\n' "$changed" | grep -Eq '(^|/)[^/]+\.csproj$|(^|/)Directory\.Packages\.props$'; then
  echo "== csproj or Directory.Packages.props changed; regenerating lock files =="
  dotnet restore barakoCMS.sln --force-evaluate || fail "dotnet restore --force-evaluate failed"
  lock_files=$(git status --porcelain -- '**/packages.lock.json' | awk '{print $2}')
  if [ -n "$lock_files" ]; then
    git add -- $lock_files
    echo "staged lock files:"
    printf '%s\n' "$lock_files"
  else
    echo "no lock file changes after restore"
  fi
else
  echo "no csproj or Directory.Packages.props change; lock files untouched"
fi

echo "sync-master: merged origin/master cleanly, no conflicts"
