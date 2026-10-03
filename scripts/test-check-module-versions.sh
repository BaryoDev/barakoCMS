#!/usr/bin/env bash
# Tests which release check-module-versions.sh measures a module from.
#
# That script has blocked every pull request from a clean master twice, each time because it picked
# the wrong reference (#675, #1041), and both times the fix was reasoned out against the live
# repository with nothing to hold it afterwards. These cases are the histories that went wrong, plus
# the ones the check exists to catch, each rebuilt as a throwaway git repository.
#
# Nothing here touches the network. NuGet's answer comes from a directory of canned files, and a
# curl that fails the run stands in front of the real one in case the script ever stops using them.
#
#   bash scripts/test-check-module-versions.sh
#
# CHECK_MODULE_VERSIONS_SCRIPT points the cases at another copy of the script, which is how they
# were shown to be red against the version they replace.

set -uo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SCRIPT=${CHECK_MODULE_VERSIONS_SCRIPT:-$ROOT/scripts/check-module-versions.sh}

pass=0
fail=0

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

die() { echo "test setup failed: $1" >&2; exit 2; }

# Git exports GIT_DIR and GIT_INDEX_FILE to hooks and to `rebase --exec`, and with those set
# `git -C <dir>` still acts on the repository they name. Run from there, every commit and tag below
# would land in the caller's repository instead of a throwaway one. Git keeps the list of such
# variables itself, which also covers the object directory and configuration passed by environment.
# A template directory is not on that list and would copy hooks into every repository made here.
# shellcheck disable=SC2046
unset $(git rev-parse --local-env-vars) GIT_TEMPLATE_DIR

# The developer's git configuration stays out of it: no signing, no hooks, no templates.
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=test@example.invalid
export GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=test@example.invalid

mkdir "$work/bin"
cat > "$work/bin/curl" <<EOF
#!/usr/bin/env bash
echo called >> "$work/curl-calls"
exit 7
EOF
chmod +x "$work/bin/curl"
PATH="$work/bin:$PATH"

module=BarakoCMS.Demo
csproj=$module/$module.csproj
n=0
tick=0
repo=""
nuget=""

new_repo() { # starts an empty repository and an empty NuGet
  n=$((n + 1))
  repo="$work/repo$n"
  nuget="$work/nuget$n"
  mkdir -p "$repo/$module" "$nuget"
  git -C "$repo" init -q || die "git init"
}

set_version() { # $1 = version, $2 = module, $3 = a line before <Version>, $4 = the <Version> line itself
  local m=${2:-$module}
  mkdir -p "$repo/$m"
  {
    printf '<Project>\n  <PropertyGroup>\n'
    [ -n "${3:-}" ] && printf '    %s\n' "$3"
    printf '    %s\n' "${4:-<Version>$1</Version>}"
    printf '  </PropertyGroup>\n</Project>\n'
  } > "$repo/$m/$m.csproj"
}

touch_module() { echo "// $1" >> "$repo/${2:-$module}/Thing.cs"; }
touch_core() { echo "// $1" >> "$repo/Core.cs"; }

commit_at() { # $1 = hours after the start, $2 = message
  local when="$((1767225600 + $1 * 3600)) +0000"
  git -C "$repo" add -A || die "git add"
  GIT_AUTHOR_DATE="$when" GIT_COMMITTER_DATE="$when" git -C "$repo" commit -q -m "$2" || die "git commit: $2"
}

# One hour apart, the way real releases are spread out. The cases about order set their own times.
commit() { # $1 = message
  tick=$((tick + 1))
  commit_at "$tick" "$1"
}

tag() { git -C "$repo" tag "$1" || die "git tag $1"; }

# An annotated tag made later than the commit it names, as a deleted and re-created tag is.
tag_again_annotated() { # $1 = tag, $2 = commit
  tick=$((tick + 1))
  GIT_COMMITTER_DATE="$((1767225600 + tick * 3600)) +0000" git -C "$repo" tag -a -m "$1" "$1" "$2" || die "git tag -a $1"
}

head_commit() { git -C "$repo" rev-parse HEAD; }
branch_from() { git -C "$repo" checkout -q -b "$1" "$2" || die "git checkout -b $1"; }

on_nuget_as() { # $1 = lowercased package id, $2.. = its published versions
  local id=$1 list="" v
  shift
  for v in "$@"; do list="$list\"$v\","; done
  echo "{\"versions\":[${list%,}]}" > "$nuget/$id.json"
}
on_nuget() { on_nuget_as barakocms.demo "$@"; }

out=""
code=0
run_env=() # extra environment for the next runs, as NAME=value
run() { # $1 = NuGet directory, defaults to this repository's canned one
  out=$(cd "$repo" && env ${run_env[@]+"${run_env[@]}"} CHECK_MODULE_VERSIONS_NUGET_DIR="${1:-$nuget}" bash "$SCRIPT" 2>&1)
  code=$?
}

check() { # $1 = description, $2 = expected (pass|fail), $3.. = text the output must contain
  local desc=$1 want=$2 ok=1 text
  shift 2
  if [ "$want" = "pass" ]; then [ "$code" = 0 ] || ok=0; else [ "$code" = 1 ] || ok=0; fi
  for text in "$@"; do
    # A here-string, not a pipe: grep -q leaves at the first match, and under pipefail an echo
    # still writing at that moment turns a match into a failure, now and then.
    grep -qF -- "$text" <<< "$out" || { ok=0; desc="$desc (output lacks: $text)"; }
  done
  if [ "$ok" = 1 ]; then
    echo "  ok    $desc (expected $want)"
    pass=$((pass + 1))
  else
    echo "  FAIL  $desc, expected $want, exit was $code"
    echo "$out" | sed 's/^/          /'
    fail=$((fail + 1))
  fi
}

# Import and Portability after 4.5.0: the module is still 4.3.0 when v4.4.0 is tagged, is set to
# 4.4.0 afterwards, and the 4.5.0 release is the first to publish it at that number.
published_by_a_later_release() {
  new_repo
  set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
  touch_core "4.4.0 work"; commit "Release 4.4.0"; tag v4.4.0
  set_version 4.4.0; touch_module "feature"; commit "module feature, set to 4.4.0"
  touch_module "follow-up"; commit "module follow-up"
  touch_core "4.5.0"; commit "Release 4.5.0"; tag v4.5.0
  on_nuget 4.3.0 4.4.0
}

echo "== a version first published by a later release (#1041) =="
published_by_a_later_release
run; check "set to 4.4.0 after v4.4.0, published by 4.5.0, nothing since" pass
check "the run says its NuGet answers are canned" pass "NuGet answers are read from $nuget, not from nuget.org"

published_by_a_later_release
touch_module "after the release"; commit "module change after 4.5.0"
run; check "the same module with a source change after v4.5.0" fail "1 commit(s)" "since v4.5.0"

echo "== a version released under the tag of the same number (#675) =="
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
set_version 4.4.0; commit "set module to 4.4.0"
touch_module "release edit"; touch_core "4.4.0"; commit "Release 4.4.0"; tag v4.4.0
on_nuget 4.3.0 4.4.0
run; check "the release commit touches the module after the version was set" pass

echo "== a release that skipped the module =="
# The case the check was written for. The module stays at 4.3.0 through two releases, so the second
# one pushed 4.3.0 again and was skipped. Measuring from the newest tag that declares 4.3.0 would
# call this clean; the earliest one is the release whose package NuGet kept.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.4.0"; commit "Release 4.4.0"; tag v4.4.0
on_nuget 4.3.0
run; check "a fix between two releases that both declare 4.3.0" fail "1 commit(s)" "since v4.3.0"

echo "== a number NuGet already holds from somewhere else (#749) =="
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
touch_core "4.0.1"; commit "Release 4.0.1"; tag v4.0.1
set_version 4.0.1; touch_module "feature"; commit "module feature, set to 4.0.1"
on_nuget 4.0.0 4.0.1
run; check "bumped to a published number in the commit that changed the code" fail \
  "which is already on NuGet" "none of the 2 release tags (v4.0.0 to v4.0.1) declares it" \
  "Source changes since v4.0.1, the newest release tag" "the number is taken by an earlier publish"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
touch_core "4.0.1"; commit "Release 4.0.1"; tag v4.0.1
set_version 4.0.1; commit "set module to 4.0.1"
touch_module "feature"; commit "module feature"
on_nuget 4.0.0 4.0.1
run; check "bumped to a published number, code changed in a later commit" fail "the number is taken by an earlier publish"

# The order that follows "Bump <Version>": the code is already in, and the bump is a commit that
# touches only the .csproj. Measured from the bump there is nothing to see.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
touch_core "4.0.1"; commit "Release 4.0.1"; tag v4.0.1
touch_module "feature"; commit "module feature, no bump"
set_version 4.0.1; commit "set module to 4.0.1"
on_nuget 4.0.0 4.0.1
run; check "code first, then a bump to a published number that touches only the csproj" fail \
  "Source changes since v4.0.1, the newest release tag" "the number is taken by an earlier publish"

# The module a release skipped, bumped afterwards to the number it was skipped under. The fix is
# older than the newest release, so no range that starts at a release or at the bump contains it.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.4.0"; commit "Release 4.4.0"; tag v4.4.0
on_nuget 4.3.0 4.4.0
run; check "skipped by the 4.4.0 release, before anybody bumps" fail "1 commit(s)" "since v4.3.0"
set_version 4.4.0; commit "set module to 4.4.0"
run; check "then bumped to 4.4.0, a published number, touching only the csproj" fail \
  "which is already on NuGet" "the number is taken by an earlier publish"

# Releases exist and none is behind HEAD: the only tag sits on a release branch.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; base=$(head_commit)
branch_from release "$base"
touch_core "4.0.0"; commit "Release 4.0.0"; tag v4.0.0
branch_from work "$base"
touch_module "feature"; commit "module feature, no bump"
set_version 4.0.1; commit "set module to 4.0.1"
on_nuget 4.0.0 4.0.1
run; check "no release tag behind HEAD, csproj-only bump to a published number" fail \
  "none of the 1 release tags (v4.0.0 to v4.0.0) declares it" "the number is taken by an earlier publish"

# Going back to a number an earlier release published. A tag does declare it, the old one, and
# everything written since then is missing from the package that holds the number.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; touch_module "second"; commit "module at 4.1.0"; tag v4.1.0
set_version 4.0.0; touch_module "third"; commit "module set back to 4.0.0"
on_nuget 4.0.0 4.1.0
run; check "set back to a number an earlier release published" fail "2 commit(s)" "since v4.0.0"

echo "== a release that has published and not tagged yet =="
# release.yml pushes to NuGet and tags afterwards. In between, the module looks exactly like a taken
# number, and the right move is to tag, so the message has to say so before it says bump.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; touch_module "feature"; commit "Release 4.1.0"
on_nuget 4.0.0 4.1.0
run; check "published, tag still to come" fail \
  "a release has published this commit and not tagged it yet" "tag the commit it published, do not bump"
tag v4.1.0
run; check "the same commit once it is tagged" pass

echo "== which tag is the earliest =="
# By name, v4.10.0 sorts before v4.9.0.
new_repo
set_version 4.9.0; touch_module "first"; commit "module at 4.9.0"; tag v4.9.0
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.10.0"; commit "Release 4.10.0"; tag v4.10.0
on_nuget 4.9.0
run; check "v4.9.0 is earlier than v4.10.0" fail "1 commit(s)" "since v4.9.0"

# A patch number tagged after a newer release: lower by version, later in time.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.2.2"; commit "Release 4.2.2"; tag v4.2.2
on_nuget 4.3.0
run; check "v4.2.2 tagged after v4.3.0 is the later release" fail "1 commit(s)" "since v4.3.0"

# A tag deleted and made again as an annotated one is dated the day it was remade.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; first_release=$(head_commit)
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.4.0"; commit "Release 4.4.0"; tag v4.4.0
tag_again_annotated v4.3.0 "$first_release"
on_nuget 4.3.0
run; check "v4.3.0 re-created as an annotated tag after v4.4.0" fail "1 commit(s)" "since v4.3.0"

# Two commits in the same second. Commit time ties, and then the names decide, wrongly.
new_repo
set_version 4.9.0; touch_module "first"; commit_at 500 "module at 4.9.0"; tag v4.9.0
touch_module "a fix"; commit_at 500 "module fix, no bump"
touch_core "4.10.0"; commit_at 500 "Release 4.10.0"; tag v4.10.0
on_nuget 4.9.0
run; check "two releases committed in the same second" fail "1 commit(s)" "since v4.9.0"

# A release commit dated before the release it is built on, as a wrong clock produces.
new_repo
set_version 4.3.0; touch_module "first"; commit_at 700 "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit_at 701 "module fix, no bump"
touch_core "4.4.0"; commit_at 600 "Release 4.4.0"; tag v4.4.0
on_nuget 4.3.0
run; check "a later release whose commit carries an earlier time" fail "1 commit(s)" "since v4.3.0"

echo "== reading the version a tag declares =="
other=BarakoCMS.Other

# Another module's number is not this module's. Other is 4.4.0 at v4.4.0 while Demo is still 4.3.0
# there, which is #1041 with the wrong answer coming from a different file instead of a tag name.
new_repo
set_version 4.3.0; touch_module "first"
set_version 4.4.0 "$other"; touch_module "first" "$other"
commit "Release 4.4.0"; tag v4.4.0
set_version 4.4.0; touch_module "feature"; commit "Demo feature, set to 4.4.0"
touch_core "4.5.0"; commit "Release 4.5.0"; tag v4.5.0
on_nuget 4.3.0 4.4.0
on_nuget_as barakocms.other 4.4.0
run; check "another module declared this number at an earlier tag" pass

# A number that starts with another number, both ways round.
new_repo
set_version 4.1.1; touch_module "first"; commit "module at 4.1.1"; tag v4.1.1
set_version 4.1.10; touch_module "feature"; commit "module feature, set to 4.1.10"
touch_core "release"; commit "Release 4.1.10"; tag v4.1.10
on_nuget 4.1.1 4.1.10
run; check "4.1.10 after 4.1.1" pass

new_repo
set_version 4.1.10; touch_module "first"; commit "module at 4.1.10"; tag v4.1.10
set_version 4.1.1; touch_module "feature"; commit "module feature, set to 4.1.1"
touch_core "release"; commit "the next release"; tag v4.2.0
on_nuget 4.1.1 4.1.10
run; check "4.1.1 after 4.1.10" pass

# Equal as numbers, different as versions.
new_repo
set_version 4.1; touch_module "first"; commit "module at 4.1"; tag v4.1.0
set_version 4.10; touch_module "feature"; commit "module feature, set to 4.10"
touch_core "release"; commit "Release 4.10"; tag v4.10.0
on_nuget 4.1 4.10
run; check "4.10 after 4.1" pass

# A second <Version> line further down, which at the first tag already holds today's number.
new_repo
set_version 4.3.0 "$module" "" "<Version>4.3.0</Version>
    <Version Condition=\"false\">9</Version><!-- <Version>4.4.0</Version> -->"
touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
set_version 4.4.0; touch_module "feature"; commit "module feature, set to 4.4.0"
touch_core "4.5.0"; commit "Release 4.5.0"; tag v4.5.0
on_nuget 4.3.0 4.4.0
run; check "only the first <Version> line of a csproj counts" pass

# <PackageVersion> is another element, and it comes first here.
new_repo
set_version 4.3.0 "$module" "<PackageVersion>9.9.9</PackageVersion>"
touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
on_nuget 4.3.0
run; check "a <PackageVersion> line before <Version>" pass
touch_module "a fix"; commit "module fix, no bump"
run; check "the same csproj with a fix after the release" fail "1 commit(s)" "since v4.3.0"

# More text on the line after the element. The working tree and the tag must be read the same way.
new_repo
set_version 4.3.0 "$module" "" "<Version>4.3.0</Version> <!-- closes with </Version> -->"
touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit "module fix, no bump"
on_nuget 4.3.0
run; check "a second </Version> later on the line" fail "1 commit(s)" "since v4.3.0"

echo "== the caller's git settings =="
# The tag scan reads git grep's output and the commit count uses pathspec magic. Each of these
# makes one of them come back empty or unreadable unless the script pins it.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
touch_module "a fix"; commit "module fix, no bump"
touch_core "4.4.0"; commit "Release 4.4.0"; tag v4.4.0
on_nuget 4.3.0
for setting in color.ui=always color.grep=always grep.patternType=fixed grep.lineNumber=true; do
  printf '[%s]\n\t%s = %s\n' "${setting%%.*}" "$(cut -d. -f2 <<< "${setting%%=*}")" "${setting#*=}" > "$work/gitconfig"
  run_env=(GIT_CONFIG_GLOBAL="$work/gitconfig")
  run; check "a skipped fix with $setting" fail "1 commit(s)" "since v4.3.0"
done
run_env=(GIT_LITERAL_PATHSPECS=1)
run; check "a skipped fix with GIT_LITERAL_PATHSPECS=1" fail "1 commit(s)" "since v4.3.0"
run_env=()

# A commit that touches only the .csproj is not a source change. That rests on pathspec magic, which
# GIT_LITERAL_PATHSPECS turns off: the exclusion then matches nothing and the edit is counted.
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
set_version 4.3.0 "$module" "<Description>new words</Description>"; commit "csproj metadata only"
on_nuget 4.3.0
run; check "a csproj-only edit after the release" pass
run_env=(GIT_LITERAL_PATHSPECS=1)
run; check "the same with GIT_LITERAL_PATHSPECS=1" pass
run_env=()

echo "== a migration the module embeds from migrations/ =="
# The file is outside the module's directory and the only line inside it is in the .csproj, which
# does not count. The module still ships it, so a change to it after the release needs a bump.
linked='<EmbeddedResource Include="..\migrations\4.6.0\demo-things.sql" LogicalName="barako-migrations/4.6.0/demo-things.sql" />'
migration() { # $1 = file name, $2 = text
  mkdir -p "$repo/migrations/4.6.0"
  echo "$2" >> "$repo/migrations/4.6.0/$1"
}
new_repo
set_version 4.3.0 "$module" "$linked"; touch_module "first"; migration demo-things.sql "create table things ();"
migration core-only.sql "select 1;"; commit "module at 4.3.0 with a migration"; tag v4.3.0
on_nuget 4.3.0
run; check "a module with a linked migration, nothing since the release" pass
migration core-only.sql "select 2;"; commit "a migration the module does not link"
run; check "a change to a migration the module does not link" pass
migration demo-things.sql "alter table things add column n int;"; commit "the module's own migration changes"
run; check "a change to the migration the module links" fail "1 commit(s)" "since v4.3.0"
run_env=(GIT_LITERAL_PATHSPECS=1)
run; check "the same with GIT_LITERAL_PATHSPECS=1" fail "1 commit(s)" "since v4.3.0"
run_env=()

new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0, no migration yet"; tag v4.3.0
on_nuget 4.3.0
set_version 4.3.0 "$module" "$linked"; migration demo-things.sql "create table things ();"
commit "the module's first migration: a csproj line and a file under migrations/"
run; check "a first migration added with only a csproj line inside the module" fail "1 commit(s)" "since v4.3.0"

echo "== a branch cut before the release that published the version =="
new_repo
set_version 4.3.0; touch_module "first"; commit "module at 4.3.0"; tag v4.3.0
set_version 4.4.0; touch_module "feature"; commit "module feature, set to 4.4.0"; cut=$(head_commit)
touch_core "4.5.0"; commit "Release 4.5.0"; tag v4.5.0
branch_from topic "$cut"
touch_core "topic work"; commit "core change on the branch"
on_nuget 4.3.0 4.4.0
run; check "the declaring tag is not behind HEAD, the branch leaves the module alone" pass
touch_module "topic work"; commit "module change on the branch"
run; check "the same branch with a module change of its own" fail "1 commit(s)" "since v4.5.0"

echo "== a version that is not on NuGet yet =="
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; commit "set module to 4.1.0"
touch_module "feature"; commit "module feature"
on_nuget 4.0.0
run; check "changes after the version was set, version unpublished" pass "4.1.0 is not on NuGet yet"

new_repo
set_version 4.0.0; touch_module "first"; commit "new module"
touch_module "second"; commit "module change"
run; check "changes to a package that has never been published" pass "has never been published"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; commit "set module to 4.1.0"
on_nuget 4.0.0
run; check "a bump with no code in it, version unpublished" pass "4.1.0 is not on NuGet yet"

echo "== NuGet cannot be reached =="
published_by_a_later_release
touch_module "after the release"; commit "module change after 4.5.0"
run "$work/no-such-directory"; check "changes since the release tag" fail "since v4.5.0" "NuGet could not be asked"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; touch_module "feature"; commit "module feature, set to 4.1.0"
run "$work/no-such-directory"; check "changes under a version no tag declares" fail "NuGet could not be asked"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; commit "set module to 4.1.0"
run "$work/no-such-directory"; check "a bump with no code in it, under a version no tag declares" fail "NuGet could not be asked"

published_by_a_later_release
run "$work/no-such-directory"; check "a released version with no changes, so NuGet is never asked" pass

echo "== a clone with no release tags =="
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"
set_version 4.1.0; commit "set module to 4.1.0"
bump=$(git -C "$repo" rev-parse --short HEAD)
touch_module "feature"; commit "module feature"
on_nuget 4.0.0 4.1.0
run; check "changes after the version was set, version published" fail \
  "No release tags (v*) in this clone" "no release tag declares that version" "since $bump, the commit that set 4.1.0"

on_nuget 4.0.0
run; check "the same history, version unpublished" pass "No release tags (v*) in this clone" "4.1.0 is not on NuGet yet"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"
set_version 4.1.0; commit "set module to 4.1.0"
on_nuget 4.0.0 4.1.0
run; check "a bump with no code in it and nothing after" pass "No release tags (v*) in this clone"

# With no release to measure from, the commit that set the version is counted as a change itself.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"
set_version 4.1.0; touch_module "feature"; commit "module feature, set to 4.1.0"
bump=$(git -C "$repo" rev-parse --short HEAD)
on_nuget 4.0.0 4.1.0
run; check "a bump that carries its code, version published" fail \
  "1 commit(s)" "since $bump, the commit that set 4.1.0 (counted too)"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"
on_nuget 4.0.0
run; check "one commit in the whole history, version published" fail "since the first commit, which set 4.0.0"

echo "== git variables inherited from a hook =="
# The whole script again, the way a hook would start it, with a bystander repository named by
# GIT_DIR. It must come through with nothing moved, added or tagged.
if [ -n "${CHECK_MODULE_VERSIONS_TEST_NESTED:-}" ]; then
  echo "  note  CHECK_MODULE_VERSIONS_TEST_NESTED is set, so this is taken to be the run this section starts, and the section is not repeated"
else
  bystander="$work/bystander"
  mkdir "$bystander"
  git -C "$bystander" init -q || die "git init bystander"
  echo "keep" > "$bystander/keep.txt"
  git -C "$bystander" add -A || die "git add bystander"
  git -C "$bystander" commit -q -m "bystander" || die "git commit bystander"
  before="HEAD $(git -C "$bystander" rev-parse HEAD), status [$(git -C "$bystander" status --porcelain)], tags [$(git -C "$bystander" tag -l)], $(git -C "$bystander" count-objects)"
  GIT_DIR="$bystander/.git" GIT_INDEX_FILE="$bystander/.git/index" GIT_OBJECT_DIRECTORY="$bystander/.git/objects" \
    CHECK_MODULE_VERSIONS_TEST_NESTED=1 bash "${BASH_SOURCE[0]}" > "$work/nested.out" 2>&1
  nested=$?
  out=$(tail -3 "$work/nested.out"); code=$nested
  check "the cases pass with GIT_DIR, GIT_INDEX_FILE and GIT_OBJECT_DIRECTORY set" pass
  out="HEAD $(git -C "$bystander" rev-parse HEAD), status [$(git -C "$bystander" status --porcelain)], tags [$(git -C "$bystander" tag -l)], $(git -C "$bystander" count-objects)"
  code=0
  [ "$out" = "$before" ] || code=1
  check "the repository those named is untouched, objects included" pass
fi

echo "== the network =="
code=0; out=""
[ ! -e "$work/curl-calls" ] || code=1
check "curl was never called" pass

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
