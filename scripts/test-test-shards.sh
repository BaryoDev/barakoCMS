#!/usr/bin/env bash
# Tests scripts/test-shards.py against a fixed fake listing: every class lands in exactly one shard,
# two runs give the same split, theory arguments do not create classes, and a listing below the
# 400-class floor (or an empty one) exits non-zero. Also covers the duration balancing: every class
# still lands once when some have no recorded duration. No build, no containers.
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

# Balancing by recorded durations. The first 300 fake classes have a duration, the other 150 do not,
# so a class missing from the file is in every run below.
python3 - "$work/durations.json" <<'PY'
import json, sys
json.dump({f"BarakoCMS.Tests.Fake{i:04d}Tests": (i * 37) % 101 + 1 for i in range(1, 301)}
          | {"BarakoCMS.Tests.GoneTests": 500}, open(sys.argv[1], "w"))
PY
bshards() { for s in 1 2 3 4; do python3 scripts/test-shards.py --shard "$s" --of 4 --listing "$work/full.txt" --durations "$work/durations.json" || return 1; done; }
all=$(bshards | sort); code=$?
expect "balanced split runs cleanly" "$code"
[ "$(wc -l <<<"$all" | tr -d ' ')" = 450 ]; expect "balanced split covers all 450 classes, 150 of them without a duration" $?
[ "$(uniq -d <<<"$all" | wc -l | tr -d ' ')" = 0 ]; expect "balanced split puts no class in two shards" $?
[ "$all" = "$(grep -o 'BarakoCMS.Tests.Fake[0-9]*Tests' "$work/full.txt" | sort -u)" ]; expect "a duration for a class that is gone adds no class" $?
[ "$(bshards)" = "$(bshards)" ]; expect "two balanced runs give the same split" $?
[ "$(bshards)" != "$(shards "$work/full.txt" 4)" ]; expect "the durations change the split" $?
loads=$(for s in 1 2 3 4; do
  python3 scripts/test-shards.py --shard "$s" --of 4 --listing "$work/full.txt" --durations "$work/durations.json" |
    python3 -c "
import json, sys
d = json.load(open('$work/durations.json'))
median = sorted(d.values())[len(d) // 2]
print(sum(d.get(l.strip(), median) for l in sys.stdin))"
done)
spread=$(sort -n <<<"$loads" | awk 'NR == 1 {lo = $1} {hi = $1} END {print hi - lo}')
[ "$spread" -le 101 ]; expect "the load, a class without a duration counted as the median, differs by at most one class between shards (spread $spread)" $?
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" --durations "$work/none.json" >/dev/null; expect "a missing durations file falls back to the stable split" $?
echo '[1, 2]' > "$work/bad.json"
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" --durations "$work/bad.json" >/dev/null 2>&1; [ $? -ne 0 ]; expect "a durations file that is not an object fails" $?
python3 scripts/test-shards.py --shard 1 --of 4 --listing "$work/full.txt" >/dev/null; expect "the checked-in durations file loads" $?
python3 -c "import json; d = json.load(open('scripts/test-class-durations.json')); assert len(d) > 400"; expect "the checked-in durations file lists more than 400 classes" $?

# record-class-durations.py sums a class's tests, theory rows included, across trx files.
mkdir -p "$work/trx"
for f in a b; do
  cat > "$work/trx/$f.trx" <<TRX
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testName="BarakoCMS.Tests.OneTests.First" duration="00:00:01.5000000" />
<UnitTestResult testName="BarakoCMS.Tests.OneTests.Theory(route: &quot;/a.b(c)&quot;)" duration="00:01:00.2500000" />
<UnitTestResult testName="BarakoCMS.Tests.Outer+Inner.Nested" duration="00:00:02.0000000" />
</Results></TestRun>
TRX
done
python3 scripts/record-class-durations.py "$work/trx" --out "$work/rec.json" >/dev/null; expect "record-class-durations runs" $?
[ "$(python3 -c "import json; d = json.load(open('$work/rec.json')); print(d['BarakoCMS.Tests.OneTests'], d['BarakoCMS.Tests.Outer+Inner'], len(d))")" = "123.5 4.0 2" ]; expect "record-class-durations sums per class across files" $?
python3 scripts/record-class-durations.py "$work/none" --out "$work/rec2.json" >/dev/null 2>&1; [ $? -ne 0 ]; expect "record-class-durations fails when it finds no trx" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
