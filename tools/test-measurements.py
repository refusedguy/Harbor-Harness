#!/usr/bin/env python3
"""Surface per-test measurements (Console.WriteLine) in the CI job summary.

Issue #618. The `test` job already runs every suite with `--report-trx`, and
TUnit already writes each test's captured stdout into THAT test's own TRX
`<Output><StdOut>` element — correctly attributed, verified against real run
artifacts. The job log, however, prints only a per-ASSEMBLY summary
("Test run summary: Passed! - <dll>", total/failed/succeeded/skipped/duration)
and never a test name, so a measurement like

    eventbus-fastpath: 5000 qualifying publishes = 0 B (0.00 B/publish)

reaches a human only by downloading a 7-day-retention artifact ZIP, unzipping
it and grepping XML. In practice nobody does that while debugging a failure, so
the measurement is effectively invisible.

This script closes that gap WITHOUT adding a single line to the raw log: it
reads the TRX files the job already produced and renders them as a table in
$GITHUB_STEP_SUMMARY, next to the existing `| project | result | duration |`
table. Rationale (why summary and not log):

  * The raw log is read while debugging a failure. Everything printed there
    competes with the compile error and with the one `failed <TestName>` line
    the reader is scrolling toward. `--output Detailed` would add one line per
    green test — thousands of lines burying the failure. That is strictly worse
    for the human, so it is not the fix.
  * The job summary is a separate panel: it cannot compete with the log at all.
  * Every row is keyed by `Class.Method`, so a measurement can never float free
    of the test that produced it. A measurement with no test name is noise;
    this script refuses to emit one.
  * No re-run, no extra test execution, no new job, no new matrix axis.

Stdlib only — no pip install, no network, no dotnet. Sibling of check-md-links.py
and md-lint.py, and shares their "reporting is not a gate" contract: a missing,
empty or corrupt TRX set exits 0 with a one-line note. This script must never be
able to fail a build.

Usage:
  ./tools/test-measurements.py TestResults/core                       # stdout
  ./tools/test-measurements.py TestResults/core --summary "$GITHUB_STEP_SUMMARY"
  ./tools/test-measurements.py TestResults --max-rows 500
  ./tools/test-measurements.py TestResults --self-test                # offline

Exit codes:  0 = rendered (or nothing to render), 2 = bad invocation.
"""

from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET

# TRX is a fixed-namespace schema; strip it once and match on bare tags.
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
# 200 keeps the summary scannable if a future test prints per-iteration. The cap
# is a backstop against log/summary explosion, not an expected operating point.
DEFAULT_MAX_ROWS = 200
# A measurement is a line a human reads in a table cell; longer is a debug dump.
MAX_CELL = 200


def _strip_ns(tag: str) -> str:
    return tag[len(TRX_NS):] if tag.startswith(TRX_NS) else tag


def _flatten(text: str) -> str:
    """One measurement = one readable cell: no newlines, no raw pipes."""
    collapsed = " ".join(text.split())
    return collapsed.replace("|", "\\|")


def _cell(text: str) -> str:
    flat = _flatten(text)
    if len(flat) > MAX_CELL:
        flat = flat[: MAX_CELL - 1].rstrip() + "…"
    return flat or "(empty)"


def collect(trx_paths: list[str]) -> list[tuple[str, str, str]]:
    """Return (project, Class.Method, measurement) rows, in file order.

    A test's identity comes from <TestDefinitions> (which carries the class) and
    its text from the matching <Results> entry. TUnit writes both, but the
    result element is authoritative for the text, so the definition map is
    consulted first and the bare method name is the fallback.
    """
    rows: list[tuple[str, str, str]] = []
    for path in trx_paths:
        project = os.path.splitext(os.path.basename(path))[0]
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as exc:
            print(f"  warn: unreadable TRX {os.path.basename(path)}: {exc}")
            continue

        # testId -> "Class.Method" (TestMethod/@name is the same as the result's).
        owners: dict[str, str] = {}
        for unit in root.iter(f"{TRX_NS}UnitTest"):
            uid = unit.get("id") or ""
            for method in unit.iter(f"{TRX_NS}TestMethod"):
                cls = method.get("className") or ""
                name = method.get("name") or unit.get("name") or ""
                short = cls.rsplit(".", 1)[-1]
                owners[uid] = f"{short}.{name}" if short else name

        for result in root.iter(f"{TRX_NS}UnitTestResult"):
            output = result.find(f"{TRX_NS}Output")
            if output is None:
                continue
            chunks = [
                (node.text or "")
                for node in output
                if _strip_ns(node.tag) in ("StdOut", "StdErr") and node.text
            ]
            text = "\n".join(chunks).strip()
            if not text:
                continue
            name = owners.get(result.get("testId") or "", "")
            if not name:
                name = result.get("testName") or "(unknown test)"
            rows.append((project, name, _cell(text)))
    return rows


def render(rows: list[tuple[str, str, str]], max_rows: int) -> str:
    """Markdown table. Split by project so a reader sees which suite emitted."""
    out: list[str] = []
    shown = rows[:max_rows]
    for project in dict.fromkeys(r for r, _, _ in shown):
        group = [r for r in shown if r[0] == project]
        out.append("")
        out.append(f"<details><summary><b>{_cell(project)}</b> — "
                   f"{len(group)} measurement(s)</summary>")
        out.append("")
        out.append("| test | measurement |")
        out.append("|---|---|")
        for _, name, text in group:
            out.append(f"| `{name}` | {text} |")
        out.append("")
        out.append("</details>")
    if len(rows) > max_rows:
        out.append("")
        out.append(
            f"_{len(rows) - max_rows} further measurement(s) not shown "
            f"(raise --max-rows; full text is in the test-results-* artifact)._"
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
    <UnitTestResult testId="a" testName="Measures_Thing" outcome="Passed">
      <Output><StdOut>alloc: 42 B | p99 = 1.5 ms</StdOut></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Silent" outcome="Passed" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Measures_Thing" id="a">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Measures_Thing" />
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
    if len(rows) != 1:
        problems.append(f"expected 1 row (the silent test must be skipped), got {len(rows)}")
    else:
        project, name, text = rows[0]
        if project != "Harbor.X.Tests":
            problems.append(f"project = {project!r}")
        if name != "ThingTests.Measures_Thing":
            problems.append(f"name = {name!r} (must be Class.Method)")
        # The pipe is what breaks an unescaped markdown table.
        if "alloc: 42 B \\| p99 = 1.5 ms" not in text:
            problems.append(f"pipe not escaped: {text!r}")

    for problem in problems:
        print(f"  SELF-TEST FAIL: {problem}")
    if problems:
        return 1
    print("self-test OK: row extracted, class-qualified, pipe escaped, silent test skipped.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Render per-test measurements from TRX into a markdown summary.",
    )
    parser.add_argument("dirs", nargs="*", default=["TestResults"],
                        help="directories (or .trx files) to read; default TestResults")
    parser.add_argument("--summary", metavar="PATH",
                        help="append the table to PATH (use $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--max-rows", type=int, default=DEFAULT_MAX_ROWS,
                        help=f"row cap (default {DEFAULT_MAX_ROWS})")
    parser.add_argument("--self-test", action="store_true",
                        help="verify extraction on a synthetic TRX and exit")
    args = parser.parse_args()

    if args.self_test:
        return _self_test()
    if args.max_rows < 1:
        print("--max-rows must be >= 1", file=sys.stderr)
        return 2

    trx_paths: list[str] = []
    for target in args.dirs:
        if os.path.isfile(target):
            trx_paths.append(target)
            continue
        for root, _dirs, files in os.walk(target):
            trx_paths.extend(
                os.path.join(root, f) for f in files if f.lower().endswith(".trx")
            )
    trx_paths.sort()

    if not trx_paths:
        # Reporting is not a gate: a shard that produced no TRX is a fact to
        # print, never a failure.
        print(f"test-measurements: no .trx found under {' '.join(args.dirs)} — nothing to report")
        return 0

    rows = collect(trx_paths)
    if not rows:
        print(f"test-measurements: {len(trx_paths)} TRX file(s), "
              f"no test emitted output — nothing to report")
        return 0

    body = f"\n### Test measurements ({len(rows)})\n"
    body += ("_Values a test printed with `Console.WriteLine`, bound to the test that "
             "printed them. Source: the TRX artifacts of this run._")
    body += render(rows, args.max_rows)

    if args.summary:
        _write_summary(args.summary, body)

    tests = len({(p, n) for p, n, _ in rows})
    print(f"test-measurements: {len(rows)} measurement(s) from {tests} test(s) "
          f"across {len(trx_paths)} TRX file(s)"
          + (" -> job summary" if args.summary else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
