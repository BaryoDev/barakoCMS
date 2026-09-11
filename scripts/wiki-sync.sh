#!/usr/bin/env bash
#
# Publish docs/*.md into the GitHub wiki as flat pages.
#
#   scripts/wiki-sync.sh <wiki-working-copy>
#
# The caller owns the wiki clone and the git history:
#
#   git clone https://github.com/BaryoDev/barakoCMS.wiki.git /tmp/wiki
#   scripts/wiki-sync.sh /tmp/wiki
#   git -C /tmp/wiki add -A && git -C /tmp/wiki commit -m "sync docs" && git -C /tmp/wiki push
#
# This script never commits and never pushes. It edits the working copy only.
#
# Page naming rule
#   The page name is the doc's file name, unchanged: docs/access-control.md becomes
#   the wiki page access-control.md, linked as (access-control). GitHub displays a
#   page name with dashes turned into spaces, so that page is titled "access control".
#   The rule is deliberately dumb so a wiki URL is predictable from the repo path and
#   a rewritten link is just the file name minus .md.
#
#   Only top-level docs/*.md are pages. docs/design/** is not: it is working material
#   that links to prototypes and screenshots, so links into it point at the repo.
#
# Generated pages
#   Docs.md and _Sidebar.md are generated here on every run and carry a line saying so.
#   Do not hand edit them.
#
# Provenance, so a sync never deletes someone else's page
#   Every page this script writes is recorded in .wiki-sync-manifest in the wiki root
#   (a dotfile, so the wiki does not render it as a page). On the next run, a page
#   listed in the old manifest that is no longer generated is deleted; anything not in
#   that manifest is left alone. Home.md and barakoCMS-*.md release pages are also
#   protected by name: if one of those ever shows up in the manifest or in the
#   generated set, the script stops instead of touching it. On a first run there is no
#   manifest, so nothing is deleted.
#
# Target check
#   The argument must be a wiki clone, not just any git directory. The script checks
#   that it is the top level of a git repository, that its origin remote URL ends in
#   .wiki.git, and that Home.md is already there. A mistyped path used to populate an
#   ordinary subdirectory and overwrite files in it.
#
# Failure behaviour
#   Pages are built and link checked in a staging directory. The wiki working copy is
#   not touched until every page is built and every link resolves, so a failure leaves
#   the wiki as it was rather than half synced.

set -euo pipefail

REPO_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
DOCS_DIR="$REPO_ROOT/docs"
BLOB_BASE="https://github.com/BaryoDev/barakoCMS/blob/master"
TREE_BASE="https://github.com/BaryoDev/barakoCMS/tree/master"
MANIFEST_NAME=".wiki-sync-manifest"
SCRIPT_REF="scripts/wiki-sync.sh"

die() { printf 'wiki-sync: %s\n' "$*" >&2; exit 1; }
say() { printf 'wiki-sync: %s\n' "$*"; }
note() { printf '%s\n' "$*"; }

usage() {
  sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//' -e '/^set -euo/d'
}

# Index grouping. A doc that is not listed lands in the Other group and is named on
# stderr, so a new doc is never silently dropped from the index.
GROUP_ORDER=(
  "Start here"
  "Content"
  "Security and access"
  "Tenancy"
  "Operations"
  "Design notes and plans"
  "Other"
)

group_of() {
  case "$1" in
    delivering-a-client-project.md|approval-by-configuration.md|deploy-in-production.md|upgrading-to-4.0.md|configuring-email.md)
      printf 'Start here' ;;
    blueprints.md|event-sourced-content-types.md|seo-fields.md|image-variants.md|url-redirects.md|delivery-api.md)
      printf 'Content' ;;
    access-control.md|compliance-posture.md|device-trust.md|session-and-token-storage.md|scanning-uploads.md)
      printf 'Security and access' ;;
    multi-tenancy.md|tenancy-at-the-database.md)
      printf 'Tenancy' ;;
    backup-and-restore.md|background-jobs.md|workflow-runs.md|module-inventory.md|idempotency.md|webhooks.md)
      printf 'Operations' ;;
    workflow-engine-rethink.md|marketplace-and-contribution-plan.md)
      printf 'Design notes and plans' ;;
    *)
      printf 'Other' ;;
  esac
}

is_protected() {
  case "$1" in
    Home.md|barakoCMS-*.md) return 0 ;;
    *) return 1 ;;
  esac
}

# ---------------------------------------------------------------- arguments

case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  "")        usage >&2; die "expected one argument, the wiki working copy (got none)" ;;
esac
[ "$#" -eq 1 ] || die "expected one argument, the wiki working copy (got $#)"

WIKI=$1
[ -d "$WIKI" ] || die "not a directory: $WIKI"
WIKI=$(cd -- "$WIKI" && pwd -P)

# The target must be the wiki clone itself. Being inside some git repository is not
# enough: that let a mistyped path populate an ordinary subdirectory and overwrite
# hand-written files in it.
wiki_not_proven() {
  die "$WIKI is not the wiki clone ($1).
Checked: top level of a git repository, origin remote URL ending in .wiki.git, Home.md present.
Clone the wiki first: git clone https://github.com/BaryoDev/barakoCMS.wiki.git"
}
WIKI_TOP=$(git -C "$WIKI" rev-parse --show-toplevel 2>/dev/null) \
  || wiki_not_proven "not a git repository"
WIKI_TOP=$(cd -- "$WIKI_TOP" && pwd -P)
[ "$WIKI_TOP" = "$WIKI" ] \
  || wiki_not_proven "a subdirectory of the git repository at $WIKI_TOP, not its top level"
WIKI_ORIGIN=$(git -C "$WIKI" remote get-url origin 2>/dev/null) \
  || wiki_not_proven "git repository with no origin remote"
case "$WIKI_ORIGIN" in
  *.wiki.git) ;;
  *) wiki_not_proven "origin remote is $WIKI_ORIGIN, which does not end in .wiki.git" ;;
esac
[ -f "$WIKI/Home.md" ] || wiki_not_proven "no Home.md, so this is not a populated wiki clone"
[ -d "$DOCS_DIR" ] || die "no docs directory at $DOCS_DIR"

# ---------------------------------------------------------------- page set

# Docs that must not reach a public wiki. The list is a file in the repository rather than a
# constant here, so adding a doc and deciding whether it is publishable is one review, in one diff.
# A stale entry is an error: a rename would otherwise silently start publishing a withheld doc.
IGNORE_FILE="$DOCS_DIR/.wikiignore"
EXCLUDED=()
if [ -f "$IGNORE_FILE" ]; then
  while IFS= read -r line; do
    line="${line%%#*}"
    line="$(printf '%s' "$line" | tr -d '[:space:]')"
    [ -n "$line" ] || continue
    [ -f "$DOCS_DIR/$line" ] || die "$IGNORE_FILE names $line, which is not in $DOCS_DIR. Fix the list."
    EXCLUDED+=("$line")
  done < "$IGNORE_FILE"
fi

is_excluded() {
  local candidate="$1" e
  for e in "${EXCLUDED[@]}"; do [ "$e" = "$candidate" ] && return 0; done
  return 1
}

PAGES=()
SKIPPED=()
while IFS= read -r path; do
  base="$(basename -- "$path")"
  if is_excluded "$base"; then
    SKIPPED+=("$base")
  else
    PAGES+=("$base")
  fi
done < <(find "$DOCS_DIR" -maxdepth 1 -name '*.md' -type f | sort)
[ "${#PAGES[@]}" -gt 0 ] || die "no markdown files to publish in $DOCS_DIR"

if [ "${#SKIPPED[@]}" -gt 0 ]; then
  say "withheld by $(basename -- "$IGNORE_FILE"): ${SKIPPED[*]}"
fi

for page in "${PAGES[@]}"; do
  is_protected "$page" && die "docs/$page would overwrite the protected wiki page $page"
  # A wiki link target cannot hold a space: ](my notes) renders as literal text, and
  # the link check would still count it as a page that exists.
  case "$page" in
    *[[:space:]]*) die "docs/$page has whitespace in its file name, so it cannot be linked as a wiki page. Rename it with dashes." ;;
  esac
done

PAGES+=("Docs.md" "_Sidebar.md")

STAGING=$(mktemp -d "${TMPDIR:-/tmp}/wiki-sync.XXXXXX")
trap 'rm -rf "$STAGING"' EXIT

REWRITER="$STAGING/.rewrite-links.pl"
cat >"$REWRITER" <<'PERL'
#!/usr/bin/perl
# Rewrite markdown link targets in one doc for the wiki. stdin to stdout.
use strict;
use warnings;

my $repo  = $ENV{WS_REPO_ROOT};
my $blob  = $ENV{WS_BLOB_BASE};
my $tree  = $ENV{WS_TREE_BASE};
my $src   = $ENV{WS_SRC};
my %page  = map { $_ => 1 } split ' ', $ENV{WS_PAGES};
my $errors = 0;

# docs/a/../b -> b, refusing anything that climbs out of the repo.
sub normalise {
    my ($path) = @_;
    my @out;
    for my $seg (split m{/}, $path) {
        next if $seg eq '' or $seg eq '.';
        if ($seg eq '..') {
            return undef unless @out;
            pop @out;
            next;
        }
        push @out, $seg;
    }
    return join '/', @out;
}

sub rewrite {
    my ($target) = @_;

    return $target if $target =~ m{^[A-Za-z][A-Za-z0-9+.\-]*:};   # http:, https:, mailto:
    return $target if $target =~ m{^//};                          # protocol relative
    return $target if $target =~ m{^\#};                          # in-page anchor

    my ($path, $frag) = split /\#/, $target, 2;
    $frag = defined $frag ? "#$frag" : '';
    my $dir_hint = $path =~ m{/$} ? 1 : 0;

    my $rel = normalise("docs/$path");
    if (!defined $rel or $rel eq '') {
        warn "  $src: link escapes the repository root ($target)\n";
        $errors++;
        return $target;
    }

    # Another published doc becomes a wiki page link.
    if ($rel =~ m{^docs/([^/]+\.md)$} and $page{$1}) {
        (my $name = $1) =~ s{\.md$}{};
        return $name . $frag;
    }

    # Anything else in the repo becomes an absolute blob or tree URL on master.
    if (-d "$repo/$rel") {
        return "$tree/$rel$frag";
    }
    if (-f "$repo/$rel") {
        return "$blob/$rel$frag";
    }

    warn "  $src: link target does not exist in the repository ($target -> $rel"
       . ($dir_hint ? ", expected a directory" : "") . ")\n";
    $errors++;
    return $target;
}

my $fenced = 0;
while (my $line = <STDIN>) {
    # Any indent, matching the link checker's awk regex, so both passes agree on
    # where a fence starts. A plain indented code block with no fence is still not
    # recognised by either pass.
    if ($line =~ m{^[ \t]*(```|~~~)}) { $fenced = !$fenced; print $line; next; }
    if ($fenced)                       { print $line; next; }
    $line =~ s{\]\(([^()\s]+)(\s+"[^"]*")?\)}{ '](' . rewrite($1) . (defined $2 ? $2 : '') . ')' }ge;
    print $line;
}

exit($errors ? 3 : 0);
PERL

# ---------------------------------------------------------------- build pages

export WS_REPO_ROOT="$REPO_ROOT" WS_BLOB_BASE="$BLOB_BASE" WS_TREE_BASE="$TREE_BASE"
export WS_PAGES="${PAGES[*]}"

declare -A TITLE=()
build_failed=0

for page in "${PAGES[@]}"; do
  case "$page" in Docs.md|_Sidebar.md) continue ;; esac

  src="$DOCS_DIR/$page"
  heading=$(awk '
    /^[ \t]*(```|~~~)/ { fenced = !fenced; next }
    fenced             { next }
    /^# /              { sub(/^# /, ""); print; exit }
  ' "$src")
  [ -n "$heading" ] || die "docs/$page has no level-one heading to take a title from"
  TITLE["$page"]=$heading

  if WS_SRC="docs/$page" perl "$REWRITER" <"$src" >"$STAGING/$page"; then
    :
  else
    note "wiki-sync: unresolved links in docs/$page"
    build_failed=1
  fi

  {
    printf '\n---\n\n'
    printf 'Generated from [`docs/%s`](%s/docs/%s) by `%s`. Edit the doc in the repository, not this page.\n' \
      "$page" "$BLOB_BASE" "$page" "$SCRIPT_REF"
  } >>"$STAGING/$page"
done

[ "$build_failed" -eq 0 ] || die "link rewriting failed, the wiki was not touched"

# ---------------------------------------------------------------- Docs.md

{
  printf '# Docs\n\n'
  printf '<!-- Generated by %s. Do not edit this page. -->\n' "$SCRIPT_REF"
  printf 'Generated by `%s` from [`docs/`](%s/docs) on master. Hand edits are overwritten on the next sync.\n\n' \
    "$SCRIPT_REF" "$TREE_BASE"

  for group in "${GROUP_ORDER[@]}"; do
    members=()
    for page in "${PAGES[@]}"; do
      case "$page" in Docs.md|_Sidebar.md) continue ;; esac
      [ "$(group_of "$page")" = "$group" ] && members+=("$page")
    done
    [ "${#members[@]}" -gt 0 ] || continue
    printf '## %s\n\n' "$group"
    for page in "${members[@]}"; do
      printf -- '- [%s](%s)\n' "${TITLE[$page]}" "${page%.md}"
    done
    printf '\n'
  done
} >"$STAGING/Docs.md"

for page in "${PAGES[@]}"; do
  case "$page" in Docs.md|_Sidebar.md) continue ;; esac
  [ "$(group_of "$page")" = "Other" ] \
    && note "wiki-sync: docs/$page is not in the group table, filed under Other" >&2
done

# ---------------------------------------------------------------- _Sidebar.md

# Release pages (barakoCMS-*.md) are written by hand, so link only the ones present.
RELEASE_PAGES=()
while IFS= read -r path; do
  [ -e "$path" ] || continue
  RELEASE_PAGES+=("$(basename -- "$path")")
done < <(find "$WIKI" -maxdepth 1 -name 'barakoCMS-*.md' -type f | sort -r)

{
  printf '### barakoCMS\n\n'
  printf '<!-- Generated by %s. Do not edit this page. -->\n\n' "$SCRIPT_REF"
  printf -- '- [Home](Home)\n'
  printf -- '- [All docs](Docs)\n\n'

  if [ "${#RELEASE_PAGES[@]}" -gt 0 ]; then
    printf '**Releases**\n\n'
    for page in "${RELEASE_PAGES[@]}"; do
      printf -- '- [%s](%s)\n' "${page%.md}" "${page%.md}"
    done
    printf '\n'
  fi

  for group in "${GROUP_ORDER[@]}"; do
    members=()
    for page in "${PAGES[@]}"; do
      case "$page" in Docs.md|_Sidebar.md) continue ;; esac
      [ "$(group_of "$page")" = "$group" ] && members+=("$page")
    done
    [ "${#members[@]}" -gt 0 ] || continue
    printf '**%s**\n\n' "$group"
    for page in "${members[@]}"; do
      # Sidebar text is the heading up to the first colon, to keep the column narrow.
      printf -- '- [%s](%s)\n' "${TITLE[$page]%%:*}" "${page%.md}"
    done
    printf '\n'
  done
} >"$STAGING/_Sidebar.md"

# ---------------------------------------------------------------- plan the apply

OLD_MANIFEST=()
if [ -f "$WIKI/$MANIFEST_NAME" ]; then
  while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    OLD_MANIFEST+=("$line")
  done <"$WIKI/$MANIFEST_NAME"
fi

in_pages() {
  local needle=$1 page
  for page in "${PAGES[@]}"; do [ "$page" = "$needle" ] && return 0; done
  return 1
}

TO_DELETE=()
for page in "${OLD_MANIFEST[@]}"; do
  is_protected "$page" && die "$MANIFEST_NAME lists the protected page $page, refusing to sync"
  case "$page" in */*|..|.) die "$MANIFEST_NAME has a suspicious entry ($page), refusing to sync" ;; esac
  in_pages "$page" && continue
  [ -e "$WIKI/$page" ] || continue
  TO_DELETE+=("$page")
done

# ---------------------------------------------------------------- link check

FINAL_PAGES="$STAGING/.final-pages"
{
  printf '%s\n' "${PAGES[@]}"
  for path in "$WIKI"/*.md; do
    [ -e "$path" ] || continue
    printf '%s\n' "$(basename -- "$path")"
  done
} | sort -u >"$FINAL_PAGES"
if [ "${#TO_DELETE[@]}" -gt 0 ]; then
  grep -vxF -f <(printf '%s\n' "${TO_DELETE[@]}") "$FINAL_PAGES" >"$FINAL_PAGES.keep"
  mv "$FINAL_PAGES.keep" "$FINAL_PAGES"
fi

LINKS="$STAGING/.links"
awk '
  FNR == 1 { fenced = 0 }
  /^[ \t]*(```|~~~)/ { fenced = !fenced; next }
  fenced { next }
  {
    line = $0
    while (match(line, /\]\([^()]*\)/)) {
      print FILENAME "\t" substr(line, RSTART + 2, RLENGTH - 3)
      line = substr(line, RSTART + RLENGTH)
    }
  }
' "$STAGING"/*.md >"$LINKS"

total=0; absolute=0; anchors=0; internal=0; broken=0
while IFS=$'\t' read -r file target; do
  total=$((total + 1))
  case "$target" in
    [A-Za-z]*:*|//*) absolute=$((absolute + 1)); continue ;;
    '#'*)            anchors=$((anchors + 1)); continue ;;
  esac
  name=${target%%#*}
  if grep -qxF "$name.md" "$FINAL_PAGES"; then
    internal=$((internal + 1))
  else
    broken=$((broken + 1))
    note "wiki-sync: $(basename -- "$file") links to ($target) but no page $name.md will exist" >&2
  fi
done <"$LINKS"

note "wiki-sync: ${#PAGES[@]} pages, $total links ($absolute absolute, $internal wiki pages, $anchors in-page anchors, $broken broken)"
[ "$broken" -eq 0 ] || die "$broken broken links, the wiki was not touched"

# ---------------------------------------------------------------- apply

for page in "${TO_DELETE[@]}"; do
  rm -f -- "$WIKI/$page"
  note "wiki-sync: removed $page (its doc is gone)"
done

for page in "${PAGES[@]}"; do
  cp -- "$STAGING/$page" "$WIKI/$page"
done

{
  printf '# Pages written by %s. Deleting a line here orphans that page.\n' "$SCRIPT_REF"
  printf '%s\n' "${PAGES[@]}" | sort
} >"$WIKI/$MANIFEST_NAME"

note "wiki-sync: synced into $WIKI"
note "wiki-sync: review with 'git -C $WIKI status --short', then add, commit and push yourself"
