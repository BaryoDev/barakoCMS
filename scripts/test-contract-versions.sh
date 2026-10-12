#!/usr/bin/env bash
#
# Proves scripts/contract-versions.py answers "a contract number went up" only when one did, in a
# throwaway repository holding copies of the files it reads.
#
#   bash scripts/test-contract-versions.sh

set -uo pipefail

cd "$(dirname "$0")/.."

SRC="$PWD"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
pass=0
fail=0

mkdir -p "$TMP/scripts" "$TMP/barakoCMS/Features/Monitoring/Meta" "$TMP/BarakoCMS.Pages" "$TMP/BarakoCMS.Forms" \
    "$TMP/BarakoCMS.Abstractions/Modules" "$TMP/BarakoCMS.Tests"
cp scripts/contract-versions.py "$TMP/scripts/"
cp barakoCMS/Features/Monitoring/Meta/ApiContract.cs "$TMP/barakoCMS/Features/Monitoring/Meta/"
cp BarakoCMS.Pages/PagesContract.cs "$TMP/BarakoCMS.Pages/"
cp BarakoCMS.Forms/FormsModule.cs "$TMP/BarakoCMS.Forms/"
cp BarakoCMS.Abstractions/Modules/ModuleContract.cs "$TMP/BarakoCMS.Abstractions/Modules/"
printf 'class T { public const int Version = 1; }\n' > "$TMP/BarakoCMS.Tests/T.cs"
git -C "$TMP" init -q && git -C "$TMP" add -A && git -C "$TMP" -c user.name=t -c user.email=t@t commit -qm base

API="$TMP/barakoCMS/Features/Monitoring/Meta/ApiContract.cs"

# expect <description> <raised|not> ; edits are made by the caller and undone afterwards
expect() {
    local what="$1" want="$2" status
    python3 "$TMP/scripts/contract-versions.py" HEAD >/dev/null 2>&1
    status=$?
    if { [ "$want" = raised ] && [ "$status" -eq 0 ]; } || { [ "$want" = not ] && [ "$status" -eq 1 ]; }; then
        echo "  ok    $what ($want)"; pass=$((pass + 1))
    else
        echo "  FAIL  $what: expected $want, exit was $status"; fail=$((fail + 1))
    fi
    git -C "$TMP" checkout -q -- . && git -C "$TMP" clean -qfd
}

expect "nothing changed" not
sed -i -E 's/(public const int Version = )([0-9]+);/\1\2; \/\/ was lower/' "$API"
expect "a comment added on the Version line" not
v=$(grep -oE 'public const int Version = [0-9]+' "$API" | grep -oE '[0-9]+$')
sed -i -E "s/(public const int Version = )$v;/\1$((v + 1));/" "$API"
expect "ApiContract.Version raised" raised
d=$(grep -oE 'public const int DeliveryVersion = [0-9]+' "$API" | grep -oE '[0-9]+$')
sed -i -E "s/(public const int DeliveryVersion = )$d;/\1$((d - 1));/" "$API"
expect "ApiContract.DeliveryVersion lowered" not
printf '// public const int Version = 999;\n' >> "$API"
expect "a higher number in a comment" not
sed -i 's/Version = 1;/Version = 2;/' "$TMP/BarakoCMS.Tests/T.cs"
expect "a number raised in a test project" not
sed -i -E 's/(public const int Version = )([0-9]+);/\1999;/' "$TMP/BarakoCMS.Abstractions/Modules/ModuleContract.cs"
expect "the package contract (ModuleContract) raised" not
sed -i -E 's/(public const int Version = )([0-9]+);/\1999;/' "$TMP/BarakoCMS.Pages/PagesContract.cs"
expect "PagesContract.Version raised" raised
sed -i -E 's/(HttpContractVersion => )([0-9]+);/\1999;/' "$TMP/BarakoCMS.Forms/FormsModule.cs"
expect "a module's HttpContractVersion raised" raised

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
