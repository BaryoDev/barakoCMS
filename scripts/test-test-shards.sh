#!/usr/bin/env bash
# Tests scripts/test-shards.py against a fixed fake listing: every class lands in exactly one shard,
# two runs give the same split, theory arguments do not create classes, and a listing below the
# 400-class floor (or an empty one) exits non-zero. No build, no containers.
#
#   bash scripts/test-test-shards.sh

set -uo pipefail
cd "$(dirname "$0")/.."

pass=0
fail=0
expect() { # $1 = description, $2 = condition result (0 is true)
  if [ "$2" = 0 ]; then echo "  ok    $1"; pass=$((pass + 1)); else echo "  FAIL  $1"; fail=$((fail + 1)); fi
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

make_listing() { # $1 = class count, $2 = file
  : > "$2"
  for i in $(seq 1 "$1"); do
    printf '  BarakoCMS.Tests.Fake%04dTests.First_test\n  BarakoCMS.Tests.Fake%04dTests.Second_test(route: "/a.b(c)")\n' "$i" "$i" >> "$2"
  done
  printf 'Discovered %d tests in assembly - x.dll (net10.0|arm64)\n' $(($1 * 2)) >> "$2"
}

shards() { # $1 = listing, $2 = N
  for s in $(seq 1 "$2"); do python3 scripts/test-shards.py --shard "$s" --of "$2" --listing "$1" || return 1; done
}

make_listing 450 "$work/full.txt"

for n in 1 3 4 7; do
  all=$(shards "$work/full.txt" "$n" | sort); code=$?
  expect "N=$n runs cleanly" "$code"
  [ "$(wc -l <<<"$all" | tr -d ' ')" = 450 ]; expect "N=$n covers all 450 classes" $?
  [ "$(uniq -d <<<"$all" | wc -l | tr -d ' ')" = 0 ]; expect "N=$n puts no class in two shards" $?
  [ "$all" = "$(grep -o 'BarakoCMS.Tests.Fake[0-9]*Tests' "$work/full.txt" | sort -u)" ]; expect "N=$n has exactly the listed classes" $?
done

for s in 1 2 3 4; do
  [ -n "$(python3 scripts/test-shards.py --shard "$s" --of 4 --listing "$work/full.txt")" ]; expect "shard $s of 4 is not empty" $?
done

[ "$(shards "$work/full.txt" 4)" = "$(shards "$work/full.txt" 4)" ]; expect "two runs give the same split" $?

make_listing 451 "$work/more.txt"
before=$(python3 scripts/test-shards.py --shard 2 --of 4 --listing "$work/full.txt")
after=$(python3 scripts/test-shards.py --shard 2 --of 4 --listing "$work/more.txt")
[ -z "$(comm -23 <(sort <<<"$before") <(sort <<<"$after"))" ]; expect "adding a class moves no existing class" $?

line=$(python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" --filter-args)
[ "$(grep -o -e '--filter-class' <<<"$line" | wc -l | tr -d ' ')" = "$(python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" | wc -l | tr -d ' ')" ]; expect "--filter-args has one --filter-class per class" $?
grep -q '^--filter-class BarakoCMS.Tests.Fake' <<<"$line"; expect "--filter-args starts with --filter-class" $?

make_listing 399 "$work/below.txt"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/below.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "399 classes fails" $?
: > "$work/empty.txt"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/empty.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "an empty listing fails" $?
python3 scripts/test-shards.py --shard 5 --of 4 --listing "$work/full.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "shard 5 of 4 fails" $?
python3 scripts/test-shards.py --shard 0 --of 4 --listing "$work/full.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "shard 0 fails" $?

# A test line the parser cannot read would drop its class from every shard without a word, so the
# listing's own count has to match the lines read.
{ cat "$work/full.txt"; printf '  BarakoCMS.Tests.FakeSpacedTests.A test named with spaces\n'; } | sed 's/^Discovered 900 /Discovered 901 /' > "$work/spaced.txt"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/spaced.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "a test line the parser cannot read fails" $?
{ cat "$work/full.txt"; printf '  Some.Stray.Warning\n'; } > "$work/stray.txt"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/stray.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "an indented non-test line fails" $?
grep -v '^Discovered' "$work/full.txt" > "$work/nocount.txt"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/nocount.txt" >/dev/null 2>&1; [ $? -ne 0 ]; expect "a listing without a Discovered count fails" $?
{ cat "$work/full.txt"; printf '  BarakoCMS.Tests.Outer+Inner.Nested_test\n'; } | sed 's/^Discovered 900 /Discovered 901 /' > "$work/nested.txt"
python3 scripts/test-shards.py --shard 1 --of 1 --listing "$work/nested.txt" | grep -qx 'BarakoCMS.Tests.Outer+Inner'; expect "a nested class keeps its plus sign" $?

# Balancing by recorded durations. A file naming some classes, skewed so a hash split would be uneven.
python3 - "$work/durations.json" <<'PY'
import json, sys
classes = {f"BarakoCMS.Tests.Fake{i:04d}Tests": (300.0 if i % 50 == 0 else 1.0 + (i % 7)) for i in range(1, 451)}
del classes["BarakoCMS.Tests.Fake0007Tests"]
classes["BarakoCMS.Tests.GoneTests"] = 999.0
json.dump({"classes": classes}, open(sys.argv[1], "w"))
PY
dshards() { # $1 = N
  for s in $(seq 1 "$1"); do python3 scripts/test-shards.py --shard "$s" --of "$1" --listing "$work/full.txt" --durations "$work/durations.json" || return 1; done
}
for n in 2 4 5; do
  all=$(dshards "$n" | sort); code=$?
  expect "with durations N=$n runs cleanly" "$code"
  [ "$all" = "$(grep -o 'BarakoCMS.Tests.Fake[0-9]*Tests' "$work/full.txt" | sort -u)" ]; expect "with durations N=$n places every class exactly once, including one missing from the file" $?
  grep -q 'GoneTests' <<<"$all"; [ $? -ne 0 ]; expect "with durations N=$n ignores a class that is no longer listed" $?
done
[ "$(dshards 4)" = "$(dshards 4)" ]; expect "with durations two runs give the same split" $?
python3 - "$work/durations.json" "$work/full.txt" <<'PY'
import json, subprocess, sys
durations = json.load(open(sys.argv[1]))["classes"]
loads = []
for shard in range(1, 5):
    out = subprocess.run(["python3", "scripts/test-shards.py", "--shard", str(shard), "--of", "4", "--listing", sys.argv[2],
                          "--durations", sys.argv[1]], capture_output=True, text=True, check=True).stdout.split()
    loads.append(sum(durations.get(name, 4.0) for name in out))
sys.exit(0 if max(loads) - min(loads) < 300 else 1)
PY
expect "with durations the shards differ by less than the longest class" $?

printf '{"classes": []}' > "$work/bad.json"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" --durations "$work/bad.json" >/dev/null 2>&1; [ $? -ne 0 ]; expect "a durations file of the wrong shape fails" $?
printf 'not json' > "$work/bad.json"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" --durations "$work/bad.json" >/dev/null 2>&1; [ $? -ne 0 ]; expect "a durations file that is not JSON fails" $?
[ "$(python3 scripts/test-shards.py --shard 2 --of 4 --listing "$work/full.txt" --durations "$work/none.json")" = "$(python3 scripts/test-shards.py --shard 2 --of 4 --listing "$work/full.txt" --no-durations)" ]; expect "a missing durations file falls back to the hash split" $?

# The report reader behind the durations file.
cat > "$work/a.trx" <<'TRX'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testName="A.B.OneTests.First" duration="00:00:01.5000000" outcome="Passed" />
<UnitTestResult testName="A.B.OneTests.Theory(route: &quot;/a.b&quot;)" duration="00:01:00.2500000" outcome="Passed" />
<UnitTestResult testName="A.B.TwoTests.Only" duration="00:00:00.1000000" outcome="Failed" />
</Results></TestRun>
TRX
python3 scripts/test-durations.py --out "$work/out.json" "$work/a.trx" >/dev/null; expect "test-durations reads a report" $?
python3 - "$work/out.json" <<'PY'
import json, sys
classes = json.load(open(sys.argv[1]))["classes"]
sys.exit(0 if classes == {"A.B.OneTests": 61.75, "A.B.TwoTests": 0.1} else 1)
PY
expect "test-durations sums a class and strips theory arguments" $?
python3 scripts/test-durations.py --out "$work/none.json" scripts/testdata/trx/broken.trx >/dev/null 2>&1; [ $? -ne 0 ]; expect "test-durations refuses an unreadable report" $?
[ ! -e "$work/none.json" ]; expect "test-durations writes nothing for an unreadable report" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
