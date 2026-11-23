#!/usr/bin/env python3
"""Counts the alerts in ZAP JSON reports and fails on a new High.

    python3 scripts/dast-summary.py report.json... [--accepted security/zap/accepted.tsv]

The repository is public and so are its CI logs, so this prints counts and nothing taken from a
report: no alert name, plugin id, URL, parameter or evidence. A person reads the full report from
the private folder the scan wrote it to.

Alerts whose plugin id is listed in the accepted file are dropped before counting. Each line of
that file is: plugin id, tab, reason, tab, date accepted. Lines starting with # are comments.

Exit codes:
  0  no High left after the accepted alerts are dropped
  1  at least one High left
  2  a report or the accepted file is missing, empty, unreadable or not what it should be

A report that cannot be read is never a pass. Standard library only.
"""
import json
import os
import sys

RISKS = (("High", 3), ("Medium", 2), ("Low", 1), ("Informational", 0))


class BadInput(Exception):
    """Carries a message that is safe to print: it names a file by basename and never a report value."""


def load_accepted(path):
    try:
        with open(path, encoding="utf-8") as handle:
            lines = handle.read().splitlines()
    except (OSError, UnicodeError):
        raise BadInput("accepted file %s could not be read" % os.path.basename(path))
    accepted = set()
    for number, line in enumerate(lines, 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        plugin_id = line.split("\t")[0].strip()
        if not (plugin_id.isascii() and plugin_id.isdigit()):
            raise BadInput("accepted file %s line %d does not start with a numeric plugin id"
                           % (os.path.basename(path), number))
        accepted.add(plugin_id)
    return accepted


def read_report(path):
    name = os.path.basename(path)
    try:
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
    except (OSError, UnicodeError):
        raise BadInput("report %s is missing or unreadable" % name)
    if not text.strip():
        raise BadInput("report %s is empty" % name)
    try:
        report = json.loads(text)
    except ValueError:
        raise BadInput("report %s is not valid JSON" % name)
    sites = report.get("site") if isinstance(report, dict) else None
    # A scan that reached nothing writes no sites. That is a scan that did not run, not a clean one.
    if not isinstance(sites, list) or not sites:
        raise BadInput("report %s has no scanned site" % name)
    alerts = []
    for site in sites:
        site_alerts = site.get("alerts") if isinstance(site, dict) else None
        if not isinstance(site_alerts, list):
            raise BadInput("report %s has a site with no alert list" % name)
        alerts.extend(site_alerts)
    return alerts


def risk_of(alert, name):
    code = alert.get("riskcode") if isinstance(alert, dict) else None
    if isinstance(code, bool):
        raise BadInput("report %s has an alert with no risk code" % name)
    try:
        code = int(code)
    except (TypeError, ValueError):
        raise BadInput("report %s has an alert with no risk code" % name)
    if code not in (0, 1, 2, 3):
        raise BadInput("report %s has an alert with an unknown risk code" % name)
    return code


def summarise(paths, accepted):
    high_left = 0
    for path in paths:
        name = os.path.basename(path)
        counts = {code: 0 for _, code in RISKS}
        dropped = 0
        for alert in read_report(path):
            code = risk_of(alert, name)
            plugin_id = str(alert.get("pluginid", "")).strip()
            if plugin_id.isascii() and plugin_id in accepted:
                dropped += 1
                continue
            counts[code] += 1
        line = ", ".join("%s %d" % (label, counts[code]) for label, code in RISKS)
        suffix = " (%d accepted)" % dropped if dropped else ""
        print("%s: %s%s" % (name, line, suffix))
        high_left += counts[3]
    return high_left


def main(argv):
    paths = []
    accepted_path = None
    args = iter(argv)
    for arg in args:
        if arg == "--accepted":
            accepted_path = next(args, None)
            if accepted_path is None:
                print("dast-summary: --accepted needs a file", file=sys.stderr)
                return 2
        else:
            paths.append(arg)
    if not paths:
        print("usage: dast-summary.py <report.json>... [--accepted file]", file=sys.stderr)
        return 2
    try:
        accepted = load_accepted(accepted_path) if accepted_path else set()
        high_left = summarise(paths, accepted)
    except BadInput as error:
        print("DAST result: could not read the scan (%s)" % error)
        return 2
    except Exception:  # a report shape nobody planned for is never a pass, and prints nothing from it
        print("DAST result: could not read the scan (unexpected report content)")
        return 2
    if high_left:
        print("DAST result: FAIL, %d new High" % high_left)
        return 1
    print("DAST result: pass, no new High")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
