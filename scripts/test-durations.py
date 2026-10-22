#!/usr/bin/env python3
"""Writes how long each backend test class took, from the .trx reports of one or more shard jobs.

    python3 scripts/test-durations.py testresults-1/results.trx testresults-2/results.trx ...
    python3 scripts/test-durations.py --out scripts/test-class-durations.json DIR_OR_TRX ...

A directory is searched for .trx files. A class's duration is the sum of its tests' durations, in
seconds, which is what a shard has to wait for when the class runs on its own. scripts/test-shards.py
reads the file this writes and spreads the classes over the shards by it. The class name is the test
name with its theory arguments and method removed, the same reading test-shards.py makes of a listing.

--summary prints each report's wall time and the gap between the slowest and the fastest instead.

Exits non-zero when no report can be read or the reports hold no test, so a refresh never replaces a
good file with an empty one.
"""

import argparse
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
from datetime import datetime

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
DURATION = re.compile(r"^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$")
DEFAULT_OUT = pathlib.Path(__file__).with_name("test-class-durations.json")


def class_of(test_name):
    bare = test_name.split("(", 1)[0]
    return bare.rsplit(".", 1)[0] if "." in bare else None


def seconds(text):
    match = DURATION.match(text or "")
    if not match:
        return None
    days, hours, minutes, secs = match.groups()
    return int(days or 0) * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(secs)


def trx_files(paths):
    for path in paths:
        p = pathlib.Path(path)
        if p.is_dir():
            yield from sorted(p.rglob("*.trx"))
        else:
            yield p


def collect(paths):
    totals = {}
    for file in trx_files(paths):
        try:
            root = ET.parse(file).getroot()
        except (OSError, ET.ParseError) as error:
            sys.exit(f"test-durations: could not read {file}: {error}")
        for result in root.iter(f"{NS}UnitTestResult"):
            name = class_of(result.get("testName", ""))
            took = seconds(result.get("duration"))
            if name is None or took is None:
                continue
            totals[name] = totals.get(name, 0.0) + took
    return totals


def wall_times(paths):
    """The seconds each report took from its first test to its last, by file."""
    times = {}
    for file in trx_files(paths):
        try:
            element = ET.parse(file).getroot().find(f"{NS}Times")
            start = datetime.fromisoformat(element.get("start").replace("Z", "+00:00"))
            finish = datetime.fromisoformat(element.get("finish").replace("Z", "+00:00"))
        except (OSError, ET.ParseError, AttributeError, ValueError):
            continue
        times[str(file)] = (finish - start).total_seconds()
    return times


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("paths", nargs="+")
    parser.add_argument("--out", default=str(DEFAULT_OUT))
    parser.add_argument("--summary", action="store_true",
                        help="print how long each report took and the gap between the slowest and fastest, and write nothing")
    args = parser.parse_args()

    if args.summary:
        times = wall_times(args.paths)
        if not times:
            sys.exit("test-durations: no report has a start and finish time")
        for file, took in sorted(times.items()):
            print(f"{took / 60:6.1f} min  {file}")
        print(f"slowest {max(times.values()) / 60:.1f} min, fastest {min(times.values()) / 60:.1f} min, "
              f"gap {(max(times.values()) - min(times.values())) / 60:.1f} min")
        return

    totals = collect(args.paths)
    if not totals:
        sys.exit("test-durations: the reports hold no test with a class and a duration; nothing written")

    document = {"classes": {name: round(totals[name], 2) for name in sorted(totals)}}
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=1)
        handle.write("\n")
    print(f"test-durations: {len(totals)} classes, {sum(totals.values()):.0f}s in total, written to {args.out}")


if __name__ == "__main__":
    main()
