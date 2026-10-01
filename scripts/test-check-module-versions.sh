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

set_version() { # $1 = version
  printf '<Project>\n  <PropertyGroup>\n    <Version>%s</Version>\n  </PropertyGroup>\n</Project>\n' "$1" > "$repo/$csproj"
}

touch_module() { echo "// $1" >> "$repo/$module/Thing.cs"; }
touch_core() { echo "// $1" >> "$repo/Core.cs"; }

# One hour apart, so tags sort by date the way real releases do. Commits made in the same second
# would leave the order to chance.
commit() { # $1 = message
  tick=$((tick + 1))
  local when="$((1767225600 + tick * 3600)) +0000"
  git -C "$repo" add -A || die "git add"
  GIT_AUTHOR_DATE="$when" GIT_COMMITTER_DATE="$when" git -C "$repo" commit -q -m "$1" || die "git commit: $1"
}

tag() { git -C "$repo" tag "$1" || die "git tag $1"; }

on_nuget() { # $@ = published versions of the module's package
  local list="" v
  for v in "$@"; do list="$list\"$v\","; done
  echo "{\"versions\":[${list%,}]}" > "$nuget/barakocms.demo.json"
}

out=""
code=0
run() { # $1 = NuGet directory, defaults to this repository's canned one
  out=$(cd "$repo" && CHECK_MODULE_VERSIONS_NUGET_DIR="${1:-$nuget}" bash "$SCRIPT" 2>&1)
  code=$?
}

check() { # $1 = description, $2 = expected (pass|fail), $3.. = text the output must contain
  local desc=$1 want=$2 ok=1 text
  shift 2
  if [ "$want" = "pass" ]; then [ "$code" = 0 ] || ok=0; else [ "$code" = 1 ] || ok=0; fi
  for text in "$@"; do
    echo "$out" | grep -qF -- "$text" || { ok=0; desc="$desc (output lacks: $text)"; }
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
  "that number is taken" "none of the 2 release tags (v4.0.0 to v4.0.1) declares it" "1 commit(s)"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
touch_core "4.0.1"; commit "Release 4.0.1"; tag v4.0.1
set_version 4.0.1; commit "set module to 4.0.1"
touch_module "feature"; commit "module feature"
on_nuget 4.0.0 4.0.1
run; check "bumped to a published number, code changed in a later commit" fail "that number is taken"

# Going back to a number an earlier release published. A tag does declare it, the old one, and
# everything written since then is missing from the package that holds the number.
new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; touch_module "second"; commit "module at 4.1.0"; tag v4.1.0
set_version 4.0.0; touch_module "third"; commit "module set back to 4.0.0"
on_nuget 4.0.0 4.1.0
run; check "set back to a number an earlier release published" fail "2 commit(s)" "since v4.0.0"

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

echo "== NuGet cannot be reached =="
published_by_a_later_release
touch_module "after the release"; commit "module change after 4.5.0"
run "$work/no-such-directory"; check "changes since the release tag" fail "since v4.5.0" "NuGet could not be asked"

new_repo
set_version 4.0.0; touch_module "first"; commit "module at 4.0.0"; tag v4.0.0
set_version 4.1.0; touch_module "feature"; commit "module feature, set to 4.1.0"
run "$work/no-such-directory"; check "changes under a version no tag declares" fail "NuGet could not be asked"

published_by_a_later_release
run "$work/no-such-directory"; check "no changes, so NuGet is never asked" pass

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

echo "== the network =="
code=0; out=""
[ ! -e "$work/curl-calls" ] || code=1
check "curl was never called" pass

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
