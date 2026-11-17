#!/usr/bin/env python3
"""The arm64/x64 reporter twins are ONE step each, and its `if:` stayed literal.

Stdlib only -- no pip, no network, no dotnet. Wired into the `workflows` job of
.github/workflows/docs.yml.

WHY THIS FILE EXISTS (issue #2205)

  The `test` job of ci.yml used to render three reports (test measurements,
  test census, skipped tests) with SIX steps: an X64 step and an ARM64 twin
  for each report. The twins had to be renamed twice, and one merge dropped
  the second step's `- ` marker. YAML read that twin as a continuation of the
  first step, the mapping carried two `if:`/`shell:`/`run:` keys, and GitHub
  created the run with ZERO jobs — 56 dead ci.yml runs, a 63-minute hole in
  the lane (docs/CI_CONFLICT_COST.md §5).

  The fix is `.github/actions/arch-gate/action.yml`: one caller step, with the
  binary chosen from `runner.arch` inside the action. This gate holds the two
  load-bearing properties of that change:

    1. each of the three report steps calls the composite action exactly once,
       and its `if:` still says `always()` and guards BOTH X64 and ARM64;
    2. no reporter step calls the arm64 binary directly any more — an orphan
       twin is the exact shape that broke the lane.

  Without (1) a refactor could quietly drop the arch guard and stop the
  ARM64 path from running; without (2) a half-finished migration could leave a
  double-key mapping waiting for the next merge.

Usage:
  python3 tools/check-arch-twins.py
  python3 tools/check-arch-twins.py --self-test
  python3 tools/check-arch-twins.py --root DIR

Exit codes:  0 = clean, 1 = findings, 2 = bad invocation / no input.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)

WORKFLOW = ".github/workflows/ci.yml"
ACTION = "./.github/actions/arch-gate"
REPORTERS = ("test-measurements", "test-census", "test-skips")


def steps_of(text):
    """The `steps:` blocks of a workflow, as (start_line, [lines])."""
    lines = text.split("\n")
    steps = []
    current = None
    for index, line in enumerate(lines):
        if line.startswith("      - "):
            if current is not None:
                steps.append(current)
            current = (index + 1, [line])
        elif current is not None:
            if line and not line.startswith("        ") and not line.startswith("          "):
                steps.append(current)
                current = None
            else:
                current[1].append(line)
    if current is not None:
        steps.append(current)
    return steps


def check(root):
    path = os.path.join(root, WORKFLOW)
    try:
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
    except OSError as error:
        print(f"::error::{WORKFLOW} could not be read: {error}", file=sys.stderr)
        return 1
    steps = steps_of(text)
    problems = []

    for gate in REPORTERS:
        callers = [(lineno, block) for lineno, block in steps
                   if "uses: " + ACTION in "\n".join(block)
                   and "gate: " + gate in "\n".join(block)]
        if len(callers) != 1:
            problems.append(
                f"{WORKFLOW} has {len(callers)} caller(s) of {ACTION} for `gate: {gate}`; "
                f"the arch twins collapse to exactly one step")
            continue
        lineno, block = callers[0]
        joined = "\n".join(block)
        if "if: always()" not in joined:
            problems.append(
                f"{WORKFLOW}:{lineno} lost `if: always()` — a failed shard skips the report "
                f"exactly when it is most wanted")
        if "runner.arch == 'X64'" not in joined or "runner.arch == 'ARM64'" not in joined:
            problems.append(
                f"{WORKFLOW}:{lineno} must keep the literal `runner.arch == 'X64'` and "
                f"`runner.arch == 'ARM64'` guards — one arch would stop running")

    for gate in REPORTERS:
        if "harbor-gates-linux-arm64 " + gate in text:
            problems.append(
                f"{WORKFLOW} still calls the arm64 binary directly for `{gate}` — "
                f"the orphan twin is the shape that made a run have zero jobs")

    for problem in problems:
        print(f"::error::{problem}", file=sys.stderr)
    if problems:
        return 1
    print(f"OK: {len(REPORTERS)} reporter step(s) call {ACTION} once, keep both arch guards, and have no arm64 orphan")
    return 0


def self_test():
    good = "\n".join([
        "name: ci", "jobs:", "  test:", "    steps:",
        "      - name: Report test measurements (issue #618)",
        "        if: always() && (runner.arch == 'X64' || runner.arch == 'ARM64')",
        "        uses: " + ACTION,
        "        with:",
        "          gate: test-measurements",
        "          path: TestResults/core",
        "      - name: Report the test census (issue #1884)",
        "        if: always() && (runner.arch == 'X64' || runner.arch == 'ARM64')",
        "        uses: " + ACTION,
        "        with:",
        "          gate: test-census",
        "          path: TestResults/core",
        "      - name: Report skipped tests (issue #1795)",
        "        if: always() && (runner.arch == 'X64' || runner.arch == 'ARM64')",
        "        uses: " + ACTION,
        "        with:",
        "          gate: test-skips",
        "          path: TestResults/core",
        "",
    ])
    with tempfile.TemporaryDirectory() as tmp:
        tree = os.path.join(tmp, "tree")
        os.makedirs(os.path.join(tree, ".github", "workflows"))
        path = os.path.join(tree, WORKFLOW)
        open(path, "w", encoding="utf-8").write(good)
        if check(tree) != 0:
            print("FAIL: the clean fixture is not clean", file=sys.stderr)
            return 1
        # 1. A lost arch guard.
        open(path, "w", encoding="utf-8").write(
            good.replace(" || runner.arch == 'ARM64'", "", 1))
        if check(tree) == 0:
            print("FAIL: a lost ARM64 guard was not caught", file=sys.stderr)
            return 1
        # 2. An orphan arm64 twin.
        open(path, "w", encoding="utf-8").write(good.replace(
            "          path: TestResults/core",
            "          path: TestResults/core\n"
            "      - name: Report test measurements (issue #618, ARM64)\n"
            "        run: tools/bin/harbor-gates-linux-arm64 test-measurements x", 1))
        if check(tree) == 0:
            print("FAIL: an orphan arm64 twin was not caught", file=sys.stderr)
            return 1
        # 3. A duplicate caller.
        open(path, "w", encoding="utf-8").write(good.replace(
            "          gate: test-skips",
            "          gate: test-skips\n"
            "      - name: Report skipped tests (duplicate)\n"
            "        uses: " + ACTION + "\n"
            "        with:\n"
            "          gate: test-skips", 1))
        if check(tree) == 0:
            print("FAIL: a duplicate caller was not caught", file=sys.stderr)
            return 1
    print("OK: the arch-twin gate fires on a lost guard, an orphan twin and a duplicate caller")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", default=REPO_ROOT)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    return self_test() if args.self_test else check(args.root)


if __name__ == "__main__":
    sys.exit(main())
