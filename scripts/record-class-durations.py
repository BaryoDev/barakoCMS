#!/usr/bin/env python3
"""Writes per-class test durations, in seconds, from the .trx files the backend shard jobs upload.

    gh run download RUN_ID -n backend-trx-1 -D trx/1     # and 2, 3, 4
    python3 scripts/record-class-durations.py trx --out scripts/test-class-durations.json

Every .trx under the given directories is read. A class's duration is the sum of its tests'
durations, so it is the time the class costs on one core; tests that run in parallel make a shard
finish sooner than the sum of its classes, but the ratio is similar for every shard.
scripts/test-shards.py reads the file to balance the shards. A class missing from it still runs, so
the file can be stale without dropping a test.
"""

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
DURATION = re.compile(r"^(\d+):(\d+):(\d+(?:\.\d+)?)$")


def class_of(test_name):
    """The class of a trx testName, or None when the name has no namespace dot."""
    base = test_name.split("(", 1)[0]
    return base.rsplit(".", 1)[0] if "." in base else None


def seconds(text):
    match = DURATION.match(text or "")
    if not match:
        sys.exit(f"record-class-durations: cannot read the duration '{text}'")
    hours, minutes, secs = match.groups()
    return int(hours) * 3600 + int(minutes) * 60 + float(secs)


def durations_from(trx_paths):
    totals = {}
    for path in trx_paths:
        for result in ET.parse(path).getroot().iter(NS + "UnitTestResult"):
            name = class_of(result.get("testName", ""))
            if name is not None:
                totals[name] = totals.get(name, 0.0) + seconds(result.get("duration"))
    return {name: round(totals[name], 2) for name in sorted(totals)}


def find_trx(roots):
    for root in roots:
        for folder, _, files in os.walk(root):
            for file in sorted(files):
                if file.endswith(".trx"):
                    yield os.path.join(folder, file)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("dirs", nargs="+")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    paths = sorted(find_trx(args.dirs))
    if not paths:
        sys.exit("record-class-durations: no .trx file found")
    durations = durations_from(paths)
    if not durations:
        sys.exit("record-class-durations: the .trx files held no test results")
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(durations, handle, indent=1, sort_keys=True)
        handle.write("\n")
    print(f"record-class-durations: {len(durations)} classes, {sum(durations.values()):.0f}s, "
          f"from {len(paths)} file(s)")


if __name__ == "__main__":
    main()
