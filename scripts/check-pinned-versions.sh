#!/usr/bin/env bash
# Three files pin the core version, and a release has to move all three together.
#
#   barakoCMS/barakoCMS.csproj                     the core version, and the release switch
#   BarakoCMS.Templates/BarakoCMS.Templates.csproj the template pack's own version
#   .../barakocms-module/.template.config/template.json  the core version a scaffolded module targets
#
# Nothing compared them until now. On the 4.1.0 release the core moved and the other two did not,
# and it surfaced as ModuleTemplateTests failing eighteen minutes into CI. That test is not the
# problem and should stay: it proves the template scaffolds against a version that exists. The
# problem was that nothing said so earlier, when saying so costs a second.
#
# Deliberately a string compare and not a semver parse. These are the same release or they are a
# mistake, and a parse would let 4.1.0 and 4.1 agree when the files have to match literally.

set -uo pipefail

cd "$(dirname "$0")/.."

core_csproj="barakoCMS/barakoCMS.csproj"
tmpl_csproj="BarakoCMS.Templates/BarakoCMS.Templates.csproj"
tmpl_json="BarakoCMS.Templates/templates/barakocms-module/.template.config/template.json"

for f in "$core_csproj" "$tmpl_csproj" "$tmpl_json"; do
  [ -f "$f" ] || { echo "check-pinned-versions: $f is missing"; exit 1; }
done

# sed rather than an XML or JSON parser: this script has to run before a build, on a machine that
# may have neither, and the shapes are fixed by the files themselves.
core=$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$core_csproj" | head -1)
tmpl=$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$tmpl_csproj" | head -1)
json=$(sed -n 's|.*"defaultValue"[[:space:]]*:[[:space:]]*"\([^"]*\)".*|\1|p' "$tmpl_json" | head -1)

# An empty capture means the shape moved, which must not read as agreement.
for pair in "core:$core" "template:$tmpl" "template.json:$json"; do
  name=${pair%%:*}
  value=${pair#*:}
  if [ -z "$value" ]; then
    echo "check-pinned-versions: could not read the $name version, so the file's shape has changed."
    echo "                       Update this script rather than removing the check."
    exit 1
  fi
done

if [ "$core" = "$tmpl" ] && [ "$core" = "$json" ]; then
  echo "All three pinned versions agree on $core."
  exit 0
fi

echo "check-pinned-versions: the three pinned versions disagree."
printf '  %-52s %s\n' "$core_csproj" "$core"
printf '  %-52s %s%s\n' "$tmpl_csproj" "$tmpl" "$([ "$tmpl" = "$core" ] || echo "   <- expected $core")"
printf '  %-52s %s%s\n' "$tmpl_json" "$json" "$([ "$json" = "$core" ] || echo "   <- expected $core")"
echo
echo "A release moves all three. The template pins the core version a scaffolded module targets,"
echo "so leaving it behind ships a template that references a package the release supersedes."
exit 1
