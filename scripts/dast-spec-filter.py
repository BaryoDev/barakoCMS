#!/usr/bin/env python3
"""Writes a copy of an OpenAPI spec without the operations listed in a skip file.

    python3 -I scripts/dast-spec-filter.py spec.json skip.tsv out.json

The signed-in ZAP pass sends the user's bearer token to every operation in the spec, and a few
operations end the token, the session or the user (logout, password change). Run against them, the
rest of the pass would go out as anonymous. The skip file lists them, one per line: method, tab,
path exactly as in the spec, tab, reason. Lines starting with # are comments.

Prints the number of operations removed and nothing taken from the spec.

Exit codes:
  0  the copy was written
  2  the spec or the skip file is unreadable or not what it should be, or an entry names an
     operation the spec does not have (a renamed route must not drop out of the list silently)
"""
import json
import os
import sys

METHODS = ("get", "put", "post", "delete", "options", "head", "patch", "trace")


class BadInput(Exception):
    """Carries a message that is safe to print: a file's basename or a line number, never a path or route."""


def load_skip(path):
    name = os.path.basename(path)
    try:
        with open(path, encoding="utf-8") as handle:
            lines = handle.read().splitlines()
    except (OSError, UnicodeError):
        raise BadInput("skip file %s could not be read" % name)
    entries = []
    for number, line in enumerate(lines, 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        fields = line.split("\t")
        method = fields[0].strip().lower()
        route = fields[1].strip() if len(fields) > 1 else ""
        if method not in METHODS or not route.startswith("/"):
            raise BadInput("skip file %s line %d needs a method, a tab and a path" % (name, number))
        entries.append((number, method, route))
    return entries


def load_spec(path):
    name = os.path.basename(path)
    try:
        with open(path, encoding="utf-8") as handle:
            spec = json.load(handle)
    except (OSError, UnicodeError, ValueError):
        raise BadInput("spec %s is missing, unreadable or not valid JSON" % name)
    if not isinstance(spec, dict) or not isinstance(spec.get("paths"), dict) or not spec["paths"]:
        raise BadInput("spec %s has no paths" % name)
    return spec


def filtered(spec, entries, skip_name):
    paths = spec["paths"]
    for number, method, route in entries:
        item = paths.get(route)
        if not isinstance(item, dict) or method not in item:
            raise BadInput("skip file %s line %d is not an operation in the spec" % (skip_name, number))
        del item[method]
        if not any(key in METHODS for key in item):
            del paths[route]
    return spec


def main(argv):
    if len(argv) != 3:
        print("usage: dast-spec-filter.py <spec.json> <skip.tsv> <out.json>", file=sys.stderr)
        return 2
    spec_path, skip_path, out_path = argv
    try:
        entries = load_skip(skip_path)
        spec = filtered(load_spec(spec_path), entries, os.path.basename(skip_path))
        with open(out_path, "w", encoding="utf-8") as handle:
            json.dump(spec, handle)
    except BadInput as error:
        print("spec filter: %s" % error)
        return 2
    except Exception:  # never a traceback, and nothing from the spec in the message
        print("spec filter: unexpected input")
        return 2
    print("signed-in spec: %d operations left out" % len(entries))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
