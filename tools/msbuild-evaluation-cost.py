#!/usr/bin/env python3
"""What a Release compile of this tree spends on PROJECT EVALUATION (issue #1919).

Stdlib only — no pip, no network, no dotnet build. It reads the MSBuild
PERFORMANCE SUMMARY (the `-clp:PerformanceSummary` console output, or the
same summary written by the `performancesummary` file-logger parameter) and
answers the question issue #1919 asks:

    dotnet/msbuild#4025 claims that NuGetSdkResolver adds 180–400 ms to
    EVERY project evaluation, which for this tree's ~109 projects would be
    19–42 s of cost that appears in NO wall-clock line. Is that true HERE,
    and what would it cost to fix?

WHAT IT READS

  The performance summary MSBuild prints at the end of a build when it is
  asked for (`-clp:PerformanceSummary`). It has four sections:

      Project Evaluation Performance Summary:   <- per project, in ms
      Project Performance Summary:
      Target Performance Summary:               <- per target, in ms
      Task Performance Summary:                 <- per task, in ms

  The first section is the one this file exists for: it is the ONLY place
  where the time MSBuild spent EVALUATING projects (as opposed to running
  targets in them) is reported. It does not appear in the build's wall
  time, in the NUKE target output, or in any per-project line of the
  ordinary console log.

  The rows are `  <ms> ms  <scope>  <n> calls`, where <scope> is the
  project file for the evaluation section and the target/task name for the
  others. A row whose ms is `*` is a RE-ENTRANT evaluation: the same
  project was evaluated again while an evaluation of it was already in
  flight (MSBuild evaluates a referenced project twice — once for its
  target frameworks, once for the build), and the counter has no value for
  it. Those rows are counted and reported, never silently dropped.

WHAT IT REPORTS

  1. The number of evaluations, the number of distinct projects, and the
     total evaluation time (the sum of the per-project counters).
  2. The reference-resolution targets, which is where a project's
     evaluation shows up as a TARGET: `GetTargetFrameworks`,
     `ResolveProjectReferences` and
     `_GetProjectReferenceTargetFrameworkProperties`. This is the number
     issue #1919 names, and unlike the evaluation counters it has no
     re-entrancy hole.
  3. The A/B: with two runs (the same build with the NuGet SDK resolver
     enabled and with it disabled through its own escape hatch,
     `MSBUILDDISABLENUGETSDKRESOLVER=1`), the difference IS the resolver's
     cost on this tree.

WHY THE A/B IS THE DECISIVE MEASUREMENT

  The 180–400 ms of dotnet/msbuild#4025 was measured in Visual Studio on a
  project that referenced a VERSIONED SDK (`MSBuild.Sdk.Extras/1.0.0`).
  The NuGet SDK resolver only does work for a versioned SDK reference
  (NuGet/NuGet.Client: "This resolver only works if the user specifies a
  version in a project or a global.json"), and this tree references none —
  every `Sdk=` in it is `Microsoft.NET.Sdk` with no version. So the claim
  may not apply here at all, and the way to find out is not to argue from
  the upstream issue but to turn the resolver off and measure the
  difference on THIS tree.

USAGE

    python3 tools/msbuild-evaluation-cost.py \
        --run "resolver-on:analysis/perf-on.log:372" \
        --run "resolver-off:analysis/perf-off.log:361" \
        [--summary "$GITHUB_STEP_SUMMARY"]

  Each --run is LABEL:PERFORMANCE_SUMMARY_FILE:WALL_SECONDS. The file is
  the performance summary (stdout of the build, or the `.perf-summary.txt`
  the file logger writes); the wall seconds is the build's own wall time,
  which the summary itself does not carry.

  `--self-test` runs the parser against a fixture whose numbers are known
  and asserts them, plus the failure path (a summary without the
  evaluation section). A rule edited until it matches nothing cannot pass
  it — the same argument as the other checkers in tools/.

  Exit code is 0 when the summaries could be read and 1 when they could
  not: a measurement instrument that cannot read its input must say so
  rather than report zero.
"""
from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

# The four sections of the MSBuild performance summary, and the key the
# rows under each are grouped by.
SECTIONS = {
    "Project Evaluation Performance Summary:": "evaluation",
    "Project Performance Summary:": "project",
    "Target Performance Summary:": "target",
    "Task Performance Summary:": "task",
}

# `  1234 ms  GetTargetFrameworks                       109 calls`
ROW = re.compile(r"^\s*(?P<ms>\d+|\*)\s+ms\s+(?P<scope>\S.*?)\s+(?P<calls>\d+)\s+calls\s*$")

# "Time Elapsed 00:06:12.34" — MSBuild's own report of the build's wall time.
ELAPSED = re.compile(r"^Time Elapsed (\d+):(\d{2}):(\d{2}(?:\.\d+)?)\s*$", re.M)

# The targets whose time IS a project evaluation of another project:
# `GetTargetFrameworks` asks a referenced project for its target
# frameworks (which evaluates it), and the two `_GetProjectReference…`
# targets do the same through the project-reference protocol.
REFERENCE_TARGETS = (
    "GetTargetFrameworks",
    "GetTargetFrameworksWithTargetFrameworkMoniker",
    "ResolveProjectReferences",
    "_GetProjectReferenceTargetFrameworkProperties",
    "GetProjectReferenceTargetFrameworkProperties",
)


def parse_summary(text: str) -> dict[str, list[tuple[str, int | None, int]]]:
    """section -> [(scope, milliseconds or None, calls)].

    None is a re-entrant row: MSBuild had no value for it.
    """
    sections: dict[str, list[tuple[str, int | None, int]]] = {}
    current: str | None = None
    for line in text.splitlines():
        header = line.strip()
        if header in SECTIONS:
            current = SECTIONS[header]
            sections.setdefault(current, [])
            continue
        if current is None:
            continue
        match = ROW.match(line)
        if not match:
            # A blank line ends the section; anything else that does not
            # parse is not a row and is ignored.
            if not line.strip():
                current = None
            continue
        ms = None if match.group("ms") == "*" else int(match.group("ms"))
        sections[current].append((match.group("scope"), ms, int(match.group("calls"))))
    return sections


def total_evaluation_ms(rows: list[tuple[str, int | None, int]]) -> tuple[int, int, int]:
    """(total ms, evaluations counted, evaluations without a value)."""
    total = 0
    counted = 0
    reentrant = 0
    for _scope, ms, calls in rows:
        if ms is None:
            reentrant += calls
            continue
        total += ms
        counted += calls
    return total, counted, reentrant


def target_totals(rows: list[tuple[str, int | None, int]]) -> dict[str, tuple[int, int]]:
    """target name -> (total ms, calls)."""
    totals: dict[str, tuple[int, int]] = {}
    for scope, ms, calls in rows:
        if ms is None:
            continue
        name = scope.strip()
        if name not in totals:
            totals[name] = (0, 0)
        prev_ms, prev_calls = totals[name]
        totals[name] = (prev_ms + ms, prev_calls + calls)
    return totals


def read_run(label: str, path: str, wall: str | None) -> dict:
    """One build's numbers, or a dict with an `error` key."""
    file = Path(path)
    if not file.is_file():
        return {"label": label, "error": f"the performance summary {path} does not exist"}
    text = file.read_text(encoding="utf-8", errors="replace")
    sections = parse_summary(text)
    if "evaluation" not in sections:
        return {
            "label": label,
            "error": (
                f"{path} has no 'Project Evaluation Performance Summary' section — "
                f"the build was not asked for one (add -clp:PerformanceSummary)"
            ),
        }
    eval_rows = sections.get("evaluation", [])
    total_ms, counted, reentrant = total_evaluation_ms(eval_rows)
    wall_seconds = None
    try:
        wall_seconds = float(wall) if wall else None
    except ValueError:
        wall_seconds = None
    if wall_seconds is None:
        # The build's own report of its wall time, which the performance
        # summary does not carry. The LAST one is the build's: a log can
        # hold the "Time Elapsed" of a nested build too.
        elapsed = ELAPSED.findall(text)
        if elapsed:
            hours, minutes, seconds = elapsed[-1]
            wall_seconds = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    return {
        "label": label,
        "path": path,
        "evaluation_total_ms": total_ms,
        "evaluation_counted": counted,
        "evaluation_reentrant": reentrant,
        "projects_evaluated": len(eval_rows),
        "slowest_evaluations": sorted(
            (row for row in eval_rows if row[1] is not None),
            key=lambda row: row[1] or 0,
            reverse=True,
        )[:10],
        "targets": target_totals(sections.get("target", [])),
        "wall_seconds": wall_seconds,
    }


def fmt_seconds(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.1f} s"


def fmt_ms(value: int) -> str:
    return f"{value / 1000:.1f} s"


def render(runs: list[dict]) -> str:
    lines: list[str] = []
    ok = [run for run in runs if "error" not in run]
    broken = [run for run in runs if "error" in run]

    lines.append("## Project-evaluation cost of the Release compile (issue #1919)")
    lines.append("")
    if broken:
        lines.append("**Not measured:**")
        lines.append("")
        for run in broken:
            lines.append(f"- `{run['label']}`: {run['error']}")
        lines.append("")
    if not ok:
        return "\n".join(lines) + "\n"

    lines.append("| run | evaluations | distinct projects | evaluation time | build wall |")
    lines.append("|---|---|---|---|---|")
    for run in ok:
        lines.append(
            f"| `{run['label']}` | {run['evaluation_counted']} | {run['projects_evaluated']} | "
            f"{fmt_ms(run['evaluation_total_ms'])} | {fmt_seconds(run['wall_seconds'])} |"
        )
    lines.append("")
    lines.append(
        "_The evaluation time is the SUM over every evaluation, so it is larger than the "
        "wall-clock share: the evaluations run in parallel on the runner's nodes, and the "
        "same project is evaluated more than once (once for its target frameworks, once for "
        "the build)._"
    )
    lines.append("")
    for run in ok:
        if run["evaluation_counted"]:
            mean = run["evaluation_total_ms"] / run["evaluation_counted"]
            lines.append(
                f"**`{run['label']}`: {mean:.0f} ms per evaluation** — the number "
                f"dotnet/msbuild#4025 quotes as 180–400 ms."
            )
            lines.append("")

    if len(ok) >= 2:
        first, second = ok[0], ok[1]
        delta_ms = second["evaluation_total_ms"] - first["evaluation_total_ms"]
        lines.append(
            f"**The difference between the two runs is the NuGet SDK resolver's cost on this "
            f"tree**: {fmt_ms(first['evaluation_total_ms'])} of evaluation with the resolver "
            f"enabled, {fmt_ms(second['evaluation_total_ms'])} with it disabled "
            f"(`MSBUILDDISABLENUGETSDKRESOLVER=1`) — a difference of "
            f"**{fmt_ms(delta_ms)}**."
        )
        lines.append("")
        if first["wall_seconds"] is not None and second["wall_seconds"] is not None:
            delta_wall = second["wall_seconds"] - first["wall_seconds"]
            lines.append(
                f"The same difference in the build's own wall time is "
                f"**{delta_wall:+.1f} s** "
                f"({fmt_seconds(first['wall_seconds'])} -> {fmt_seconds(second['wall_seconds'])})."
            )
            lines.append("")

    lines.append("### Where the evaluation time goes (the reference-resolution targets)")
    lines.append("")
    lines.append(
        "_The times below are INCLUSIVE: a target that runs inside itself is counted at "
        "every nesting level, and `ResolveProjectReferences` does — it resolves a "
        "project's references, and resolving them resolves THEIR references. So these "
        "numbers are not a cost and must not be added up; the CALL counts are the "
        "meaningful part, and the evaluation time above is the cost._"
    )
    lines.append("")
    lines.append("| target | inclusive time | calls |")
    lines.append("|---|---|---|")
    for run in ok:
        rows = [(name, ms, calls) for name, (ms, calls) in run["targets"].items()
                if name in REFERENCE_TARGETS]
        rows.sort(key=lambda row: row[1], reverse=True)
        for name, ms, calls in rows:
            lines.append(f"| `{name}` ({run['label']}) | {fmt_ms(ms)} | {calls} |")
    lines.append("")

    lines.append("### The slowest project evaluations")
    lines.append("")
    for run in ok:
        lines.append(f"**`{run['label']}`**")
        lines.append("")
        lines.append("| project | evaluation time |")
        lines.append("|---|---|")
        for scope, ms, _calls in run["slowest_evaluations"]:
            lines.append(f"| `{scope}` | {fmt_ms(ms or 0)} |")
        lines.append("")

    for run in ok:
        if run["evaluation_reentrant"]:
            lines.append(
                f"_{run['label']}: {run['evaluation_reentrant']} re-entrant evaluation(s) "
                f"(the same project evaluated while an evaluation of it was still in flight) "
                f"have no value in MSBuild's summary and are not in the totals above — "
                f"the totals are a lower bound._"
            )
            lines.append("")
    return "\n".join(lines) + "\n"


SELF_TEST_FIXTURE = """\
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:06:12.34

Project Evaluation Performance Summary:
    1234 ms  /w/src/Harbor.Core/Harbor.Core.csproj                       1 calls
    5678 ms  /w/src/Harbor.Application/Harbor.Application.csproj         2 calls
      * ms  /w/src/Harbor.Reentrant/Harbor.Reentrant.csproj               1 calls

Project Performance Summary:
    9000 ms  /w/src/Harbor.Core/Harbor.Core.csproj                       1 calls

Target Performance Summary:
    3000 ms  GetTargetFrameworks                                          50 calls
    1000 ms  ResolveProjectReferences                                     20 calls
     500 ms  _GetProjectReferenceTargetFrameworkProperties               20 calls
    4000 ms  CoreCompile                                                 109 calls

Task Performance Summary:
    2500 ms  Csc                                                          109 calls
"""


def self_test() -> int:
    """Assert the parser against a fixture whose numbers are known.

    A rule edited until it matches nothing is indistinguishable from a rule
    that found nothing, so the fixture has to have numbers this file
    asserts, not just rows.
    """
    problems: list[str] = []

    def check(condition: bool, message: str) -> None:
        if not condition:
            problems.append(message)

    with tempfile.TemporaryDirectory() as tmp:
        good = Path(tmp) / "perf.log"
        good.write_text(SELF_TEST_FIXTURE, encoding="utf-8")
        run = read_run("fixture", str(good), "372")

        check("error" not in run, f"the fixture should parse: {run.get('error')}")
        if "error" not in run:
            check(run["evaluation_counted"] == 3,
                  f"3 evaluations have a value, got {run['evaluation_counted']}")
            check(run["evaluation_total_ms"] == 1234 + 5678,
                  f"the evaluation total is 6912 ms, got {run['evaluation_total_ms']}")
            check(run["evaluation_reentrant"] == 1,
                  f"1 evaluation is re-entrant, got {run['evaluation_reentrant']}")
            check(run["projects_evaluated"] == 3,
                  f"3 distinct projects, got {run['projects_evaluated']}")
            targets = run["targets"]
            check(targets.get("GetTargetFrameways") is None,
                  "a target name that is not one of the reference targets must not be counted")
            check(targets.get("GetTargetFrameworks") == (3000, 50),
                  f"GetTargetFrameways is 3000 ms / 50 calls, got {targets.get('GetTargetFrameworks')}")
            check(targets.get("ResolveProjectReferences") == (1000, 20),
                  f"ResolveProjectReferences is 1000 ms / 20 calls, "
                  f"got {targets.get('ResolveProjectReferences')}")
            check(targets.get("_GetProjectReferenceTargetFrameworkProperties") == (500, 20),
                  f"_GetProjectReferenceTargetFrameworkProperties is 500 ms / 20 calls, "
                  f"got {targets.get('_GetProjectReferenceTargetFrameworkProperties')}")

        # The failure path: a summary without the evaluation section must say
        # so, not report zero.
        empty = Path(tmp) / "empty.log"
        empty.write_text("Build succeeded.\n", encoding="utf-8")
        broken = read_run("empty", str(empty), "10")
        check("error" in broken,
              "a summary without the evaluation section must be reported as not measured")

        missing = read_run("missing", str(Path(tmp) / "nope.log"), "10")
        check("error" in missing, "a missing summary file must be reported as not measured")

    if problems:
        print("::error::the evaluation-cost self-test failed:")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    print("ok: the performance summary is parsed into the numbers it says it is")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--run",
        action="append",
        default=[],
        metavar="LABEL:SUMMARY_FILE:WALL_SECONDS",
        help="one build: its label, the performance-summary file, its wall time in seconds",
    )
    parser.add_argument("--summary", help="write the report to this file as well")
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="parse a fixture with known numbers and assert them, then check the failure path",
    )
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    if not args.run:
        print("::error::no --run given: there is nothing to measure")
        return 1

    runs: list[dict] = []
    for spec in args.run:
        parts = spec.split(":", 2)
        if len(parts) != 3:
            print(f"::error::malformed --run {spec!r}: expected LABEL:SUMMARY_FILE:WALL_SECONDS")
            return 1
        label, path, wall = parts
        runs.append(read_run(label, path, wall))

    report = render(runs)
    print(report)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    return 1 if any("error" in run for run in runs) else 0


if __name__ == "__main__":
    sys.exit(main())
