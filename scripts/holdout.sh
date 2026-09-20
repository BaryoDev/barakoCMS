#!/usr/bin/env bash
# Step 0 of Holdout: prove that a test added by this PR notices the change this PR made.
#
#   bash scripts/holdout.sh --spec body.md
#   bash scripts/holdout.sh --spec body.md --dry-run
#
# The spec is a fenced ```holdout block in the PR body naming, for each new test, the production
# hunk it depends on. This script holds that hunk out (reverts it), reruns the named test, and
# requires it to fail. A test that passes with its hunk held out does not test that hunk.
#
#   ```holdout
#   - test: BarakoCMS.Tests.ShareLinks.RedeemTests
#     breaks: barakoCMS/Features/Sites/ShareLinks/Redeem.cs "RevokedAt is not null"
#   - untested: barakoCMS/Features/Sites/ShareLinks/Endpoints.cs #1
#     why: route registration, covered by the inventory check
#   ```
#
# Exit codes are distinct so a caller cannot collapse them into "non-zero, whatever":
#   0 pass          every binding held out and its test failed, then passed again on restore
#   1 caught        a test passed with its hunk held out
#   2 unresolved    anchor matched no hunk, or matched more than one, or zero tests ran
#   3 inconclusive  the held-out tree did not build, so nothing was proven
#
# Runs in a temporary worktree. Never mutates the caller's tree: reverting a hunk over uncommitted
# work and restoring with git checkout is how uncommitted work gets lost.

set -uo pipefail

# Resolve the repository from the working directory, not from where this file happens to live. A
# copy of this script run from outside its checkout used to cd to the wrong place and report
# "cannot find merge base", which reads as a git problem rather than as a path problem.
repo_root=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo "holdout: not inside a git repository"; exit 2; }
cd "$repo_root"

spec=""
dry_run=0
while [ $# -gt 0 ]; do
  case "$1" in
    --spec) shift; [ $# -gt 0 ] || { echo "holdout: --spec needs a file"; exit 2; }; spec="$1"; shift ;;
    --dry-run) dry_run=1; shift ;;
    *) echo "holdout: unrecognised argument '$1'"; exit 2 ;;
  esac
done

[ -n "$spec" ] || { echo "holdout: --spec is required"; exit 2; }
[ -f "$spec" ] || { echo "holdout: spec file not found: $spec"; exit 2; }

base_ref="${HOLDOUT_BASE:-origin/master}"
base=$(git merge-base "$base_ref" HEAD 2>/dev/null) || {
  echo "holdout: cannot find merge base with $base_ref"; exit 2; }

# ---------------------------------------------------------------------------
# Parse the ```holdout block. Two directives: "test:/breaks:" pairs and
# "untested:/why:" pairs. Anything else in the block is an error, not a skip.
# ---------------------------------------------------------------------------
block=$(awk '/^```holdout[[:space:]]*$/{f=1;next} /^```[[:space:]]*$/{if(f)exit} f' "$spec")

if [ -z "$block" ]; then
  echo "holdout: no \`\`\`holdout block in $spec"
  echo "         A PR touching production code must bind each new test to the hunk it depends on,"
  echo "         or list the hunk under untested: with a reason."
  exit 2
fi

bindings=()   # each entry: "test<TAB>path<TAB>anchor"
untested=()   # each entry: "path<TAB>anchor"
cur_test=""
cur_untested=""
none=0
none_reason=""

while IFS= read -r line; do
  case "$line" in
    "- test: "*)      cur_test="${line#- test: }"; cur_untested="" ;;
    "- untested: "*)  cur_untested="${line#- untested: }"; cur_test="" ;;
    *"breaks: "*)
      [ -n "$cur_test" ] || { echo "holdout: 'breaks:' with no preceding 'test:'"; exit 2; }
      target="${line#*breaks: }"
      path="${target%% *}"
      anchor="${target#"$path"}"
      anchor="${anchor# }"
      bindings+=("$cur_test	$path	$anchor")
      cur_test=""
      ;;
    *"why: "*)
      [ -n "$cur_untested" ] || { echo "holdout: 'why:' with no preceding 'untested:'"; exit 2; }
      path="${cur_untested%% *}"
      anchor="${cur_untested#"$path"}"
      untested+=("$path	${anchor# }")
      cur_untested=""
      ;;
    "none:"*)
      # A change with no production hunks at all still declares that, with a reason. Deliberately
      # explicit: a missing block and a change that needs no bindings must not look the same, or
      # every missing block reads as the second.
      none_reason="${line#none:}"
      none_reason="${none_reason# }"
      [ -n "$none_reason" ] || { echo "holdout: 'none:' needs a reason"; exit 2; }
      none=1
      ;;
    ""|"#"*) ;;
    *) echo "holdout: unparsed line in block: $line"; exit 2 ;;
  esac
done <<< "$block"

# ---------------------------------------------------------------------------
# The production files this change touches, as one definition rather than two.
#
# Excluded, and why each one is not a thing a test can be bound to:
#   *.Tests/**            the tests themselves
#   *.md                  documentation
#   *.csproj, *.props, *.targets, packages.lock.json
#                         build metadata. A lock file hunk has no behaviour to hold out, and it
#                         changes on every dependency bump, so demanding a declaration for one is
#                         a tax rather than a gate.
#   pure renames          a file moved with no content change has no hunk at all. `git diff` reports
#                         it with zero additions and zero deletions.
#
# Measured on an assembly split (#971): the unfiltered list asked for 286 declarations, 41 of them
# build metadata and 60 of them pure moves. A check nobody can satisfy is a check nobody runs.
# ---------------------------------------------------------------------------
production_files() {
  git diff --name-only --diff-filter=ad "$base" -- \
    'barakoCMS/**' 'BarakoCMS.*/**' \
    ':(exclude)*.Tests/**' \
    ':(exclude)**/*.md' \
    ':(exclude)**/*.csproj' \
    ':(exclude)**/*.props' \
    ':(exclude)**/*.targets' \
    ':(exclude)**/packages.lock.json' 2>/dev/null \
  | while IFS= read -r f; do
      [ -n "$f" ] || continue
      # A rename with no content change produces no @@ hunk. Nothing to hold out, nothing to declare.
      if [ "$(git diff --unified=0 "$base" -- "$f" 2>/dev/null | grep -c '^@@')" -gt 0 ]; then
        printf '%s\n' "$f"
      fi
    done
}

if [ "$none" -eq 1 ] && { [ ${#bindings[@]} -gt 0 ] || [ ${#untested[@]} -gt 0 ]; }; then
  echo "holdout: 'none:' cannot be combined with bindings."
  exit 2
fi

if [ "$none" -eq 1 ]; then
  # none: is checked against the diff, never taken on trust, or it is just a way of opting out.
  # Two shapes are legitimate, and both are verified rather than asserted:
  #
  #   1. The change touches no production file at all. Docs, a changelog, a release.
  #   2. The change adds no test file at all. There are no new tests, so there are no bindings to
  #      make, and demanding a declaration per production file is a tax rather than a gate. An
  #      assembly split (#971) asked for 245 of them; a check nobody can satisfy is one nobody runs.
  #
  # The second cannot be used to dodge a binding: add a single test and this fails again, which is
  # exactly the change for which a binding is owed.
  declared_none_files=$(production_files)
  # No --diff-filter here. production_files() excludes added and deleted paths on purpose; this
  # check exists to notice an ADDED test file, so reusing that filter made it unable to fire. It
  # was written that way first and the fixture caught it.
  new_tests=$(git diff --name-only "$base" -- \
    '*Tests/**' '*Test/**' '*Tests.cs' '*Test.cs' 2>/dev/null || true)

  if [ -n "$declared_none_files" ] && [ -n "$new_tests" ]; then
    echo "holdout: the block says none, but this change adds or edits tests while touching"
    echo "         production code, so at least one binding is owed. Test files in the diff:"
    for f in $new_tests; do echo "         $f"; done
    exit 2
  fi

  if [ -z "$declared_none_files" ]; then
    echo "holdout: none declared ($none_reason), and the diff touches no production file"
  else
    count=$(printf '%s\n' "$declared_none_files" | grep -c . || echo 0)
    echo "holdout: none declared ($none_reason). $count production file(s) changed and no test file"
    echo "         was added or edited, so there is no binding to make."
  fi
  exit 0
fi

if [ ${#bindings[@]} -eq 0 ] && [ ${#untested[@]} -eq 0 ]; then
  echo "holdout: the block is empty, so this would pass having held out nothing."
  exit 2
fi

echo "holdout: $((${#bindings[@]})) binding(s), $((${#untested[@]})) untested hunk(s), base ${base:0:8}"

# ---------------------------------------------------------------------------
# Resolve an anchor to exactly one hunk of one file's diff against the base.
# Anchor forms: "some added line substring"   (quoted content anchor)
#               #2                            (1-based hunk ordinal)
# Line numbers are deliberately not supported: they do not survive a rebase.
# Prints the hunk's patch on stdout. Non-zero means unresolved.
# ---------------------------------------------------------------------------
resolve_hunk() {
  local path="$1" anchor="$2"
  local diff_file; diff_file=$(mktemp)
  git diff --unified=3 "$base" -- "$path" > "$diff_file" 2>/dev/null

  if [ ! -s "$diff_file" ]; then
    rm -f "$diff_file"; echo "holdout: no diff for $path against the base" >&2; return 1
  fi

  # A file the change adds outright is one hunk covering the whole file, and holding it out deletes
  # the file. In a compiled language that never builds, so the run can only end inconclusive. Say so
  # here rather than after spending two builds discovering it. Measured on a real feature branch:
  # every one of its ten production files was new, so every binding cost 29 seconds to learn nothing.
  if grep -q '^--- /dev/null' "$diff_file"; then
    rm -f "$diff_file"
    echo "holdout: $path is added by this change, so holding any of it out deletes the file." >&2
    echo "         Nothing can be proven that way. Bind a test to a hunk that modifies an existing" >&2
    echo "         file, or list this one under untested: with a reason." >&2
    return 1
  fi

  local header; header=$(sed -n '1,/^@@/p' "$diff_file" | sed '$d')
  local count; count=$(grep -c '^@@' "$diff_file")
  local want=0

  if [[ "$anchor" == \#* ]]; then
    want="${anchor#\#}"
    if ! [[ "$want" =~ ^[0-9]+$ ]] || [ "$want" -lt 1 ] || [ "$want" -gt "$count" ]; then
      rm -f "$diff_file"; echo "holdout: $path has $count hunk(s), no $anchor" >&2; return 1
    fi
  else
    local needle="${anchor%\"}"; needle="${needle#\"}"
    [ -n "$needle" ] || { rm -f "$diff_file"; echo "holdout: empty anchor for $path" >&2; return 1; }
    local i=0 hits=0 first=0
    while IFS= read -r l; do
      case "$l" in
        @@*) i=$((i+1)) ;;
        +*) if [ "$i" -gt 0 ] && [[ "$l" == *"$needle"* ]]; then
              if [ "$first" -ne "$i" ]; then hits=$((hits+1)); first="$i"; fi
            fi ;;
      esac
    done < "$diff_file"
    if [ "$hits" -eq 0 ]; then
      rm -f "$diff_file"; echo "holdout: anchor $anchor matches no added line in $path" >&2; return 1
    fi
    if [ "$hits" -gt 1 ]; then
      rm -f "$diff_file"; echo "holdout: anchor $anchor is ambiguous, matches $hits hunks in $path" >&2; return 1
    fi
    want="$first"
  fi

  printf '%s\n' "$header"
  awk -v want="$want" '/^@@/{n++} n==want' "$diff_file"
  rm -f "$diff_file"
  return 0
}

# ---------------------------------------------------------------------------
# Every production hunk must be claimed: bound to a test, or listed as untested.
# An unclaimed hunk is a silent gap; naming it makes the omission a written claim.
# ---------------------------------------------------------------------------
prod_files=$(production_files)

unclaimed=0
for f in $prod_files; do
  total=$(git diff --unified=3 "$base" -- "$f" 2>/dev/null | grep -c '^@@' || echo 0)
  [ "$total" -gt 0 ] || continue
  claimed=0
  for b in ${bindings[@]+"${bindings[@]}"}; do
    [ "$(printf '%s' "$b" | cut -f2)" = "$f" ] && claimed=$((claimed+1))
  done
  for u in ${untested[@]+"${untested[@]}"}; do
    [ "$(printf '%s' "$u" | cut -f1)" = "$f" ] && claimed=$((claimed+1))
  done
  if [ "$claimed" -eq 0 ]; then
    echo "holdout: UNCLAIMED  $f ($total hunk(s)) is bound to no test and not listed untested:"
    unclaimed=$((unclaimed+1))
  fi
done
[ "$unclaimed" -eq 0 ] || { echo "holdout: $unclaimed production file(s) unclaimed"; exit 2; }

if [ "$dry_run" -eq 1 ]; then
  echo "holdout: --dry-run, resolving anchors without running tests"
  rc=0
  for b in ${bindings[@]+"${bindings[@]}"}; do
    t=$(printf '%s' "$b" | cut -f1); p=$(printf '%s' "$b" | cut -f2); a=$(printf '%s' "$b" | cut -f3)
    if patch=$(resolve_hunk "$p" "$a"); then
      lines=$(printf '%s\n' "$patch" | grep -c '^+' || true)
      echo "holdout: ok        $t -> $p $a ($lines added line(s))"
    else
      rc=2
    fi
  done
  exit $rc
fi

# ---------------------------------------------------------------------------
# Run each binding in a throwaway worktree at HEAD.
# ---------------------------------------------------------------------------
wt=$(mktemp -d)
cleanup() { git worktree remove --force "$wt" >/dev/null 2>&1 || rm -rf "$wt"; }
trap cleanup EXIT

git worktree add --detach "$wt" HEAD >/dev/null 2>&1 || { echo "holdout: cannot create worktree"; exit 2; }

run_class() {  # prints total:failed, or "none" when no summary line appeared
  local dir="$1" class="$2" out; out=$(mktemp)
  ( cd "$dir" && dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj \
      --no-build --configuration Release -- -class "$class" ) > "$out" 2>&1
  local total failed
  total=$(grep -oE 'Total: [0-9]+' "$out" | tail -1 | grep -oE '[0-9]+' || true)
  failed=$(grep -oE 'Failed: [0-9]+' "$out" | tail -1 | grep -oE '[0-9]+' || echo 0)
  rm -f "$out"
  [ -n "$total" ] || { echo "none"; return; }
  echo "${total}:${failed:-0}"
}

# Every hunk declared untested and nothing bound. That is a legitimate answer for a change that
# ships data rather than behaviour, so it passes, but it must not borrow the words of a run that
# held something out. Say what happened, and stop before spending a build proving nothing.
if [ ${#bindings[@]} -eq 0 ]; then
  echo "holdout: no bindings. ${#untested[@]} hunk(s) declared untested, nothing was held out."
  for u in ${untested[@]+"${untested[@]}"}; do
    echo "         untested  $(printf '%s' "$u" | cut -f1) $(printf '%s' "$u" | cut -f2)"
  done
  exit 0
fi

echo "== holdout: building the clean tree =="
( cd "$wt" && dotnet restore barakoCMS.sln --locked-mode >/dev/null 2>&1 \
  && dotnet build barakoCMS.sln --configuration Release --no-restore >/dev/null 2>&1 ) \
  || { echo "holdout: the clean tree does not build, nothing can be proven"; exit 3; }

status=0
for b in ${bindings[@]+"${bindings[@]}"}; do
  test_id=$(printf '%s' "$b" | cut -f1)
  path=$(printf '%s' "$b" | cut -f2)
  anchor=$(printf '%s' "$b" | cut -f3)
  echo "== holdout: $test_id -> $path $anchor =="

  patch_file=$(mktemp)
  if ! resolve_hunk "$path" "$anchor" > "$patch_file"; then
    rm -f "$patch_file"; status=2; continue
  fi

  # 1. clean run: the test must exist and pass, or the rest proves nothing.
  clean=$(run_class "$wt" "$test_id")
  if [ "$clean" = "none" ]; then
    echo "holdout: UNRESOLVED  $test_id produced no summary line"; rm -f "$patch_file"; status=2; continue
  fi
  c_total="${clean%%:*}"; c_failed="${clean##*:}"
  if [ "$c_total" -eq 0 ]; then
    echo "holdout: UNRESOLVED  $test_id matched 0 tests"; rm -f "$patch_file"; status=2; continue
  fi
  if [ "$c_failed" -ne 0 ]; then
    echo "holdout: INCONCLUSIVE  $test_id already fails on the clean tree ($c_failed of $c_total)"
    # Every test in a class failing is rarely the change's doing. An integration class needs
    # Testcontainers, and a stopped Docker fails all of them identically, which reads as a broken
    # branch unless the message says otherwise.
    if [ "$c_failed" -eq "$c_total" ] && ! docker info >/dev/null 2>&1; then
      echo "                       All of them failed and the Docker daemon is unreachable."
      echo "                       An integration class needs Testcontainers. Start Docker, or bind"
      echo "                       a unit test class instead."
    fi
    rm -f "$patch_file"; status=3; continue
  fi

  # 2. hold the hunk out.
  if ! ( cd "$wt" && git apply -R --unidiff-zero "$patch_file" 2>/dev/null ); then
    echo "holdout: UNRESOLVED  the hunk does not apply in reverse (rebase drift?)"
    rm -f "$patch_file"; status=2; continue
  fi

  # 3. rebuild. A held-out hunk that breaks the build proves nothing either way.
  if ! ( cd "$wt" && dotnet build barakoCMS.sln --configuration Release --no-restore >/dev/null 2>&1 ); then
    echo "holdout: INCONCLUSIVE  the tree does not build with $path $anchor held out."
    echo "                       Bind a behaviour line, not a signature or a declaration."
    ( cd "$wt" && git checkout -- . >/dev/null 2>&1 )
    rm -f "$patch_file"; status=3; continue
  fi

  # 4. the named test must now fail.
  held=$(run_class "$wt" "$test_id")
  h_total="${held%%:*}"; h_failed="${held##*:}"

  if [ "$held" = "none" ] || [ "$h_total" -eq 0 ]; then
    echo "holdout: UNRESOLVED  $test_id ran 0 tests with the hunk held out"
    status=2
  elif [ "$h_failed" -eq 0 ]; then
    echo "holdout: CAUGHT      $test_id passed with its hunk held out"
    echo "                     hunk     $path $anchor"
    echo "                     clean    $c_total ran, 0 failed"
    echo "                     held out $h_total ran, 0 failed   <- must fail here"
    echo "                     The test does not depend on the code it claims to test."
    status=1
  else
    echo "holdout: ok          $test_id failed as required ($h_failed of $h_total)"
  fi

  # 5. restore and confirm. A restore that does not restore is its own defect.
  ( cd "$wt" && git checkout -- . >/dev/null 2>&1 )
  ( cd "$wt" && dotnet build barakoCMS.sln --configuration Release --no-restore >/dev/null 2>&1 ) \
    || { echo "holdout: INCONCLUSIVE  the tree does not build after restore"; status=3; }
  rm -f "$patch_file"
done

case "$status" in
  0) echo "holdout: ${#bindings[@]} binding(s) held out, each named test failed and passed again on restore" ;;
  1) echo "holdout: a test passed with its hunk held out" ;;
  2) echo "holdout: a binding could not be resolved, so nothing was proven for it" ;;
  3) echo "holdout: inconclusive, the held-out tree did not build" ;;
esac
exit $status
