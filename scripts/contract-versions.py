#!/usr/bin/env python3
"""Exits 0 when an HTTP contract version is higher in the working tree than at the given commit.

The numbers read: ApiContract.Version and ApiContract.DeliveryVersion in the core, and in each
module (BarakoCMS.*, tests excluded) a literal `HttpContractVersion => N`, or the
`const int Version = N` of a *Contract.cs file that HttpContractVersion returns (Pages). Comments
are ignored. A module whose whole project was deleted since the base commit counts as raised: its
routes are gone with it, and consumers see it leave /api/meta/describe. Used by scripts/openapi-contract-check.sh when a baseline edit is itself breaking.

Usage: scripts/contract-versions.py <base commit>
"""

import os
import re
import subprocess
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
CORE = "barakoCMS/Features/Monitoring/Meta/ApiContract.cs"


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(line.split("//", 1)[0] for line in text.splitlines())


def numbers(text, path):
    text = strip_comments(text)
    found = {}
    if path == CORE:
        for name in ("Version", "DeliveryVersion"):
            m = re.search(rf"const\s+int\s+{name}\s*=\s*(\d+)\s*;", text)
            if m:
                found[f"{path}:{name}"] = int(m.group(1))
    else:
        m = re.search(r"HttpContractVersion\s*=>\s*(\d+)\s*;", text)
        if m:
            found[f"{path}:HttpContractVersion"] = int(m.group(1))
        if path.endswith("Contract.cs"):
            m = re.search(r"const\s+int\s+Version\s*=\s*(\d+)\s*;", text)
            if m:
                found[f"{path}:Version"] = int(m.group(1))
    return found


def candidates():
    files = [CORE]
    for top in sorted(os.listdir(ROOT)):
        if not top.startswith("BarakoCMS.") or top.endswith(".Tests") or top in ("BarakoCMS.Abstractions", "BarakoCMS.Testing"):
            continue
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, top)):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
            for f in filenames:
                if f.endswith(".cs"):
                    files.append(os.path.relpath(os.path.join(dirpath, f), ROOT).replace(os.sep, "/"))
    return files


def removed_modules(base):
    shown = subprocess.run(["git", "-C", ROOT, "ls-tree", "-r", "--name-only", base], capture_output=True, text=True)
    if shown.returncode != 0:
        return []
    gone = []
    for path in shown.stdout.splitlines():
        top = path.split("/", 1)[0]
        if not top.startswith("BarakoCMS.") or top.endswith(".Tests") or not path.endswith(".cs"):
            continue
        if os.path.exists(os.path.join(ROOT, top, f"{top}.csproj")):
            continue
        before = subprocess.run(["git", "-C", ROOT, "show", f"{base}:{path}"], capture_output=True, text=True)
        for key in numbers(before.stdout, path) if before.returncode == 0 else {}:
            gone.append(f"{key}: removed with {top}")
    return gone


def main():
    base = sys.argv[1]
    raised = removed_modules(base)
    for path in candidates():
        full = os.path.join(ROOT, path)
        now = numbers(open(full, encoding="utf-8-sig").read(), path) if os.path.exists(full) else {}
        if not now:
            continue
        shown = subprocess.run(["git", "-C", ROOT, "show", f"{base}:{path}"], capture_output=True, text=True)
        before = numbers(shown.stdout, path) if shown.returncode == 0 else {}
        for key, value in now.items():
            if key in before and value > before[key]:
                raised.append(f"{key}: {before[key]} -> {value}")
    for line in raised:
        print(f"contract version raised: {line}")
    return 0 if raised else 1


if __name__ == "__main__":
    sys.exit(main())
