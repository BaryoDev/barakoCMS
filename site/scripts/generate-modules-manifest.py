#!/usr/bin/env python3
"""Regenerate data/modules.json from the repository's packable .csproj files.

The marketplace reads NuGet. This manifest is only the fallback for when NuGet has nothing to show —
before the first tagged release, or if the API is unreachable — so the page is never blank.
"""
import glob, json, os, re

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
out = os.path.join(os.path.dirname(__file__), "..", "data", "modules.json")

mods = []
for proj in sorted(glob.glob(os.path.join(root, "*", "*.csproj"))):
    s = open(proj).read()
    if "<IsPackable>true</IsPackable>" not in s:
        continue
    pid = re.search(r"<PackageId>([^<]+)</PackageId>", s)
    ver = re.search(r"<Version>([^<]+)</Version>", s)
    desc = re.search(r"<Description>([^<]+)</Description>", s, re.S)
    if not (pid and ver):
        continue
    mods.append({
        "id": pid.group(1),
        "version": ver.group(1),
        "description": " ".join(desc.group(1).split()) if desc else "",
        "official": True,
    })

core = [m for m in mods if m["id"] == "BarakoCMS"]
rest = sorted((m for m in mods if m["id"] != "BarakoCMS"), key=lambda m: m["id"])
json.dump({"generated": "from the repository's csproj files", "modules": core + rest},
          open(out, "w"), indent=2)
print(f"wrote {len(mods)} packages to data/modules.json")
