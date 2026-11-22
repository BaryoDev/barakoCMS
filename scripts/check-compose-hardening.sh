#!/usr/bin/env bash
# Fails when a service in a compose file has lost one of the hardening options every service keeps.
#
#   scripts/check-compose-hardening.sh docker-compose.yml docker-compose.prod.yml ...
#
# Each file is resolved with `docker compose config --format json`, so the check reads what Docker
# would run, after variable defaults and extends, not the text of the file. A service must have:
#
#   read_only            true, with tmpfs or a volume for every path it writes
#   cap_drop             ALL (capabilities added back go in cap_add, with the reason in a comment)
#   security_opt         no-new-privileges:true
#   user                 not root, or the label org.barakocms.hardening.root with the reason
#   mem_limit            above zero
#   pids_limit           above zero
#   healthcheck          a command, except on a one-shot service with `restart: "no"`
#
# The compose variables are given throwaway values so a file with required variables resolves. They
# say nothing about the stack: only the shape of each service is read.
#
# A jq or compose error fails the run. A file with no services fails the run. The check never
# passes because it could not look.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: check-compose-hardening.sh <compose-file>..." >&2
  exit 2
fi
command -v docker > /dev/null || { echo "check-compose-hardening: docker not found" >&2; exit 2; }
command -v jq > /dev/null || { echo "check-compose-hardening: jq not found" >&2; exit 2; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# Read instead of the project's own .env, so the result is the same on a laptop and in CI.
cat > "$tmp/throwaway.env" <<'ENV'
BARAKO_TAG=check
DB_PASSWORD=throwaway-value-for-the-check
JWT_KEY=throwaway-value-for-the-check-0123456789
JWT_SECRET=throwaway-value-for-the-check-0123456789
ADMIN_PASSWORD=throwaway-value-for-the-check
DOMAIN_API=api.example.test
ACME_EMAIL=ops@example.test
FRONTEND_ORIGINS=https://console.example.test
ENV

# Prints one line per missing option: "<service>: <option>". Empty output means the file is fine.
# `// 0` and `// []` only fill in what the file left out; a present but wrong value still fails.
read -r -d '' PROGRAM <<'JQ' || true
def has_root_label: ((.labels // {})["org.barakocms.hardening.root"] // "") | length > 0;
def user_name: ((.user // "") | tostring | split(":")[0]);
def number_or_zero: (. // 0) | if type == "number" then . else (tonumber? // 0) end;

if (.services | type) != "object" or (.services | length) == 0 then
  error("the file resolves to no services")
else
  .services | to_entries[] | .key as $name | .value as $svc
  | $svc
  | [
      (if .read_only == true then empty else "read_only" end),
      (if ((.cap_drop // []) | map(tostring | ascii_upcase) | index("ALL")) != null then empty else "cap_drop ALL" end),
      (if ((.security_opt // []) | map(tostring) | any(test("^no-new-privileges[:=]true$"))) then empty else "no-new-privileges" end),
      (if has_root_label or (user_name != "" and user_name != "root" and user_name != "0") then empty else "a non-root user or the org.barakocms.hardening.root label" end),
      (if (.mem_limit | number_or_zero) > 0 then empty else "mem_limit" end),
      (if (.pids_limit | number_or_zero) > 0 then empty else "pids_limit" end),
      (if .restart == "no" then empty
       elif ((.healthcheck.test // []) | type == "array" and length > 0 and .[0] != "NONE") and (.healthcheck.disable != true) then empty
       else "a healthcheck" end)
    ]
  | .[] | "\($name): missing \(.)"
end
JQ

failed=0
for file in "$@"; do
  [ -f "$file" ] || { echo "check-compose-hardening: $file is not a file" >&2; exit 2; }

  # env -i, so a variable in the caller's shell cannot change what the file resolves to.
  if ! resolved=$(env -i PATH="$PATH" HOME="${HOME:-/tmp}" docker compose --env-file "$tmp/throwaway.env" -f "$file" config --format json 2> "$tmp/compose.err"); then
    echo "check-compose-hardening: docker compose could not resolve $file:" >&2
    cat "$tmp/compose.err" >&2
    exit 2
  fi

  if ! findings=$(printf '%s' "$resolved" | jq -r "$PROGRAM" 2> "$tmp/jq.err"); then
    echo "check-compose-hardening: could not read the resolved $file:" >&2
    cat "$tmp/jq.err" >&2
    exit 2
  fi

  if [ -n "$findings" ]; then
    while IFS= read -r line; do
      echo "FAIL $file, service $line" >&2
    done <<< "$findings"
    failed=1
  else
    echo "ok   $file"
  fi
done

exit "$failed"
