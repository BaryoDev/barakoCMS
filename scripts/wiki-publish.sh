#!/usr/bin/env bash
#
# Run wiki-sync.sh on a wiki clone, commit what it wrote and push it.
#
#   scripts/wiki-publish.sh <wiki-working-copy>
#
# .github/workflows/wiki-sync.yml runs this on every push to master that touches docs/. By hand:
#
#   git clone https://github.com/BaryoDev/barakoCMS.wiki.git /tmp/wiki
#   scripts/wiki-publish.sh /tmp/wiki
#
# The caller owns the clone and its credentials. This script holds no token and never clones.
#
# Exit codes
#   0  the wiki was pushed, or it already matched docs/ and nothing was committed. The last line
#      says which.
#   1  anything else: the sync failed, the commit failed, the push was refused, or the wiki's
#      branch did not end at the new commit. Nothing is retried.
#
# What it refuses before writing anything
#   A wiki clone with uncommitted changes, because "git add -A" would publish them under the sync's
#   name. Uncommitted changes under docs/ or to wiki-sync.sh in the source checkout, because the
#   commit message names the source commit and would name one that does not hold what was published.
#
# The commit
#   One commit, message "sync docs from BaryoDev/barakoCMS@<source commit>", no body. The author is
#   WIKI_COMMIT_NAME and WIKI_COMMIT_EMAIL when both are set (the workflow sets them to the Actions
#   bot), and git's configured identity otherwise, so a run by hand is in the name of who ran it.
#
# The push
#   A plain push to the branch the clone is on, never forced. If the wiki moved since the clone,
#   the push is rejected and this exits 1 saying so; clone again and rerun. The push is the only
#   write to the wiki, and a push moves the branch in one step, so a run stopped at any point
#   leaves the wiki either as it was or fully synced.

set -euo pipefail

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$HERE/.." && pwd)
SOURCE_SLUG="BaryoDev/barakoCMS"

say() { printf 'wiki-publish: %s\n' "$*"; }
die() { printf 'wiki-publish: %s\n' "$*" >&2; summary "Wiki not published" "$*"; exit 1; }

# A heading and one line on the workflow run's page. Does nothing outside Actions.
summary() {
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] || return 0
  printf '### %s\n\n%s\n' "$1" "$2" >>"$GITHUB_STEP_SUMMARY" || true
}

[ "$#" -eq 1 ] || die "expected one argument, the wiki working copy (got $#)"
[ -d "$1" ] || die "not a directory: $1"
WIKI=$(cd -- "$1" && pwd -P)

SOURCE_SHA=$(git -C "$REPO_ROOT" rev-parse --verify --quiet 'HEAD^{commit}') \
  || die "$REPO_ROOT is not a git checkout with a commit, so there is no source commit to name"
source_dirty=$(git -C "$REPO_ROOT" status --porcelain -- docs scripts/wiki-sync.sh)
[ -z "$source_dirty" ] \
  || die "uncommitted changes in the source checkout, so $SOURCE_SHA would not describe what is published:
$source_dirty"

# wiki-sync.sh proves the target is a wiki clone. This only needs it to be a git repository so the
# checks below can run, and they must run before the sync writes into it.
git -C "$WIKI" rev-parse --git-dir >/dev/null 2>&1 || die "$WIKI is not a git repository"
wiki_dirty=$(git -C "$WIKI" status --porcelain)
[ -z "$wiki_dirty" ] \
  || die "$WIKI has uncommitted changes, which the sync commit would publish:
$wiki_dirty"
BRANCH=$(git -C "$WIKI" symbolic-ref --quiet --short HEAD) \
  || die "$WIKI is on a detached HEAD, so there is no branch to push to"
BASE=$(git -C "$WIKI" rev-parse --verify --quiet 'HEAD^{commit}') \
  || die "$WIKI has no commit yet. Create the first wiki page on GitHub, then clone again."

# Both or neither. One without the other would commit under a half-set identity.
IDENTITY=()
if [ -n "${WIKI_COMMIT_NAME:-}" ] || [ -n "${WIKI_COMMIT_EMAIL:-}" ]; then
  [ -n "${WIKI_COMMIT_NAME:-}" ] && [ -n "${WIKI_COMMIT_EMAIL:-}" ] \
    || die "set both WIKI_COMMIT_NAME and WIKI_COMMIT_EMAIL, or neither"
  IDENTITY=(-c "user.name=$WIKI_COMMIT_NAME" -c "user.email=$WIKI_COMMIT_EMAIL")
fi

bash "$HERE/wiki-sync.sh" "$WIKI" || die "wiki-sync.sh failed, nothing was committed or pushed"

git -C "$WIKI" add -A
if git -C "$WIKI" diff --cached --quiet; then
  say "nothing to publish, the wiki already matches docs/ at $SOURCE_SHA"
  summary "Nothing to publish" "The wiki already matches \`docs/\` at \`$SOURCE_SHA\`."
  exit 0
fi

say "changed pages:"
git -C "$WIKI" diff --cached --name-status

# Signing is switched off for this one commit: the runner has no key, and a by-hand run on a box
# that signs by default would otherwise stop here to ask for one.
git -C "$WIKI" ${IDENTITY[@]+"${IDENTITY[@]}"} -c commit.gpgsign=false \
  commit --quiet -m "sync docs from $SOURCE_SLUG@$SOURCE_SHA" \
  || die "git commit failed in $WIKI, nothing was pushed"
NEW=$(git -C "$WIKI" rev-parse HEAD)

if ! git -C "$WIKI" push origin "HEAD:refs/heads/$BRANCH"; then
  remote=$(git -C "$WIKI" ls-remote origin "refs/heads/$BRANCH" 2>/dev/null | cut -f1 || true)
  if [ -n "$remote" ] && [ "$remote" != "$BASE" ]; then
    die "push rejected: the wiki moved from $BASE to $remote after it was cloned. Nothing was published. Clone it again and rerun."
  fi
  die "push failed and the wiki is still at ${remote:-an unknown commit}. Nothing was published."
fi

# The exit code of the push is not taken on trust: read the branch back.
remote=$(git -C "$WIKI" ls-remote origin "refs/heads/$BRANCH" | cut -f1) \
  || die "pushed $NEW, but could not read $BRANCH back from the wiki to confirm it"
[ "$remote" = "$NEW" ] \
  || die "push reported success but $BRANCH on the wiki is at ${remote:-nothing}, not $NEW"

say "published $NEW to $BRANCH, from $SOURCE_SLUG@$SOURCE_SHA"
summary "Wiki published" "Wiki commit \`$NEW\`, from \`$SOURCE_SLUG@$SOURCE_SHA\`."
