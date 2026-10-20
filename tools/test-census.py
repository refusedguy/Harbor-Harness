#!/usr/bin/env python3
"""Per-test and per-project WALL census from the CI TRX artifacts.

Stdlib only — no pip, no network, no dotnet. It reads the TRX artifacts a CI
run already produced, so it adds no run, no build and no dependency.

WHY THIS FILE EXISTS (issue #1884)

    The benchmark baselines in `docs/BENCHMARKS.md` had gone stale, and the
    staleness had a shape: the numbers were MEASURED ONCE, on a suite that
    no longer exists, and nobody noticed because nothing compared them to a
    current run. The clearest case is the architecture suite: the recorded
    baseline says 45 tests, the suite has since grown past a thousand, and
    the 2.19 s next to it was measured on the 45.

    A spike (the ArchUnitNET evaluation, for one) cannot be priced against
    a number that describes a different tree. What it needs is a number that
    describes THIS tree, measured the way CI measures it, and re-measurable
    by anyone with the same two commands.

    The data for that already exists. Every test shard in `.github/workflows/ci.yml`
    runs each project as a plain executable with `--report-trx`, so the run
    already produces one TRX per project, and a TRX carries, per test, the
    `UnitTestResult/@duration` the runner measured, and per assembly, the
    `<Times>` window the runner reports as the assembly's wall.

    The sibling tool `tools/test-measurements.py` reads those same TRX files,
    but it reads only the tests that PRINTED something: it renders the
    measurements a test wrote to stdout, so it sees ~75 of the ~8 000 tests
    in a full run. That is the right shape for a measurement table and the
    WRONG shape for a census. A census that skips the silent 99 % cannot
    answer "how many tests does this suite have" or "what does this project
    cost". So this tool is the census: every result, printed or not.

WHAT IT ANSWERS

    Per project: how many tests were DISCOVERED (not "how many passed"),
    how many passed / failed / skipped, the SUM of the per-test durations,
    the WALL the runner measured for the assembly, and the parallelism
    factor (sum of durations / wall) that says whether the project is one
    serial chain or many lanes.

    Per test: the same duration the runner measured, with the class, so a
    maintainer can sort the suite by cost and see which class owns it.

    Totals: the same, summed over every project in the run.

    The "discovered" number is the one the issue asks for, and it is not the
    same as the "passed" number on purpose: a suite that discovers 1 046
    tests and passes 1 046 is a suite that discovered 1 046 tests, and a
    suite that discovers 1 046 and passes 12 is a broken discovery. The
    census reports both, and never one in place of the other.

WHAT IT IS NOT

    It does not run anything. No build, no restore, no network, no dotnet:
    it reads the artifacts a CI run already produced, so it can be run on a
    laptop, in a job summary, or against a downloaded artifact ZIP. It is
    therefore also not a gate: it reports, it never fails a build. A
    missing, empty or corrupt TRX set is a fact to print, not an error.

    And it does not replace the CI job's own summary table: that table is
    the runner's word, this file is the artifact's word. Where they
    disagree, both are printed and the disagreement is the finding.

Usage:
  ./tools/test-census.py TestResults/core                       # markdown
  ./tools/test-census.py TestResults --format csv > census.csv   # sortable
  ./tools/test-census.py TestResults --format csv | sort -t, -k5 -nr | head -30
  ./tools/test-census.py TestResults --summary "$GITHUB_STEP_SUMMARY"
  ./tools/test-census.py TestResults --top 40                    # slowest tests
  ./tools/test-census.py TestResults --self-test                # offline

Exit codes:  0 = rendered (or nothing to render), 2 = bad invocation.
"""

from __future__ import annotations

import argparse
import csv
import io
import os
import sys
import xml.etree.ElementTree as ET
from datetime import datetime

# TRX is a fixed-namespace schema; strip it once and match on bare tags.
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
# The markdown view caps the per-test table: a summary panel with 8 000 rows
# is not a summary. The csv/tsv formats are NOT capped, because the whole
# point of those is to be sorted by the reader.
DEFAULT_TOP = 25


def _strip_ns(tag: str) -> str:
    return tag[len(TRX_NS):] if tag.startswith(TRX_NS) else tag


def _duration_seconds(raw: str | None) -> float | None:
    """TRX `@duration` is a .NET TimeSpan ("hh:mm:ss.fffffff") -> seconds."""
    if not raw:
        return None
    parts = raw.split(":")
    if len(parts) != 3:
        return None
    try:
        hours = float(parts[0])
        minutes = float(parts[1])
        seconds = float(parts[2])
    except ValueError:
        return None
    return hours * 3600.0 + minutes * 60.0 + seconds


def _timestamp(raw: str | None) -> datetime | None:
    """TRX timestamps are ISO-8601 with an offset and 7 fractional digits.

    `datetime.fromisoformat` on the runner's Python (3.12) accepts 1-6
    fractional digits only, and the TRX writer emits 7, so the fraction is
    trimmed here rather than in every caller.
    """
    if not raw:
        return None
    text = raw.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    if "." in text:
        head, _, tail = text.partition(".")
        digits = ""
        rest = ""
        for index, char in enumerate(tail):
            if char.isdigit():
                digits += char
            else:
                rest = tail[index:]
                break
        text = f"{head}.{digits[:6]}{rest}"
    try:
        return datetime.fromisoformat(text)
    except ValueError:
        return None


def _outcome_counts(results: list[dict]) -> dict[str, int]:
    counts = {"passed": 0, "failed": 0, "skipped": 0, "other": 0}
    for result in results:
        outcome = (result["outcome"] or "").lower()
        if outcome == "passed":
            counts["passed"] += 1
        elif outcome in ("failed", "error", "timeout", "aborted"):
            counts["failed"] += 1
        elif outcome in ("notexecuted", "notrunnable", "inconclusive", "warning",
                         "pending", "disconnected"):
            counts["skipped"] += 1
        else:
            counts["other"] += 1
    return counts


class TestRecord:
    """One test result, with the class it belongs to."""

    __slots__ = ("shard", "project", "name", "outcome", "duration")

    def __init__(self, shard: str, project: str, name: str, outcome: str,
                 duration: float | None) -> None:
        self.shard = shard
        self.project = project
        self.name = name
        self.outcome = outcome
        self.duration = duration


class ProjectRecord:
    """One TRX file: one test project, one assembly run."""

    __slots__ = ("shard", "project", "tests", "wall", "sum_duration")

    def __init__(self, shard: str, project: str) -> None:
        self.shard = shard
        self.project = project
        self.tests: list[TestRecord] = []
        self.wall: float | None = None
        self.sum_duration: float = 0.0

    @property
    def discovered(self) -> int:
        return len(self.tests)

    @property
    def counted_duration(self) -> float:
        """Sum of the durations the TRX actually carries.

        A result with no `@duration` contributes 0 to the sum but still
        counts as DISCOVERED: the count and the cost are separate facts.
        """
        return self.sum_duration

    @property
    def factor(self) -> float | None:
        """Parallelism factor: sum of durations / wall.

        ~1.0 means the project ran one lane at a time (a serial chain).
        """
        if not self.wall or self.wall <= 0:
            return None
        return self.counted_duration / self.wall


def collect(trx_paths: list[str]) -> list[ProjectRecord]:
    """Read the TRX files into one ProjectRecord per file, in file order."""
    projects: list[ProjectRecord] = []
    for path in trx_paths:
        project_name = os.path.splitext(os.path.basename(path))[0]
        shard = os.path.basename(os.path.dirname(path)) or "-"
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as exc:
            print(f"  warn: unreadable TRX {os.path.basename(path)}: {exc}")
            continue

        record = ProjectRecord(shard, project_name)

        # The wall: the <Times> window the runner reports as the assembly's
        # duration (creation -> finish). It is the same window the CI job
        # summary's per-project duration is measured from.
        for times in root.iter(f"{TRX_NS}Times"):
            creation = _timestamp(times.get("creation"))
            finish = _timestamp(times.get("finish"))
            if creation and finish:
                record.wall = (finish - creation).total_seconds()
                break

        # testId -> "Class.Method": the definition carries the class, the
        # result carries the name.
        owners: dict[str, str] = {}
        for unit in root.iter(f"{TRX_NS}UnitTest"):
            uid = unit.get("id") or ""
            for method in unit.iter(f"{TRX_NS}TestMethod"):
                cls = method.get("className") or ""
                name = method.get("name") or unit.get("name") or ""
                short = cls.rsplit(".", 1)[-1]
                owners[uid] = f"{short}.{name}" if short else name

        for result in root.iter(f"{TRX_NS}UnitTestResult"):
            name = owners.get(result.get("testId") or "", "")
            if not name:
                name = result.get("testName") or "(unknown test)"
            duration = _duration_seconds(result.get("duration"))
            record.tests.append(TestRecord(
                shard=shard,
                project=project_name,
                name=name,
                outcome=result.get("outcome") or "Unknown",
                duration=duration,
            ))
            if duration is not None:
                record.sum_duration += duration

        projects.append(record)
    return projects


def _fmt_seconds(value: float | None) -> str:
    if value is None:
        return "—"
    if value < 1.0:
        return f"{value * 1000:.0f} ms"
    return f"{value:.2f} s"


def _fmt_factor(value: float | None) -> str:
    if value is None:
        return "—"
    return f"{value:.2f}"


def render_project_table(projects: list[ProjectRecord]) -> str:
    """One row per test project: discovered, passed/failed/skipped, wall, factor."""
    out: list[str] = []
    out.append("| shard | project | discovered | passed | failed | skipped | "
               "sum of durations | wall | parallelism |")
    out.append("|---|---|---:|---:|---:|---:|---:|---:|---:|")
    for record in projects:
        counts = _outcome_counts([{"outcome": t.outcome} for t in record.tests])
        out.append(
            f"| {record.shard} | `{record.project}` | {record.discovered} | "
            f"{counts['passed']} | {counts['failed']} | {counts['skipped']} | "
            f"{_fmt_seconds(record.counted_duration)} | "
            f"{_fmt_seconds(record.wall)} | {_fmt_factor(record.factor)} |"
        )
    return "\n".join(out)


def render_totals(projects: list[ProjectRecord]) -> str:
    """The run's totals: the number a baseline compares against."""
    tests: list[dict] = []
    for record in projects:
        tests.extend({"outcome": t.outcome} for t in record.tests)
    counts = _outcome_counts(tests)
    total_duration = sum(r.counted_duration for r in projects)
    walls = [r.wall for r in projects if r.wall is not None]
    total_wall = sum(walls) if walls else None
    factor = (total_duration / total_wall) if total_wall else None
    return (
        f"| **TOTAL** | **{len(projects)} project(s)** | **{len(tests)}** | "
        f"**{counts['passed']}** | **{counts['failed']}** | **{counts['skipped']}** | "
        f"**{_fmt_seconds(total_duration)}** | **{_fmt_seconds(total_wall)}** | "
        f"**{_fmt_factor(factor)}** |"
    )


def slowest_tests(projects: list[ProjectRecord], top: int) -> list[TestRecord]:
    """The `top` most expensive tests of the run, most expensive first."""
    measured = [t for r in projects for t in r.tests if t.duration is not None]
    measured.sort(key=lambda t: t.duration or 0.0, reverse=True)
    if top > 0:
        measured = measured[:top]
    return measured


def render_test_table(tests: list[TestRecord]) -> str:
    out: list[str] = []
    out.append("| shard | project | test | outcome | duration |")
    out.append("|---|---|---|---|---:|")
    for test in tests:
        out.append(f"| {test.shard} | `{test.project}` | `{test.name}` | "
                   f"{test.outcome} | {_fmt_seconds(test.duration)} |")
    return "\n".join(out)


def render_csv(projects: list[ProjectRecord], delimiter: str) -> str:
    """One row per test, machine-sortable: the census the maintainers sort."""
    buffer = io.StringIO()
    writer = csv.writer(buffer, delimiter=delimiter, lineterminator="\n")
    writer.writerow(["shard", "project", "test", "outcome", "duration_s"])
    for record in projects:
        for test in record.tests:
            writer.writerow([test.shard, test.project, test.name, test.outcome,
                             "" if test.duration is None else f"{test.duration:.6f}"])
    return buffer.getvalue().rstrip("\n")


def find_trx(targets: list[str]) -> list[str]:
    """Every .trx under the given directories (or the files themselves)."""
    trx_paths: list[str] = []
    for target in targets:
        if os.path.isfile(target):
            trx_paths.append(target)
            continue
        for root, _dirs, files in os.walk(target):
            trx_paths.extend(
                os.path.join(root, f) for f in files if f.lower().endswith(".trx")
            )
    trx_paths.sort()
    return trx_paths


def _write_summary(summary_path: str, text: str) -> None:
    with open(summary_path, "a", encoding="utf-8") as handle:
        handle.write(text + "\n")


def _self_test() -> int:
    """Prove the census reads EVERY result, not only the ones that printed.

    The load-bearing fixture is the SILENT test: `test-measurements.py` skips
    it by design, and a census that skipped it too would report a suite of
    1 test where the runner discovered 3.
    """
    sample = """<?xml version="1.0" encoding="utf-8"?>
<TestRun id="run" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2026-10-20T12:00:00.0000000+00:00" queuing="2026-10-20T12:00:00.0000000+00:00" start="2026-10-20T12:00:00.1000000+00:00" finish="2026-10-20T12:00:10.0000000+00:00" />
  <Results>
    <UnitTestResult testId="a" testName="Measures_Thing" outcome="Passed" duration="00:00:02.0000000">
      <Output><StdOut>alloc: 42 B</StdOut></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Silent" outcome="Passed" duration="00:00:03.0000000" />
    <UnitTestResult testId="c" testName="Broken" outcome="Failed" duration="00:00:01.0000000" />
  </Results>
  <TestDefinitions>
    <UnitTest name="Measures_Thing" id="a">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Measures_Thing" />
    </UnitTest>
    <UnitTest name="Silent" id="b">
      <TestMethod className="Harbor.X.Tests.ThingTests" name="Silent" />
    </UnitTest>
    <UnitTest name="Broken" id="c">
      <TestMethod className="Harbor.X.Tests.OtherTests" name="Broken" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
"""
    import tempfile

    problems = []
    with tempfile.TemporaryDirectory() as tmp:
        shard_dir = os.path.join(tmp, "core")
        os.makedirs(shard_dir)
        path = os.path.join(shard_dir, "Harbor.X.Tests.trx")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(sample)
        projects = collect([path])

        if len(projects) != 1:
            problems.append(f"expected 1 project, got {len(projects)}")
        else:
            record = projects[0]
            if record.project != "Harbor.X.Tests":
                problems.append(f"project = {record.project!r}")
            if record.shard != "core":
                problems.append(f"shard = {record.shard!r} (the parent dir)")
            # The census counts the silent and the failed test too.
            if record.discovered != 3:
                problems.append(
                    f"discovered = {record.discovered} (must be 3: the census "
                    f"counts every result, printed or not)")
            counts = _outcome_counts([{"outcome": t.outcome} for t in record.tests])
            if counts["passed"] != 2 or counts["failed"] != 1:
                problems.append(f"passed/failed = {counts['passed']}/{counts['failed']} "
                                f"(must be 2/1)")
            if abs(record.counted_duration - 6.0) > 1e-6:
                problems.append(f"sum of durations = {record.counted_duration} (must be 6.0)")
            if record.wall is None or abs(record.wall - 10.0) > 1e-6:
                problems.append(f"wall = {record.wall} (must be 10.0 s)")
            if record.factor is None or abs(record.factor - 0.6) > 1e-6:
                problems.append(f"parallelism = {record.factor} (must be 0.6)")
            names = [t.name for t in record.tests]
            if "ThingTests.Silent" not in names:
                problems.append(f"the silent test is missing from {names}")

            table = render_project_table(projects)
            if "| 3 | 2 | 1 | 0 |" not in table:
                problems.append("the project row does not carry discovered/passed/failed/skipped")
            totals = render_totals(projects)
            if "**3**" not in totals:
                problems.append("the totals row does not carry the discovered count")

            csv_text = render_csv(projects, ",")
            if len(csv_text.strip().splitlines()) != 4:
                problems.append(f"csv has {len(csv_text.strip().splitlines())} lines "
                                f"(must be 4: header + 3 tests)")

            slowest = slowest_tests(projects, 1)
            if len(slowest) != 1 or slowest[0].name != "ThingTests.Silent":
                problems.append(f"the slowest test is {slowest[0].name if slowest else None!r}, "
                                f"must be ThingTests.Silent (3.0 s)")

        # A corrupt TRX is a fact to print, never a failure.
        broken = os.path.join(tmp, "broken.trx")
        with open(broken, "w", encoding="utf-8") as handle:
            handle.write("<TestRun><not-xml")
        if collect([broken]) != []:
            problems.append("a corrupt TRX must be skipped, not parsed")

    for problem in problems:
        print(f"  SELF-TEST FAIL: {problem}")
    if problems:
        return 1
    print("self-test OK: every result counted (silent and failed included), "
          "duration summed, wall and parallelism read, corrupt TRX skipped.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Per-test and per-project wall census from the CI TRX artifacts.",
    )
    parser.add_argument("dirs", nargs="*", default=["TestResults"],
                        help="directories (or .trx files) to read; default TestResults")
    parser.add_argument("--format", choices=("markdown", "csv", "tsv"),
                        default="markdown", help="output format (default markdown)")
    parser.add_argument("--summary", metavar="PATH",
                        help="append the markdown table to PATH (use $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--top", type=int, default=DEFAULT_TOP,
                        help=f"slowest tests to list in the markdown view "
                             f"(default {DEFAULT_TOP}; 0 = all)")
    parser.add_argument("--self-test", action="store_true",
                        help="verify the census on a synthetic TRX and exit")
    args = parser.parse_args()

    if args.self_test:
        return _self_test()
    if args.top < 0:
        print("--top must be >= 0", file=sys.stderr)
        return 2

    trx_paths = find_trx(args.dirs)
    if not trx_paths:
        # Reporting is not a gate: a shard that produced no TRX is a fact to
        # print, never a failure.
        print(f"test-census: no .trx found under {' '.join(args.dirs)} — nothing to report")
        return 0

    projects = collect(trx_paths)
    if not projects:
        print(f"test-census: {len(trx_paths)} TRX file(s), none readable — "
              f"nothing to report")
        return 0

    if args.format in ("csv", "tsv"):
        delimiter = "," if args.format == "csv" else "\t"
        print(render_csv(projects, delimiter))
        return 0

    body = f"\n### Test census ({len(projects)} project(s))\n"
    body += ("_Every result the runner discovered, from the TRX artifacts of this run. "
             "The wall is the TRX `<Times>` window; the parallelism factor is the sum "
             "of the per-test durations divided by that window._")
    body += "\n\n" + render_project_table(projects)
    body += "\n" + render_totals(projects) + "\n"

    slowest = slowest_tests(projects, args.top)
    if slowest:
        body += f"\n#### The {len(slowest)} slowest test(s)\n\n"
        body += render_test_table(slowest) + "\n"

    if args.summary:
        _write_summary(args.summary, body)

    print(body)

    discovered = sum(r.discovered for r in projects)
    print(f"test-census: {discovered} test(s) across {len(projects)} project(s) "
          f"in {len(trx_paths)} TRX file(s)"
          + (" -> job summary" if args.summary else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
