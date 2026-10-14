#!/usr/bin/env python3
"""Stops methods over the line limit from growing, and stops new ones appearing (#969).

A ratchet, not a rewrite. scripts/method-length-baseline.txt lists every method in production code
that is over the limit today, with its length. The check fails when:

  - a method over the limit is not in the baseline (a new one, or one that grew past the limit),
  - a method in the baseline is longer than its recorded length,
  - a baseline entry names a method that no longer exists or is now within the limit, so the list
    cannot keep an allowance nobody needs.

A method that shrank but is still over the limit passes, and the run says so: lower its number in
the baseline in the same change to keep the ratchet tight.

Usage:
  scripts/check-method-length.py                 check the working tree
  scripts/check-method-length.py --update        rewrite the baseline from the working tree
  scripts/check-method-length.py --root DIR --baseline FILE   check another tree (the fixture test)

Methods are found by a small scanner, not a compiler: strings, characters and comments are blanked,
braces are matched, and a block counts as a method body when the text before it is a member
signature (name, parameter list, then nothing, a where clause or a base/this call) or a property or
event accessor. Length is from the first line of the signature to the closing brace. Local functions
count on their own as well as inside the method that holds them.

Not measured, because they are not block bodies a scanner can tell apart from expressions without a
compiler: expression-bodied members (=> ...), including one whose expression is a long lambda, and
operators. Both branches of an #if are read as code, so a signature duplicated across #if and #else
confuses the nesting; the check then fails rather than passes, and the fix is to keep the
signature outside the conditional.
"""

import argparse
import os
import re
import sys

LIMIT = 70
DEFAULT_BASELINE = "scripts/method-length-baseline.txt"

NOT_METHODS = {
    "if", "for", "foreach", "while", "switch", "using", "lock", "catch", "fixed", "return", "new",
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "when", "await", "throw",
    "extension", "stackalloc", "is", "and", "or", "not", "base", "this", "delegate",
}
ACCESSORS = {"get", "set", "init", "add", "remove"}
TYPE_KEYWORDS = re.compile(r"\b(class|struct|interface|record|enum|namespace)\b")


def mask(text):
    """Blanks string, character and comment contents, keeping every newline where it was."""
    out = list(text)
    n = len(text)
    i = 0

    def blank(a, b):
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    def skip_string(i):
        # Returns the index after the literal starting at i (which is at its prefix or quote).
        j = i
        interpolated = 0
        verbatim = False
        while j < n and text[j] in "$@":
            if text[j] == "$":
                interpolated += 1
            else:
                verbatim = True
            j += 1
        if text.startswith('"""', j):
            q = j
            while q < n and text[q] == '"':
                q += 1
            run = q - j
            end = text.find('"' * run, q)
            return n if end < 0 else end + run
        j += 1  # opening quote
        depth = 0
        while j < n:
            c = text[j]
            if interpolated and c == "{":
                if depth == 0 and text.startswith("{{", j):
                    j += 2
                    continue
                depth += 1
            elif interpolated and c == "}" and depth > 0:
                depth -= 1
            elif depth > 0 and c in "\"'$@":
                if c == "'":
                    j = skip_char(j)
                    continue
                if c == '"' or (j + 1 < n and text[j + 1] in '"$@'):
                    j = skip_string(j)
                    continue
            elif depth == 0 and verbatim and c == '"':
                if text.startswith('""', j):
                    j += 2
                    continue
                return j + 1
            elif depth == 0 and not verbatim and c == "\\":
                j += 2
                continue
            elif depth == 0 and c == '"':
                return j + 1
            elif depth == 0 and c == "\n" and not verbatim:
                return j
            j += 1
        return n

    def skip_char(i):
        j = i + 1
        while j < n and text[j] != "'" and text[j] != "\n":
            j += 2 if text[j] == "\\" else 1
        return j + 1

    while i < n:
        c = text[i]
        if text.startswith("//", i):
            end = text.find("\n", i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(i, end)
            i = end
        elif c == '"' or (c in "$@" and re.match(r'[$@]+"', text[i:i + 4])):
            end = skip_string(i)
            blank(i, end)
            i = end
        elif c == "'":
            end = skip_char(i)
            blank(i, end)
            i = end
        elif c == "#" and (i == 0 or text[i - 1] == "\n" or text[:i].rsplit("\n", 1)[-1].strip() == ""):
            end = text.find("\n", i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        else:
            i += 1
    return "".join(out)


def strip_attributes(header):
    """Drops leading [Attribute(...)] groups, so their parentheses are not read as parameters."""
    h = header.lstrip()
    while h.startswith("["):
        depth = 0
        for k, c in enumerate(h):
            if c == "[":
                depth += 1
            elif c == "]":
                depth -= 1
                if depth == 0:
                    h = h[k + 1:].lstrip()
                    break
        else:
            return h
    return h


def method_name(header):
    """The member name if the header is a method-like signature, else None."""
    h = strip_attributes(header)
    if not h:
        return None
    words = h.split()
    if words and words[-1] in ACCESSORS:
        return words[-1]
    if "=>" in h:
        return None

    # The parameter list is the last top-level parenthesis group, after taking off a trailing
    # where clause or base/this call, so a tuple return type such as (int a, int b) Foo() or
    # Task<(bool, string)> FooAsync() is not read as the parameters.
    sig = h.rstrip()
    m_base = re.search(r"\)\s*:\s*(base|this)\s*\(.*\)$", sig, re.S)
    if m_base:
        sig = sig[:m_base.start() + 1]
    m_where = re.search(r"\)\s*where\b.*$", sig, re.S)
    if m_where:
        sig = sig[:m_where.start() + 1]
    if not sig.endswith(")"):
        return None
    depth = 0
    open_at = -1
    for k in range(len(sig) - 1, -1, -1):
        if sig[k] == ")":
            depth += 1
        elif sig[k] == "(":
            depth -= 1
            if depth == 0:
                open_at = k
                break
    if open_at < 0:
        return None
    before = sig[:open_at].rstrip()
    if TYPE_KEYWORDS.search(before) or re.search(r"(?<![=!<>])=(?!=)", before):
        return None
    before = re.sub(r"<[^()]*>$", "", before).rstrip()
    m = re.search(r"([A-Za-z_]\w*)$", before)
    if not m:
        return None
    name = m.group(1)
    if name in NOT_METHODS:
        return None
    # Split on brackets and commas as well as spaces, so `Call(new Foo(x) {` sees the "new".
    preceding = [w for w in re.split(r"[\s(\[,]+", before[:m.start()]) if w]
    if preceding and preceding[-1] in ("new", "await", "return", "throw", "=", "=>", "in", "is"):
        return None
    if not preceding and name[0].islower():
        # A bare call such as `foo(x) {` cannot open a block; a local function has a return type.
        return None
    return name


def scan(path, text):
    """Yields (member, start line, length) for every method body in a file."""
    masked = mask(text)
    line_of = []
    line = 1
    for c in masked:
        line_of.append(line)
        if c == "\n":
            line += 1

    stack = []  # (kind, name, open index, start line)
    boundary = 0
    for i, c in enumerate(masked):
        if c in ";":
            boundary = i + 1
        elif c == "{":
            header = masked[boundary:i]
            stripped = strip_attributes(header)
            first = len(header) - len(header.lstrip())
            if stripped:
                first = header.find(stripped[:1], len(header) - len(header.lstrip()))
            start_line = line_of[boundary + max(first, 0)] if boundary + max(first, 0) < len(line_of) else line_of[i]
            name = method_name(header)
            if name is not None:
                stack.append(("method", name, i, start_line))
            else:
                tm = re.search(r"\b(class|struct|interface|record|enum)\s+([A-Za-z_]\w*)", header)
                stack.append(("type" if tm else "block", tm.group(2) if tm else None, i, start_line))
            boundary = i + 1
        elif c == "}":
            if stack:
                kind, name, _, start_line = stack.pop()
                if kind == "method":
                    types = [s[1] for s in stack if s[0] == "type"]
                    owners = [s[1] for s in stack if s[0] == "method"]
                    member = ".".join(types + owners + [name])
                    yield member, start_line, line_of[i] - start_line + 1
            boundary = i + 1


def source_files(root):
    for dirpath, dirnames, filenames in os.walk(root):
        rel = os.path.relpath(dirpath, root)
        parts = [] if rel == "." else rel.split(os.sep)
        if parts and (parts[0] not in ("barakoCMS",) and not parts[0].startswith("BarakoCMS.")):
            dirnames[:] = []
            continue
        if parts and (parts[0].endswith(".Tests") or parts[0] == "BarakoCMS.Testing"):
            dirnames[:] = []
            continue
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj") and not d.startswith(".")]
        for f in sorted(filenames):
            if f.endswith(".cs"):
                yield os.path.join(dirpath, f)


def measure(root):
    found = {}
    for path in source_files(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        with open(path, encoding="utf-8-sig") as fh:
            text = fh.read()
        seen = {}
        for member, start, length in scan(rel, text):
            seen[member] = seen.get(member, 0) + 1
            key = f"{rel}::{member}" + (f"#{seen[member]}" if seen[member] > 1 else "")
            found[key] = (length, start)
    return found


def read_baseline(path):
    entries = {}
    if not os.path.exists(path):
        return entries
    with open(path, encoding="utf-8") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            length, key = line.split(None, 1)
            entries[key] = int(length)
    return entries


def write_baseline(path, found):
    over = sorted(((k, v[0]) for k, v in found.items() if v[0] > LIMIT), key=lambda kv: kv[0])
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(f"# Methods over {LIMIT} lines in production code, with their length in lines (#969).\n")
        fh.write("# Written by scripts/check-method-length.py --update. A number here may only go down;\n")
        fh.write("# raising one, or adding a line, is a reviewed decision the pull request explains.\n")
        for key, length in over:
            fh.write(f"{length} {key}\n")
    return len(over)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    ap.add_argument("--baseline", default=None)
    ap.add_argument("--update", action="store_true")
    args = ap.parse_args()

    root = os.path.abspath(args.root)
    baseline_path = args.baseline or os.path.join(root, DEFAULT_BASELINE)
    found = measure(root)

    # The control: a scanner that finds nothing would pass every rule below.
    if len(found) < 10:
        print(f"method-length: found {len(found)} methods under {root}, so the scan is not reading the code")
        return 1

    if args.update:
        count = write_baseline(baseline_path, found)
        print(f"method-length: wrote {count} methods over {LIMIT} lines to {baseline_path}")
        return 0

    baseline = read_baseline(baseline_path)
    problems = []
    shrunk = []
    for key, (length, start) in sorted(found.items()):
        if length <= LIMIT:
            continue
        allowed = baseline.get(key)
        path, member = key.split("::", 1)
        where = f"{path}:{start} {member}"
        if allowed is None:
            problems.append(f"{where} is {length} lines, over the {LIMIT}-line limit and not in the baseline")
        elif length > allowed:
            problems.append(f"{where} grew from {allowed} to {length} lines")
        elif length < allowed:
            shrunk.append(f"{where} is down from {allowed} to {length} lines; lower it in the baseline")
    for key, allowed in sorted(baseline.items()):
        if key not in found:
            problems.append(f"{key} is in the baseline but no longer exists; remove its line")
        elif found[key][0] <= LIMIT:
            problems.append(f"{key} is within {LIMIT} lines now; remove its line from the baseline")

    for line in shrunk:
        print(f"method-length: {line}")
    if problems:
        for line in problems:
            print(f"method-length: {line}")
        print(f"method-length: {len(problems)} problem(s). Split the method, or, if the growth is deliberate,")
        print("               run scripts/check-method-length.py --update and say why in the pull request.")
        return 1
    over = sum(1 for v in found.values() if v[0] > LIMIT)
    print(f"method-length: {len(found)} methods scanned, {over} over {LIMIT} lines, none grew")
    return 0


if __name__ == "__main__":
    sys.exit(main())
