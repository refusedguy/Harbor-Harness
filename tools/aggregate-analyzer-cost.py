#!/usr/bin/env python3
"""Turn the per-arm measurements of the analyzer cost harness into a table.

Stdlib only. Reads the JSON and the log `tools/measure-build-cpu.py` wrote
for every arm of `.github/workflows/meas-analyzer-cost.yml` and answers two
questions:

  1. What does each analyzer family cost the build? — the wall-time
     difference between the arm's OWN baseline and the arm. The two builds
     run back to back in the same job, on the same runner, so the machine's
     speed cancels out of the difference. (The unpaired design — one arm per
     job, compared against a baseline from another job — measured a 16 %
     spread on two builds of the SAME tree and reported removing an
     analyzer as making the build 90 s slower; see the workflow's header.)

  2. How much of the compiler's time does each family take? — the compiler's
     own report (`-p:ReportAnalyzer=true`), which prints the elapsed time per
     analyzer assembly for every compilation. That is the cost INSIDE one
     compilation, and it is the number that separates "this package is
     expensive" from "this package is referenced by many projects".

The two numbers answer different questions and neither replaces the other.
The wall difference is what the runner pays; the analyzer time is what the
family takes out of a compilation, and a family that is expensive per
compilation is expensive everywhere it is referenced.

USAGE
    python3 tools/aggregate-analyzer-cost.py meas --summary "$GITHUB_STEP_SUMMARY"
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import statistics
import sys

# The family a measured analyzer assembly belongs to. The assembly name is
# the part of the assembly identity before ", Version=".
ASSEMBLY_MARKERS = (
    ("Roslynator", "Roslynator.Analyzers"),
    ("SonarAnalyzer", "SonarAnalyzer.CSharp"),
    ("NetAnalyzers", "Microsoft.CodeAnalysis.NetAnalyzers"),
    ("BannedApiAnalyzers", "Microsoft.CodeAnalysis.BannedApiAnalyzers"),
    ("PublicApiAnalyzers", "Microsoft.CodeAnalysis.PublicApiAnalyzers"),
    ("VisualStudio.Threading.Analyzers", "Microsoft.VisualStudio.Threading.Analyzers"),
    ("Meziantou", "Meziantou.Analyzer"),
    ("AsyncFixer", "AsyncFixer"),
    ("ReflectionAnalyzers", "ReflectionAnalyzers"),
    ("DependencyInjection.Lifetime.Analyzers", "DependencyInjection.Lifetime.Analyzers"),
    ("CSharpFunctionalExtensions.Analyzers", "CSharpFunctionalExtensions.Analyzers"),
    ("DotNetProjectFile.Analyzers", "DotNetProjectFile.Analyzers"),
)

# The assembly the SDK itself contributes. It is not one of the packages
# this repository references, and it is listed so that its time is not
# silently folded into somebody else's row.
SDK_ASSEMBLY = "Microsoft.CodeAnalysis.Analyzers"

# "   0.242   48   Roslyn.Diagnostics.CSharp.Analyzers, Version=1.2.0.0, ..."
_REPORT_ROW = re.compile(
    r"^\s*(?P<seconds>[<>]?\d+(?:\.\d+)?)\s+(?P<pct>\d+|<1)\s{2,}(?P<name>\S.*?)\s*$"
)

# The log `measure-build-cpu.py` writes prefixes every line with the elapsed
# seconds since the command started.
_LOG_PREFIX = re.compile(r"^\s*\d+\.\d+\s\|\s?")

_TOTAL = re.compile(
    r"^Total analyzer execution time:\s*(?P<seconds>\d+(?:\.\d+)?)\s*seconds"
)


def family_of(assembly):
    """The family an analyzer assembly belongs to."""
    if assembly == SDK_ASSEMBLY:
        return SDK_ASSEMBLY
    for marker, family in ASSEMBLY_MARKERS:
        if marker in assembly:
            return family
    return assembly


def read_arms(meas_dir):
    """Every arm's two halves, keyed by label."""
    arms = {}
    for path in sorted(glob.glob(os.path.join(meas_dir, "*.json"))):
        name = os.path.basename(path)[: -len(".json")]
        label, _, half = name.rpartition("-")
        if half not in ("base", "arm") or not label:
            continue
        try:
            with open(path, encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            continue
        arms.setdefault(label, {})[half] = data
    return arms


def wall_of(arm):
    return float(arm.get("wall_seconds") or 0.0)


def csc_ms_of(arm):
    return int(arm.get("tasks", {}).get("Csc", {}).get("total_ms") or 0)


def cores_of(arm):
    return arm.get("cpu", {}).get("avg_busy_cores")


def parse_analyzer_report(log_path):
    """The compiler's own per-analyzer report: {assembly: seconds}.

    The report is printed once per compilation, so the numbers are summed
    over every project of the build. Only the ASSEMBLY rows are read: they
    are the ones that carry a ", Version=" identity, and they are the unit
    a package is measured in.
    """
    totals = {}
    if not os.path.exists(log_path):
        return totals
    with open(log_path, encoding="utf-8", errors="replace") as handle:
        for raw in handle:
            line = _LOG_PREFIX.sub("", raw.rstrip("\n"))
            if _TOTAL.match(line.strip()):
                continue
            match = _REPORT_ROW.match(line)
            if not match:
                continue
            name = match.group("name")
            if ", Version=" not in name:
                continue
            assembly = name.split(", Version=")[0].strip()
            try:
                seconds = float(match.group("seconds").lstrip("<>"))
            except ValueError:
                continue
            totals[assembly] = totals.get(assembly, 0.0) + seconds
    return totals


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("meas_dir", help="the directory the arms wrote into")
    parser.add_argument("--summary", help="a markdown file to append the table to")
    parser.add_argument("--json-out", default="analyzer-cost.json")
    parser.add_argument("--md-out", default="analyzer-cost.md")
    args = parser.parse_args()

    arms = read_arms(args.meas_dir)
    if not arms:
        print("no arms found", file=sys.stderr)
        return 1

    noise = []
    rows = []
    narrowed = None
    per_family_report = {}

    for label in sorted(arms):
        halves = arms[label]
        if "base" not in halves or "arm" not in halves:
            print(f"{label}: incomplete arm, skipped")
            continue
        base_wall = wall_of(halves["base"])
        arm_wall = wall_of(halves["arm"])
        delta = base_wall - arm_wall
        row = {
            "label": label,
            "baseline_wall_seconds": base_wall,
            "arm_wall_seconds": arm_wall,
            "wall_delta_seconds": delta,
            "arm_csc_ms": csc_ms_of(halves["arm"]),
            "arm_avg_busy_cores": cores_of(halves["arm"]),
        }
        if label.startswith("null-"):
            noise.append(delta)
        elif label == "narrowed":
            narrowed = row
        elif label.endswith("-report"):
            rows.append(row)
        else:
            rows.append(row)

        if label.endswith("-report"):
            totals = parse_analyzer_report(
                os.path.join(args.meas_dir, f"{label}-arm.log")
            )
            for assembly, seconds in totals.items():
                family = family_of(assembly)
                entry = per_family_report.setdefault(
                    family, {"seconds": 0.0, "assemblies": []}
                )
                entry["seconds"] += seconds
                entry["assemblies"].append(assembly)
    for entry in per_family_report.values():
        entry["assemblies"].sort()

    rows.sort(key=lambda row: -(row["wall_delta_seconds"] or 0.0))

    noise_floor = max((abs(value) for value in noise), default=0.0)
    noise_median = statistics.median(noise) if noise else 0.0

    lines = []
    lines.append("# The cost of each analyzer family")
    lines.append("")
    lines.append(
        "Every arm builds the same tree twice in one job — the baseline "
        "(every analyzer family ON, in every tree) and then its own arm — so "
        "the runner's speed cancels out of the difference."
    )
    lines.append("")
    if noise:
        lines.append(
            f"The noise floor, measured by the control arms (the baseline "
            f"against itself): median {noise_median:+.1f} s, largest "
            f"{noise_floor:.1f} s. A difference below that is not a "
            f"difference."
        )
        lines.append("")
    if narrowed:
        lines.append(
            f"**The narrowed tree: {narrowed['arm_wall_seconds']:.1f} s against "
            f"a baseline of {narrowed['baseline_wall_seconds']:.1f} s in the "
            f"same job — {narrowed['wall_delta_seconds']:+.1f} s "
            f"({narrowed['wall_delta_seconds'] / narrowed['baseline_wall_seconds'] * 100:+.1f} %).**"
        )
        lines.append("")
    lines.append("## The marginal cost of each family")
    lines.append("")
    lines.append(
        "The difference between the arm's own baseline and the arm, in the "
        "same job, on the same runner. Everything else in the two builds is "
        "the same tree and the same command."
    )
    lines.append("")
    lines.append(
        "| family | baseline (s) | arm (s) | difference (s) | arm Csc (ms) | avg busy cores |"
    )
    lines.append("|---|---|---|---|---|---|")
    for row in rows:
        lines.append(
            f"| `{row['label']}` | {row['baseline_wall_seconds']:.1f} | "
            f"{row['arm_wall_seconds']:.1f} | {row['wall_delta_seconds']:+.1f} | "
            f"{row['arm_csc_ms']} | {row['arm_avg_busy_cores']} |"
        )
    lines.append("")
    lines.append("## The compiler's own report")
    lines.append("")
    lines.append(
        "The elapsed time per analyzer assembly, summed over every "
        "compilation of the build (`-p:ReportAnalyzer=true`). This is the "
        "cost inside one compilation; the wall difference above is what the "
        "runner pays for it."
    )
    lines.append("")
    if per_family_report:
        lines.append("| family | analyzer seconds | assemblies |")
        lines.append("|---|---|---|")
        for family, entry in sorted(
            per_family_report.items(), key=lambda kv: -kv[1]["seconds"]
        ):
            lines.append(
                f"| `{family}` | {entry['seconds']:.1f} | "
                f"{', '.join(entry['assemblies'])} |"
            )
    else:
        lines.append("_The compiler printed no analyzer report in this run._")
    lines.append("")

    result = {
        "noise_floor_seconds": noise_floor,
        "noise_median_seconds": noise_median,
        "narrowed": narrowed,
        "marginal_cost": rows,
        "analyzer_seconds_per_family": per_family_report,
        "arms": {
            label: {
                "baseline_wall_seconds": wall_of(halves["base"]),
                "arm_wall_seconds": wall_of(halves["arm"]),
                "wall_delta_seconds": wall_of(halves["base"]) - wall_of(halves["arm"]),
                "arm_csc_ms": csc_ms_of(halves["arm"]),
                "arm_avg_busy_cores": cores_of(halves["arm"]),
                "exit_code": halves["arm"].get("exit_code"),
            }
            for label, halves in sorted(arms.items())
            if "base" in halves and "arm" in halves
        },
    }

    markdown = "\n".join(lines)
    print(markdown)
    with open(args.json_out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    with open(args.md_out, "w", encoding="utf-8") as handle:
        handle.write(markdown)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(markdown)
            handle.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
