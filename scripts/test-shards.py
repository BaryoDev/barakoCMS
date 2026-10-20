#!/usr/bin/env python3
"""Prints the backend test classes that belong to shard I of N, one fully qualified name per line.

The classes come from the built assembly, not from the source: `dotnet test --list-tests` prints one
fully qualified test name per line, and the class is the name with its theory arguments and method
removed. A class with no recorded duration goes to shard (sha256(name) mod N) + 1, so a new class
lands in a shard without anyone editing anything, and the split is the same on every run.

Classes that have a recorded duration in scripts/test-class-durations.json (written by
scripts/test-durations.py from the shard jobs' .trx reports) are placed longest first on the shard
with the least work so far, so the shards finish close together. A class without a recorded
duration counts as the median one on the shard its hash picked. A stale file only makes the shards
uneven: every class in the listing lands in exactly one shard whatever the file says, and a class
named in the file but no longer in the listing is ignored. --no-durations ignores the file.

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
import math
import pathlib
import re
import statistics
import subprocess
import sys

FLOOR = 400
DEFAULT_DURATIONS = pathlib.Path(__file__).with_name("test-class-durations.json")
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


def read_durations(path):
    """Seconds per class from the file at path, or {} when there is no file. A file that is not the
    expected JSON stops the run: guessing would hide a broken refresh."""
    if path is None or not path.exists():
        return {}
    try:
        classes = json.loads(path.read_text(encoding="utf-8"))["classes"]
        if not isinstance(classes, dict):
            raise TypeError("classes is not an object")
    except (OSError, ValueError, KeyError, TypeError) as error:
        sys.exit(f"test-shards: {path} is not a durations file ({error}); "
                 "regenerate it with scripts/test-durations.py or delete it")
    return {name: float(took) for name, took in classes.items()
            if isinstance(took, (int, float)) and not isinstance(took, bool)
            and math.isfinite(took) and took >= 0}


def split(classes, count, durations=None):
    durations = durations or {}
    shards = {n: [] for n in range(1, count + 1)}
    known = sorted(((durations[c], c) for c in classes if c in durations), key=lambda item: (-item[0], item[1]))
    estimate = statistics.median(took for took, _ in known) if known else 0.0
    load = {n: 0.0 for n in shards}

    for name in classes:
        if name not in durations:
            n = shard_of(name, count)
            shards[n].append(name)
            load[n] += estimate

    for took, name in known:
        n = min(load, key=lambda k: (load[k], k))
        shards[n].append(name)
        load[n] += took

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
    parser.add_argument("--durations", help="durations file; default scripts/test-class-durations.json when it exists")
    parser.add_argument("--no-durations", action="store_true")
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

    durations_path = None if args.no_durations else (pathlib.Path(args.durations) if args.durations else DEFAULT_DURATIONS)
    chosen = split(classes, args.count, read_durations(durations_path))[args.shard]
    if not chosen:
        sys.exit(f"test-shards: shard {args.shard} of {args.count} is empty")

    if args.filter_args:
        print(" ".join(f"--filter-class {name}" for name in chosen))
    else:
        print("\n".join(chosen))


if __name__ == "__main__":
    main()
