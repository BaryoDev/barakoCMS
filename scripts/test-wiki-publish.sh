#!/usr/bin/env bash
# Tests that wiki-publish.sh exits non-zero whenever the wiki did not end up synced, and zero only
# when it pushed or had nothing to push.
#
# The workflow that runs it is green or red on that exit code alone, so the case that matters most
# is a run that published nothing and still exited zero: a rejected push, a failed sync, a commit
# that never left the runner.
#
# No network. A throwaway source repository holds a copy of the two scripts and a small docs/, and
# a local bare repository named *.wiki.git stands in for the wiki.
#
#   bash scripts/test-wiki-publish.sh

set -uo pipefail

SCRIPTS=$(cd -- "$(dirname -- "$0")" && pwd)

# The developer's own git configuration must not decide the result: a signing key, a hook path or a
# default branch name would each change what these cases see.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
unset GITHUB_STEP_SUMMARY

pass=0
fail=0

check() { # $1 = description, $2 = condition already evaluated as an exit code
  if [ "$2" = 0 ]; then
    echo "  ok    $1"
    pass=$((pass + 1))
  else
    echo "  FAIL  $1"
    fail=$((fail + 1))
  fi
}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

SRC="$WORK/source"
REMOTE="$WORK/remote/fixture.wiki.git"
WIKI="$WORK/wiki"
BOT_NAME="wiki bot"
BOT_EMAIL="wiki-bot@example.invalid"

fixture_git() { git -c user.name=fixture -c user.email=fixture@example.invalid "$@"; }
remote_head() { git -C "$REMOTE" rev-parse --verify --quiet refs/heads/master; }
publish() { # output to $WORK/out, exit code returned
  WIKI_COMMIT_NAME="$BOT_NAME" WIKI_COMMIT_EMAIL="$BOT_EMAIL" \
    bash "$SRC/scripts/wiki-publish.sh" "$WIKI" >"$WORK/out" 2>&1
}
fresh_clone() { rm -rf "$WIKI"; git clone --quiet "$REMOTE" "$WIKI"; }

# ---------------------------------------------------------------- fixture

mkdir -p "$SRC/scripts" "$SRC/docs" "$WORK/remote"
cp "$SCRIPTS/wiki-sync.sh" "$SCRIPTS/wiki-publish.sh" "$SRC/scripts/"
printf '# Blueprints\n\nSee [webhooks](webhooks.md) and [the script](../scripts/wiki-sync.sh).\n' >"$SRC/docs/blueprints.md"
printf '# Webhooks\n\nFirst version.\n' >"$SRC/docs/webhooks.md"
git init --quiet --initial-branch=master "$SRC"
fixture_git -C "$SRC" add -A
fixture_git -C "$SRC" commit --quiet -m "source fixture"

git init --quiet --bare --initial-branch=master "$REMOTE"
git clone --quiet "$REMOTE" "$WORK/seed" 2>/dev/null
printf '# Home\n\nWritten by hand.\n' >"$WORK/seed/Home.md"
fixture_git -C "$WORK/seed" add -A
fixture_git -C "$WORK/seed" commit --quiet -m "first page"
git -C "$WORK/seed" push --quiet origin HEAD:refs/heads/master
SEEDED=$(remote_head)
[ -n "$SEEDED" ]; check "the fixture wiki has its first page" $?

# ---------------------------------------------------------------- cases

echo "== a first publish commits and pushes =="
fresh_clone
SOURCE_SHA=$(git -C "$SRC" rev-parse HEAD)
publish; check "it exits zero" $?
PUSHED=$(remote_head)
[ -n "$PUSHED" ] && [ "$PUSHED" != "$SEEDED" ]; check "the wiki moved to a new commit" $?
[ "$(git -C "$REMOTE" rev-list --count "$SEEDED..master")" = 1 ]; check "it is one commit on top of the first page" $?
[ "$(git -C "$REMOTE" log -1 --format=%B master)" = "sync docs from BaryoDev/barakoCMS@$SOURCE_SHA" ]
check "the message is one line naming the source commit, with no trailer" $?
[ "$(git -C "$REMOTE" log -1 --format='%an <%ae> / %cn <%ce>' master)" = "$BOT_NAME <$BOT_EMAIL> / $BOT_NAME <$BOT_EMAIL>" ]
check "author and committer are the identity it was given" $?
PAGES=$(git -C "$REMOTE" ls-tree --name-only master)
for page in Home.md blueprints.md webhooks.md Docs.md _Sidebar.md .wiki-sync-manifest; do
  printf '%s\n' "$PAGES" | grep -qxF "$page"; check "the pushed tree has $page" $?
done
git -C "$REMOTE" show master:webhooks.md | grep -q '^First version\.$'; check "the page holds the doc's text" $?
git -C "$REMOTE" show master:Home.md | grep -q '^Written by hand\.$'; check "the hand-written page is untouched" $?
grep -q "^wiki-publish: published $PUSHED to master" "$WORK/out"; check "it says what it published" $?
grep -q "^wiki-publish: publishing docs/ at BaryoDev/barakoCMS@$SOURCE_SHA, checked out on master$" "$WORK/out"
check "it names the source commit and branch before it writes" $?

echo "== a second run with nothing changed pushes nothing and says so =="
fresh_clone
publish; check "it exits zero" $?
[ "$(remote_head)" = "$PUSHED" ]; check "the wiki did not move" $?
grep -q '^wiki-publish: nothing to publish' "$WORK/out"; check "it says there was nothing to publish" $?
[ -z "$(git -C "$WIKI" status --porcelain)" ]; check "the clone is left clean" $?

echo "== a changed doc is published =="
printf '# Webhooks\n\nSecond version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "change a doc"
fresh_clone
publish; check "it exits zero" $?
CHANGED=$(remote_head)
[ "$CHANGED" != "$PUSHED" ]; check "the wiki moved" $?
git -C "$REMOTE" show master:webhooks.md | grep -q '^Second version\.$'; check "the page holds the new text" $?
git -C "$REMOTE" log -1 --format=%s master | grep -qF "@$(git -C "$SRC" rev-parse HEAD)"; check "the message names the new source commit" $?

echo "== a push rejected because the wiki moved fails =="
printf '# Webhooks\n\nThird version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "change it again"
fresh_clone
git clone --quiet "$REMOTE" "$WORK/other"
printf '# Home\n\nEdited on the web.\n' >"$WORK/other/Home.md"
fixture_git -C "$WORK/other" commit --quiet -am "a hand edit"
git -C "$WORK/other" push --quiet origin HEAD:refs/heads/master
MOVED=$(remote_head)
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$MOVED" ]; check "the wiki still ends at the hand edit" $?
grep -q 'push rejected: the wiki moved' "$WORK/out"; check "it says the wiki moved" $?
! grep -q '^wiki-publish: published' "$WORK/out"; check "it does not claim to have published" $?

echo "== the rerun on a fresh clone then publishes on top of the hand edit =="
fresh_clone
publish; check "it exits zero" $?
[ "$(git -C "$REMOTE" rev-parse master~1)" = "$MOVED" ]; check "the sync commit sits on the hand edit" $?
git -C "$REMOTE" show master:Home.md | grep -q '^Edited on the web\.$'; check "the hand edit survived" $?
git -C "$REMOTE" show master:webhooks.md | grep -q '^Third version\.$'; check "the page holds the new text" $?

echo "== a push the remote refuses fails =="
printf '# Webhooks\n\nFourth version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "and again"
BEFORE=$(remote_head)
printf '#!/bin/sh\necho "refused by the fixture" >&2\nexit 1\n' >"$REMOTE/hooks/pre-receive"
chmod +x "$REMOTE/hooks/pre-receive"
fresh_clone
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
grep -q 'push failed and the wiki is still at' "$WORK/out"; check "it says the push failed" $?
rm -f "$REMOTE/hooks/pre-receive"

echo "== a push that reports success without moving the wiki fails =="
# A git that answers every push with exit zero and does nothing, in front of the real one.
REAL_GIT=$(command -v git)
mkdir -p "$WORK/shim"
cat >"$WORK/shim/git" <<SHIM
#!/bin/sh
for arg in "\$@"; do [ "\$arg" = push ] && exit 0; done
exec "$REAL_GIT" "\$@"
SHIM
chmod +x "$WORK/shim/git"
fresh_clone
PATH="$WORK/shim:$PATH" publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
grep -q 'push reported success but master on the wiki is at .* which does not contain' "$WORK/out"; check "it says the branch does not contain the commit" $?
fixture_git -C "$SRC" reset --quiet --hard HEAD~1

echo "== a sync that fails commits nothing and pushes nothing =="
printf '# Webhooks\n\nSee [a doc that is gone](gone.md).\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "a broken link"
BEFORE=$(remote_head)
fresh_clone
CLONE_HEAD=$(git -C "$WIKI" rev-parse HEAD)
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
[ "$(git -C "$WIKI" rev-parse HEAD)" = "$CLONE_HEAD" ]; check "no commit was made in the clone" $?
[ -z "$(git -C "$WIKI" status --porcelain)" ]; check "the clone was not written to" $?
grep -q 'wiki-sync.sh failed, nothing was committed or pushed' "$WORK/out"; check "it says the sync failed" $?
fixture_git -C "$SRC" reset --quiet --hard HEAD~1

echo "== a clone with uncommitted changes is refused =="
BEFORE=$(remote_head)
fresh_clone
printf 'a stray note\n' >"$WIKI/Scratch.md"
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
grep -q 'has uncommitted changes' "$WORK/out"; check "it names the reason" $?

echo "== uncommitted doc changes in the source are refused =="
fresh_clone
printf '# Webhooks\n\nNot committed.\n' >"$SRC/docs/webhooks.md"
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
grep -q 'uncommitted changes in the source checkout' "$WORK/out"; check "it names the reason" $?
fixture_git -C "$SRC" checkout --quiet -- docs/webhooks.md

echo "== half an identity is refused =="
fresh_clone
WIKI_COMMIT_NAME="$BOT_NAME" WIKI_COMMIT_EMAIL="" bash "$SRC/scripts/wiki-publish.sh" "$WIKI" >"$WORK/out" 2>&1; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?

echo "== a clone made the way actions/checkout makes it is accepted =="
# checkout sets origin to https://github.com/<owner>/<repo>.wiki, with no .git on the end. Every
# other case here clones a path ending in .wiki.git, which is what a person typing the URL gets.
ln -s fixture.wiki.git "$WORK/remote/fixture.wiki"
printf '# Webhooks\n\nSixth version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "a change for the checkout-shaped clone"
BEFORE=$(remote_head)
rm -rf "$WIKI"; git clone --quiet "$WORK/remote/fixture.wiki" "$WIKI"
case "$(git -C "$WIKI" remote get-url origin)" in *.wiki) true ;; *) false ;; esac
check "the origin ends in .wiki, with no .git" $?
publish; check "it exits zero" $?
[ "$(remote_head)" != "$BEFORE" ]; check "the wiki moved" $?
git -C "$REMOTE" show master:webhooks.md | grep -q '^Sixth version\.$'; check "the page holds the new text" $?

echo "== an origin that is not a wiki is refused =="
# The same repository under a name with no .wiki in it, as the source repository's URL would be.
ln -s fixture.wiki.git "$WORK/remote/fixture"
printf '# Webhooks\n\nSeventh version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "a change that must not land"
BEFORE=$(remote_head)
rm -rf "$WIKI"; git clone --quiet "$WORK/remote/fixture" "$WIKI"
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the repository did not move" $?
grep -q 'ends in neither .wiki.git nor .wiki' "$WORK/out"; check "it names the reason" $?
[ -z "$(git -C "$WIKI" status --porcelain)" ]; check "the clone was not written to" $?
fixture_git -C "$SRC" reset --quiet --hard HEAD~1

echo "== a web edit landing between the push and the read back is still a publish =="
# A git that pushes for real, then pushes a second commit from another clone before returning.
git clone --quiet "$REMOTE" "$WORK/late"
mkdir -p "$WORK/shim-late"
cat >"$WORK/shim-late/git" <<SHIM
#!/bin/sh
for arg in "\$@"; do
  if [ "\$arg" = push ]; then
    "$REAL_GIT" "\$@" || exit \$?
    "$REAL_GIT" -C "$WORK/late" fetch --quiet origin || exit 1
    "$REAL_GIT" -C "$WORK/late" reset --quiet --hard origin/master || exit 1
    printf 'Saved on the web.\n' >>"$WORK/late/Home.md"
    "$REAL_GIT" -C "$WORK/late" -c user.name=fixture -c user.email=fixture@example.invalid commit --quiet -am "a late hand edit" || exit 1
    "$REAL_GIT" -C "$WORK/late" push --quiet origin HEAD:refs/heads/master || exit 1
    exit 0
  fi
done
exec "$REAL_GIT" "\$@"
SHIM
chmod +x "$WORK/shim-late/git"
printf '# Webhooks\n\nEighth version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "a change with an edit behind it"
fresh_clone
PATH="$WORK/shim-late:$PATH" publish; check "it exits zero" $?
[ "$(git -C "$REMOTE" log -1 --format=%s master)" = "a late hand edit" ]; check "the wiki ends at the late edit" $?
git -C "$REMOTE" log -1 --format=%s master~1 | grep -qF "@$(git -C "$SRC" rev-parse HEAD)"; check "the sync commit is under it" $?
grep -q 'moved on to .* after the push, and it contains' "$WORK/out"; check "it says the branch moved on and contains the commit" $?

echo "== a doc named like a page somebody else wrote is refused =="
git -C "$WORK/other" fetch --quiet origin
git -C "$WORK/other" reset --quiet --hard origin/master
printf '# Configuration\n\nWritten by hand, not by the sync.\n' >"$WORK/other/Configuration.md"
fixture_git -C "$WORK/other" add -A
fixture_git -C "$WORK/other" commit --quiet -m "a hand-written page"
git -C "$WORK/other" push --quiet origin HEAD:refs/heads/master
printf '# Configuration\n\nFrom the doc.\n' >"$SRC/docs/Configuration.md"
fixture_git -C "$SRC" add -A
fixture_git -C "$SRC" commit --quiet -m "a doc with the same name"
BEFORE=$(remote_head)
fresh_clone
grep -qxF webhooks.md "$WIKI/.wiki-sync-manifest" && ! grep -qxF Configuration.md "$WIKI/.wiki-sync-manifest"
check "the manifest exists and does not list the page" $?
publish; code=$?
[ "$code" -ne 0 ]; check "it exits non-zero (was $code)" $?
[ "$(remote_head)" = "$BEFORE" ]; check "the wiki did not move" $?
grep -q 'refusing to overwrite: Configuration.md' "$WORK/out"; check "it names the page" $?
git -C "$REMOTE" show master:Configuration.md | grep -q '^Written by hand, not by the sync\.$'; check "the hand-written page is intact" $?
[ -z "$(git -C "$WIKI" status --porcelain)" ]; check "the clone was not written to" $?

echo "== the same page is written once the manifest lists it =="
printf 'Configuration.md\n' >>"$WORK/other/.wiki-sync-manifest"
fixture_git -C "$WORK/other" commit --quiet -am "hand the page over to the sync"
git -C "$WORK/other" push --quiet origin HEAD:refs/heads/master
fresh_clone
publish; check "it exits zero" $?
git -C "$REMOTE" show master:Configuration.md | grep -q '^From the doc\.$'; check "the page holds the doc's text" $?

echo "== an ignored file in docs/ is not published =="
printf 'docs/NOTES.md\n' >"$SRC/.gitignore"
printf '# Webhooks\n\nNinth version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" add -A
fixture_git -C "$SRC" commit --quiet -m "ignore a working note, change a doc"
printf '# Notes\n\nA working note.\n' >"$SRC/docs/NOTES.md"
[ -z "$(git -C "$SRC" status --porcelain)" ]; check "git status does not show the ignored file" $?
fresh_clone
publish; check "it exits zero" $?
PAGES=$(git -C "$REMOTE" ls-tree --name-only master)
printf '%s\n' "$PAGES" | grep -qxF webhooks.md; check "the pushed tree has the tracked pages" $?
git -C "$REMOTE" show master:webhooks.md | grep -q '^Ninth version\.$'; check "the tracked change was published" $?
! printf '%s\n' "$PAGES" | grep -qxF NOTES.md; check "the pushed tree has no NOTES.md" $?
! git -C "$REMOTE" show master:Docs.md | grep -q 'NOTES'; check "the index does not link it" $?
grep -q 'docs/NOTES.md is not tracked by git' "$WORK/out"; check "it says the file was left out" $?
rm -f "$SRC/docs/NOTES.md"

echo "== a result is written to the run summary when there is one =="
printf '# Webhooks\n\nFifth version.\n' >"$SRC/docs/webhooks.md"
fixture_git -C "$SRC" commit --quiet -am "one more"
fresh_clone
GITHUB_STEP_SUMMARY="$WORK/summary.md" WIKI_COMMIT_NAME="$BOT_NAME" WIKI_COMMIT_EMAIL="$BOT_EMAIL" \
  bash "$SRC/scripts/wiki-publish.sh" "$WIKI" >"$WORK/out" 2>&1
check "it exits zero" $?
grep -q '^### Wiki published$' "$WORK/summary.md"; check "the summary says the wiki was published" $?

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1
