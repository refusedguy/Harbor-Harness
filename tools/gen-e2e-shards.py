#!/usr/bin/env python3
"""Generate the process plan for the headless Avalonia E2E suite.

THE PROBLEM THIS FILE SOLVES
----------------------------
`tests/Harbor.E2E.App.Avalonia` drives ONE headless Avalonia application:
`HeadlessAvaloniaDriver` keeps `Application.Current`, the desktop lifetime and
the DI host in STATIC fields, and Avalonia permits exactly one `Application`
per AppDomain. The driver also sets `$HOME` (an environment variable is not
thread-local) and `CommonConfig.OverrideHarborHomeForTests` pins the Harbor home
for the whole process. So the fifteen classes in that project cannot share a
process in parallel - and they cannot share it at all without serialising.

The lever is the PROCESS, not the attribute: each shard is one `dotnet exec`
of the same assembly with its own `--treenode-filter`, so every shard gets its
own Avalonia application, its own `$HOME` and its own Harbor home. The shard
membership is what this file generates.

WHY THE LIST IS GENERATED AND NOT HAND-WRITTEN
-----------------------------------------------
A hand-written list of test classes is a list that drifts. The repo has two
open issues about exactly that shape (#1800, #1823), and the class table in
`E2eShards.cs` already grew one entry by hand while a guard test existed to
catch the drift - and the guard was removed because a new UNKEYED test class
reshuffled the suite's order and broke a real test twice (PR #1818).

So the list here is derived, and it is derived from two sources that cannot
quietly disagree with the code:

  1. THE CLASS UNIVERSE comes from the SOURCE TREE: every top-level type in
     `tests/Harbor.E2E.App.Avalonia/**` and `tests/Harbor.E2E.Framework/**`
     whose own extent declares a `[Test]`. The framework project is in the
     universe because it is a REFERENCED TEST PROJECT: TUnit discovers and
     runs its classes inside every project that references it, so its 26
     `StateTestRunnerTests` cases are part of this assembly's 133 - and a
     filter that did not name them would DROP them from CI silently.

  2. THE COSTS come from a MEASURED TRX artifact of a real CI run (the
     `test-results-e2e` artifact). The split is longest-processing-time-first
     over the measured per-class durations, not over the class count: the two
     heaviest classes alone are half the project's time.

The plan records WHICH artifact the costs came from, so a stale plan is
visible instead of merely wrong. `tests/Harbor.Architecture.Tests` re-derives
the class universe from the tree and fails when a class is in no shard - that
is the arch-rule half of this pair (the plan is data, the rule is the gate).

Usage:
  ./tools/gen-e2e-shards.py --trx path/to/Harbor.E2E.App.Avalonia.trx
  ./tools/gen-e2e-shards.py --trx <file> --shards 3 --measured-run 39404823168
  ./tools/gen-e2e-shards.py --print-universe          # list the classes, no write

Exit codes: 0 = plan written (or printed), 2 = bad invocation, 3 = no TRX.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

# TRX is a fixed-namespace schema; strip it once and match on bare tags.
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# The two project directories whose test classes this assembly discovers:
# its own, and the referenced test project whose classes TUnit runs here too.
PROJECT_DIRS = ("Harbor.E2E.App.Avalonia", "Harbor.E2E.Framework")

# A top-level type declaration. Column 0 only - a nested type is not a suite
# and TUnit does not schedule one. Mirrors the matcher the process-environment
# arch rule uses, so the two readers agree on what a "class" is.
TYPE_DECLARATION = re.compile(
    r"^(?:\[[^\]]*\]\s*|public\s+|internal\s+|private\s+|protected\s+|sealed\s+|abstract\s+"
    r"|static\s+|partial\s+|file\s+|unsafe\s+)*(?:class|record|struct)\s+(?P<name>[A-Za-z_]\w*)"
)

# A `[Test]` attribute at the start of a line, ignoring what follows the name so
# `[Test]`, `[Test(...)]` and `[Test, NotInParallel("k")]` all match while
# `[TestKitSomething]` does not.
TEST_ATTRIBUTE = re.compile(r"^\s*\[Test(\]|\(|\s|,)")

# Build output is not source. A stale copy under bin/ would be counted as a
# second class of the same name.
EXCLUDED_DIRS = ("/bin/", "/obj/", "/.worktrees/")


def repo_root(start: str) -> str:
    """Walk up from `start` until a `Harbor.slnx` marks the repository root."""
    directory = os.path.abspath(start)
    while True:
        if os.path.isfile(os.path.join(directory, "Harbor.slnx")):
            return directory
        parent = os.path.dirname(directory)
        if parent == directory:
            raise SystemExit(f"no Harbor.slnx above {start}")
        directory = parent


def source_files(root: str) -> list[str]:
    """Every .cs file under the two project directories, sorted."""
    found: list[str] = []
    for project in PROJECT_DIRS:
        base = os.path.join(root, "tests", project)
        if not os.path.isdir(base):
            continue
        for current, dirs, files in os.walk(base):
            normalised = current.replace("\\", "/")
            dirs[:] = [d for d in dirs if f"/{d}/" not in EXCLUDED_DIRS]
            if any(marker in normalised + "/" for marker in EXCLUDED_DIRS):
                continue
            for name in files:
                if name.endswith(".cs"):
                    found.append(os.path.join(current, name))
    return sorted(found)


# Comment stripping, mirroring `SourceScan.StripComments` in
# tests/Harbor.Architecture.Tests exactly: block comments first, then line
# comments, both replaced by spaces so the line count is preserved. The two
# readers must agree on what a "test class" is - the arch rule grades the plan
# this file writes, and a rule that disagreed with its own generator would
# report a class that does not exist.
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
LINE_COMMENT = re.compile(r"//[^\n]*")


def strip_comments(text: str) -> str:
    """Remove // and /* */ comments, preserving the line count.

    A block comment is blanked character by character (newlines kept), exactly
    as `SourceScan.StripComments` does in the architecture tests: the line
    numbers the class scan indexes into must still point at the real lines.
    """
    return LINE_COMMENT.sub(" ", BLOCK_COMMENT.sub(_blank, text))


def _blank(match: re.Match[str]) -> str:
    """The block comment's own shape, with every non-newline character blanked."""
    return "".join("\n" if character == "\n" else " " for character in match.group(0))


def class_universe(root: str) -> dict[str, str]:
    """Map class name -> declaring file, for every test class in the tree.

    A type is a test class when ITS OWN extent declares a `[Test]`: the
    extent runs from the type's declaration to the next top-level declaration,
    so a helper class in the same file does not inherit its neighbour's tests
    and a file with two suites yields two classes.
    """
    universe: dict[str, str] = {}
    for path in source_files(root):
        try:
            with open(path, "r", encoding="utf-8") as handle:
                raw = handle.read()
        except OSError:
            continue
        code = strip_comments(raw).split("\n")
        declarations = []
        for index, line in enumerate(code):
            match = TYPE_DECLARATION.match(line)
            if match:
                declarations.append((index, match.group("name")))
        for position, (index, name) in enumerate(declarations):
            end = declarations[position + 1][0] if position + 1 < len(declarations) else len(code)
            if any(TEST_ATTRIBUTE.match(code[i]) for i in range(index, end)):
                universe.setdefault(name, os.path.relpath(path, root).replace("\\", "/"))
    return universe


def trx_duration(value: str | None) -> float:
    """TRX durations are `hh:mm:ss.fffffff`."""
    if not value:
        return 0.0
    parts = value.split(":")
    if len(parts) == 3:
        return int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
    if len(parts) == 2:
        return int(parts[0]) * 60 + float(parts[1])
    return float(parts[0])


def read_trx(path: str) -> dict[str, tuple[float, int]]:
    """Map class simple name -> (seconds, test count) for one TRX file."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise SystemExit(f"cannot read TRX {path}: {error}")

    methods: dict[str, str] = {}
    for unit_test in root.iter(TRX_NS + "UnitTest"):
        method = unit_test.find(TRX_NS + "TestMethod")
        if method is None:
            continue
        class_name = method.get("className") or ""
        methods[unit_test.get("id", "")] = class_name.rsplit(".", 1)[-1]

    totals: dict[str, list[float]] = {}
    for result in root.iter(TRX_NS + "UnitTestResult"):
        class_name = methods.get(result.get("testId", ""), "?")
        entry = totals.setdefault(class_name, [0.0, 0])
        entry[0] += trx_duration(result.get("duration"))
        entry[1] += 1
    return {name: (value[0], int(value[1])) for name, value in totals.items()}


def longest_processing_time_first(costs: dict[str, float], shards: int) -> list[list[str]]:
    """Split classes over `shards` bins, heaviest class first onto the lightest bin."""
    bins: list[list[str]] = [[] for _ in range(shards)]
    loads = [0.0] * shards
    for name, cost in sorted(costs.items(), key=lambda item: (-item[1], item[0])):
        target = min(range(shards), key=lambda index: (loads[index], index))
        bins[target].append(name)
        loads[target] += cost
    return bins


def balance(costs: dict[str, float], shards: int) -> list[list[str]]:
    """Split classes over `shards` bins, minimising the SLOWEST bin.

    Longest-processing-time-first alone is a heuristic, and on this suite it
    leaves 2.8 s on the table: LPT over the fifteen non-Settings classes gives
    31.17 s / 25.51 s, while the partition {AvaloniaUiTests, SessionListTests,
    StandaloneControlsTests} = 28.33 s against 28.35 s for the rest is the
    balanced one. The wall of a shard IS its slowest bin, so the search
    optimises that number rather than the sum.

    LPT is the seed and a local search finishes it: repeatedly move one class
    from the slowest bin to another bin, or swap one class between two bins,
    for as long as the slowest bin gets lighter. Both moves are tried because
    a swap can improve a split that no single move can reach.
    """
    if shards <= 1:
        return [sorted(costs)]

    bins = longest_processing_time_first(costs, shards)
    loads = [sum(costs[name] for name in names) for names in bins]

    def spread() -> float:
        return max(loads)

    improved = True
    while improved:
        improved = False
        slowest = max(range(shards), key=lambda index: (loads[index], -index))

        # A MOVE: take one class out of the slowest bin and try it elsewhere.
        for source_class in list(bins[slowest]):
            for target in range(shards):
                if target == slowest:
                    continue
                candidate = list(loads)
                candidate[slowest] -= costs[source_class]
                candidate[target] += costs[source_class]
                if max(candidate) < spread():
                    bins[slowest].remove(source_class)
                    bins[target].append(source_class)
                    loads = candidate
                    improved = True
                    break
            if improved:
                break
        if improved:
            continue

        # A SWAP: exchange one class of the slowest bin with one of another.
        for target in range(shards):
            if target == slowest:
                continue
            for source_class in list(bins[slowest]):
                for target_class in list(bins[target]):
                    candidate = list(loads)
                    candidate[slowest] += costs[target_class] - costs[source_class]
                    candidate[target] += costs[source_class] - costs[target_class]
                    if max(candidate) < spread():
                        bins[slowest].remove(source_class)
                        bins[target].remove(target_class)
                        bins[slowest].append(target_class)
                        bins[target].append(source_class)
                        loads = candidate
                        improved = True
                        break
                if improved:
                    break
            if improved:
                break

    return [sorted(names) for names in bins]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--trx", action="append", default=[],
                        help="a TRX file from the test-results-e2e artifact (repeatable)")
    parser.add_argument("--shards", type=int, default=2, help="how many processes to split into")
    parser.add_argument("--dedicated", action="append", default=[],
                        help="a class that gets a process of its own (repeatable); "
                             "each one consumes a shard slot")
    parser.add_argument("--measured-run", default="", help="the CI run id the TRX came from")
    parser.add_argument("--measured-artifact", default="test-results-e2e",
                        help="the CI artifact the TRX came from")
    parser.add_argument("--out", default="", help="the plan file to write")
    parser.add_argument("--print-universe", action="store_true",
                        help="list the discovered test classes and exit")
    args = parser.parse_args()

    root = repo_root(os.getcwd())
    universe = class_universe(root)

    if args.print_universe:
        for name in sorted(universe):
            print(f"{name}\t{universe[name]}")
        print(f"# {len(universe)} test classes", file=sys.stderr)
        return 0

    if not args.trx:
        print("gen-e2e-shards.py: --trx is required", file=sys.stderr)
        return 2
    if args.shards < 1:
        print("gen-e2e-shards.py: --shards must be >= 1", file=sys.stderr)
        return 2
    if len(args.dedicated) > args.shards:
        print("gen-e2e-shards.py: --dedicated names more classes than there are shards",
              file=sys.stderr)
        return 2

    measured: dict[str, tuple[float, int]] = {}
    for path in args.trx:
        for name, (seconds, count) in read_trx(path).items():
            entry = measured.setdefault(name, (0.0, 0))
            measured[name] = (entry[0] + seconds, entry[1] + count)

    # The universe is the source tree's; the TRX only supplies the costs. A
    # class the measurement predates still gets a shard (with a zero cost), so
    # the generated plan is never missing a class the tree has.
    seconds = {name: measured.get(name, (0.0, 0))[0] for name in universe}
    counts = {name: measured.get(name, (0.0, 0))[1] for name in universe}
    unknown = sorted(set(measured) - set(universe))
    if unknown:
        print(f"gen-e2e-shards.py: warning: TRX names classes the tree does not have: "
              f"{', '.join(unknown)}", file=sys.stderr)

    # A dedicated class runs in a process of its own. The repository documents
    # one: `SettingsTests.Settings_Cancel_RevertsChanges` is skipped with the
    # reason "orphaned dispatcher continuations from previously-initialized
    # hosts mutate Application.Current state between this test's mutation and
    # its Cancel revert ... Deterministic fix requires process-per-class
    # isolation". A process of its own is that fix, and it costs nothing in
    # wall time: the class leaves the balanced pool, so the remaining classes
    # are split more evenly than they were before it was taken out.
    dedicated = [name for name in args.dedicated if name in universe]
    for name in args.dedicated:
        if name not in universe:
            print(f"gen-e2e-shards.py: warning: --dedicated {name} is not a test class "
                  f"of this assembly", file=sys.stderr)

    pooled = {name: seconds[name] for name in universe if name not in dedicated}
    pooled_shards = args.shards - len(dedicated)
    if pooled_shards < 1:
        bins = [[name] for name in dedicated]
    else:
        bins = balance(pooled, pooled_shards)
        bins = [[name] for name in dedicated] + bins

    out_path = args.out or os.path.join(
        root, "tests", "Harbor.E2E.App.Avalonia", "e2e-shards.json")

    shard_entries = []
    for names in bins:
        shard_entries.append({
            "id": f"shard-{len(shard_entries)}",
            "classes": sorted(names),
            "dedicated": len(names) == 1 and names[0] in dedicated,
            "measuredSeconds": round(sum(seconds[name] for name in names), 2),
            "measuredTests": sum(counts[name] for name in names),
        })

    # Heaviest shard first: the report reads in the order the wall is spent,
    # and the slowest shard is the one that decides it.
    shard_entries.sort(key=lambda entry: (-entry["measuredSeconds"], entry["id"]))
    for index, entry in enumerate(shard_entries):
        entry["id"] = f"shard-{index}"

    plan = {
        "schemaVersion": 1,
        "project": "Harbor.E2E.App.Avalonia",
        "description": (
            "One process per shard. The Avalonia Application, $HOME and the pinned "
            "Harbor home are process-wide, so the classes cannot share a process. "
            "Generated by tools/gen-e2e-shards.py - do not hand-edit."
        ),
        "generated": {
            "tool": "tools/gen-e2e-shards.py",
            "classUniverseFrom": "the source tree (tests/Harbor.E2E.App.Avalonia + tests/Harbor.E2E.Framework)",
            "costsFrom": "the TRX durations of the CI run named below",
            "measuredFromCiRun": args.measured_run,
            "measuredFromArtifact": args.measured_artifact,
            "measuredAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "trxFiles": [os.path.basename(path) for path in args.trx],
        },
        "totals": {
            "shards": args.shards,
            "dedicatedClasses": sorted(dedicated),
            "measuredTests": sum(counts.values()),
            "measuredTestSeconds": round(sum(seconds.values()), 2),
        },
        "shards": shard_entries,
    }

    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump(plan, handle, indent=2)
        handle.write("\n")

    print(f"wrote {out_path}: {args.shards} shard(s), "
          f"{plan['totals']['measuredTests']} test(s), "
          f"max shard {max(s['measuredSeconds'] for s in shard_entries):.2f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
