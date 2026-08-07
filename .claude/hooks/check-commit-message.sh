#!/usr/bin/env bash
# PreToolUse hook for Bash, filtered to git commit by the `if` rule in settings.json.
#
# Keeps AI attribution trailers out of the history. They are noise in a changelog, they
# misattribute authorship, and once pushed they are only removable by rewriting history.
# The `attribution` block in .claude/settings.json turns off the built-in trailer; this
# catches a message that carries one anyway.

set -uo pipefail

payload=$(cat)
command=$(printf '%s' "$payload" | jq -r '.tool_input.command // empty')
[ -z "$command" ] && exit 0

case "$command" in
  *"git commit"*) ;;
  *) exit 0 ;;
esac

deny() {
  jq -n --arg r "$1" '{
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "deny",
      permissionDecisionReason: $r
    }
  }'
  exit 0
}

if printf '%s' "$command" | grep -qiE 'co-authored-by:[[:space:]]*(claude|anthropic)'; then
  deny "Remove the Co-Authored-By trailer. Commits here carry no AI attribution: it misattributes authorship and can only be removed by rewriting history once pushed. See CLAUDE.md section 8."
fi

if printf '%s' "$command" | grep -qiE 'generated with \[?claude|🤖 generated'; then
  deny "Remove the 'Generated with Claude Code' line from the commit message. See CLAUDE.md section 8."
fi

exit 0
