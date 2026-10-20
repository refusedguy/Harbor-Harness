#!/usr/bin/env python3
"""String-switch census, table proposal and ratchet (issue #1575).

Stdlib only -- no pip, no network, no dotnet. It lives in `tools/` next to
check-test-dup.py / check-md-links.py / check-test-json-reflection.py and asks
the question none of them asks:

  check-test-dup.py              is a test body a copy of another test body?
  check-test-json-reflection.py  does a test serialize through the reflection
                                 resolver?
  THIS ONE                      does the product dispatch on a string literal
                                 with a `case` ladder, and is that ladder on
                                 the exceptions list?

WHY THIS FILE EXISTS (issue #1575)

  #1575 filed the shape as boilerplate: 121 `case "..."` labels under `src/`,
  of which 47 live in one file (`Harbor.Tui.CellForge.Engine/Input/BindingsMap.cs`,
  the key-spec parser) and the rest are spread over `HostsCatalog`,
  `PanelRows`, `InlineImageProbe`, `PermissionPresentation` and a dozen
  others. The proposed replacement is a `FrozenDictionary` table, and the
  argument for it is that the vocabulary becomes DATA: one place to read, one
  place to extend, and no way to add a key to one switch and forget the
  sibling switch that answers the same question.

  The issue is a real duplication, and it is ALSO the reason this file is a
  gate and not a cleanup. A `case` ladder is a perfectly good way to dispatch
  on a string; the compiler emits a hash jump table for a large one, and the
  "O(1) instead of a linear cascade" argument in the issue is not what the
  compiler does. What the table buys is that the vocabulary has ONE owner,
  and that argument only holds if nothing else is allowed to grow a second
  ladder over the same strings. Without a gate, the next `case` ladder lands
  the week after the tables do, and the two drift -- which is exactly the
  failure the tables are supposed to remove.

  So the rule is a RATCHET, in the shape the repository already uses for
  `tools/test-json-reflection-exceptions.txt` and
  `tools/test-project-baseline.txt`: every string-dispatched switch that is
  still in the tree must be named in
  `tools/string-switch-exceptions.txt` with a reason, a NEW one fails the
  gate, and a STALE one (the ladder the tree no longer has) fails it too. A
  closed hole cannot stay blessed.

WHAT COUNTS AS A STRING SWITCH

  A `switch` whose selector dispatches on a string, in either C# form:

    switch (kind) { case "udp": ... }        a switch STATEMENT
    kind switch { "udp" => ... }             a switch EXPRESSION

  The test is the LABEL, not the selector's declared type: a C# `case` label
  that is a string literal only compiles when the selector is a string, so
  `case "udp"` is a string dispatch by construction. A `case 'x'` (a char
  literal) is not, and neither is `switch (someEnum)`.

  Both forms are counted and reported. The RATCHET covers the statement form
  -- the population #1575 counts, the one with the duplicated vocabulary --
  and the expression form is reported as a measured, ungated class, because
  a `switch` expression over a small closed vocabulary is not the same
  defect and the tree has 140-odd such arms today.

  Switches inside a raw string literal are not switches. The source
  generators under `src/Harbor.CodeGen/` EMIT `case` ladders, and an earlier
  census that did not blank string literals counted the emitted text as if
  it were a ladder. This one blanks comments and literals before it looks
  for anything (the same noise-stripper shape
  `check-abstractions-contract.py` uses), so a ladder in a raw string is
  invisible to it and a ladder in real code is not.

  One limit, stated rather than implied: the content of an INTERPOLATION HOLE
  is code, and this file blanks it along with the literal around it. A
  `switch` written inside an interpolation hole would therefore be missed.
  No such site exists in the tree today (the census reports the count it
  found, and the floors below are what catch a scanner that stops seeing).

THE PROPOSALS (--propose) AND THE APPLIED REWRITES (--apply)

  `--propose` answers the issue's question mechanically: given a switch, it
  prints the `FrozenDictionary` initializer that would replace it, when every
  arm of that switch reduces to ONE value expression. That covers the shapes
  where the ladder is a vocabulary and nothing else -- `case "off": return
  InlineImageKind.None;` -- and it refuses (with the reason) on the shapes
  where the arm bodies differ, carry a `when` guard, or assign to more than
  one local. A proposal the tool cannot derive is a proposal a human has to
  write, and the tool says so instead of guessing.

  `--apply` applies the rewrites that were WRITTEN BY HAND after reading each
  site, from `tools/string-switch-rewrites.py`. Exact text, checked to occur
  exactly once, so a rewrite that no longer matches the tree is a hard error
  rather than a silent no-op. This is the "look at every site by eye" half
  of the issue's instruction, kept reproducible: the rewrite is data, the
  script applies it, and the census before and after is the proof that it
  removed what it claimed to remove.

FLOORS

  `--min-labels` is the scan-size floor, in the spirit of the other gates:
  a census that silently stopped matching would report a clean tree, and a
  clean tree is the one answer this gate must never give by accident. The
  floor is set below the measured count, and a PR that legitimately removes
  labels lowers it in the same diff, where a reviewer can see it.

EXIT CODES

  0  clean
  1  a finding: an unlisted string switch, a stale exception, a floor
     violation, or a rewrite that no longer matches the tree
  2  no input (a scan root that does not exist)
"""

from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import os
import re
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_ROOTS = ("src", "apps")
EXCEPTIONS_FILE = os.path.join(REPO_ROOT, "tools", "string-switch-exceptions.txt")
REWRITES_MODULE = os.path.join(REPO_ROOT, "tools", "string-switch-rewrites.py")
SKIP_DIR_PARTS = ("bin", "obj")

# The noise stripper. Comments and literals are blanked (positions preserved)
# so that a ladder inside a raw string is not a ladder, and a `case` inside a
# comment is not a `case`.
#
# The branch order is safe because the four constructs cannot overlap in
# their STARTING positions: a `/` opens a comment, a `"` opens a string, a
# `'` opens a char. A `/` inside a string is never reached, because the
# string branch consumed the whole literal from its opening quote; a `"`
# inside a comment is never reached for the same reason. The raw-string
# branch is first because `"""` would otherwise be read as a string, a
# string and a string.
_NOISE = re.compile(
    r'"""[\s\S]*?"""'
    r"|//[^\n]*"
    r"|/\*[\s\S]*?\*/"
    r'|"(?:[^"\\\n]|\\.)*"'
    r"|'(?:[^'\\\n]|\\.)*'",
)

_SWITCH_KEYWORD = re.compile(r"(?<![\w.])switch(?![\w])")


# ---------------------------------------------------------------------------
# Source handling
# ---------------------------------------------------------------------------


def read(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


def mask_source(source: str) -> str:
    """Blank comments and literals, preserving every position and newline."""
    return _NOISE.sub(_blank, source)


def _blank(match: "re.Match[str]") -> str:
    return "".join("\n" if ch == "\n" else " " for ch in match.group(0))


def iter_source_files(root: str):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_PARTS]
        for name in sorted(filenames):
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def _read_literal(source: str, index: int):
    """Read the string literal that starts at `index`. Returns (value, end)."""
    if index >= len(source) or source[index] != '"':
        return None, index
    i = index + 1
    out = []
    while i < len(source):
        ch = source[i]
        if ch == "\\" and i + 1 < len(source):
            nxt = source[i + 1]
            out.append({"n": "\n", "t": "\t", "r": "\r", "0": "\0"}.get(nxt, nxt))
            i += 2
            continue
        if ch == '"':
            return "".join(out), i + 1
        out.append(ch)
        i += 1
    return "".join(out), len(source)


def _match_bracket(masked: str, start: int, open_ch: str, close_ch: str) -> int:
    """Index of the bracket closing the one at `start`, or -1."""
    depth = 0
    i = start
    n = len(masked)
    while i < n:
        ch = masked[i]
        if ch == open_ch:
            depth += 1
        elif ch == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _is_word_char(ch: str) -> bool:
    return ch.isalnum() or ch == "_"


def _arm_marks(masked: str, start: int, end: int):
    """The `case` / `default` / `=>` marks at the TOP level of a switch body.

    Top level means brace depth zero relative to the body, so a nested switch
    inside an arm does not contribute its arms to the outer switch. Without
    this, a switch expression nested in a switch statement's arm would be
    counted twice.
    """
    marks = []
    depth = 0
    i = start
    while i < end:
        ch = masked[i]
        if ch == "{":
            depth += 1
            i += 1
            continue
        if ch == "}":
            depth -= 1
            i += 1
            continue
        if depth == 0:
            if masked.startswith("case", i) and not _is_word_char(masked[i - 1] if i else " ") and not _is_word_char(
                masked[i + 4] if i + 4 < len(masked) else " "
            ):
                marks.append(("case", i))
                i += 4
                continue
            if masked.startswith("default", i) and not _is_word_char(masked[i - 1] if i else " ") and not _is_word_char(
                masked[i + 7] if i + 7 < len(masked) else " "
            ):
                marks.append(("default", i))
                i += 7
                continue
            if masked.startswith("=>", i):
                marks.append(("=>", i))
                i += 2
                continue
        i += 1
    return marks


def _selector_before(source: str, index: int) -> str:
    """The expression a switch EXPRESSION dispatches on."""
    head = source[:index]
    match = re.search(
        r"([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)*)\s*$",
        head,
    )
    return match.group(1) if match else ""


def _normalize(text: str) -> str:
    return " ".join(text.split())


class SwitchSite:
    """One string-dispatched switch."""

    def __init__(self, path, line, kind, selector, labels, guarded, has_default):
        self.path = path
        self.line = line
        self.kind = kind
        self.selector = selector
        self.labels = labels
        self.guarded = guarded
        self.has_default = has_default

    @property
    def key(self) -> str:
        return "%s | %s" % (self.path, self.selector)

    def as_dict(self) -> dict:
        return {
            "path": self.path,
            "line": self.line,
            "kind": self.kind,
            "selector": self.selector,
            "labels": list(self.labels),
            "guarded": self.guarded,
            "has_default": self.has_default,
        }

    def __str__(self) -> str:
        return "%s:%d  %s  %s  [%d labels]" % (
            self.path,
            self.line,
            self.kind,
            self.selector or "?",
            len(self.labels),
        )


def find_string_switches(source: str, path: str):
    """Every string-dispatched switch in one C# file."""
    masked = mask_source(source)
    sites = []
    for match in _SWITCH_KEYWORD.finditer(masked):
        index = match.start()
        after = index + len("switch")
        while after < len(masked) and masked[after] in " \t\r\n":
            after += 1
        if after >= len(masked):
            continue

        if masked[after] == "(":
            close = _match_bracket(masked, after, "(", ")")
            if close < 0:
                continue
            selector = _normalize(source[after + 1 : close])
            brace = close + 1
            while brace < len(masked) and masked[brace] in " \t\r\n":
                brace += 1
            if brace >= len(masked) or masked[brace] != "{":
                continue
            kind = "statement"
        elif masked[after] == "{":
            selector = _selector_before(source, index)
            brace = after
            kind = "expression"
        else:
            continue

        body_end = _match_bracket(masked, brace, "{", "}")
        if body_end < 0:
            continue

        labels = []
        guarded = False
        has_default = False
        for mark, at in _arm_marks(masked, brace + 1, body_end):
            if mark == "case":
                cursor = at + 4
                while cursor < len(source) and source[cursor] in " \t\r\n":
                    cursor += 1
                value, end = _read_literal(source, cursor)
                if value is None:
                    continue
                labels.append(value)
                tail = masked[end:]
                tail_match = re.match(r"\s*when\b", tail)
                if tail_match:
                    guarded = True
            elif mark == "default":
                has_default = True
            else:  # "=>" is a switch expression arm
                cursor = at - 1
                while cursor >= 0 and source[cursor] in " \t\r\n":
                    cursor -= 1
                if cursor < 0:
                    continue
                if source[cursor] == '"':
                    value, _ = _read_literal(source, source.rfind('"', 0, cursor))
                    if value is not None:
                        labels.append(value)
                elif source[cursor] == "_":
                    has_default = True

        if not labels:
            continue

        sites.append(
            SwitchSite(
                path,
                source.count("\n", 0, index) + 1,
                kind,
                selector,
                labels,
                guarded,
                has_default,
            )
        )
    return sites


def census(roots, base: str = REPO_ROOT):
    """Every string-dispatched switch under the given roots.

    `roots` are relative to `base` unless they are absolute. The relative
    path recorded on a site is relative to `base` too, so the self-test can
    point the same code at a throwaway tree.
    """
    sites = []
    for root in roots:
        tree = root if os.path.isabs(root) else os.path.join(base, root)
        if not os.path.isdir(tree):
            continue
        for path in iter_source_files(tree):
            rel = os.path.relpath(path, base).replace(os.sep, "/")
            sites.extend(find_string_switches(read(path), rel))
    return sites


# ---------------------------------------------------------------------------
# The exceptions list (the ratchet)
# ---------------------------------------------------------------------------


def load_exceptions(path: str):
    exceptions = []
    if not os.path.exists(path):
        return exceptions
    with open(path, encoding="utf-8") as handle:
        for number, line in enumerate(handle, 1):
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            parts = [part.strip() for part in text.split("|")]
            if len(parts) < 3:
                raise SystemExit(
                    "%s:%d: an exception needs three fields "
                    "(path | selector | reason), got %d" % (path, number, len(parts))
                )
            exceptions.append(
                {
                    "key": "%s | %s" % (parts[0], parts[1]),
                    "path": parts[0],
                    "selector": parts[1],
                    "reason": "|".join(parts[2:]),
                    "line": number,
                }
            )
    return exceptions


# ---------------------------------------------------------------------------
# --propose: the table a switch would become
# ---------------------------------------------------------------------------

_ARM_RETURN = re.compile(r"^\s*return\s+(?P<value>[^;]+);\s*$", re.S)
_ARM_ASSIGN = re.compile(
    r"^\s*(?P<target>[A-Za-z_][A-Za-z0-9_]*)\s*(?P<op>=|\|=)\s*(?P<value>[^;]+);\s*$", re.S
)
_TRAILING_BREAK = re.compile(r"\bbreak\s*;\s*$")


def _arm_value(body: str):
    """The single value an arm body produces, or None when it is not one."""
    text = _TRAILING_BREAK.sub("", body.strip()).strip()
    match = _ARM_RETURN.match(text)
    if match:
        return match.group("value").strip(), "="
    match = _ARM_ASSIGN.match(text)
    if match:
        return match.group("value").strip(), match.group("op")
    return None, None


def _switch_body(source: str, masked: str, index: int):
    """(brace, body_end) of the switch whose keyword is at `index`."""
    after = index + len("switch")
    while after < len(masked) and masked[after] in " \t\r\n":
        after += 1
    if after >= len(masked) or masked[after] != "(":
        return None, None
    close = _match_bracket(masked, after, "(", ")")
    if close < 0:
        return None, None
    brace = close + 1
    while brace < len(masked) and masked[brace] in " \t\r\n":
        brace += 1
    if brace >= len(masked) or masked[brace] != "{":
        return None, None
    body_end = _match_bracket(masked, brace, "{", "}")
    if body_end < 0:
        return None, None
    return brace, body_end


def propose_table(site: SwitchSite, source: str):
    """The FrozenDictionary initializer that would replace `site`, or a reason.

    Only the shape where every arm reduces to ONE value expression is
    proposed. An arm that assigns two locals, carries a `when` guard, or
    does something other than produce a value is reported as not mechanical
    -- the tool does not guess.
    """
    if site.kind != "statement":
        return None, "a switch EXPRESSION has no arms to group; hand it."
    if site.guarded:
        return None, "an arm carries a `when` guard; the guard is part of the dispatch."

    masked = mask_source(source)
    index = None
    for match in _SWITCH_KEYWORD.finditer(masked):
        if source.count("\n", 0, match.start()) + 1 == site.line:
            index = match.start()
            break
    if index is None:
        return None, "the switch could not be re-located in the file."

    brace, body_end = _switch_body(source, masked, index)
    if brace is None:
        return None, "the switch body could not be located."

    groups = []
    run = []
    default_body = None
    marks = _arm_marks(masked, brace + 1, body_end)
    for mark, at in marks:
        if mark == "case":
            cursor = at + 4
            while cursor < len(source) and source[cursor] in " \t\r\n":
                cursor += 1
            value, end = _read_literal(source, cursor)
            if value is None:
                run = []
                continue
            tail = end
            while tail < len(source) and source[tail] in " \t\r\n":
                tail += 1
            if source[tail : tail + 4] == "when":
                return None, "an arm carries a `when` guard; the guard is part of the dispatch."
            if tail < len(source) and source[tail] == ":":
                tail += 1
            # A label joins the run above it only when that run has no body
            # yet: `case "a": case "b": <body>` is one arm with two labels,
            # while `case "a": <body> case "b": <body>` is two arms.
            if run and masked[run[-1][1] : at].strip():
                groups.append(([label for label, _ in run], source[run[-1][1] : at]))
                run = []
            run.append((value, tail))
            continue
        # A `default` (or an expression arm) closes the run of case labels.
        if run:
            groups.append(([label for label, _ in run], source[run[-1][1] : at]))
            run = []
        if mark == "default":
            default_body = source[at + len("default") : body_end]
        else:
            return None, "the switch body has an expression arm; hand it."

    if run:
        groups.append(([label for label, _ in run], source[run[-1][1] : body_end]))
    if not groups:
        return None, "the switch has no string-labelled arms."

    lines = []
    for labels, body in groups:
        value, op = _arm_value(body)
        if value is None:
            return None, "an arm body is not a single value expression: %r" % (
                _normalize(body)[:60]
            )
        for label in labels:
            lines.append('    ["%s"] = %s,' % (label, value))

    body = "\n".join(lines)
    if default_body is not None:
        value, _ = _arm_value(default_body)
        if value is None:
            body += "\n    // the default arm is NOT a table entry: %r" % (
                _normalize(default_body)[:60]
            )
        else:
            body += "\n    // the default arm answers %r" % value
    return body, None


# ---------------------------------------------------------------------------
# --apply: the reviewed rewrites
# ---------------------------------------------------------------------------


def load_rewrites():
    """The hand-written rewrites, as a list of rewrite records.

    The rewrites live in their own module (tools/string-switch-rewrites.py)
    so that the C# they carry is readable rather than embedded in a string
    literal in this file. It is loaded the way a plugin is loaded, by path,
    because tools/ is not a package.
    """
    if not os.path.exists(REWRITES_MODULE):
        return []
    spec = importlib.util.spec_from_file_location(
        "harbor_string_switch_rewrites", REWRITES_MODULE
    )
    if spec is None or spec.loader is None:
        return []
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return getattr(module, "REWRITES", [])


def apply_rewrites(rewrites, root: str):
    """Apply every rewrite. Returns (applied, errors).

    A rewrite is a list of (old, new) text pairs for one file. Each old text
    must occur EXACTLY once in the file; a pair that does not match is a hard
    error, because a rewrite that silently did nothing is a rewrite that
    looks like it worked.
    """
    applied = []
    errors = []
    for rewrite in rewrites:
        path = os.path.join(root, rewrite["path"])
        if not os.path.exists(path):
            errors.append("%s: no such file" % rewrite["path"])
            continue
        source = read(path)
        original = source
        for old, new in rewrite["pairs"]:
            # A pair is SKIPPED when its new text is already in the file: the
            # rewrite is already there. That is what makes --apply idempotent,
            # and it is the only safe rule for a pair whose old side is pure
            # context (an insertion), because such an old side is still in the
            # file after the rewrite and would otherwise be applied twice.
            if new in source:
                continue
            count = source.count(old)
            if count == 0:
                errors.append(
                    "%s: the old text does not occur in the file (the tree moved)"
                    % rewrite["path"]
                )
                source = None
                break
            if count > 1:
                errors.append(
                    "%s: the old text occurs %d times; the rewrite is ambiguous"
                    % (rewrite["path"], count)
                )
                source = None
                break
            source = source.replace(old, new, 1)
        if source is None:
            continue
        if source != original:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(source)
            applied.append((rewrite["path"], rewrite.get("note", ""), True))
        else:
            applied.append((rewrite["path"], rewrite.get("note", ""), False))
    return applied, errors


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------


def report(sites, as_json: bool) -> int:
    statements = [s for s in sites if s.kind == "statement"]
    expressions = [s for s in sites if s.kind == "expression"]
    labels = sum(len(s.labels) for s in sites)
    if as_json:
        print(
            json.dumps(
                {
                    "switches": len(sites),
                    "statements": len(statements),
                    "expressions": len(expressions),
                    "labels": labels,
                    "statement_labels": sum(len(s.labels) for s in statements),
                    "expression_labels": sum(len(s.labels) for s in expressions),
                    "sites": [s.as_dict() for s in sites],
                },
                indent=2,
            )
        )
        return 0

    print("string-switch census")
    print("  switches (statement form) : %d" % len(statements))
    print("  switches (expression form) : %d" % len(expressions))
    print("  string labels, statements  : %d" % sum(len(s.labels) for s in statements))
    print("  string labels, expressions : %d" % sum(len(s.labels) for s in expressions))
    by_file = {}
    for site in sites:
        by_file.setdefault(site.path, []).append(site)
    for path in sorted(by_file):
        for site in by_file[path]:
            print("    %s" % site)
    return 0


def run_gate(sites, exceptions, min_labels: int) -> int:
    """The ratchet. Returns 1 on a finding."""
    findings = []

    statements = [s for s in sites if s.kind == "statement"]
    expressions = [s for s in sites if s.kind == "expression"]
    labels = sum(len(s.labels) for s in sites)
    statement_labels = sum(len(s.labels) for s in statements)

    listed = {e["key"] for e in exceptions}
    for site in statements:
        if site.key not in listed:
            findings.append(
                "  UNLISTED  %s:%d  %s  [%d labels]: add it to "
                "tools/string-switch-exceptions.txt with a reason, or convert it"
                % (site.path, site.line, site.selector or "?", len(site.labels))
            )

    present = {s.key for s in statements}
    for exception in exceptions:
        if exception["key"] not in present:
            findings.append(
                "  STALE    %s (tools/string-switch-exceptions.txt:%d): the tree no "
                "longer has that switch; delete the row" % (exception["key"], exception["line"])
            )

    if labels < min_labels:
        findings.append(
            "  FLOOR    the census found %d string labels, the floor is %d: the "
            "scanner is probably not looking at the tree any more" % (labels, min_labels)
        )

    if findings:
        print("string-switch gate: %d finding(s)" % len(findings), file=sys.stderr)
        for finding in findings:
            print(finding, file=sys.stderr)
        print(
            "\nEvery string-dispatched `switch` must be named in "
            "tools/string-switch-exceptions.txt with a reason, or converted to a "
            "table (tools/check-string-switch.py --propose names the table a "
            "switch would become).",
            file=sys.stderr,
        )
        return 1

    print(
        "string-switch gate: %d statement-form switch(es), %d label(s), all listed "
        "in tools/string-switch-exceptions.txt (%d expression-form switch(es) "
        "reported, not gated)"
        % (len(statements), statement_labels, len(expressions))
    )
    return 0


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------


SELF_TEST_SOURCE = """
namespace Harbor.Sample;

public class Sample
{
    public string Dispatch(string kind)
    {
        switch (kind)
        {
            case "udp":
                return "a";
            case "tcp":
                return "b";
            default:
                return "c";
        }
    }

    public string ExpressionDispatch(string kind) =>
        kind switch
        {
            "udp" => "a",
            _ => "b",
        };

    public void Comment()
    {
        // case "not-a-switch": this is prose.
        /* case "also-not": prose too. */
    }

    public string RawString() => \"\"\"
        case "emitted": this is text, not code.
        \"\"\";

    public char CharDispatch(char c) => c switch
    {
        'a' => 'b',
        _ => c,
    };
}
"""


def self_test() -> int:
    failures = []

    def check(name: str, condition: bool, detail: str = "") -> None:
        if condition:
            print("  ok    %s" % name)
        else:
            print("  FAIL  %s %s" % (name, detail))
            failures.append(name)

    sites = find_string_switches(SELF_TEST_SOURCE, "Sample.cs")
    statements = [s for s in sites if s.kind == "statement"]
    expressions = [s for s in sites if s.kind == "expression"]

    check(
        "the statement-form switch is found",
        len(statements) == 1 and statements[0].selector == "kind",
        str(sites),
    )
    check(
        "its two labels are read",
        statements and statements[0].labels == ["udp", "tcp"],
        str(statements),
    )
    check(
        "its default arm is seen",
        statements and statements[0].has_default,
        str(statements),
    )
    check(
        "the switch EXPRESSION is found too",
        len(expressions) == 1 and expressions[0].labels == ["udp"],
        str(expressions),
    )
    check(
        "a `case` in a line comment is not a switch",
        not any("not-a-switch" in label for s in sites for label in s.labels),
        str(sites),
    )
    check(
        "a `case` in a block comment is not a switch",
        not any("also-not" in label for s in sites for label in s.labels),
        str(sites),
    )
    check(
        "a `case` in a raw string is not a switch",
        not any("emitted" in label for s in sites for label in s.labels),
        str(sites),
    )
    check(
        "a char-literal arm is not a string switch",
        not any("a" == label for s in sites for label in s.labels),
        str(sites),
    )

    # The ratchet must go red on an unlisted switch, and on a stale row.
    with tempfile.TemporaryDirectory() as tmp:
        root = os.path.join(tmp, "src")
        os.makedirs(root)
        with open(os.path.join(root, "Sample.cs"), "w", encoding="utf-8") as handle:
            handle.write(SELF_TEST_SOURCE)
        found = census([root], base=tmp)
        findings = []
        listed = set()
        for site in found:
            if site.kind == "statement" and site.key not in listed:
                findings.append(site)
        check(
            "an unlisted string switch is a finding",
            len(findings) == 1,
            str(found),
        )
        check(
            "a listed exception matches the switch it names",
            "src/Sample.cs | kind" in {s.key for s in found},
            str(found),
        )
        check(
            "an exception for a switch the tree does not have is stale",
            "src/Sample.cs | not-a-switch" not in {s.key for s in found},
            str(found),
        )

        # The gate itself, end to end, on a throwaway tree. Its own output is
        # captured: the self-test's job is to assert the VERDICT, and a
        # self-test that prints the gate's own findings makes the CI log read
        # like a failing run.
        exceptions_file = os.path.join(tmp, "exceptions.txt")

        def verdict(exceptions_text: str, min_labels: int) -> int:
            with open(exceptions_file, "w", encoding="utf-8") as handle:
                handle.write(exceptions_text)
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                return run_gate(found, load_exceptions(exceptions_file), min_labels)

        check(
            "the gate is green when every switch is listed",
            verdict("src/Sample.cs | kind | listed\n", 0) == 0,
            "",
        )
        check(
            "the gate is red when a row names a switch the tree does not have",
            verdict("src/Sample.cs | gone | stale row\n", 0) == 1,
            "",
        )
        check(
            "the gate is red when a switch is not listed",
            verdict("", 0) == 1,
            "",
        )
        check(
            "the gate is red when the census finds fewer labels than the floor",
            verdict("src/Sample.cs | kind | listed\n", 10_000) == 1,
            "",
        )

    # The proposal must produce a table for the mechanical shape.
    sample_site = statements[0]
    table, reason = propose_table(sample_site, SELF_TEST_SOURCE)
    check(
        "the proposal names a value per label",
        table is not None and '["udp"] = "a",' in table and '["tcp"] = "b",' in table,
        "%r %r" % (table, reason),
    )

    # The reviewed rewrites must describe the tree as it stands: every pair's
    # NEW text has to be in the file it names. A rewrite that was edited
    # without the file, or a file that moved on without the rewrite, shows up
    # here rather than as a --apply that quietly does nothing.
    rewrites = load_rewrites()
    check("the rewrites module carries the reviewed rewrites", bool(rewrites))
    drifted = []
    for rewrite in rewrites:
        path = os.path.join(REPO_ROOT, rewrite["path"])
        source = read(path)
        for old, new in rewrite["pairs"]:
            if new not in source:
                drifted.append(rewrite["path"])
                break
    check(
        "every reviewed rewrite is in step with the tree it names",
        not drifted,
        str(drifted),
    )

    if failures:
        print("self-test FAILED: %d check(s)" % len(failures), file=sys.stderr)
        return 1
    print("self-test ok: the census still finds a planted string switch.")
    return 0


# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Census the string-dispatched switches and gate the ones that stay."
    )
    parser.add_argument(
        "--root",
        action="append",
        default=None,
        help="a tree to scan, relative to the repository root (default: src and apps)",
    )
    parser.add_argument(
        "--exceptions",
        default=EXCEPTIONS_FILE,
        help="the exceptions file (default: tools/string-switch-exceptions.txt)",
    )
    parser.add_argument(
        "--report", action="store_true", help="print the census and exit 0"
    )
    parser.add_argument("--json-out", action="store_true", help="print the census as JSON")
    parser.add_argument(
        "--check",
        action="store_true",
        help="the ratchet: every string switch must be listed, and no listing may be stale",
    )
    parser.add_argument(
        "--min-labels",
        type=int,
        default=0,
        help="the scan-size floor: the census must find at least this many string labels",
    )
    parser.add_argument(
        "--propose",
        metavar="PATH:LINE",
        help="print the table the switch at PATH:LINE would become",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="apply the reviewed rewrites from tools/string-switch-rewrites.py",
    )
    parser.add_argument(
        "--tree",
        default=REPO_ROOT,
        help="the tree the rewrites are applied to (default: the repository root)",
    )
    parser.add_argument(
        "--self-test", action="store_true", help="prove the census still finds a switch"
    )
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    roots = args.root if args.root else list(DEFAULT_ROOTS)
    for root in roots:
        if not os.path.isdir(os.path.join(REPO_ROOT, root)):
            print("no such tree: %s" % root, file=sys.stderr)
            return 2

    if args.apply:
        rewrites = load_rewrites()
        if not rewrites:
            print("no rewrites to apply", file=sys.stderr)
            return 2
        tree = os.path.abspath(args.tree)
        before = census(roots, base=tree)
        applied, errors = apply_rewrites(rewrites, tree)
        after = census(roots, base=tree)
        removed = len(before) - len(after)
        changed = 0
        for path, note, did_change in applied:
            changed += 1 if did_change else 0
            print(
                "%s %s (%s)"
                % ("applied" if did_change else "already applied", path, note or "reviewed rewrite")
            )
        for error in errors:
            print("  ERROR %s" % error, file=sys.stderr)
        print(
            "string switches: %d -> %d (%d removed)"
            % (len(before), len(after), removed)
        )
        if errors:
            return 1
        expected = sum(
            rewrite.get("expect_removed", 1)
            for rewrite, (_, _, did_change) in zip(rewrites, applied)
            if did_change
        )
        if removed != expected:
            print(
                "  ERROR the rewrites removed %d switch(es) but %d were expected"
                % (removed, expected),
                file=sys.stderr,
            )
            return 1
        if changed == 0:
            print("nothing to do: the tree already carries every rewrite.")
        return 0

    sites = census(roots)

    if args.propose:
        try:
            path, _, line = args.propose.rpartition(":")
        except ValueError:
            path, line = args.propose, ""
        tree = os.path.abspath(args.tree)
        target = None
        for site in census(roots, base=tree):
            if site.path == path and str(site.line) == line:
                target = site
                break
        if target is None:
            print("no string switch at %s" % args.propose, file=sys.stderr)
            return 1
        source = read(os.path.join(tree, target.path))
        table, reason = propose_table(target, source)
        if table is None:
            print("not mechanical: %s" % reason, file=sys.stderr)
            return 1
        print(table)
        return 0

    if args.json_out:
        return report(sites, True)

    if args.report:
        return report(sites, False)

    exceptions = load_exceptions(args.exceptions)
    return run_gate(sites, exceptions, args.min_labels)


if __name__ == "__main__":
    sys.exit(main())
