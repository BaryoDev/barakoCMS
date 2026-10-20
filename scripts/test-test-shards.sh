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
  printf 'Discovered 2 tests in assembly - x.dll (net10.0|arm64)\n' >> "$2"
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

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
