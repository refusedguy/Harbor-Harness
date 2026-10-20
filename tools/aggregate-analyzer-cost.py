#!/usr/bin/env python3
"""Turn the per-arm measurements of the analyzer cost harness into a table.

Stdlib only. Reads the JSON and the log `tools/measure-build-cpu.py` wrote
for every arm of `.github/workflows/meas-analyzer-cost.yml` and answers two
questions:

  1. What does each analyzer family cost the build? — the wall-time
     difference between the `all-on` arm and the `no-<family>` arm. The
     difference is the MARGINAL cost of that family: everything else is the
     same tree, the same runner and the same command.

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
    """Every arm's measurement, keyed by label."""
    arms = {}
    for path in sorted(glob.glob(os.path.join(meas_dir, "*.json"))):
        label = os.path.basename(path)[: -len(".json")]
        try:
            with open(path, encoding="utf-8") as handle:
                arms[label] = json.load(handle)
        except (OSError, ValueError):
            continue
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
            line = raw.rstrip("\n")
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


def median_wall(arms, labels):
    values = [wall_of(arms[label]) for label in labels if label in arms]
    return statistics.median(values) if values else None


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

    all_on_labels = sorted(label for label in arms if label.startswith("all-on"))
    narrowed_labels = sorted(label for label in arms if label.startswith("narrowed"))
    no_labels = sorted(label for label in arms if label.startswith("no-"))

    baseline = median_wall(arms, all_on_labels)
    narrowed = median_wall(arms, narrowed_labels)

    report_totals = {}
    for label in arms:
        if label.startswith("report"):
            report_totals = parse_analyzer_report(
                os.path.join(args.meas_dir, f"{label}.log")
            )
            break

    per_family_report = {}
    for assembly, seconds in report_totals.items():
        family = family_of(assembly)
        entry = per_family_report.setdefault(
            family, {"seconds": 0.0, "assemblies": []}
        )
        entry["seconds"] += seconds
        entry["assemblies"].append(assembly)
    for entry in per_family_report.values():
        entry["assemblies"].sort()

    rows = []
    for label in no_labels:
        arm = arms[label]
        family_label = label[len("no-") :]
        wall = wall_of(arm)
        delta = (baseline - wall) if baseline is not None else None
        rows.append(
            {
                "family_label": family_label,
                "wall_seconds": wall,
                "csc_ms": csc_ms_of(arm),
                "avg_busy_cores": cores_of(arm),
                "baseline_wall_seconds": baseline,
                "wall_delta_seconds": delta,
            }
        )
    rows.sort(key=lambda row: -(row["wall_delta_seconds"] or 0.0))

    result = {
        "baseline_wall_seconds": baseline,
        "baseline_arms": all_on_labels,
        "narrowed_wall_seconds": narrowed,
        "narrowed_arms": narrowed_labels,
        "marginal_cost": rows,
        "analyzer_seconds_per_family": per_family_report,
        "arms": {
            label: {
                "wall_seconds": wall_of(arm),
                "csc_ms": csc_ms_of(arm),
                "avg_busy_cores": cores_of(arm),
                "exit_code": arm.get("exit_code"),
            }
            for label, arm in sorted(arms.items())
        },
    }

    lines = []
    lines.append("# The cost of each analyzer family")
    lines.append("")
    if baseline is not None:
        lines.append(
            f"Baseline (`all-on`, the median of {len(all_on_labels)} runs): "
            f"**{baseline:.1f} s** of wall time."
        )
    else:
        lines.append("Baseline: no `all-on` arm in this run.")
    if narrowed is not None and baseline is not None:
        lines.append("")
        lines.append(
            f"Narrowed tree (the branch's own declaration, the median of "
            f"{len(narrowed_labels)} runs): **{narrowed:.1f} s**, "
            f"**{baseline - narrowed:.1f} s** against the baseline "
            f"({(baseline - narrowed) / baseline * 100:.1f} %)."
        )
    lines.append("")
    lines.append("## The marginal cost of each family")
    lines.append("")
    lines.append(
        "The difference between the `all-on` arm and the `no-<family>` arm. "
        "Everything else in the two arms is the same tree, the same runner "
        "and the same command."
    )
    lines.append("")
    lines.append("| family | wall (s) | against the baseline (s) | Csc (ms) | avg busy cores |")
    lines.append("|---|---|---|---|---|")
    for row in rows:
        delta = row["wall_delta_seconds"]
        delta_text = f"{delta:+.1f}" if delta is not None else "n/a"
        lines.append(
            f"| `{row['family_label']}` | {row['wall_seconds']:.1f} | {delta_text} | "
            f"{row['csc_ms']} | {row['avg_busy_cores']} |"
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
