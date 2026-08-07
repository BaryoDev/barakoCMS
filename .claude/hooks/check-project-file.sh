#!/usr/bin/env bash
# PreToolUse hook for Edit|Write.
#
# Enforces the two build rules from CLAUDE.md that are easy to break by habit:
#   1. Package versions live in Directory.Packages.props, never in a .csproj.
#   2. No floating versions, because they make a build of the same commit non-reproducible.
#
# Denies the edit with an explanation rather than letting it land and fail in CI.

set -uo pipefail

payload=$(cat)

file=$(printf '%s' "$payload" | jq -r '.tool_input.file_path // empty')
[ -z "$file" ] && exit 0

# Whatever text this call would put into the file.
added=$(printf '%s' "$payload" | jq -r '[.tool_input.content, .tool_input.new_string, (.tool_input.edits // [])[]?.new_string] | map(select(. != null)) | join("\n")')
[ -z "$added" ] && exit 0

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

# Allows the edit through, but surfaces the convention so it is a deliberate choice
# rather than an accident.
warn() {
  jq -n --arg m "$1" '{ systemMessage: $m }'
  exit 0
}

case "$file" in
  *Directory.Packages.props)
    if printf '%s' "$added" | grep -qE 'Version="[^"]*\*'; then
      deny "Floating package version rejected. A wildcard such as 3.7.* lets two builds of the same commit resolve different dependencies, and central package management rejects it outright. Pin the exact version. See CLAUDE.md section 3."
    fi
    ;;
  *.csproj)
    if printf '%s' "$added" | grep -qE '<PackageReference[^>]*Version='; then
      deny "Package versions do not belong in a .csproj. This repo uses central package management: add or update the <PackageVersion> entry in Directory.Packages.props, then reference the package without a version, e.g. <PackageReference Include=\"Marten\" />. See CLAUDE.md section 3."
    fi
    for prop in TargetFramework Nullable ImplicitUsings LangVersion Company Authors PackageLicenseExpression RepositoryUrl; do
      if printf '%s' "$added" | grep -qE "<${prop}>"; then
        warn "<${prop}> is already set for every project in Directory.Build.props. Overriding it here is allowed when this project genuinely differs, but if the whole solution should move, change it centrally instead. See CLAUDE.md section 3."
      fi
    done
    ;;
esac

exit 0
