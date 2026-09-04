#!/usr/bin/env bash
# Runs everything a PR needs proven before it opens, so an agent (or a person) does not have to
# rediscover the rules by hand each time. Exits non-zero on the first failure with a one-line reason.
#
#   bash scripts/preflight.sh -class Some.Fully.Qualified.TestClass [-class Another.TestClass ...]
#
# Every "-class FQN" is passed straight through to the xunit.v3 native runner via
# `dotnet run --no-build -- -class FQN`, one run per class. Never runs the whole suite: other agents
# share this machine and a full run is both slow and not what any single PR needs proven.

set -uo pipefail

cd "$(dirname "$0")/.."

classes=()
while [ $# -gt 0 ]; do
  case "$1" in
    -class)
      shift
      [ $# -gt 0 ] || { echo "preflight: -class needs a value"; exit 1; }
      classes+=("$1")
      shift
      ;;
    *)
      echo "preflight: unrecognised argument '$1'"
      exit 1
      ;;
  esac
done

fail() { echo "preflight: $1"; exit 1; }

# Locked restore first, before any build: `dotnet build`'s implicit restore rewrites every
# packages.lock.json to match Directory.Packages.props, so a locked-mode check run after a build
# always passes even against a lock file that no longer matches what is committed. Build with
# --no-restore afterwards so it cannot silently redo that restore and hide the same problem.
echo "== locked restore =="
dotnet restore barakoCMS.sln --locked-mode || fail "restore --locked-mode failed (lock file is stale)"

echo "== build =="
dotnet build barakoCMS.sln --configuration Release --no-restore || fail "build failed"

for class in ${classes[@]+"${classes[@]}"}; do
  echo "== test: $class =="
  test_output=$(mktemp)
  dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj --no-build --configuration Release -- -class "$class" 2>&1 \
    | tee "$test_output"
  run_status=$?
  if [ "$run_status" -ne 0 ]; then
    rm -f "$test_output"
    fail "test class $class failed"
  fi
  # A class name that matches nothing still discovers and "finishes" cleanly, exit 0, with a
  # summary line reading "Total: 0" and no Failed/Errors fields at all. Parse it so a typo in
  # -class reads as a failure instead of a silent zero-test pass.
  total=$(grep -oE 'Total: [0-9]+' "$test_output" | tail -1 | grep -oE '[0-9]+' || true)
  rm -f "$test_output"
  [ -n "$total" ] || fail "test class $class produced no summary line"
  [ "$total" -gt 0 ] || fail "test class $class matched 0 tests (check for a typo in the class name)"
done

echo "== changelog fragments =="
bash scripts/changelog-assemble.sh --check || fail "changelog-assemble --check failed"

echo "== module versions =="
bash scripts/check-module-versions.sh || fail "check-module-versions.sh failed"

echo "== dash and banned-word scan =="
# Words come from ~/.claude/CLAUDE.md at run time, never inlined here: the banned list itself is
# banned from appearing in a shell command, and a commit hook rejects it if it does.
banned_source="$HOME/.claude/CLAUDE.md"
[ -f "$banned_source" ] || fail "cannot find $banned_source; the banned-word list cannot be read, fix HOME or restore the file rather than skip the scan"

banned_file=$(mktemp)
trap 'rm -f "$banned_file"' EXIT
python3 - "$banned_source" "$banned_file" <<'PY' || fail "failed to extract the banned-word list from $banned_source"
import re, sys
src, out = sys.argv[1], sys.argv[2]
text = open(src, encoding='utf-8').read()
m = re.search(r'Banned:\s*(.+)', text)
words = []
if m:
    words = re.findall(r'"([^"]+)"', m.group(1))
with open(out, 'w', encoding='utf-8') as f:
    for w in words:
        if w.strip():
            f.write(w + "\n")
PY

# Diffed against the merge base rather than "origin/master...HEAD", and against the working tree
# rather than HEAD, so uncommitted changes are scanned too, not just committed ones. Untracked
# files are added separately: `git diff` never sees a file that has not been added, and a new file
# is all "added" lines.
merge_base=$(git merge-base origin/master HEAD)
tracked_added=$(git diff "$merge_base" -- . | grep -E '^\+' | grep -Ev '^\+\+\+')

untracked_added=""
while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || continue
  # Treat every line of a new untracked file as added, same as a diff would show it once staged.
  untracked_added="$untracked_added
$(sed 's/^/+/' "$f")"
done < <(git ls-files --others --exclude-standard)

diff_added="$tracked_added
$untracked_added"

if [ -n "${diff_added//$'\n'/}" ]; then
  # Any character at or above U+2000 (general punctuation and beyond: em/en dashes, curly quotes,
  # ellipses, arrows) on an added line.
  bad_char=$(printf '%s\n' "$diff_added" | python3 -c "
import sys
for line in sys.stdin:
    for ch in line:
        if ord(ch) >= 0x2000:
            print(line.rstrip('\n'))
            break
" | head -1)
  [ -z "$bad_char" ] || fail "added line has a character above U+2000: $bad_char"

  if [ -s "$banned_file" ]; then
    while IFS= read -r word; do
      [ -n "$word" ] || continue
      hit=$(printf '%s\n' "$diff_added" | grep -iF -- "$word" | head -1) || true
      if [ -n "$hit" ]; then
        fail "added line uses banned word '$word': $hit"
      fi
    done < "$banned_file"
  fi
fi

echo "== workflow duplicate-key parse =="
changed_workflows=$( { git diff --name-only "$merge_base" -- '.github/workflows/*.yml' '.github/workflows/*.yaml'; \
  git ls-files --others --exclude-standard -- '.github/workflows/*.yml' '.github/workflows/*.yaml'; } | sort -u)
if [ -n "$changed_workflows" ]; then
  parser=""
  if python3 -c "import yaml" >/dev/null 2>&1; then
    parser=python
  else
    js_yaml=$(find "$HOME" -maxdepth 6 -type d -name js-yaml -path '*/node_modules/*' -print -quit 2>/dev/null)
    [ -n "$js_yaml" ] && parser=node
  fi

  case "$parser" in
    python)
      while IFS= read -r wf; do
        [ -n "$wf" ] || continue
        # A deleted workflow file still shows up in the diff's name list but no longer exists in
        # the working tree; nothing to parse, and it is not a duplicate-key problem.
        [ -f "$wf" ] || continue
        python3 - "$wf" <<'PY' || fail "duplicate key (or invalid YAML) in $wf"
import sys, yaml

class NoDupLoader(yaml.SafeLoader):
    pass

def no_duplicates_constructor(loader, node, deep=False):
    mapping = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in mapping:
            raise ValueError(f"duplicate key: {key!r}")
        mapping[key] = loader.construct_object(value_node, deep=deep)
    return mapping

NoDupLoader.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, no_duplicates_constructor
)

with open(sys.argv[1], encoding='utf-8') as f:
    yaml.load(f, Loader=NoDupLoader)
PY
      done <<< "$changed_workflows"
      ;;
    node)
      while IFS= read -r wf; do
        [ -n "$wf" ] || continue
        [ -f "$wf" ] || continue
        node -e "
          const yaml = require('$js_yaml');
          const fs = require('fs');
          yaml.load(fs.readFileSync(process.argv[1], 'utf8'));
        " "$wf" || fail "duplicate key (or invalid YAML) in $wf"
      done <<< "$changed_workflows"
      ;;
    *)
      fail "no duplicate-key-rejecting YAML parser available; install pyyaml or js-yaml"
      ;;
  esac
fi

echo "preflight: all checks passed"
