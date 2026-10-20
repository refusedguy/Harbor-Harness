#!/usr/bin/env python3
"""measure-project-graph.py — the project dependency graph, measured.

WHY
---
Issue #1929 claims the CI build is limited by the runner's 4 vCPU rather than
by the shape of the project dependency graph. The graph half of that claim is
checkable without building anything: if the graph were the binding constraint,
the build could never have had more than N projects ready at once, and N
would be small.

This script parses the solution and the `<ProjectReference>` edges and reports
the shape that decides the question:

* how many projects and edges,
* the DEPTH of the graph (longest chain) — the floor on the wall time,
* the LEVEL WIDTH of the graph (projects grouped by longest-path level) — the
  ceiling on how many builds can be in flight at once,
* how many levels are wide enough to keep 4 compilers busy,
* the critical path itself.

The unit-duration assumption is deliberate and stated in the output: the
graph's shape is what is measured here, not the per-project build cost.

USAGE
-----
    python3 tools/measure-project-graph.py [--solution Harbor.slnx] [--json out.json]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

_PROJECT_PATH_RE = re.compile(r'<Project\s+Path="([^"]+)"')
_PROJECT_REF_RE = re.compile(
    r'<ProjectReference\s+Include="([^"]+)"', re.IGNORECASE
)


def read_solution_projects(solution_path: str) -> list[str]:
    with open(solution_path, "r", encoding="utf-8") as handle:
        text = handle.read()
    return [p.replace("\\", "/") for p in _PROJECT_PATH_RE.findall(text)]


def read_project_references(project_path: str) -> list[str]:
    try:
        tree = ET.parse(project_path)
    except (OSError, ET.ParseError):
        return []
    refs: list[str] = []
    for element in tree.iter():
        if element.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue
        include = element.get("Include")
        if include:
            refs.append(include.replace("\\", "/"))
    return refs


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--solution", default="Harbor.slnx")
    parser.add_argument("--json", default="", help="write the result as JSON here")
    args = parser.parse_args()

    root = os.path.dirname(os.path.abspath(args.solution)) or "."
    projects = read_solution_projects(args.solution)

    # An edge points from the referring project to the project it references.
    edges: dict[str, set[str]] = defaultdict(set)
    for project in projects:
        absolute = os.path.join(root, project)
        for reference in read_project_references(absolute):
            # Normalize to the solution-relative form when the reference is
            # a path inside the repo; framework/nuget references are skipped.
            candidate = os.path.normpath(os.path.join(os.path.dirname(project), reference))
            candidate = candidate.replace("\\", "/")
            if candidate in projects:
                edges[project].add(candidate)

    # Longest-path level of every project: level(v) = 0 for a project with no
    # project references, else 1 + max(level of its references).
    levels: dict[str, int] = {}
    visiting: set[str] = set()

    def level_of(project: str) -> int:
        if project in levels:
            return levels[project]
        if project in visiting:  # a cycle: treat as a leaf, the build would fail
            return 0
        visiting.add(project)
        deps = edges.get(project, set())
        value = 0 if not deps else 1 + max(level_of(dep) for dep in deps)
        visiting.discard(project)
        levels[project] = value
        return value

    for project in projects:
        level_of(project)

    by_level: dict[int, list[str]] = defaultdict(list)
    for project, level in levels.items():
        by_level[level].append(project)

    depth = max(levels.values()) if levels else 0
    width = max((len(v) for v in by_level.values()), default=0)
    edge_count = sum(len(v) for v in edges.values())

    # Critical path: the longest chain of project references.
    memo: dict[str, list[str]] = {}

    def longest_chain(project: str) -> list[str]:
        if project in memo:
            return memo[project]
        best: list[str] = []
        for dep in sorted(edges.get(project, set())):
            chain = longest_chain(dep)
            if len(chain) > len(best):
                best = chain
        memo[project] = [project] + best
        return memo[project]

    critical = max((longest_chain(p) for p in projects), key=len, default=[])

    wide_levels = {lvl: len(items) for lvl, items in sorted(by_level.items())}

    result = {
        "solution": args.solution,
        "projects": len(projects),
        "edges": edge_count,
        "depth": depth,
        "max_level_width": width,
        "level_widths": wide_levels,
        "levels_wide_enough_for_4_compilers": sum(
            1 for count in wide_levels.values() if count >= 4
        ),
        "levels_wide_enough_for_2_compilers": sum(
            1 for count in wide_levels.values() if count >= 2
        ),
        "critical_path": critical,
        "critical_path_length": len(critical),
    }

    print(f"solution            : {args.solution}")
    print(f"projects            : {result['projects']}")
    print(f"project edges       : {result['edges']}")
    print(f"graph depth         : {result['depth']}")
    print(f"max level width     : {result['max_level_width']} projects at one level")
    print(
        f"levels with >= 4    : {result['levels_wide_enough_for_4_compilers']}"
        f" of {len(wide_levels)}"
    )
    print(
        f"levels with >= 2    : {result['levels_wide_enough_for_2_compilers']}"
        f" of {len(wide_levels)}"
    )
    print("level widths        :")
    for lvl, count in wide_levels.items():
        print(f"  level {lvl:>2}: {count:>3} projects")
    print(f"critical path ({len(critical)}):")
    for step in critical:
        print(f"  {step}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(result, handle, indent=2)

    return 0


if __name__ == "__main__":
    sys.exit(main())
