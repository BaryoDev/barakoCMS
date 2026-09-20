#!/usr/bin/env bash
# Holdout's own gate. Builds a throwaway git repo with a known one-hunk change, runs
# scripts/holdout.sh against a set of specs, and requires each to produce its expected exit code.
#
#   bash scripts/testdata/holdout/run-fixtures.sh
#
# A checker that is never checked is the thing holdout exists to catch, so this runs in preflight
# and any change to holdout.sh must keep all cases green.

set -uo pipefail

holdout=$(cd "$(dirname "$0")/../../.." && pwd)/scripts/holdout.sh
[ -f "$holdout" ] || { echo "fixtures: cannot find scripts/holdout.sh"; exit 1; }

fx=$(mktemp -d)
cleanup() { rm -rf "$fx"; }
trap cleanup EXIT

# A repo whose feat branch adds exactly one line to one production file.
(
  cd "$fx"
  git init -q .
  git config user.email fixture@example.invalid
  git config user.name fixture
  mkdir -p barakoCMS/Features scripts
  cat > barakoCMS/Features/Redeem.cs <<'CS'
public class Redeem {
    public bool Check(Key k) {
        return true;
    }
}
CS
  git add -A && git commit -qm base && git branch -M master && git checkout -qb feat
  cat > barakoCMS/Features/Redeem.cs <<'CS'
public class Redeem {
    public bool Check(Key k) {
        if (k.RevokedAt is not null) return false;
        return true;
    }
}
CS
  # A file the branch adds outright, to prove holdout refuses to bind one. Reversing a whole-file
  # hunk deletes the file, which cannot compile, so the run could only ever end inconclusive.
  cat > barakoCMS/Features/Added.cs <<'CS'
public class Added {
    public int Value => 1;
}
CS
  mkdir -p BarakoCMS.Tests
  cat > BarakoCMS.Tests/RedeemTests.cs <<'CS'
public class RedeemTests { }
CS
  git add -A && git commit -qm "reject revoked keys and add a new file"
) >/dev/null 2>&1

cp "$holdout" "$fx/scripts/holdout.sh"
chmod +x "$fx/scripts/holdout.sh"

spec() { printf '%s\n' "$2" > "$fx/$1.md"; }

spec ok        '```holdout
- test: Some.Tests.RedeemTests
  breaks: barakoCMS/Features/Redeem.cs "RevokedAt is not null"
- untested: barakoCMS/Features/Added.cs #1
  why: a file this change adds outright cannot be held out
```'
spec ordinal   '```holdout
- test: Some.Tests.RedeemTests
  breaks: barakoCMS/Features/Redeem.cs #1
- untested: barakoCMS/Features/Added.cs #1
  why: a file this change adds outright cannot be held out
```'
spec noanchor  '```holdout
- test: Some.Tests.RedeemTests
  breaks: barakoCMS/Features/Redeem.cs "NotInTheDiffAnywhere"
```'
spec unclaimed '```holdout
- untested: barakoCMS/Features/Other.cs #1
  why: unrelated file
```'
spec outrange  '```holdout
- test: Some.Tests.RedeemTests
  breaks: barakoCMS/Features/Redeem.cs #9
```'
spec junk      '```holdout
- garbage: nonsense
```'
spec newfile   '```holdout
- test: Some.Tests.AddedTests
  breaks: barakoCMS/Features/Added.cs "public int Value"
- untested: barakoCMS/Features/Redeem.cs #1
  why: covered elsewhere
```'
spec allunt    '```holdout
- untested: barakoCMS/Features/Redeem.cs #1
  why: shipped data, no compiled behaviour to hold out
- untested: barakoCMS/Features/Added.cs #1
  why: added outright
```'
spec nonenotests '```holdout
none: a refactor with no new tests
```'
spec nonelie   '```holdout
none: claiming nothing while the diff touches production
```'
spec noneplus  '```holdout
none: contradictory
- test: T
  breaks: barakoCMS/Features/Redeem.cs "RevokedAt"
```'
spec noblock   'a PR body with no holdout block at all'
spec emptyblk  '```holdout
```'

pass=0
fail=0
check() {
  local name="$1" want="$2" file="$3"
  ( cd "$fx" && HOLDOUT_BASE=master bash scripts/holdout.sh --spec "$file.md" --dry-run ) >/dev/null 2>&1
  local got=$?
  if [ "$got" -eq "$want" ]; then
    printf "  ok    %-26s exit %d\n" "$name" "$got"
    pass=$((pass + 1))
  else
    printf "  FAIL  %-26s want %d got %d\n" "$name" "$want" "$got"
    fail=$((fail + 1))
  fi
}

echo "== holdout fixtures =="
check "valid content anchor"    0 ok
check "valid ordinal anchor"    0 ordinal
check "anchor matches nothing"  2 noanchor
check "unclaimed production"    2 unclaimed
check "ordinal out of range"    2 outrange
check "malformed block line"    2 junk
check "binding to a new file"   2 newfile
check "none over real changes"  2 nonelie
check "none, tests added"       2 nonenotests
check "none plus a binding"     2 noneplus
check "every hunk untested"     0 allunt
check "no holdout block"        2 noblock
check "empty holdout block"     2 emptyblk
echo "  ---- $pass passed, $fail failed"

[ "$fail" -eq 0 ] || exit 1
