#!/usr/bin/env python3
"""Publish the SKIPPED tests of a run, with their reasons, in the job summary.

Issue #1795. The `test` job runs every suite with `--report-trx`, and MTP
writes a skipped test into the TRX as a `NotExecuted` result whose
`<Output><DebugTrace>` carries the skip reason the test declared. That is
the whole inventory of "tests that did not run" — and until now it was
invisible: the shard loop prints per-ASSEMBLY counts only, the job summary
never mentioned the skipped ones, and the first sign of them was an issue
(#1795) that counted 20 skipped tests in a suite of 133 by hand.

WHY THE COUNT MATTERS MORE THAN THE LIST

A skipped test is a test that asserts nothing. A suite can go from 133 tests
to 113 real ones and stay green, and the only way to notice is to look. The
house rule this script serves: a skipped test needs a REASON (the reason is
printed next to the name, so a skip without one is visible), and the COUNT
must not grow silently (the count is the headline of the section, on every
run, in the summary every reviewer sees).

The Avalonia golden lane is the worked example: 20 of its 133 tests were
skipped on every CI run — 12 of them the golden frames, skipped because a
raster baseline captured on one machine never matches another. Those skips
are gone (the golden lane compares the CONTENT of the frame now, which is
machine-independent); the seven `KnownFlake` skips that remain are printed
here with their reason, every run, so a reader can see that they are
accounted for.

This script is REPORTING, never a gate: it must not be able to fail a build.
A missing, empty or corrupt TRX set exits 0 with a one-line note. Sibling
of test-measurements.py, and it shares its "the summary, not the log"
rationale: the log is read while debugging a failure, and this belongs in
the panel that cannot compete with it.

Usage:
  ./tools/test-skips.py TestResults/e2e              # stdout
  ./tools/test-skips.py TestResults --summary "$GITHUB_STEP_SUMMARY"
  ./tools/test-skips.py TestResults --self-test      # offline

Exit codes:  0 = rendered (or nothing to render), 2 = bad invocation.
"""

from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET

# TRX is a fixed-namespace schema; strip it once and match on bare tags.
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
# A reason is a sentence a human reads; longer than this is a bug report
# pasted into a skip message, not a reason.
MAX_CELL = 200
# The table is a list, not a report. Past this many rows the summary stops
# being scannable; the full set is in the test-results-* artifact.
DEFAULT_MAX_ROWS = 100


def _strip_ns(tag: str) -> str:
    return tag[len(TRX_NS):] if tag.startswith(TRX_NS) else tag


def _flatten(text: str) -> str:
    """One reason = one readable cell: no newlines, no raw pipes."""
    collapsed = " ".join(text.split())
    return collapsed.replace("|", "\\|")


def _cell(text: str) -> str:
    flat = _flatten(text)
    if len(flat) > MAX_CELL:
        flat = flat[: MAX_CELL - 1].rstrip() + "…"
    return flat or "(no reason given)"


def _reason_of(result: ET.Element) -> str:
    """The skip reason the test declared.

    MTP records it in `<Output><DebugTrace>` as `Skipped: <reason>`. The
    prefix is stripped: the reason is the sentence, not the marker. A skip
    with no DebugTrace has no reason — that is a hole in the house rule and
    the table says so.
    """
    output = result.find(f"{TRX_NS}Output")
    if output is None:
        return ""
    for node in output:
        if _strip_ns(node.tag) == "DebugTrace" and node.text:
            text = node.text.strip()
            marker = "Skipped:"
            if text.startswith(marker):
                text = text[len(marker):].strip()
            return text
    return ""


def collect(trx_paths: list[str]) -> list[tuple[str, str, str]]:
    """Return (project, Class.Method, reason) rows for every skipped test.

    A test's identity comes from <TestDefinitions> (which carries the class)
    and the outcome from the matching <Results> entry.
    """
    rows: list[tuple[str, str, str]] = []
    for path in trx_paths:
        project = os.path.splitext(os.path.basename(path))[0]
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as exc:
            print(f"  warn: unreadable TRX {os.path.basename(path)}: {exc}")
            continue

        owners: dict[str, str] = {}
        for unit in root.iter(f"{TRX_NS}UnitTest"):
            uid = unit.get("id") or ""
            for method in unit.iter(f"{TRX_NS}TestMethod"):
                cls = method.get("className") or ""
                name = method.get("name") or unit.get("name") or ""
                short = cls.rsplit(".", 1)[-1]
                owners[uid] = f"{short}.{name}" if short else name

        for result in root.iter(f"{TRX_NS}UnitTestResult"):
            if (result.get("outcome") or "") != "NotExecuted":
                continue
            name = owners.get(result.get("testId") or "", "")
            if not name:
                name = result.get("testName") or "(unknown test)"
            rows.append((project, name, _cell(_reason_of(result))))
    return rows


def render(rows: list[tuple[str, str, str]], max_rows: int) -> str:
    """Markdown table. Split by project so a reader sees which suite skipped."""
    out: list[str] = []
    out.append("")
    out.append(f"### Skipped tests: {len(rows)}")
    out.append("")
    if not rows:
        out.append("_No test was skipped in this shard._")
        return "\n".join(out)

    out.append("A skipped test asserts nothing. Every skip below carries the "
               "reason it was skipped; a skip without a reason is a hole.")
    out.append("")
    shown = rows[:max_rows]
    for project in dict.fromkeys(r[0] for r in shown):
        group = [r for r in shown if r[0] == project]
        out.append(f"<details><summary><b>{_cell(project)}</b> — "
                   f"{len(group)} skipped</summary>")
        out.append("")
        out.append("| test | reason |")
        out.append("|---|---|")
        for _, name, reason in group:
            out.append(f"| `{name}` | {reason} |")
        out.append("")
        out.append("</details>")
    if len(rows) > max_rows:
        out.append("")
        out.append(
            f"_{len(rows) - max_rows} further skipped test(s) not shown "
            f"(raise --max-rows; the full set is in the test-results-* artifact)._"
        )
    return "\n".join(out)


def _write_summary(summary_path: str, text: str) -> None:
    with open(summary_path, "a", encoding="utf-8") as handle:
        handle.write(text + "\n")


def _self_test() -> int:
    """Prove the extraction still works on a synthetic TRX (offline, no repo)."""
    sample = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="a" testName="Golden_Frame" outcome="NotExecuted">
      <Output><DebugTrace>Skipped: pixel-golden baselines are machine-pinned</DebugTrace></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Passes" outcome="Passed" />
    <UnitTestResult testId="c" testName="Silent_Skip" outcome="NotExecuted" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Golden_Frame" id="a">
      <TestMethod className="Harbor.X.Tests.GoldenTests" name="Golden_Frame" />
    </UnitTest>
    <UnitTest name="Silent_Skip" id="c">
      <TestMethod className="Harbor.X.Tests.OtherTests" name="Silent_Skip" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
"""
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "Harbor.X.Tests.trx")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(sample)
        rows = collect([path])

    problems = []
    if len(rows) != 2:
        problems.append(f"expected 2 skipped rows (the passing test must not be one), got {len(rows)}")
    else:
        project, name, reason = rows[0]
        if project != "Harbor.X.Tests":
            problems.append(f"project = {project!r}")
        if name != "GoldenTests.Golden_Frame":
            problems.append(f"name = {name!r} (must be Class.Method)")
        if reason != "pixel-golden baselines are machine-pinned":
            problems.append(f"reason = {reason!r} (the 'Skipped:' marker must be stripped)")
        if rows[1][2] != "(no reason given)":
            problems.append(f"a skip with no reason must say so: {rows[1][2]!r}")

    for problem in problems:
        print(f"  SELF-TEST FAIL: {problem}")
    if problems:
        return 1
    print("self-test OK: 2 skips extracted, class-qualified, reason kept, reasonless skip flagged.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Render the skipped tests of a run, with their reasons, into a markdown summary.",
    )
    parser.add_argument("dirs", nargs="*", default=["TestResults"],
                        help="directories (or .trx files) to read; default TestResults")
    parser.add_argument("--summary", metavar="PATH",
                        help="append the table to PATH (use $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--max-rows", type=int, default=DEFAULT_MAX_ROWS,
                        help=f"rows to show (default {DEFAULT_MAX_ROWS})")
    parser.add_argument("--self-test", action="store_true",
                        help="run the offline self-test and exit")
    args = parser.parse_args()

    if args.self_test:
        return _self_test()

    if args.max_rows <= 0:
        print("error: --max-rows must be positive", file=sys.stderr)
        return 2

    trx_paths: list[str] = []
    for entry in args.dirs:
        if os.path.isfile(entry) and entry.endswith(".trx"):
            trx_paths.append(entry)
            continue
        for root, _dirs, files in os.walk(entry):
            for name in sorted(files):
                if name.endswith(".trx"):
                    trx_paths.append(os.path.join(root, name))

    if not trx_paths:
        print("no TRX files found — nothing to report")
        return 0

    rows = collect(trx_paths)
    text = render(rows, args.max_rows)
    print(text)
    if args.summary:
        _write_summary(args.summary, text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
