#!/usr/bin/env bash
# Prints one line per review-worthy rule the current diff against origin/master fires: touched
# auth/permission surface, a raw-SQL-shaped call, secret handling, background/concurrency
# primitives, destructive deletes, infra/supply-chain files, or a test losing an assertion. This is
# advisory, not a gate: it always exits 0, and prints nothing on a diff that fires no rule.
#
#   bash scripts/needs-review.sh

set -uo pipefail

cd "$(dirname "$0")/.."

merge_base=$(git merge-base origin/master HEAD 2>/dev/null) || merge_base=""

if [ -z "$merge_base" ]; then
  echo "needs-review: no merge base with origin/master found; nothing to check"
  exit 0
fi

# Working tree against merge base, not HEAD, so uncommitted changes are covered too. Untracked
# files never show up in `git diff`, so they are folded in as synthetic new-file diff blocks. The
# three tooling scripts are excluded from their own scan: their source names every rule keyword as
# a string literal, which would otherwise flag this script and its siblings on every run.
tracked_diff=$(git diff "$merge_base" -- . \
  ':(exclude)scripts/preflight.sh' ':(exclude)scripts/sync-master.sh' ':(exclude)scripts/needs-review.sh')

untracked_diff=""
while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || continue
  case "$f" in
    scripts/preflight.sh|scripts/sync-master.sh|scripts/needs-review.sh) continue ;;
  esac
  untracked_diff="$untracked_diff
--- /dev/null
+++ b/$f
$(sed 's/^/+/' "$f")"
done < <(git ls-files --others --exclude-standard)

diff_file=$(mktemp)
trap 'rm -f "$diff_file"' EXIT
{ printf '%s\n' "$tracked_diff"; printf '%s\n' "$untracked_diff"; } > "$diff_file"

python3 - "$diff_file" <<'PY'
import os
import re
import sys

diff_text = open(sys.argv[1], encoding='utf-8', errors='replace').read()

file_added = {}
file_removed = {}
all_paths = set()

current_file = None
pending_old = None
for line in diff_text.splitlines():
    if line.startswith('--- '):
        p = line[4:]
        if p.startswith('a/'):
            p = p[2:]
        pending_old = None if p == '/dev/null' else p
        continue
    if line.startswith('+++ '):
        p = line[4:]
        if p.startswith('b/'):
            p = p[2:]
        current_file = pending_old if p == '/dev/null' else p
        if current_file:
            all_paths.add(current_file)
            file_added.setdefault(current_file, [])
            file_removed.setdefault(current_file, [])
        continue
    if current_file is None:
        continue
    if line.startswith('+'):
        file_added[current_file].append(line[1:])
    elif line.startswith('-'):
        file_removed[current_file].append(line[1:])

def basename(path):
    return os.path.basename(path)

def stem(path):
    return os.path.splitext(basename(path))[0]

rule_hits = {}

def hit(rule_id, path):
    rule_hits.setdefault(rule_id, set()).add(path)

AUTH_FILE_STEMS = {"PermissionResolver", "ApiKeyScopeProcessor", "SystemCapabilities"}
AUTH_FEATURE_DIRS = ("Features/Auth/", "Features/Roles/", "Features/ApiKeys/", "Features/Tenants/", "Features/Public/")

sql_re = re.compile(r'MatchesSql\s*\(|OrderBySql\s*\(|Query<[^>]*>\s*\(\s*"')
secret_word_re = re.compile(r'secret|token|password|apikey', re.IGNORECASE)
concurrency_re = re.compile(r'BackgroundService|IHostedService|Channel<|Lease|Claim')
delete_re = re.compile(r'DeleteWhere|HardDelete|Erase')

for path in all_paths:
    norm = path.replace(os.sep, '/')

    if norm.startswith('barakoCMS/Infrastructure/Auth/') or stem(norm) in AUTH_FILE_STEMS \
       or any(d in norm for d in AUTH_FEATURE_DIRS):
        hit('auth-surface', norm)

    if basename(norm) == 'ServiceCollectionExtensions.cs' or 'migrations/' in norm.lower():
        hit('schema-and-wiring', norm)

    if norm.startswith('.github/workflows/') or basename(norm).startswith('Dockerfile') \
       or basename(norm) == 'Directory.Packages.props':
        hit('infra-and-supply-chain', norm)

    is_endpoint_file = 'Endpoint' in basename(norm)
    is_test_file = basename(norm).endswith('Tests.cs') or 'BarakoCMS.Tests/' in norm

    for l in file_added.get(path, []):
        if is_endpoint_file and 'Configure()' in l:
            hit('endpoint-configure', norm)
        if sql_re.search(l):
            hit('dynamic-sql', norm)
        if 'AutoCreate' in l:
            hit('schema-and-wiring', norm)
        if 'ISecretProtector' in l or secret_word_re.search(l):
            hit('secrets', norm)
        if concurrency_re.search(l):
            hit('background-and-concurrency', norm)
        if delete_re.search(l):
            hit('destructive-delete', norm)

    if is_test_file:
        for l in file_removed.get(path, []):
            stripped = l.lstrip()
            if stripped.startswith('Should(') or stripped.startswith('Assert.'):
                hit('weakened-test-assertions', norm)

RULE_LABELS = {
    'auth-surface': 'auth/permission surface touched',
    'endpoint-configure': 'Configure() changed in an Endpoint file',
    'dynamic-sql': 'dynamic SQL construction (MatchesSql/OrderBySql/Query<T>("..."))',
    'schema-and-wiring': 'schema or DI wiring touched (ServiceCollectionExtensions.cs, migrations/, AutoCreate)',
    'secrets': 'secret handling touched (ISecretProtector, or an identifier naming secret/token/password/apikey)',
    'background-and-concurrency': 'background work or concurrency primitive touched (BackgroundService/IHostedService/Channel</Lease/Claim)',
    'destructive-delete': 'destructive delete touched (DeleteWhere/HardDelete/Erase)',
    'infra-and-supply-chain': 'infra or supply chain touched (.github/workflows, Dockerfile*, Directory.Packages.props)',
    'weakened-test-assertions': 'a test file had an assertion line removed (Should(/Assert.)',
}

RULE_ORDER = [
    'auth-surface', 'endpoint-configure', 'dynamic-sql', 'schema-and-wiring', 'secrets',
    'background-and-concurrency', 'destructive-delete', 'infra-and-supply-chain',
    'weakened-test-assertions',
]

for rid in RULE_ORDER:
    if rid in rule_hits:
        paths = ', '.join(sorted(rule_hits[rid]))
        print(f"needs-review: {RULE_LABELS[rid]} -- {paths}")
PY

exit 0
