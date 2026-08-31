#!/usr/bin/env bash
# Fails when a tracked npm lockfile sits in a directory no Dependabot entry watches.
#
#   scripts/check-dependabot-coverage.sh [dependabot.yml]
#
# site/ went unwatched for months (#153) and nothing said so, because a missing
# ecosystem entry is silence, not an error. Dependabot validates the config it is
# given; it has no opinion about the directory nobody listed. This is the check
# that has an opinion.
#
# Scope is npm on purpose. The nuget and github-actions entries both point at "/"
# and discover the whole tree from there, so there is no per-directory listing to
# fall behind. npm is the ecosystem where each lockfile needs naming.
set -euo pipefail

CONFIG="${1:-.github/dependabot.yml}"
[ -f "$CONFIG" ] || { echo "dependabot-coverage: $CONFIG not found" >&2; exit 1; }

# Directories, relative to the repo root and written the way Dependabot writes
# them ("/admin"), for every tracked package-lock.json. Vendored trees are not ours
# to update, so node_modules is excluded.
LOCKDIRS=$(git ls-files '*package-lock.json' \
  | grep -v '/node_modules/' \
  | sed 's#/package-lock.json$##; s#^package-lock.json$#.#' \
  | sed 's#^#/#; s#^/\.$#/#' \
  | sort -u)

[ -n "$LOCKDIRS" ] || { echo "dependabot-coverage: no tracked package-lock.json found" >&2; exit 1; }

# Parsed with a YAML parser rather than grepped. A grep for `directory:` cannot tell
# an npm entry from a nuget one, and would call site/ covered because some other
# ecosystem happens to mention the same path.
WATCHED=$(ruby -ryaml -e '
  cfg = YAML.load_file(ARGV[0])
  updates = cfg["updates"] || []
  updates.each do |u|
    next unless u["package-ecosystem"] == "npm"
    dirs = u["directories"] || [u["directory"]]
    dirs.compact.each { |d| puts d }
  end
' "$CONFIG" | sort -u)

FAIL=0
for dir in $LOCKDIRS; do
  if printf '%s\n' "$WATCHED" | grep -qx -- "$dir"; then
    echo "  OK   $dir is watched by an npm Dependabot entry"
  else
    echo "::error::$dir has a tracked package-lock.json and no npm entry in $CONFIG watches it."
    FAIL=1
  fi
done

if [ "$FAIL" -ne 0 ]; then
  echo "dependabot-coverage: add a package-ecosystem: \"npm\" entry for the directories above." >&2
  exit 1
fi

echo "dependabot-coverage: every tracked npm lockfile is watched."
