#!/usr/bin/env bash
# Tests scripts/report-trx-failures.py against fixed reports: a failed test is named with its
# message and file:line, and a report with no failure or no readable XML exits non-zero, so the
# step never claims to have explained a failure it could not see.
#
#   bash scripts/test-report-trx-failures.sh

set -uo pipefail
cd "$(dirname "$0")/.."

data=scripts/testdata/trx
pass=0
fail=0

expect() { # $1 = description, $2 = condition result (0 is true)
  if [ "$2" = 0 ]; then echo "  ok    $1"; pass=$((pass + 1)); else echo "  FAIL  $1"; fail=$((fail + 1)); fi
}

out=$(python3 scripts/report-trx-failures.py "$data/failed.trx"); code=$?
expect "a failed report exits 0" "$code"
grep -q '^1 failed test(s):$' <<<"$out"; expect "it counts one failure" $?
grep -q '^FAILED BarakoCMS.Tests.RoleApiTests.Renaming_onto_a_taken_name_is_a_conflict$' <<<"$out"; expect "it names the failed test" $?
grep -q 'but found HttpStatusCode.InternalServerError' <<<"$out"; expect "it prints the message" $?
grep -q 'RoleApiTests.cs:line 212' <<<"$out"; expect "it prints the stack line" $?
grep -q '^::error title=BarakoCMS.Tests.RoleApiTests.Renaming_onto_a_taken_name_is_a_conflict::Expected' <<<"$out"; expect "it annotates the failure" $?
! grep -q 'A_role_keeps_its_name' <<<"$out"; expect "it leaves passed tests out" $?

out=$(python3 scripts/report-trx-failures.py "$data/theory.trx")
grep -qF '::error title=Routes.Refuse(path%3A "/a"%2C expected%3A 403)::Expected 403' <<<"$out"; expect "it escapes a theory name in the annotation title" $?

python3 scripts/report-trx-failures.py "$data/passed.trx" > /dev/null; code=$?
[ "$code" = 1 ]; expect "a report with no failure exits 1" $?

python3 scripts/report-trx-failures.py "$data/broken.trx" > /dev/null; code=$?
[ "$code" = 1 ]; expect "an unreadable report exits 1" $?

python3 scripts/report-trx-failures.py "$data/missing.trx" > /dev/null; code=$?
[ "$code" = 1 ]; expect "a missing report exits 1" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
