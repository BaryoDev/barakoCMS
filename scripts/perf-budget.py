#!/usr/bin/env python3
"""Compares a scripts/perf-measure.sh result with perf-budget.json and fails on any metric over budget.

perf-budget.json holds, per architecture, each metric's baseline (the number it was set from) and
its budget (the most it may be). Every metric is lower-is-better. A budget of null records the
number without enforcing it, and the summary says so on that row: that is how a new architecture
starts, until a run on it gives a baseline to set the budget from.

The table goes to stdout and, with --summary, is appended to that file (the job summary in CI), with
the baseline, this run, the change and the budget, so a failure shows both numbers and an
improvement shows without failing.

Fails closed: a missing or non-numeric value, a metric in the results with no budget line, or an
architecture the file does not mention is a failure, not a pass.

Usage: scripts/perf-budget.py <results json> <perf-budget.json> [--summary FILE]
Exit: 0 within budget, 1 over budget or unreadable.
"""

import argparse
import json
import math
import sys


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results")
    ap.add_argument("budget")
    ap.add_argument("--summary")
    a = ap.parse_args()

    try:
        results = json.load(open(a.results, encoding="utf-8"))
        budget = json.load(open(a.budget, encoding="utf-8"))
        arch = results["arch"]
        measured = results["metrics"]
        if not isinstance(measured, dict) or not measured:
            raise ValueError("no metrics")
    except Exception as e:  # noqa: BLE001, anything unreadable fails closed
        print(f"perf-budget: cannot read the results or the budget ({e}), so nothing was compared")
        return 1

    budgets = budget.get(arch)
    if not isinstance(budgets, dict) or not budgets:
        print(f"perf-budget: perf-budget.json has no budgets for '{arch}'")
        return 1

    problems = []
    rows = []
    for metric in sorted(set(budgets) | set(measured)):
        entry = budgets.get(metric)
        value = measured.get(metric)
        if entry is None:
            problems.append(f"{metric} was measured but has no line in perf-budget.json")
            continue
        if not number(value):
            problems.append(f"{metric} has no numeric value in the results ({value!r})")
            continue
        base, limit = entry.get("baseline"), entry.get("budget")
        if limit is not None and not number(limit):
            problems.append(f"{metric}'s budget is not a number ({limit!r})")
            continue
        change = f"{value - base:+.1f}" if number(base) else "n/a"
        if limit is None:
            status = "recorded, no budget yet"
        elif value > limit:
            status = "OVER BUDGET"
            problems.append(f"{metric} is {value:g}, over its budget of {limit:g} (baseline {base})")
        elif number(base) and value < base:
            status = "within, better than baseline"
        else:
            status = "within"
        rows.append(f"| {metric} | {base if base is not None else 'n/a'} | {value:g} | {change} | "
                    f"{limit if limit is not None else 'none'} | {status} |")

    lines = [f"## Performance budget ({arch})", "",
             "| metric | baseline | this run | change | budget | result |",
             "| --- | ---: | ---: | ---: | ---: | --- |", *rows, ""]
    if problems:
        lines += ["**Failed:**", *[f"- {p}" for p in problems], "",
                  "If the growth is deliberate, raise the budget in perf-budget.json and say why in the pull request."]
    text = "\n".join(lines) + "\n"
    print(text)
    if a.summary:
        with open(a.summary, "a", encoding="utf-8") as f:
            f.write(text)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
