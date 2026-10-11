#!/usr/bin/env python3
"""Prints each failed test in a .trx report: its name, message and the top of its stack trace.

The suite writes 40,000 lines of log, and the line naming a failed test is somewhere in the middle
of them. This runs after the test step fails, so the names are the last thing the job prints, and
each one also becomes an ::error annotation on the run.

    python3 scripts/report-trx-failures.py testresults/results.trx

Exits 0 having printed the failures, and 1 when the report cannot be read or names none, since a
failed step with nothing to show for it is worth saying out loud.
"""

import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
MESSAGE_LINES = 20
STACK_LINES = 8


def annotation(text):
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def annotation_property(text):
    return annotation(text).replace(":", "%3A").replace(",", "%2C")


def lines(text, limit):
    kept = (text or "").strip().splitlines()
    return kept[:limit] + ([f"... ({len(kept) - limit} more lines)"] if len(kept) > limit else [])


def main(path):
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        print(f"::error::Could not read {path}: {error}")
        return 1

    failed = [r for r in root.iter(f"{NS}UnitTestResult") if r.get("outcome") == "Failed"]
    if not failed:
        print(f"::error::{path} names no failed test, so the failure is outside the test results.")
        return 1

    print(f"{len(failed)} failed test(s):")
    for result in failed:
        name = result.get("testName", "(unnamed)")
        info = result.find(f"{NS}Output/{NS}ErrorInfo")
        message = info.findtext(f"{NS}Message") if info is not None else ""
        stack = info.findtext(f"{NS}StackTrace") if info is not None else ""

        print()
        print(f"FAILED {name}")
        for line in lines(message, MESSAGE_LINES):
            print(f"    {line}")
        for line in lines(stack, STACK_LINES):
            print(f"    {line}")
        first = (message or "").strip().splitlines()[:1]
        print(f"::error title={annotation_property(name)}::{annotation(first[0] if first else 'failed')}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: report-trx-failures.py <results.trx>")
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
