#!/usr/bin/env python3
"""aggregate-build-vcpu.py — turn the per-run measurements into the table.

Reads the `<label>.json` files that tools/measure-build-cpu.py wrote (one per
CI run), groups them by arm and reports, per arm: the wall-time range, the
CPU utilisation, the compiler work, and the RATIO against the baseline arm —
the number that adjudicates issue #1929.

    python3 tools/aggregate-build-vcpu.py meas/ --baseline a-prop0

The ratio is reported as a RANGE (min..max over the replicates of the arm
against the median of the baseline arm), because a single ratio would hide
exactly the run-to-run noise this experiment is trying to measure.

Two ratios are reported, and the difference between them is the point:

  wall ratio          the whole build step, gate included;
  compile-window      the same wall MINUS the architecture gate (the `Exec`
                      task), because that gate is a single-process run of the
                      architecture test suite: it is serial in every arm, so it
                      dilutes the wall ratio by a fixed amount and must be
                      taken out before the two arms can be compared.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import sys


def load(directory: str) -> dict[str, dict]:
    runs: dict[str, dict] = {}
    for name in sorted(os.listdir(directory)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(directory, name), "r", encoding="utf-8") as handle:
            data = json.load(handle)
        runs[data.get("label", name[:-5])] = data
    return runs


def arm_of(label: str) -> str:
    return label.split("-r")[0] if "-r" in label else label


def fmt_range(values: list[float]) -> str:
    return (
        f"{min(values):.1f}..{max(values):.1f} "
        f"(median {statistics.median(values):.1f})"
    )


def compile_window(data: dict) -> float | None:
    """Wall time minus the architecture gate (a serial Exec task)."""
    wall = data.get("wall_seconds")
    if wall is None:
        return None
    gate = data.get("tasks", {}).get("Exec", {}).get("total_ms")
    if not gate:
        return wall
    return wall - gate / 1000.0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory")
    parser.add_argument("--baseline", default="a-prop0")
    args = parser.parse_args()

    runs = load(args.directory)
    if not runs:
        print(f"no measurements in {args.directory}")
        return 1

    arms: dict[str, list[dict]] = {}
    for label, data in runs.items():
        arms.setdefault(arm_of(label), []).append(data)

    baseline_walls = [d["wall_seconds"] for d in arms.get(args.baseline, [])]
    baseline_median = statistics.median(baseline_walls) if baseline_walls else None
    baseline_windows = [
        w for w in (compile_window(d) for d in arms.get(args.baseline, [])) if w is not None
    ]
    baseline_window_median = (
        statistics.median(baseline_windows) if baseline_windows else None
    )

    # Runner-speed proxy. The architecture gate is the SAME single-process
    # workload in every arm (one Exec task running the architecture test
    # suite), so its duration says how fast the machine that ran the build
    # was. Dividing a run's wall by its own gate duration (relative to the
    # baseline arm's median gate) removes the runner-speed difference — which
    # on shared runners is worth up to 2x, and would otherwise be read as a
    # difference between the arms.
    gates = [
        d.get("tasks", {}).get("Exec", {}).get("total_ms", 0) or 0
        for d in arms.get(args.baseline, [])
    ]
    gates = [g for g in gates if g]
    baseline_gate_median = statistics.median(gates) if gates else None

    def normalized_wall(data: dict) -> float | None:
        wall = data.get("wall_seconds")
        gate = data.get("tasks", {}).get("Exec", {}).get("total_ms", 0) or 0
        if not wall or not gate or not baseline_gate_median:
            return None
        return wall * (baseline_gate_median / gate)

    print(
        f"{'arm':<12} {'n':>2} {'wall (s)':<30} {'avg cores':>10} {'max cores':>10} "
        f"{'Csc (s)':>9} {'Csc n':>6} {'gate (s)':>9} {'wall ratio':>30} {'window ratio':>30}"
    )
    for arm, data in sorted(arms.items()):
        walls = [d["wall_seconds"] for d in data]
        avg = [d["cpu"].get("avg_busy_cores", 0) or 0 for d in data]
        peak = [d["cpu"].get("max_busy_cores", 0) or 0 for d in data]
        csc_ms = [
            d.get("tasks", {}).get("Csc", {}).get("total_ms", 0) or 0 for d in data
        ]
        csc_n = [
            d.get("tasks", {}).get("Csc", {}).get("calls", 0) or 0 for d in data
        ]
        gate_ms = [
            d.get("tasks", {}).get("Exec", {}).get("total_ms", 0) or 0 for d in data
        ]
        ratios = [w / baseline_median for w in walls] if baseline_median else []
        windows = [w for w in (compile_window(d) for d in data) if w is not None]
        window_ratios = (
            [w / baseline_window_median for w in windows]
            if baseline_window_median
            else []
        )
        print(
            f"{arm:<12} {len(data):>2} {fmt_range(walls):<30} "
            f"{statistics.mean(avg):>10.2f} {statistics.mean(peak):>10.2f} "
            f"{statistics.mean(csc_ms) / 1000:>9.1f} {statistics.mean(csc_n):>6.1f} "
            f"{statistics.mean(gate_ms) / 1000:>9.1f} "
            f"{(fmt_range(ratios) if ratios else 'n/a'):>30} "
            f"{(fmt_range(window_ratios) if window_ratios else 'n/a'):>30}"
        )
        normalized = [
            w for w in (normalized_wall(d) for d in data) if w is not None
        ]
        if normalized and baseline_median:
            norm_ratios = [w / baseline_median for w in normalized]
            print(
                f"{'':<12} {'':>2} speed-normalized wall ratio: "
                f"{fmt_range(norm_ratios)}"
            )

    print()
    for label, data in sorted(runs.items()):
        pf = data.get("projects_in_flight", {})
        if pf.get("available"):
            print(
                f"{label}: projects in flight — avg {pf.get('avg_in_flight')}, "
                f"max {pf.get('max_in_flight')}, "
                f">=4 for {pf.get('fraction_at_or_above_4')} of the build "
                f"({pf.get('projects_started')} started, {pf.get('projects_finished')} finished)"
            )
    return 0


if __name__ == "__main__":
    sys.exit(main())
