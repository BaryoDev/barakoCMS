#!/usr/bin/env python3
"""Spreads the integration test classes over parallel xUnit collections.

Every class that needs the shared IntegrationTestFixture carries [Collection("IntegrationN")] or
[Collection("IntegrationSerial")]. Each parallel collection owns a fixture (its own Postgres
container and host), and xUnit runs the collections side by side. A class in IntegrationSerial runs
alone, after the parallel ones, because it touches state the whole process shares.

    python3 scripts/assign-test-collections.py            # rewrite the attributes and the definitions
    python3 scripts/assign-test-collections.py --check    # exit 1 when they are out of date

scripts/test-collections.json lists what stays serial (one reason per file) and the files that must
share a collection because they share a static. Everything else is placed longest first on the
collection with the least recorded work (scripts/test-class-durations.json). A class with no
recorded duration goes to the lightest collection. With one collection the serial list is not used
and every class goes to Integration1, so one fixture serves the whole process. The collection count is 1, or
BARAKO_TEST_COLLECTIONS / --collections N when this script runs; it is written into
BarakoCMS.Tests/IntegrationCollections.cs, because xUnit reads collection names at compile time.

It is 1 because FastEndpoints resolves services and job queues through process-wide statics, so
hosts of two fixtures live in one process at once answer with each other's services and databases
(403s for users the other database lacks, missing tables, a disposed provider). Raise it only once
that is fixed (#1231).
"""

import argparse
import json
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TESTS = ROOT / "BarakoCMS.Tests"
CONFIG = ROOT / "scripts" / "test-collections.json"
DURATIONS = ROOT / "scripts" / "test-class-durations.json"
DEFINITIONS = TESTS / "IntegrationCollections.cs"
SERIAL = "IntegrationSerial"
DEFAULT_COLLECTIONS = 1
UNKNOWN_WEIGHT = 1.0

ATTRIBUTE = re.compile(r'\[Collection\("(?:Sequential|Integration\d+|IntegrationSerial)"\)\]')
CLASS = re.compile(r"\b(?:class|record)\s+(\w+)")
NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)


def source_files():
    for path in sorted(TESTS.rglob("*.cs")):
        parts = path.relative_to(TESTS).parts
        if parts[0] in ("bin", "obj"):
            continue
        yield path


def find_classes():
    """(path, class name, fully qualified name, attribute start, attribute end) for every class carrying an integration collection."""
    found = []
    for path in source_files():
        text = path.read_text(encoding="utf-8")
        matches = list(ATTRIBUTE.finditer(text))
        if not matches:
            continue
        ns = NAMESPACE.search(text)
        namespace = ns.group(1) if ns else ""
        for match in matches:
            declared = CLASS.search(text, match.end())
            if declared is None:
                sys.exit(f"{path}: no class follows the collection attribute at offset {match.start()}")
            name = declared.group(1)
            found.append((path, name, f"{namespace}.{name}" if namespace else name, match.start(), match.end()))
    return found


def load_config():
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    for key in ("serial", "together"):
        if key not in config:
            sys.exit(f"{CONFIG}: missing '{key}'")
    return config


def relative(path):
    return path.relative_to(TESTS).as_posix()


def assign(classes, config, count):
    durations = json.loads(DURATIONS.read_text(encoding="utf-8"))["classes"]
    files = {relative(c[0]) for c in classes}

    stale = [f for f in config["serial"] if f not in files]
    for group in config["together"]:
        stale += [f for f in group["files"] if f not in files]
    if stale:
        sys.exit("scripts/test-collections.json names files with no integration class: " + ", ".join(stale))

    in_group = {}
    for index, group in enumerate(config["together"]):
        for f in group["files"]:
            if f in config["serial"]:
                sys.exit(f"{f} is both serial and in a together group")
            in_group[f] = index

    # With one collection there is nothing to run apart from, and a serial collection would be a
    # second fixture alive in the same process, which is the problem one collection avoids.
    units = {}
    for path, name, full, start, end in classes:
        rel = relative(path)
        if rel in config["serial"] and count > 1:
            continue
        key = ("group", in_group[rel]) if rel in in_group else ("class", full)
        units.setdefault(key, []).append((path, start, full))

    def weight(members):
        return sum(durations.get(m[2], UNKNOWN_WEIGHT) for m in members)

    known = {k: v for k, v in units.items() if all(m[2] in durations for m in v)}
    unknown = {k: v for k, v in units.items() if k not in known}

    load = [0.0] * count
    placed = {}
    for key, members in sorted(known.items(), key=lambda kv: (-weight(kv[1]), str(kv[0]))):
        lightest = min(range(count), key=lambda i: (load[i], i))
        load[lightest] += weight(members)
        placed[key] = lightest
    for key, members in sorted(unknown.items(), key=lambda kv: str(kv[0])):
        lightest = min(range(count), key=lambda i: (load[i], i))
        load[lightest] += weight(members)
        placed[key] = lightest

    result = {}
    for key, members in units.items():
        for path, start, full in members:
            result[(path, start)] = f"Integration{placed[key] + 1}"
    if count > 1:
        for path, name, full, start, end in classes:
            if relative(path) in config["serial"]:
                result[(path, start)] = SERIAL
    return result, load


def definitions(count):
    lines = [
        "using Xunit;",
        "",
        "namespace BarakoCMS.Tests;",
        "",
        "// Written by scripts/assign-test-collections.py. Do not edit by hand.",
    ] + ([
        "// Each parallel collection owns an IntegrationTestFixture (its own Postgres container and host),",
        "// and xUnit runs the collections side by side. IntegrationSerial runs alone, after them.",
    ] if count > 1 else [
        "// One collection, so one IntegrationTestFixture serves every integration class in the process.",
    ]) + [
        "",
    ]
    for i in range(1, count + 1):
        lines += [
            f'[CollectionDefinition("Integration{i}")]',
            f"public class IntegrationCollection{i} : ICollectionFixture<IntegrationTestFixture>;",
            "",
        ]
    if count > 1:
        lines += [
            f'[CollectionDefinition("{SERIAL}", DisableParallelization = true)]',
            "public class IntegrationSerialCollection : ICollectionFixture<IntegrationTestFixture>;",
            "",
        ]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="exit 1 when the attributes are out of date")
    parser.add_argument(
        "--collections", type=int,
        default=int(os.environ.get("BARAKO_TEST_COLLECTIONS", DEFAULT_COLLECTIONS)),
    )
    parser.add_argument("--report", action="store_true", help="print each collection's recorded seconds")
    args = parser.parse_args()
    if args.collections < 1:
        sys.exit("--collections must be at least 1")

    classes = find_classes()
    result, load = assign(classes, load_config(), args.collections)

    changed = []
    by_path = {}
    for (path, start), collection in result.items():
        by_path.setdefault(path, []).append((start, collection))

    for path, entries in by_path.items():
        original = path.read_text(encoding="utf-8")
        pieces, cursor = [], 0
        for start, collection in sorted(entries):
            end = ATTRIBUTE.match(original, start).end()
            pieces.append(original[cursor:start])
            pieces.append(f'[Collection("{collection}")]')
            cursor = end
        pieces.append(original[cursor:])
        updated = "".join(pieces)
        if updated != original:
            changed.append(relative(path))
            if not args.check:
                path.write_text(updated, encoding="utf-8")

    expected = definitions(args.collections)
    current = DEFINITIONS.read_text(encoding="utf-8") if DEFINITIONS.exists() else ""
    if current != expected:
        changed.append(relative(DEFINITIONS))
        if not args.check:
            DEFINITIONS.write_text(expected, encoding="utf-8")

    if args.report:
        for i, seconds in enumerate(load, 1):
            print(f"Integration{i}: {seconds:.0f}s recorded")
        serial = sum(1 for c in result.values() if c == SERIAL)
        print(f"{SERIAL}: {serial} classes")

    if args.check and changed:
        print("Out of date, run python3 scripts/assign-test-collections.py:", file=sys.stderr)
        for f in changed:
            print("  " + f, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
