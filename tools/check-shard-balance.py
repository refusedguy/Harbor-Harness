#!/usr/bin/env python3
"""Gate: are the CI test shards balanced, by the last measured cost of every
project?

Stdlib only — no pip, no network, no dotnet. It reads two files:

    tools/ci-project-lists.json   the shard lists (the ONE source, issue #1800)
    tools/shard-costs.json        what each project cost, measured on a CI run

WHY THIS FILE EXISTS (issue #1781)

    The CI test shards were laid out by MODULE, not by cost, and the layout
    came out 5.96x skewed: on dev@b9f3c83c (run 39504714459) the `core` shard
    ran 143 s and the `e2e` shard ran 24 s. The pull-request wall is the
    MAXIMUM shard, not the sum (issue #1929 measured it), so the whole pull
    request waited for the slowest shard — and the slowest shard was slow
    because ONE project (Harbor.Architecture.Tests, 91 s of a 143 s shard) sat
    in it next to thirteen projects that together cost 37 s.

    Rebalancing that by hand fixes the layout ONCE, and nothing in the
    repository notices the next time a project gets slow, a project is added,
    or a shard is renamed. A layout that is balanced today and silently
    unbalances tomorrow is the same defect as the one this gate was filed for.

    So the balance needs a GATE. And a gate that measures time cannot be one:
    the house policy (docs/CI_GATES.md, and the check that enforces it) says
    outright that a gate must not measure time, because "a measurement that
    fails a build on a slow runner is a gate that gets switched off". A
    runner's own speed moves a shard by 2x between runs, so a threshold on a
    live wall clock is a coin toss.

    What CAN be gated is the LAYOUT, judged by a RECORDED measurement. That is
    what this file does: it reads the shard lists and the measured cost of
    every project, computes what each shard would cost, and fails when the
    shards are too far apart.

WHAT IT CHECKS

    SHARD-SKEW-EXCEEDED      the shards' predicted walls are more than
                             --max-skew apart (the ratio of the heaviest
                             shard to the lightest). This is the rule that
                             catches the runaway shard: a project that grew,
                             or a shard that swallowed one.
    SHARD-PROJECT-DUPLICATED a project is in more than one shard. It would
                             run twice per CI run, and the two copies would
                             be counted twice in the balance.
    SHARD-COST-STALE         a recorded cost names a project that no shard
                             runs any more. The file then describes a tree
                             that is not there, and the numbers it carries
                             are the numbers of a tree nobody runs.

    A project a shard runs that has NO recorded cost is a NOTE, not a
    finding: a new test project has to be measured before it can be priced,
    and the measurement is a CI run. The project is priced at the MEDIAN of
    the measured costs so the balance stays honest, the note says how many
    projects were priced that way, and the next measurement replaces the
    estimate with a number.

WHAT IT CANNOT ANSWER, STATED RATHER THAN IMPLIED

    It does not know what a shard costs NOW. The costs are a measurement of
    one run, recorded in the file with the run id, the commit and the date,
    and they go stale as the tree grows. The gate therefore answers a
    question about the LAYOUT: "given what the last measurement says, is the
    layout balanced?" — and it is the layout that a pull request changes when
    it moves a project from one shard to another.

    It does not time anything. There is no clock in this file, by policy.

    It does not check that the workflow RUNS what the source says. That is
    `tools/ci-shard-slices.py`'s job (issue #1800): the source is generated
    into the workflow, and that gate fails when the two disagree. This gate
    reads the SOURCE, because the source is the layout CI runs.

HOW IT READS THE SOURCE

    `tools/ci-project-lists.json` is JSON, so it is read with the standard
    library's `json` module. The shape it looks for is `shards.<name>.projects`,
    the one the source itself documents.

    If it cannot read what it expects, it says so and exits non-zero. A
    checker that quietly checks nothing is the failure mode this file exists
    to prevent.

NON-VACUITY

    --self-test builds throwaway trees and asserts the failure path: a clean
    tree stays clean; a runaway shard fails with SHARD-SKEW-EXCEEDED; a
    project in two shards fails; a stale cost fails; and a missing source or
    a missing cost file is an ERROR, not a clean verdict.

Usage:
  ./tools/check-shard-balance.py
  ./tools/check-shard-balance.py --max-skew 4.0
  ./tools/check-shard-balance.py --self-test

Exit codes:  0 = balanced, 1 = findings, 2 = bad invocation / no input.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile

SOURCE = os.path.join("tools", "ci-project-lists.json")
COSTS = os.path.join("tools", "shard-costs.json")

# The skew the gate allows: the ratio of the heaviest shard's wall to the
# lightest one's. Measured on dev@b9f3c83c (run 39504714459): the four shards
# were 143 / 62 / 35 / 24 s, a skew of 5.96, and the layout that moved the
# architecture suite into its own shard measures 3.46 on the same costs. The
# threshold sits between the two, so the layout this gate was written for
# passes and the layout it replaced does not.
DEFAULT_MAX_SKEW = 4.0

# A shard's wall is the sum of its projects' measured costs plus what the
# shard spends on the projects themselves: the process start and the TRX
# write. Measured on the same run, the per-project overhead is 0.56-0.66 s in
# all four shards, and the first project of a shard pays a JIT warm-up of
# 0.6-6.5 s, which the per-shard constant carries.
DEFAULT_PER_PROJECT_SECONDS = 0.6
DEFAULT_PER_SHARD_SECONDS = 2.0


class Finding:
    """One thing the gate found."""

    def __init__(self, code, subject, message):
        self.code = code
        self.subject = subject
        self.message = message

    def __str__(self):
        return f"{self.code}  {self.subject}: {self.message}"


# ---------------------------------------------------------------------------
# The shard lists
# ---------------------------------------------------------------------------


def read_shards(path):
    """The shard lists of the source: shard name -> the projects it runs.

    Returns (shards, error). The error is a string when the file is missing,
    unreadable, or does not carry the shape this gate reads; the gate then
    reports the problem instead of a clean verdict.
    """
    if not os.path.isfile(path):
        return None, f"the source {path} does not exist"
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as error:
        return None, f"the source {path} could not be read: {error}"
    if not isinstance(data, dict):
        return None, f"the source {path} is not an object"
    shards = data.get("shards")
    if not isinstance(shards, dict) or not shards:
        return None, f"the source {path} carries no shards"
    projects = {}
    for name, entry in shards.items():
        if not isinstance(entry, dict) or not isinstance(entry.get("projects"), list):
            return None, f"the source {path} gives the `{name}` shard no project list"
        projects[name] = [str(item) for item in entry["projects"]]
    if not any(projects.values()):
        return None, f"the source {path} names no project at all"
    return projects, None


# ---------------------------------------------------------------------------
# The measured costs
# ---------------------------------------------------------------------------


def read_costs(path):
    """The recorded measurement: the per-project costs and the overhead model.

    Returns (data, error). The error is a string when the file is missing,
    unreadable, or does not carry the shape this gate reads; the gate then
    reports the problem instead of a clean verdict.
    """
    if not os.path.isfile(path):
        return None, f"the measurement file {path} does not exist"
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as error:
        return None, f"the measurement file {path} could not be read: {error}"
    if not isinstance(data, dict):
        return None, f"the measurement file {path} is not an object"
    projects = data.get("projects")
    if not isinstance(projects, dict) or not projects:
        return None, f"the measurement file {path} carries no projects"
    for name, seconds in projects.items():
        if not isinstance(seconds, (int, float)) or seconds < 0:
            return None, (
                f"the measurement file {path} prices {name} as {seconds!r}, "
                "which is not a number of seconds"
            )
    return data, None


def median(values):
    ordered = sorted(values)
    count = len(ordered)
    if count == 0:
        return 0.0
    middle = count // 2
    if count % 2:
        return float(ordered[middle])
    return (ordered[middle - 1] + ordered[middle]) / 2.0


# ---------------------------------------------------------------------------
# The analysis
# ---------------------------------------------------------------------------


def analyse(shards, costs):
    """(findings, notes, rows, skew, heaviest, lightest) for a layout and a
    measurement.

    `rows` is the table the gate prints: one row per shard with the number of
    projects, the measured seconds they cost together and the wall the shard
    would spend on them.
    """
    findings = []
    notes = []

    per_project = costs["projects"]
    overhead = costs.get("overhead") or {}
    per_project_seconds = float(
        overhead.get("perProjectSeconds", DEFAULT_PER_PROJECT_SECONDS)
    )
    per_shard_seconds = float(
        overhead.get("perShardSeconds", DEFAULT_PER_SHARD_SECONDS)
    )

    # A project in two shards runs twice per CI run, and the balance below
    # would count its cost twice.
    owner = {}
    for shard, projects in sorted(shards.items()):
        for project in projects:
            owner.setdefault(project, []).append(shard)
    for project, where in sorted(owner.items()):
        if len(where) > 1:
            findings.append(Finding(
                "SHARD-PROJECT-DUPLICATED",
                project,
                f"the project runs in {len(where)} shards ({', '.join(where)}), "
                "so it runs that many times per CI run and its cost is counted "
                "that many times. Every project belongs to exactly one shard.",
            ))

    # A recorded cost for a project no shard runs: the measurement describes a
    # tree that is not the tree.
    running = set(owner)
    for project in sorted(per_project):
        if project not in running:
            findings.append(Finding(
                "SHARD-COST-STALE",
                project,
                f"the measurement records a cost for {project}, and no shard "
                "runs it any more. The measurement describes a tree that is "
                "not this one; re-measure it.",
            ))

    # A project with no recorded cost is priced at the median of the
    # measured ones, so the balance stays honest while it waits for its own
    # measurement.
    unmeasured = sorted(
        project for project in running if project not in per_project
    )
    default = median(list(per_project.values())) if per_project else 0.0
    if unmeasured:
        notes.append(
            f"{len(unmeasured)} project(s) have no measured cost and are "
            f"priced at the median ({default:.2f} s): "
            f"{', '.join(unmeasured)}. Measure them and refresh "
            f"{COSTS} to replace the estimate with a number."
        )

    rows = []
    for shard, projects in sorted(shards.items()):
        measured = 0.0
        for project in projects:
            measured += float(per_project.get(project, default))
        wall = per_shard_seconds + per_project_seconds * len(projects) + measured
        rows.append({
            "shard": shard,
            "projects": len(projects),
            "measured": measured,
            "wall": wall,
        })

    if rows:
        heaviest = max(row["wall"] for row in rows)
        lightest = min(row["wall"] for row in rows)
        skew = heaviest / lightest if lightest > 0 else float("inf")
    else:
        heaviest = lightest = 0.0
        skew = float("inf")

    return findings, notes, rows, skew, heaviest, lightest


def skew_finding(rows, skew, max_skew):
    """The finding for a layout that is too far apart, or None."""
    if skew <= max_skew:
        return None
    worst = max(rows, key=lambda row: row["wall"])
    best = min(rows, key=lambda row: row["wall"])
    return Finding(
        "SHARD-SKEW-EXCEEDED",
        worst["shard"],
        f"the `{worst['shard']}` shard would spend {worst['wall']:.1f} s and "
        f"the `{best['shard']}` shard {best['wall']:.1f} s, a skew of "
        f"{skew:.2f}x, and the limit is {max_skew:.2f}x. The pull "
        "request waits for the heaviest shard, so a skew is a wall of "
        "waiting on nothing. Move a project, or split the heavy shard.",
    )


# ---------------------------------------------------------------------------
# The self-test
# ---------------------------------------------------------------------------

CLEAN_SOURCE = {
    "version": 1,
    "shards": {
        "core": {"projects": ["Alpha.Tests", "Beta.Tests"], "cli": False},
        "platform": {"projects": ["Gamma.Tests"], "cli": False},
    },
}

CLEAN_COSTS = {
    "projects": {
        "Alpha.Tests": 10.0,
        "Beta.Tests": 10.0,
        "Gamma.Tests": 10.0,
    },
    "overhead": {
        "perShardSeconds": 1.0,
        "perProjectSeconds": 0.5,
    },
}


def _write_tree(root, source, costs_data):
    os.makedirs(os.path.join(root, "tools"), exist_ok=True)
    with open(os.path.join(root, SOURCE), "w", encoding="utf-8") as handle:
        json.dump(source, handle, indent=2)
    with open(os.path.join(root, COSTS), "w", encoding="utf-8") as handle:
        json.dump(costs_data, handle, indent=2)


def _run(root, max_skew):
    """The gate's verdict for a tree.

    (findings, notes, rows, skew, heaviest, lightest) — the same shape in
    every case, so the self-test unpacks it the same way whatever it planted.
    """
    shards, error = read_shards(os.path.join(root, SOURCE))
    if error:
        return [Finding("SHARD-SOURCE-UNREADABLE", SOURCE, error)], [], [], 0.0, 0.0, 0.0
    costs, error = read_costs(os.path.join(root, COSTS))
    if error:
        return [Finding("SHARD-COST-UNREADABLE", COSTS, error)], [], [], 0.0, 0.0, 0.0
    findings, notes, rows, skew, heaviest, lightest = analyse(shards, costs)
    finding = skew_finding(rows, skew, max_skew)
    if finding is not None:
        findings.append(finding)
    return findings, notes, rows, skew, heaviest, lightest


def _self_test():
    """Plant one defect per rule and assert that the rule fires."""
    failures = []

    def check(name, condition, detail):
        if not condition:
            failures.append(f"{name}: {detail}")

    root = tempfile.mkdtemp(prefix="shard-balance-")
    try:
        # 1. A clean tree is clean.
        _write_tree(root, CLEAN_SOURCE, CLEAN_COSTS)
        findings, _notes, rows, skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        check("clean", not findings, f"the clean tree produced {findings}")
        check("clean-rows", len(rows) == 2, f"the clean tree produced {rows}")
        check("clean-skew", skew < DEFAULT_MAX_SKEW,
              f"the clean tree skewed {skew}")
        check("clean-walls",
              {row["shard"]: round(row["wall"], 3) for row in rows}
              == {"core": 22.0, "platform": 11.5},
              f"the clean tree computed {[(r['shard'], r['wall']) for r in rows]}")

        # 2. The runaway shard. One project costs ten times what the others
        #    do, and the gate must fail on the skew.
        runaway = json.loads(json.dumps(CLEAN_COSTS))
        runaway["projects"]["Alpha.Tests"] = 400.0
        _write_tree(root, CLEAN_SOURCE, runaway)
        findings, _notes, _rows, skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        codes = {finding.code for finding in findings}
        check("runaway", "SHARD-SKEW-EXCEEDED" in codes,
              f"the runaway shard produced {codes} (skew {skew})")

        # 3. The same tree under a lax threshold is clean again: the rule is
        #    the threshold, not a hard-coded number.
        findings, _notes, _rows, _skew, _h, _l = _run(root, 1000.0)
        check("runaway-lax", not findings,
              f"the runaway shard still failed under a lax threshold: {findings}")

        # 4. A project in two shards.
        duplicated = json.loads(json.dumps(CLEAN_SOURCE))
        duplicated["shards"]["platform"]["projects"].append("Alpha.Tests")
        _write_tree(root, duplicated, CLEAN_COSTS)
        findings, _notes, _rows, _skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        codes = {finding.code for finding in findings}
        check("duplicated", "SHARD-PROJECT-DUPLICATED" in codes,
              f"the duplicated project produced {codes}")

        # 5. A recorded cost for a project no shard runs.
        stale = json.loads(json.dumps(CLEAN_COSTS))
        stale["projects"]["Gone.Tests"] = 1.0
        _write_tree(root, CLEAN_SOURCE, stale)
        findings, _notes, _rows, _skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        codes = {finding.code for finding in findings}
        check("stale", "SHARD-COST-STALE" in codes,
              f"the stale cost produced {codes}")

        # 6. A missing measurement file is an error, not a clean verdict.
        os.remove(os.path.join(root, COSTS))
        findings, _notes, _rows, _skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        check("missing-costs", bool(findings),
              "a missing measurement file produced a clean verdict")

        # 7. A source with no shard list is an error too.
        _write_tree(root, {"version": 1, "shards": {}}, CLEAN_COSTS)
        findings, _notes, _rows, _skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        codes = {finding.code for finding in findings}
        check("missing-shards", "SHARD-SOURCE-UNREADABLE" in codes,
              f"a source with no shard list produced {codes}")

        # 8. A project with no measured cost is a note, not a finding.
        unmeasured = json.loads(json.dumps(CLEAN_COSTS))
        del unmeasured["projects"]["Gamma.Tests"]
        _write_tree(root, CLEAN_SOURCE, unmeasured)
        findings, notes, rows, _skew, _h, _l = _run(root, DEFAULT_MAX_SKEW)
        check("unmeasured", not findings,
              f"an unmeasured project produced {findings}")
        check("unmeasured-note", bool(notes),
              "an unmeasured project produced no note")
        check("unmeasured-priced", any(row["shard"] == "platform" and row["wall"] > 0
                                       for row in rows),
              f"the unmeasured project was not priced: {rows}")
    finally:
        shutil.rmtree(root, ignore_errors=True)

    if failures:
        print("self-test FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("ok: the self-test proves the gate fails on a runaway shard, on a "
          "duplicated project and on a stale cost, and stays quiet on a "
          "clean tree and on an unmeasured project")
    return 0


# ---------------------------------------------------------------------------
# The entry point
# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description="Check that the CI test shards are balanced by the "
                    "measured cost of every project."
    )
    parser.add_argument("--source", default=SOURCE,
                        help="the source the shard lists come from")
    parser.add_argument("--costs", default=COSTS,
                        help="the recorded measurement of the project costs")
    parser.add_argument("--max-skew", type=float, default=DEFAULT_MAX_SKEW,
                        help="the ratio of the heaviest shard to the lightest "
                             "one the layout may reach (default: "
                             f"{DEFAULT_MAX_SKEW})")
    parser.add_argument("--min-projects", type=int, default=0,
                        help="a floor on how many projects the shards run "
                             "between them. A gate that read fewer than this "
                             "cannot judge the layout, and a scan that "
                             "matches nothing must not report 'balanced'")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the failure path on a throwaway tree")
    args = parser.parse_args()

    if args.self_test:
        return _self_test()

    if args.max_skew <= 0:
        print("::error::--max-skew must be a positive number")
        return 2

    shards, error = read_shards(args.source)
    if error:
        print(f"::error::{error}")
        return 2

    costs, error = read_costs(args.costs)
    if error:
        print(f"::error::{error}")
        return 2

    findings, notes, rows, skew, heaviest, lightest = analyse(shards, costs)

    project_count = sum(len(projects) for projects in shards.values())
    if args.min_projects and project_count < args.min_projects:
        print(f"::error::the shards run {project_count} project(s) between them, "
              f"and the floor is {args.min_projects}. A layout that reads "
              "fewer projects than that is a layout this gate cannot judge.")
        return 1

    source = costs.get("source") or {}
    print(f"shard balance, from the measurement of run "
          f"{source.get('run', '?')} ({source.get('sha', '?')}, "
          f"{source.get('date', '?')}):")
    print("| shard | projects | measured s | wall s |")
    print("|---|---:|---:|---:|")
    for row in rows:
        print(f"| {row['shard']} | {row['projects']} | "
              f"{row['measured']:.1f} | {row['wall']:.1f} |")
    print(f"heaviest {heaviest:.1f} s, lightest {lightest:.1f} s, "
          f"skew {skew:.2f}x (the limit is {args.max_skew:.2f}x)")
    for note in notes:
        print(f"note: {note}")

    if skew > args.max_skew:
        finding = skew_finding(rows, skew, args.max_skew)
        if finding is not None:
            findings.append(finding)

    if findings:
        print("::error::the CI test shards are not balanced:")
        for finding in findings:
            print(f"  - {finding}")
        return 1

    print("ok: the shards are balanced by the recorded measurement")
    return 0


if __name__ == "__main__":
    sys.exit(main())
