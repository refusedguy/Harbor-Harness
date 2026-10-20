#!/usr/bin/env python3
"""Run the headless Avalonia E2E suite as N processes, one per shard.

WHY THIS EXISTS
---------------
`tests/Harbor.E2E.App.Avalonia` drives ONE headless Avalonia application:
`HeadlessAvaloniaDriver` keeps `Application.Current`, the desktop lifetime and
the DI host in STATIC fields, and Avalonia permits exactly one `Application`
per AppDomain - there is no second app to build, so a second class cannot have
a window of its own. The classes also share two more process-wide things: the
driver sets `$HOME` (an environment variable is not thread-local), and
`CommonConfig.OverrideHarborHomeForTests` pins the Harbor home for the whole
process.

Inside one process that means the classes must take turns, and the project's
wall time is the SUM of its class durations. Across PROCESSES it does not: a
second process gets its own Avalonia application, its own `$HOME` and its own
Harbor home. So the shard list in `tests/Harbor.E2E.App.Avalonia/e2e-shards.json`
is run as one `dotnet exec` per shard, each with its own `--treenode-filter`.

WHAT THIS SCRIPT GUARDS
-----------------------
A filter that silently drops tests is worse than the serialisation this change
removes: the suite would report green while running fewer tests than it does
today. Three mechanisms, each failing LOUDLY rather than quietly:

  1. PER-PROCESS. Every shard runs with `--minimum-expected-tests 1`, so a
     shard that discovered NOTHING exits non-zero instead of passing.
  2. PER-CLASS. A discovery pass (`--list-tests json`, which runs no test)
     enumerates every test the assembly discovers, keyed by class. Each
     shard's TRX is then compared against that map: a class in the plan whose
     tests are missing from its shard's TRX is a DROPPED CLASS and fails the
     run, naming the class and the shard. This is what catches the trap the
     shard table warns about - `StateTestRunnerTests` lives in the REFERENCED
     `Harbor.E2E.Framework` project, and TUnit runs a referenced test
     project's classes inside every project that references it, so a filter
     that did not name them would drop 26 tests without a word.
  3. IN TOTAL. The sum of the shards' TRX counts is compared against the sum of
     the discovery pass. A shard that lost tests cannot hide behind the others.

A POISONED PROCESS FAILS ITS CLASS, NOT THE SUITE
--------------------------------------------------
Avalonia issue 22021 (github.com/AvaloniaUI/Avalonia/issues/22021, opened
2026-08-19) documents that headless `HeadlessUnitTestSession.Dispatch`
intermittently throws a cross-thread `VerifyAccess` inside
`EnsureSharedApplication`, which POISONS the process: every later dispatch
fails identically, so all remaining tests in that process fail. Upstream
measured 3.3-5.0% of processes hitting it across three OSes over 120 runs each,
and states that "the only recovery we found is a new process".

So this script treats a poisoned shard as what it is - one shard's classes
failed - and says so, loudly, with the upstream reference:

  * the other shards are NOT cancelled: they run to completion and report their
    own results, so one poisoned process does not blind the rest of the suite;
  * the poisoned shard FAILS (exit code non-zero): a poisoned process is not
    retried here, because a silent retry would hide the one failure mode that
    most needs to be visible;
  * the summary names the shard, its classes, and the upstream issue.

Usage:
  ./tools/run-e2e-avalonia-shards.py --dll <path.dll> --results-dir TestResults/e2e
  ./tools/run-e2e-avalonia-shards.py --dll <path.dll> --results-dir out --guard "--minimum-expected-tests 1"

Exit codes: 0 = every shard passed and the totals matched, 1 = a shard failed
or the totals did not match, 2 = bad invocation.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

DEFAULT_PLAN = os.path.join("tests", "Harbor.E2E.App.Avalonia", "e2e-shards.json")

# The upstream defect's signature, as it appears in a shard's output. Matching
# it is what turns "shard-1 failed" into "shard-1 was POISONED by
# Avalonia#22021, and here is the upstream issue".
POISON_SIGNATURES = (
    "EnsureSharedApplication",
    "EnsureIsolatedApplication",
    "The calling thread cannot access this object because a different thread owns it",
)

POISON_REFERENCE = (
    "AvaloniaUI/Avalonia#22021 - headless `HeadlessUnitTestSession.Dispatch` "
    "intermittently throws a cross-thread `VerifyAccess` in "
    "`EnsureSharedApplication`, poisoning the process; upstream states the only "
    "recovery is a new process"
)


def repo_root(start: str) -> str:
    """Walk up from `start` until a `Harbor.slnx` marks the repository root."""
    directory = os.path.abspath(start)
    while True:
        if os.path.isfile(os.path.join(directory, "Harbor.slnx")):
            return directory
        parent = os.path.dirname(directory)
        if parent == directory:
            return os.path.abspath(start)
        directory = parent


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


def filter_for(class_names: list[str]) -> str:
    """The `--treenode-filter` expression that selects exactly these classes.

    The shape is `/*/*/(ClassA)|(ClassB)/*`: the assembly and namespace
    segments are wildcards (the classes live in three namespaces - this one,
    `ComponentTests`, and the referenced `Harbor.E2E.Framework`), the class
    segment is the OR of the requested names, and the test segment is a
    wildcard.
    """
    if not class_names:
        raise ValueError("a shard must name at least one class")
    return "/*/*/(" + ")|(".join(class_names) + ")/*"


def load_plan(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as handle:
        plan = json.load(handle)
    if plan.get("schemaVersion") != 1:
        raise ValueError(f"{path}: unsupported schemaVersion {plan.get('schemaVersion')!r}")
    shards = plan.get("shards") or []
    if not shards:
        raise ValueError(f"{path}: no shards")
    seen: dict[str, str] = {}
    for shard in shards:
        if not shard.get("id"):
            raise ValueError(f"{path}: a shard has no id")
        if not shard.get("classes"):
            raise ValueError(f"{path}: shard {shard['id']} names no classes")
        for name in shard["classes"]:
            if name in seen:
                raise ValueError(
                    f"{path}: class {name} is in both {seen[name]} and {shard['id']}")
            seen[name] = shard["id"]
    return plan


def run_discovery(dll: str) -> tuple[dict[str, int], int, str]:
    """Enumerate every test the assembly discovers, keyed by class name.

    Returns (per-class counts, total, note). `note` is empty when the
    discovery pass worked, and explains itself when it did not - the caller
    reports that note, because a guard that silently degrades is worse than no
    guard.
    """
    command = ["dotnet", "exec", dll, "--list-tests", "json"]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=300)
    except (OSError, subprocess.SubprocessError) as error:
        return ({}, 0, f"discovery pass could not run ({error})")

    if completed.returncode != 0:
        tail = (completed.stderr or completed.stdout or "").strip().splitlines()
        detail = tail[-1] if tail else "no output"
        return ({}, 0,
                f"discovery pass exited {completed.returncode} ({detail}); "
                "the exact per-class check was SKIPPED and the plan's measured "
                "total was used as a floor instead")

    text = completed.stdout or ""
    document = parse_json_document(text)
    if document is None:
        return ({}, 0,
                "discovery pass printed no JSON; the exact per-class check was "
                "SKIPPED and the plan's measured total was used as a floor instead")

    tests = document.get("tests")
    if not isinstance(tests, list):
        return ({}, 0,
                "discovery pass printed JSON without a `tests` array; the exact "
                "per-class check was SKIPPED and the plan's measured total was "
                "used as a floor instead")

    counts: dict[str, int] = {}
    for entry in tests:
        if not isinstance(entry, dict):
            continue
        name = class_of(entry)
        if name:
            counts[name] = counts.get(name, 0) + 1
    return (counts, len(tests), "")


def parse_json_document(text: str) -> dict | None:
    """Parse the discovery output, tolerating a banner in front of the JSON."""
    text = text.strip()
    if not text:
        return None
    try:
        document = json.loads(text)
        return document if isinstance(document, dict) else None
    except json.JSONDecodeError:
        pass
    start = text.find("{")
    while start >= 0:
        try:
            document = json.loads(text[start:])
            return document if isinstance(document, dict) else None
        except json.JSONDecodeError:
            start = text.find("{", start + 1)
    return None


def class_of(entry: dict) -> str:
    """The class a discovered test belongs to.

    `type` carries the namespace and type name when the host asked for the
    full identifier. The `uid` is the fallback: it is the test's tree path,
    whose class segment is the third one.
    """
    identifier = entry.get("type")
    if isinstance(identifier, dict):
        type_name = identifier.get("typeName")
        if isinstance(type_name, str) and type_name:
            return type_name.rsplit(".", 1)[-1]
    uid = entry.get("uid")
    if isinstance(uid, str):
        segments = [segment for segment in uid.split("/") if segment]
        if len(segments) >= 3:
            return segments[2]
    return ""


def read_trx(path: str) -> tuple[dict[str, int], int, int, int]:
    """Map class name -> test count, plus (total, failed, not-executed)."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return ({}, 0, 0, 0)

    methods: dict[str, str] = {}
    for unit_test in root.iter(TRX_NS + "UnitTest"):
        method = unit_test.find(TRX_NS + "TestMethod")
        if method is None:
            continue
        class_name = method.get("className") or ""
        methods[unit_test.get("id", "")] = class_name.rsplit(".", 1)[-1]

    counts: dict[str, int] = {}
    total = failed = not_executed = 0
    for result in root.iter(TRX_NS + "UnitTestResult"):
        class_name = methods.get(result.get("testId", ""), "?")
        counts[class_name] = counts.get(class_name, 0) + 1
        total += 1
        outcome = (result.get("outcome") or "").lower()
        if outcome in ("failed", "error", "timeout", "aborted"):
            failed += 1
        elif outcome == "notexecuted":
            not_executed += 1
    return (counts, total, failed, not_executed)


def run_shard(shard: dict, dll: str, results_dir: str, guard: list[str]) -> dict:
    """Run one shard in its own process and collect what it did."""
    shard_id = shard["id"]
    shard_dir = os.path.join(results_dir, shard_id)
    screenshot_dir = os.path.join(shard_dir, "screenshots")
    os.makedirs(shard_dir, exist_ok=True)
    os.makedirs(screenshot_dir, exist_ok=True)

    expression = filter_for(list(shard["classes"]))
    command = [
        "dotnet", "exec", dll,
        "--treenode-filter", expression,
        *guard,
        # The same zero-test policy the `test` job gives every other project:
        # a shard that discovered nothing must fail, not pass.
        "--zero-tests-policy", "strict",
        "--report-trx",
        "--results-directory", shard_dir,
        "--report-trx-filename", f"{shard_id}.trx",
    ]

    environment = dict(os.environ)
    # Each shard writes its screenshots into its OWN directory. Two processes
    # sharing one directory is a race: `AvaloniaUiTests` deletes the stale
    # `??-*.png` files at init, so one shard's init would delete the frames
    # another shard had just written.
    environment["HARBOR_E2E_SHARD_DIR"] = screenshot_dir
    environment["HARBOR_E2E_SHARD_ID"] = shard_id

    log_path = os.path.join(shard_dir, "shard.log")
    started = time.monotonic()
    with open(log_path, "w", encoding="utf-8") as log:
        log.write(f"# {shard_id}\n# filter: {expression}\n")
        log.flush()
        completed = subprocess.run(
            command, stdout=log, stderr=subprocess.STDOUT, env=environment)
    elapsed = time.monotonic() - started

    trx_path = os.path.join(shard_dir, f"{shard_id}.trx")
    counts, total, failed, not_executed = read_trx(trx_path)

    output = ""
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as handle:
            output = handle.read()
    except OSError:
        pass

    poisoned = any(signature in output for signature in POISON_SIGNATURES)

    return {
        "id": shard_id,
        "classes": list(shard["classes"]),
        "filter": expression,
        "exitCode": completed.returncode,
        "seconds": elapsed,
        "tests": total,
        "failed": failed,
        "notExecuted": not_executed,
        "counts": counts,
        "trx": trx_path if os.path.isfile(trx_path) else "",
        "log": log_path,
        "poisoned": poisoned,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dll", required=True, help="the test assembly to run")
    parser.add_argument("--results-dir", required=True,
                        help="where the per-shard TRX files and logs go")
    parser.add_argument("--plan", default="", help="the shard plan (default: the repo's)")
    parser.add_argument("--guard", default="",
                        help="extra MTP guard arguments, e.g. '--minimum-expected-tests 1'")
    parser.add_argument("--summary", default=os.environ.get("GITHUB_STEP_SUMMARY", ""),
                        help="the job summary file to append a table to")
    args = parser.parse_args()

    root = repo_root(os.getcwd())
    plan_path = args.plan or os.path.join(root, DEFAULT_PLAN)
    if not os.path.isabs(plan_path):
        plan_path = os.path.join(root, plan_path)

    if not os.path.isfile(args.dll):
        print(f"run-e2e-avalonia-shards.py: no assembly at {args.dll}", file=sys.stderr)
        return 2
    if not os.path.isfile(plan_path):
        print(f"run-e2e-avalonia-shards.py: no plan at {plan_path}", file=sys.stderr)
        return 2

    try:
        plan = load_plan(plan_path)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"run-e2e-avalonia-shards.py: {error}", file=sys.stderr)
        return 2

    guard = args.guard.split() if args.guard else []
    # The results directory must be ABSOLUTE: the test platform resolves a
    # relative --results-directory against the ASSEMBLY's directory, not the
    # working directory, so a relative path puts the TRX where the caller
    # cannot find it (and where the CI's upload glob cannot see it).
    results_dir = os.path.abspath(args.results_dir)
    os.makedirs(results_dir, exist_ok=True)

    discovered_counts, discovered_total, discovery_note = run_discovery(args.dll)
    if discovery_note:
        print(f"::warning::{discovery_note}")

    shards = plan["shards"]
    with ThreadPoolExecutor(max_workers=len(shards)) as pool:
        results = list(pool.map(
            lambda shard: run_shard(shard, args.dll, results_dir, guard),
            shards))

    total_tests = sum(result["tests"] for result in results)
    total_failed = sum(result["failed"] for result in results)
    failed_shards = [result for result in results if result["exitCode"] != 0]

    # ── The guards ────────────────────────────────────────────────────────
    problems: list[str] = []

    for result in results:
        if result["exitCode"] != 0:
            problems.append(
                f"shard {result['id']} exited {result['exitCode']} "
                f"({result['failed']} failed, {result['tests']} test(s)); "
                f"classes: {', '.join(result['classes'])}; log: {result['log']}")
        if result["poisoned"]:
            problems.append(
                f"shard {result['id']} was POISONED by the upstream headless "
                f"dispatcher defect - {POISON_REFERENCE}. Its classes "
                f"({', '.join(result['classes'])}) failed with it; the other "
                f"shards were not cancelled. Log: {result['log']}")

    if discovery_note:
        # The exact check is unavailable; fall back to the plan's measured
        # total as a FLOOR so a dropped shard is still caught.
        floor = int(plan.get("totals", {}).get("measuredTests", 0))
        if floor and total_tests < floor:
            problems.append(
                f"the shards ran {total_tests} test(s) but the plan measured "
                f"{floor} - at least one class was DROPPED. Note: {discovery_note}")
    else:
        if total_tests != discovered_total:
            problems.append(
                f"the shards ran {total_tests} test(s) but the assembly "
                f"discovers {discovered_total} - at least one class was DROPPED")
        for result in results:
            for name in result["classes"]:
                expected = discovered_counts.get(name)
                if expected is None:
                    problems.append(
                        f"class {name} is in shard {result['id']} but the "
                        f"assembly discovers no test for it")
                    continue
                actual = result["counts"].get(name, 0)
                if actual != expected:
                    problems.append(
                        f"class {name} ran {actual} test(s) in shard "
                        f"{result['id']} but the assembly discovers {expected} - "
                        f"the shard's filter does not match what it claims")

    # ── The report ────────────────────────────────────────────────────────
    lines = [
        "| shard | classes | tests | failed | skipped | seconds | result |",
        "|---|---|--:|--:|--:|--:|---|",
    ]
    for result in results:
        status = "pass"
        if result["exitCode"] != 0:
            status = "FAIL"
        if result["poisoned"]:
            status += " (poisoned - AvaloniaUI/Avalonia#22021)"
        lines.append(
            f"| {result['id']} | {len(result['classes'])} | {result['tests']} | "
            f"{result['failed']} | {result['notExecuted']} | {result['seconds']:.1f} | "
            f"{status} |")
    lines.append(
        f"| **total** | {sum(len(r['classes']) for r in results)} | {total_tests} | "
        f"{total_failed} | {sum(r['notExecuted'] for r in results)} | "
        f"{max(r['seconds'] for r in results):.1f} (critical path) | |")
    if discovered_total:
        lines.append("")
        lines.append(
            f"Discovered by the assembly: **{discovered_total}** test(s); "
            f"run across {len(results)} process(es). "
            f"{discovery_note or 'The per-class and total counts match.'}")
    else:
        lines.append("")
        lines.append(f"Discovery pass unavailable: {discovery_note}")

    report = "\n".join(lines)
    print(report)
    if args.summary:
        try:
            with open(args.summary, "a", encoding="utf-8") as handle:
                handle.write(report + "\n\n")
        except OSError as error:
            print(f"run-e2e-avalonia-shards.py: cannot write the summary ({error})",
                  file=sys.stderr)

    if problems:
        print("")
        for problem in problems:
            print(f"::error::{problem}")
        # The shards' own output goes to their log files, not to the job log,
        # so a failure would otherwise be invisible here. Print the whole log
        # of every shard that failed: the CI log is where a human looks first,
        # and the artifact is where they look second.
        for result in results:
            if result["exitCode"] == 0:
                continue
            print("")
            print(f"::group::{result['id']} output ({result['log']})")
            try:
                with open(result["log"], "r", encoding="utf-8", errors="replace") as handle:
                    print(handle.read())
            except OSError as error:
                print(f"(cannot read {result['log']}: {error})")
            print("::endgroup::")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
