#!/usr/bin/env python3
"""Prints the backend test classes that belong to shard I of N, one fully qualified name per line.

The classes come from the built assembly, not from the source: `dotnet test --list-tests` prints one
fully qualified test name per line, and the class is the name with its theory arguments and method
removed. A class with a duration in scripts/test-class-durations.json (written by
scripts/record-class-durations.py) is placed longest first on the lightest shard, so the shards finish
close together. A class without one goes to shard (sha256(name) mod N) + 1, so a new class lands in a
shard without anyone editing anything, and a stale file only makes the shards uneven. The split is the
same on every run.

    dotnet build BarakoCMS.Tests/BarakoCMS.Tests.csproj --configuration Release
    python3 scripts/test-shards.py --shard 2 --of 4            # one class per line
    python3 scripts/test-shards.py --shard 2 --of 4 --filter-args

--filter-args prints one line, `--filter-class A --filter-class B ...`, which Microsoft.Testing.Platform
takes as an OR of classes:

    dotnet test --project BarakoCMS.Tests/BarakoCMS.Tests.csproj --no-build --configuration Release \\
      $(python3 scripts/test-shards.py --shard 2 --of 4 --filter-args)

Shards are numbered 1 to N. --listing FILE (or - for stdin) reads a saved listing instead of running
dotnet test. Exits non-zero, printing why, when the lines read differ from the listing's own 'Discovered N tests' count,
when the listing holds fewer than 400 classes (an empty
listing would otherwise read as an even split of nothing), when the arguments are out of range, or
when the shard comes out empty.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys

FLOOR = 400
DURATIONS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "test-class-durations.json")
TEST_LINE = re.compile(r"^\s+([A-Za-z_][\w.+]*)(\(.*)?$")


DISCOVERED = re.compile(r"^Discovered (\d+) tests? in assembly")


def classes_from_listing(text):
    found = set()
    read = 0
    announced = None
    for line in text.splitlines():
        count = DISCOVERED.match(line)
        if count:
            announced = (announced or 0) + int(count.group(1))
        match = TEST_LINE.match(line)
        if match and "." in match.group(1):
            found.add(match.group(1).rsplit(".", 1)[0])
            read += 1
    if announced is None:
        sys.exit("test-shards: the listing has no 'Discovered N tests in assembly' line, "
                 "so its test count cannot be checked")
    if read != announced:
        sys.exit(f"test-shards: the listing announces {announced} tests but {read} test lines were read; "
                 "a test name the parser cannot read would leave its class in no shard")
    return sorted(found)


def shard_of(name, count):
    return int(hashlib.sha256(name.encode()).hexdigest(), 16) % count + 1


def load_durations(path):
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    if not isinstance(data, dict) or not all(isinstance(v, (int, float)) and v >= 0 for v in data.values()):
        sys.exit(f"test-shards: {path} must be a JSON object of class name to seconds")
    return data


def default_weight(durations):
    """The weight of a class with no recorded duration: the median recorded one, or 1 with no data."""
    values = sorted(durations.values())
    return values[len(values) // 2] if values else 1.0


def split(classes, count, durations=None):
    """Classes with a recorded duration are balanced, longest first onto the lightest shard. A class
    without one goes where its hash puts it, and counts as a median-sized class in that shard's load,
    so a stale file makes the shards uneven and never drops a class."""
    durations = durations or {}
    shards = {n: [] for n in range(1, count + 1)}
    load = {n: 0.0 for n in shards}
    weight = default_weight(durations)
    known = []
    for name in classes:
        if name in durations:
            known.append(name)
        else:
            shard = shard_of(name, count)
            shards[shard].append(name)
            load[shard] += weight
    for name in sorted(known, key=lambda n: (-durations[n], n)):
        shard = min(shards, key=lambda n: (load[n], n))
        shards[shard].append(name)
        load[shard] += durations[name]
    return {n: sorted(names) for n, names in shards.items()}


def list_tests():
    result = subprocess.run(
        ["dotnet", "test", "--project", "BarakoCMS.Tests/BarakoCMS.Tests.csproj", "--no-build",
         "--configuration", "Release", "--list-tests"],
        capture_output=True, text=True)
    if result.returncode != 0:
        sys.stderr.write(result.stdout + result.stderr)
        sys.exit("test-shards: dotnet test --list-tests failed; build the test project first")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--shard", type=int, required=True)
    parser.add_argument("--of", type=int, required=True, dest="count")
    parser.add_argument("--listing")
    parser.add_argument("--filter-args", action="store_true")
    parser.add_argument("--durations", default=DURATIONS,
                        help="JSON of class name to seconds; default scripts/test-class-durations.json")
    args = parser.parse_args()

    if args.count < 1 or not 1 <= args.shard <= args.count:
        sys.exit(f"test-shards: shard {args.shard} of {args.count} is out of range")

    if args.listing == "-":
        text = sys.stdin.read()
    elif args.listing:
        with open(args.listing, encoding="utf-8") as handle:
            text = handle.read()
    else:
        text = list_tests()

    classes = classes_from_listing(text)
    if len(classes) < FLOOR:
        sys.exit(f"test-shards: the listing found {len(classes)} test classes, fewer than {FLOOR}; "
                 "refusing to split a listing that may be incomplete")

    chosen = split(classes, args.count, load_durations(args.durations))[args.shard]
    if not chosen:
        sys.exit(f"test-shards: shard {args.shard} of {args.count} is empty")

    if args.filter_args:
        print(" ".join(f"--filter-class {name}" for name in chosen))
    else:
        print("\n".join(chosen))


if __name__ == "__main__":
    main()
