#!/usr/bin/env python3
"""Does every interactive Avalonia control in a View AXAML have an accessible name?

Stdlib only -- no pip, no network, no dotnet. Wired into the
`accessible-names` job of .github/workflows/docs.yml.

WHY THIS FILE EXISTS (issue #433)

  The contrast gate (tools/check-contrast.py, #2421) measures the COLOUR half
  of the WCAG 2.1 AA audit from the real theme dictionaries. This gate asks
  the other question: an interactive control that the accessibility tree can
  focus needs a name. Avalonia derives that name from `Content`, from an
  attached `ToolTip.Tip`, or from `AutomationProperties.Name`; a control with
  none of the three reaches the tree as an unlabelled button or edit field.

  A C# test can inflate a view and inspect its automation peer, but it cannot
  tell a REVIEWER which AXAML line introduced the nameless control, and it
  cannot fire on the pull request that broke the build. This is a text gate
  over the View AXAML: it reports `file:line` for every interactive control
  without a name, and it ratchets the controls the tree already carries in
  tools/accessible-names-baseline.txt. A NEW nameless control fails; a row
  the tree no longer produces also fails, so the ratchet can only shrink.

  The tag set is the focusable Avalonia input family used in these views.
  Property elements (`<Button.Flyout>`) are not controls and are skipped.

Usage:
  python3 tools/check-accessible-names.py [--root DIR] [--baseline FILE]
                                     [--min-files N] [--min-controls N]
                                     [--min-baseline N]
  python3 tools/check-accessible-names.py --write-baseline FILE
  python3 tools/check-accessible-names.py --self-test

Exit codes:  0 = every control is named or ratcheted, 1 = findings,
             2 = bad invocation.
"""
from __future__ import annotations

import argparse
import io
import os
import re
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)
VIEW_DIR = "apps/Harbor.App.Avalonia/Views"
DEFAULT_BASELINE = "tools/accessible-names-baseline.txt"

# Focusable input controls. A property element such as <Button.Flyout> is
# excluded by the lookahead: the character after the local name must be
# whitespace, '/' or '>', never '.'.
INTERACTIVE = (
    "AutoCompleteBox", "Button", "CheckBox", "ComboBox", "DatePicker",
    "DropDownButton", "HyperlinkButton", "ListBox", "MenuButton",
    "NumericUpDown", "RadioButton", "Slider", "SplitButton", "TextBox",
    "TimePicker", "ToggleButton", "ToggleSwitch", "TreeView",
)
TAG_RE = re.compile(
    r"<(?:[A-Za-z_][\w]*:)?(" + "|".join(INTERACTIVE) + r")(?=[\s/>])")
COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
CONTENT_RE = re.compile(r"(?:^|\s)Content\s*=\s*\"[^\"]+\"")
TIP_RE = re.compile(r"ToolTip\.Tip\s*=\s*\"[^\"]+\"")
AUTOMATION_RE = re.compile(r"AutomationProperties\.Name\s*=\s*\"[^\"]+\"")


def strip_comments(text):
    """Blank XML comments, preserving their newlines so line numbers hold."""
    return COMMENT_RE.sub(lambda m: "\n" * m.group(0).count("\n"), text)


def opening_tag_end(text, start):
    """Index of the '>' that closes the tag opening at `start`, quotes honoured."""
    quote = None
    index = start + 1
    while index < len(text):
        char = text[index]
        if quote is not None:
            if char == quote:
                quote = None
        elif char in ('"', "'"):
            quote = char
        elif char == ">":
            return index
        index += 1
    return len(text)


def has_name(text, match, end):
    """True when this control declares a name by attribute or inline text."""
    opening = text[match.start():end + 1]
    if (CONTENT_RE.search(opening) or TIP_RE.search(opening)
            or AUTOMATION_RE.search(opening)):
        return True
    if opening.rstrip().endswith("/>"):
        return False
    close = re.search(r"</\s*" + match.group(1) + r"\s*>", text[end + 1:])
    if close is None:
        return False
    body = text[end + 1:end + 1 + close.start()]
    # Only direct text is a name. Nested elements need their own audit and are
    # deliberately not guessed at here.
    direct = body.split("<", 1)[0].strip()
    return bool(direct)


def scan(root):
    """Return (findings, files_scanned, controls_seen) under VIEW_DIR."""
    view_root = os.path.join(root, VIEW_DIR)
    findings = []
    files = []
    if os.path.isdir(view_root):
        for directory, subdirs, names in os.walk(view_root):
            subdirs.sort()
            for name in sorted(names):
                if name.endswith(".axaml"):
                    files.append(os.path.join(directory, name))
    controls = 0
    for path in files:
        with open(path, encoding="utf-8") as handle:
            text = strip_comments(handle.read())
        relative = os.path.relpath(path, root).replace(os.sep, "/")
        for match in TAG_RE.finditer(text):
            controls += 1
            end = opening_tag_end(text, match.start())
            if has_name(text, match, end):
                continue
            line = text.count("\n", 0, match.start()) + 1
            findings.append((relative, line, match.group(1)))
    return findings, len(files), controls


def key(finding):
    """The stable identity of a finding, as stored in the ratchet."""
    return f"{finding[0]}:{finding[1]}:{finding[2]}"


def load_baseline(path):
    rows = set()
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            row = raw.strip()
            if row and not row.startswith("#"):
                rows.add(row)
    return rows


def check(root, baseline_path, min_files, min_controls, min_baseline, emit=None):
    """Gate on new nameless controls; return 0 clean, 1 on any finding."""
    emit = emit or (lambda line: print(line))
    findings, files, controls = scan(root)
    if not os.path.isfile(baseline_path):
        emit(f"::error::baseline {baseline_path} is missing; refusing an "
             f"unratcheted scan")
        return 1
    baseline = load_baseline(baseline_path)
    present = {key(finding) for finding in findings}
    problems = []

    for finding in findings:
        if key(finding) in baseline:
            continue
        where = f"{finding[0]}:{finding[1]}"
        problems.append(
            f"{where}: interactive {finding[2]} has no accessible name "
            f"(Content, ToolTip.Tip or AutomationProperties.Name)")
    for row in sorted(baseline - present):
        problems.append(
            f"stale baseline row {row}: no such nameless control exists in "
            f"the tree; the ratchet must shrink, not stay green")

    for flag, seen, floor, noun in (
            ("--min-files", files, min_files, "AXAML files scanned"),
            ("--min-controls", controls, min_controls, "interactive controls seen"),
            ("--min-baseline", len(baseline), min_baseline, "ratchet rows")):
        if seen < floor:
            problems.append(
                f"only {seen} {noun}; the floor is {flag} {floor}")

    for problem in problems:
        emit(f"::error::{problem}")
    if problems:
        emit(f"FAIL: {len(problems)} accessible-name problem(s)")
        return 1
    emit(f"OK: {controls} interactive control(s) in {files} View AXAML file(s), "
         f"{len(findings)} nameless control(s) ratcheted by {len(baseline)} "
         f"row(s); no new or stale findings")
    return 0


# --- fixture data, not code ---
CLEAN_FIXTURE = """<UserControl xmlns="https://github.com/avaloniaui">
  <Button Content="Save" />
  <Button ToolTip.Tip="Refresh" />
  <Button AutomationProperties.Name="Copy" />
  <Button>Accept</Button>
  <TextBox AutomationProperties.Name="Query" />
</UserControl>
"""

BAD_FIXTURE = """<UserControl xmlns="https://github.com/avaloniaui">
  <Button Click="Save_Click" />
</UserControl>
"""

MULTILINE_FIXTURE = """<UserControl xmlns="https://github.com/avaloniaui">
  <Button
      Classes="hds-icon"
      ToolTip.Tip="Toggle theme"
      Click="Theme_Click" />
</UserControl>
"""

COMMENT_FIXTURE = """<UserControl xmlns="https://github.com/avaloniaui">
  <!-- <Button Click="NotAControl" /> -->
  <Button Content="Real" />
</UserControl>
"""
# --- end fixture data ---


def self_test():
    """Prove the gate fails on a planted nameless control and a stale row."""
    with tempfile.TemporaryDirectory() as tmp:
        views = os.path.join(tmp, VIEW_DIR)
        os.makedirs(views, exist_ok=True)
        fixture = os.path.join(views, "Fixture.axaml")
        baseline = os.path.join(tmp, "baseline.txt")

        def write(text):
            with open(fixture, "w", encoding="utf-8") as handle:
                handle.write(text)

        def write_baseline(rows=()):
            with open(baseline, "w", encoding="utf-8") as handle:
                for row in rows:
                    handle.write(row + "\n")

        def run(rows=(), min_files=0, min_controls=0, min_baseline=0):
            write_baseline(rows)
            output = io.StringIO()
            code = check(tmp, baseline, min_files, min_controls, min_baseline,
                         emit=lambda line: output.write(line + "\n"))
            return code, output.getvalue()

        def case(name, ok):
            if not ok:
                print("FAIL: " + name, file=sys.stderr)
                return 1
            return 0

        write(CLEAN_FIXTURE)
        code, output = run()
        if case(f"the clean fixture is not clean:\n{output}",
                code == 0 and "no accessible name" not in output):
            return 1

        # 1. A nameless control is named with its file and line, and fails.
        write(BAD_FIXTURE)
        code, output = run()
        if case(f"a planted nameless control was not named:\n{output}",
                code == 1 and "Fixture.axaml:2" in output
                and "Button" in output and "no accessible name" in output):
            return 1

        # 2. The ratchet suppresses exactly that control.
        write(BAD_FIXTURE)
        code, output = run(rows=(f"{VIEW_DIR}/Fixture.axaml:2:Button",))
        if case(f"a ratchewed violation was not suppressed:\n{output}",
                code == 0):
            return 1

        # 3. A row the tree no longer produces is a failure, not a free pass.
        write(CLEAN_FIXTURE)
        code, output = run(rows=(f"{VIEW_DIR}/Fixture.axaml:2:Button",))
        if case(f"a stale ratchet row was not caught:\n{output}",
                code == 1 and "stale baseline row" in output
                and "Fixture.axaml:2:Button" in output):
            return 1

        # 4. A name on a later line of a multi-line tag still counts.
        write(MULTILINE_FIXTURE)
        code, output = run()
        if case(f"a multi-line tag with a name was called nameless:\n{output}",
                code == 0):
            return 1

        # 5. A control inside an XML comment is not a control.
        write(COMMENT_FIXTURE)
        code, output = run()
        if case(f"a commented-out control was counted:\n{output}",
                code == 0):
            return 1

        # 6. A narrowed scan cannot look clean below its floor.
        write(CLEAN_FIXTURE)
        code, output = run(min_controls=99)
        if case(f"a scan below the control floor was not caught:\n{output}",
                code == 1 and "--min-controls" in output):
            return 1

    print("OK: the accessible-name gate fires on a planted nameless control "
          "and a stale ratchet row, and honours both the tag span and its "
          "floor")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", default=REPO_ROOT)
    parser.add_argument("--baseline", default=DEFAULT_BASELINE)
    parser.add_argument("--min-files", type=int, default=0)
    parser.add_argument("--min-controls", type=int, default=0)
    parser.add_argument("--min-baseline", type=int, default=0)
    parser.add_argument("--write-baseline", metavar="FILE")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.write_baseline:
        findings, files, controls = scan(args.root)
        with open(args.write_baseline, "w", encoding="utf-8") as handle:
            handle.write("# ratchet for tools/check-accessible-names.py: one "
                         "row per nameless interactive control the tree "
                         "already carries. Rows may only shrink.\n")
            for finding in sorted(findings):
                handle.write(key(finding) + "\n")
        print(f"wrote {len(findings)} row(s) from {controls} control(s) in "
              f"{files} file(s) to {args.write_baseline}")
        return 0
    return check(args.root, args.baseline, args.min_files, args.min_controls,
                 args.min_baseline)


if __name__ == "__main__":
    sys.exit(main())
