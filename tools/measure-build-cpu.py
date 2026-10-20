#!/usr/bin/env python3
"""measure-build-cpu.py — run a build command while measuring what it costs.

WHY THIS EXISTS
---------------
Issue #1929 asks a falsifiable question: is the CI build limited by the
runner's 4 vCPU, or by the shape of the project dependency graph? The
proposed falsification test is "build the same commit with one compiler at a
time and compare the wall time".

A wall time alone cannot answer that, because a build has BOTH a parallel
part (the compiler work) and a serial part (restore, the architecture gate,
per-project fixed overhead). What can answer it is the pair

    wall time  +  CPU utilisation (busy cores over the build window)

because "four cores were busy the whole time" and "one core was busy the
whole time" is a measurement, while "the graph looked wide" is a model.

THIS SCRIPT MEASURES, PER RUN
-----------------------------
* wall time of the command (the number the issue's ratio is built on),
* CPU utilisation sampled from /proc/stat once a second: average / peak busy
  cores, and how long the machine sat at 4 busy cores,
* MSBuild's own task aggregates, parsed out of the console log's Performance
  Summary: the count and the total duration of the `Csc` tasks (the compiler
  work), of `Exec` (the architecture gate) and of the other heavy tasks,
* the project timeline: which project started when, and which finished when,
  from which the number of projects in flight over time is derived — i.e. the
  "was the graph ever able to keep 4 compilers busy?" question, measured
  instead of modelled. The timeline needs normal verbosity (MSBuild only
  prints the "is building" line there); at minimal verbosity only the
  completion side is available.

It is a MEASUREMENT tool: it does not gate, it does not touch the build, and
it works on any command. Standard library only.

USAGE
-----
    python3 tools/measure-build-cpu.py --label baseline --out-dir meas -- \
        dotnet msbuild Harbor.slnx -t:Build -p:Configuration=Release

OUTPUTS (in --out-dir)
----------------------
    <label>.log   every line of the command's output, prefixed with the
                  elapsed seconds since the command started
    <label>.json  the measurement: wall time, CPU samples and their summary,
                  the MSBuild task aggregates, the project timeline, and the
                  "projects in flight" profile
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
import traceback

# "  Harbor.Abstractions -> /home/runner/work/.../Harbor.Abstractions.dll"
# — printed when the project is BUILT, i.e. a completion marker.
_ARROW_RE = re.compile(r"^\s*(?P<name>\S+)\s+->\s+(?P<out>\S+\.(?:dll|exe))\s*$")

# '  1>Project "/…/Harbor.slnx" (1) is building "/…/x.csproj" (2) with 3 targets.'
# — the project START marker. Normal verbosity only.
_IS_BUILDING_RE = re.compile(
    r'^\s*\d+>Project\s+"[^"]*"\s+\(\d+\)\s+is building\s+"(?P<proj>[^"]+)"'
)

# '  2>Done Building Project "/…/x.csproj" (default targets).'
_DONE_RE = re.compile(r'^\s*\d+>Done Building Project\s+"(?P<proj>[^"]+)"')

# Performance Summary rows: "    434717 ms  Csc     112 calls"
_PERF_ROW_RE = re.compile(
    r"^\s*(?P<ms>\d+)\s+ms\s+(?P<task>\S+)\s+(?P<calls>\d+)\s+calls\s*$"
)


def read_cpu_times() -> tuple[int, int]:
    """Return (total_jiffies, busy_jiffies) from /proc/stat.

    Busy excludes idle AND iowait: a process blocked on I/O is not using a
    core, and counting iowait as busy would overstate how parallel the build
    was — which is exactly the number this script exists to measure.
    """
    with open("/proc/stat", "r", encoding="ascii") as handle:
        fields = handle.readline().split()
    values = [int(v) for v in fields[1:]]
    total = sum(values)
    idle = values[3]
    iowait = values[4] if len(values) > 4 else 0
    return total, total - idle - iowait


def cpu_model() -> str:
    try:
        with open("/proc/cpuinfo", "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if line.lower().startswith("model name"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return "unknown"


def stat_cpu_count() -> int:
    """How many CPUs /proc/stat accounts for.

    This is NOT necessarily os.cpu_count(): a sandbox can expose four
    schedulable CPUs while /proc/stat only carries the CPUs of the cgroup
    the job runs in. The number of busy CORES is derived from the jiffy
    deltas of /proc/stat, so the divisor has to be the one /proc/stat uses —
    otherwise "the machine is 99 % busy" and "one core is busy" look
    identical, which is the whole question this script exists to answer.
    """
    try:
        with open("/proc/stat", "r", encoding="ascii") as handle:
            lines = handle.readlines()
    except OSError:
        return 0
    count = sum(1 for line in lines if line.startswith("cpu") and not line.startswith("cpu "))
    return count


def read_process_cpu() -> dict[str, int]:
    """Total CPU jiffies per process name (comm).

    The machine-level number says HOW MUCH of the CPU was used; this says
    WHO used it — the compiler server, the MSBuild nodes, the architecture
    test process. Without it, "the build used three cores" cannot be told
    apart from "the build used one core and something else used two".
    """
    totals: dict[str, int] = {}
    try:
        entries = os.listdir("/proc")
    except OSError:
        return totals
    for entry in entries:
        if not entry.isdigit():
            continue
        try:
            with open(f"/proc/{entry}/stat", "r", encoding="utf-8", errors="replace") as fh:
                data = fh.read()
            # comm is parenthesised and may contain spaces, so the fields
            # after it start at the last ')'.
            open_paren = data.find("(")
            close_paren = data.rfind(")")
            if open_paren < 0 or close_paren < open_paren:
                continue
            comm = data[open_paren + 1 : close_paren]
            fields = data[close_paren + 2 :].split()
            # fields[0] is state; utime is field 14 and stime field 15 of the
            # whole stat line, i.e. index 11 and 12 after the state.
            totals[comm] = totals.get(comm, 0) + int(fields[11]) + int(fields[12])
        except (OSError, ValueError, IndexError):
            continue
    return totals


class CpuSampler(threading.Thread):
    """Samples /proc/stat on a fixed interval and keeps the whole series."""

    def __init__(self, interval: float, ncpu: int) -> None:
        super().__init__(daemon=True)
        self.interval = interval
        self.ncpu = max(1, ncpu)
        self.samples: list[tuple[float, float]] = []  # (elapsed_s, busy_cores)
        self.process_samples: list[dict[str, float]] = []
        # NOTE: the attribute names below are deliberately NOT `_stop`/`_start`:
        # threading.Thread owns private methods with those names, and shadowing
        # them breaks Thread.join() with "TypeError: 'Event' object is not
        # callable".
        self._stop_event = threading.Event()
        self._started_at = 0.0
        self._prev_total, self._prev_busy = read_cpu_times()
        self._prev_process = read_process_cpu()

    def run(self) -> None:
        self._started_at = time.monotonic()
        previous_time = self._started_at
        previous_process = self._prev_process
        while not self._stop_event.wait(self.interval):
            now = time.monotonic()
            total, busy = read_cpu_times()
            delta_total = total - self._prev_total
            delta_busy = busy - self._prev_busy
            elapsed = now - previous_time
            # delta_total counts jiffies on EVERY cpu /proc/stat knows about,
            # so delta_busy/delta_total is the FRACTION of the machine that is
            # busy. The number of busy CORES is that fraction times the number
            # of cpus /proc/stat accounts for — the difference between "the
            # machine is 99 % busy" (4 of 4 cores) and "one core is busy"
            # (25 % of a 4-cpu box), which is the whole question.
            cores = (
                (delta_busy / delta_total) * self.ncpu if delta_total > 0 else 0.0
            )
            self.samples.append((now - self._started_at, round(cores, 3)))

            current_process = read_process_cpu()
            breakdown: dict[str, float] = {}
            for name, jiffies in current_process.items():
                delta = jiffies - previous_process.get(name, jiffies)
                if delta > 0:
                    breakdown[name] = round(delta / 100.0, 2)  # jiffies -> seconds
            self.process_samples.append(
                {"t": round(now - self._started_at, 3), "cpu_seconds": breakdown}
            )
            self._prev_total, self._prev_busy = total, busy
            previous_process = current_process
            previous_time = now

    def stop_sampling(self) -> None:
        self._stop_event.set()


def process_cpu_totals(samples: list[dict]) -> dict[str, float]:
    """Sum the per-process CPU seconds over the whole run."""
    totals: dict[str, float] = {}
    for sample in samples:
        for name, seconds in sample.get("cpu_seconds", {}).items():
            totals[name] = totals.get(name, 0.0) + seconds
    return {k: round(v, 1) for k, v in sorted(totals.items(), key=lambda kv: -kv[1])}


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(fraction * (len(ordered) - 1)))))
    return ordered[index]


def summarize_cpu(samples: list[tuple[float, float]], ncpu: int) -> dict:
    cores = [c for _, c in samples]
    if not cores:
        return {"samples": 0}
    return {
        "samples": len(cores),
        "avg_busy_cores": round(sum(cores) / len(cores), 3),
        "p50_busy_cores": round(percentile(cores, 0.50), 3),
        "p95_busy_cores": round(percentile(cores, 0.95), 3),
        "max_busy_cores": round(max(cores), 3),
        "fraction_at_or_above_3_5_cores": round(
            sum(1 for c in cores if c >= 3.5) / len(cores), 4
        ),
        "fraction_below_1_5_cores": round(
            sum(1 for c in cores if c < 1.5) / len(cores), 4
        ),
        "ncpu": ncpu,
    }


def project_key(path_or_name: str) -> str:
    """Normalise a project path or name to a comparable key."""
    base = path_or_name.replace("\\", "/").rstrip("/").split("/")[-1]
    if base.endswith(".csproj"):
        base = base[: -len(".csproj")]
    return base


def parse_log(log_path: str) -> tuple[list[dict], dict, float]:
    """Recover the timeline, the task aggregates and the last output stamp."""
    events: list[dict] = []
    tasks: dict[str, dict] = {}
    last_stamp = 0.0
    try:
        handle = open(log_path, "r", encoding="utf-8", errors="replace")
    except OSError:
        return events, tasks, last_stamp
    with handle:
        for line in handle:
            head, _, rest = line.partition("|")
            try:
                stamp = float(head.strip())
            except ValueError:
                continue
            last_stamp = max(last_stamp, stamp)
            match = _ARROW_RE.match(rest)
            if match:
                events.append(
                    {
                        "t": stamp,
                        "kind": "done",
                        "project": project_key(match.group("name")),
                        "output": match.group("out"),
                    }
                )
                continue
            match = _IS_BUILDING_RE.match(rest)
            if match:
                events.append(
                    {"t": stamp, "kind": "start", "project": project_key(match.group("proj"))}
                )
                continue
            match = _DONE_RE.match(rest)
            if match:
                events.append(
                    {"t": stamp, "kind": "done", "project": project_key(match.group("proj"))}
                )
                continue
            match = _PERF_ROW_RE.match(rest)
            if match:
                name = match.group("task")
                tasks[name] = {
                    "total_ms": int(match.group("ms")),
                    "calls": int(match.group("calls")),
                }
    return events, tasks, last_stamp


def in_flight_profile(events: list[dict], wall: float) -> dict:
    """How many projects were in flight over time, and how often.

    This is the issue's "95 % of the time >= 4 projects in flight" metric,
    measured: a project is in flight from its start event to its done event.
    """
    starts = sorted((e["t"], e["project"]) for e in events if e["kind"] == "start")
    dones = sorted((e["t"], e["project"]) for e in events if e["kind"] == "done")
    profile: dict = {
        "available": False,
        "projects_started": len(starts),
        "projects_finished": len(dones),
    }
    if not starts:
        return profile

    grid = 0.0
    step = 1.0
    inflight_at: list[tuple[float, int]] = []
    si = di = 0
    open_projects: set[str] = set()
    while grid <= wall:
        while si < len(starts) and starts[si][0] <= grid:
            open_projects.add(starts[si][1])
            si += 1
        while di < len(dones) and dones[di][0] <= grid:
            open_projects.discard(dones[di][1])
            di += 1
        inflight_at.append((grid, len(open_projects)))
        grid += step

    if not inflight_at:
        return profile

    counts = [c for _, c in inflight_at]
    profile["available"] = True
    profile["grid_seconds"] = step
    profile["avg_in_flight"] = round(sum(counts) / len(counts), 3)
    profile["max_in_flight"] = max(counts)
    for threshold in (1, 2, 3, 4, 6, 8, 12, 16, 24):
        profile[f"fraction_at_or_above_{threshold}"] = round(
            sum(1 for c in counts if c >= threshold) / len(counts), 4
        )
    return profile


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True, help="name of this run")
    parser.add_argument("--out-dir", required=True, help="directory for the outputs")
    parser.add_argument(
        "--interval",
        type=float,
        default=1.0,
        help="CPU sampling interval in seconds (default: 1.0)",
    )
    parser.add_argument(
        "command", nargs=argparse.REMAINDER, help="the command to run (after --)"
    )
    args = parser.parse_args()

    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        parser.error("no command given")

    os.makedirs(args.out_dir, exist_ok=True)
    log_path = os.path.join(args.out_dir, f"{args.label}.log")
    json_path = os.path.join(args.out_dir, f"{args.label}.json")

    ncpu = os.cpu_count() or 0
    ncpu_stat = stat_cpu_count() or ncpu
    sampler = CpuSampler(args.interval, ncpu_stat)
    sampler.start()

    started = time.monotonic()
    exit_code = 0
    with open(log_path, "w", encoding="utf-8", errors="replace") as log:
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        assert process.stdout is not None
        for line in process.stdout:
            log.write(f"{time.monotonic() - started:.3f} | {line}")
            log.flush()
        exit_code = process.wait()
    wall = time.monotonic() - started
    sampler.stop_sampling()
    sampler.join(timeout=5.0)

    result = {
        "label": args.label,
        "command": command,
        "exit_code": exit_code,
        "wall_seconds": round(wall, 3),
        "ncpu": ncpu,
        "ncpu_stat": ncpu_stat,
        "cpu_model": cpu_model(),
        "cpu": summarize_cpu(sampler.samples, ncpu_stat),
        "cpu_samples": [[round(t, 3), c] for t, c in sampler.samples],
        "process_cpu_seconds": process_cpu_totals(sampler.process_samples),
    }

    # The measurement above is the point of the run: write it BEFORE the
    # analysis, so a failure in the analysis cannot lose the numbers.
    with open(json_path, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)

    try:
        events, tasks, last_stamp = parse_log(log_path)
        result["last_output_seconds"] = round(last_stamp, 3)
        result["tasks"] = tasks
        result["projects_in_flight"] = in_flight_profile(events, wall)
        result["timeline"] = events
    except Exception:  # noqa: BLE001 — the measurement above already survived
        result["analysis_error"] = traceback.format_exc()

    with open(json_path, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)

    cpu = result["cpu"]
    tasks = result.get("tasks", {})
    csc = tasks.get("Csc", {})
    print(
        f"[measure-build-cpu] label={args.label} exit={exit_code} "
        f"wall={wall:.1f}s avg_cores={cpu.get('avg_busy_cores')} "
        f"max_cores={cpu.get('max_busy_cores')} "
        f"at4={cpu.get('fraction_at_or_above_3_5_cores')} "
        f"csc_calls={csc.get('calls')} csc_ms={csc.get('total_ms')} -> {json_path}"
    )
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
