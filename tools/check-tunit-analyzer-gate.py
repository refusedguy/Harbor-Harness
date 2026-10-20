#!/usr/bin/env python3
"""The control for the TUnit analyzer gate (#1836): the analyzer must report a
planted offender, and a waiver must suppress exactly its own site.

WHAT THIS TOOL IS
-----------------
  TUnit 1.75 ships its own compiler analyzers inside its NuGet packages
  (TUnit.Analyzers in TUnit.Core, TUnit.Assertions.Analyzers in
  TUnit.Assertions), and tests/Directory.Build.props promotes two of their
  diagnostics — TUnitAssertions0005 and TUnitAssertions0015 — to errors for
  every test project. A promotion nobody watches is a decoration: if the
  analyzer stopped shipping, or the promotion stopped applying, the build
  would go quietly green and the gate would protect nothing.

  So the gate is proved by a PLANTED OFFENDER. tests/TUnitAnalyzerCanary is a
  project that is deliberately NOT in Harbor.slnx and carries two offences:

    * one that the analyzer must report (the build of that project must
      FAIL, with TUnitAssertions0005, at that exact line);
    * one that a scoped waiver suppresses (the build must NOT mention it).

  This tool builds the canary and asserts exactly that outcome. It is a
  control, not a report: it exits non-zero unless it saw the one diagnostic
  it planted.

  A tool that checks nothing is green forever, so the failure paths are the
  point:

    * the build SUCCEEDED            -> the planted offender was not an error
                                         -> the gate is not armed -> FAIL
    * the build failed for another reason -> the control did not run
                                         -> FAIL, loudly
    * the diagnostic is at another line -> the waiver or the fixture drifted
                                         -> FAIL
    * the waived site was reported    -> a waiver grew past its own site
                                         -> FAIL
    * more than one diagnostic        -> the canary is not the fixture it
                                         claims to be -> FAIL

  `--selftest` runs the same analysis over synthetic logs that each violate
  one of those rules, and requires the analysis to REJECT every one of them.
  A parser that cannot fail is not a parser.

Stdlib only — no pip, no network. It shells out to `dotnet build` because
that is the only thing that can observe what the analyzer does.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

CANARY_PROJECT = os.path.join("tests", "TUnitAnalyzerCanary", "TUnitAnalyzerCanary.csproj")
CANARY_SOURCE = os.path.join("tests", "TUnitAnalyzerCanary", "PlantedOffender.cs")

PLANTED_METHOD = "Planted_Offender_MustBeReported"
WAIVED_METHOD = "Waived_Offender_MustNotBeReported"

# A compiler diagnostic line, e.g.
#   /repo/tests/TUnitAnalyzerCanary/PlantedOffender.cs(31,9): error TUnitAssertions0005: Assert.That(...) should not be used with a constant value [/repo/...csproj]
DIAGNOSTIC_RE = re.compile(
    r"^(?P<file>[^\r\n(]+)\((?P<line>\d+),(?P<col>\d+)\):\s+"
    r"(?P<severity>error|warning)\s+"
    r"(?P<id>TUnit[A-Za-z]*\d+):\s*(?P<message>.*?)\s*$",
    re.M,
)


def read(path: str) -> str:
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def method_line(source: str, method: str) -> int:
    """The 1-based line of the Assert.That(...) inside the named method.

    The method is found by its DECLARATION, not by its name: the canary's
    doc comments mention both method names, and a search that stopped at the
    first mention would point the tool at a comment.
    """
    lines = source.splitlines()
    start = None
    for index, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith(("public ", "private ", "internal ", "protected ")) \
                and f" {method}(" in stripped:
            start = index
            break
    if start is None:
        raise SystemExit(f"canary source has no method named {method}")
    for index in range(start, len(lines)):
        if "Assert.That(" in lines[index]:
            return index + 1
    raise SystemExit(f"method {method} has no Assert.That(...) call")


def analyse(log: str, planted_line: int, waived_line: int) -> list[str]:
    """The problems with one build log. An empty list means the control held."""
    problems: list[str] = []

    diagnostics = [
        m for m in DIAGNOSTIC_RE.finditer(log)
        if m.group("id") == "TUnitAssertions0005"
    ]

    if not diagnostics:
        problems.append(
            "the analyzer reported no TUnitAssertions0005 at all: either the "
            "analyzer stopped shipping, or the promotion to an error stopped "
            "applying, or the planted offender stopped being one. The gate is "
            "not armed."
        )
        return problems

    for match in diagnostics:
        line = int(match.group("line"))
        if line == waived_line:
            problems.append(
                f"the waived site (line {waived_line}) was reported: the "
                f"suppression did not cover its own site and nothing else."
            )
        elif line != planted_line:
            problems.append(
                f"a TUnitAssertions0005 was reported at line {line}, which is "
                f"neither the planted offender (line {planted_line}) nor the "
                f"waived one (line {waived_line})."
            )

    if len(diagnostics) > 1:
        problems.append(
            f"{len(diagnostics)} TUnitAssertions0005 diagnostics were reported; "
            f"the canary plants exactly one."
        )

    return problems


def build_canary(configuration: str) -> tuple[int, str]:
    """Build the canary. Returns (exit code, combined output)."""
    command = [
        "dotnet", "build", CANARY_PROJECT,
        "-c", configuration,
        "--nologo",
        "-v:m",
    ]
    completed = subprocess.run(
        command,
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    return completed.returncode, (completed.stdout or "") + (completed.stderr or "")


def selftest() -> int:
    """The analysis must REJECT every synthetic log that breaks a rule."""
    source = read(os.path.join(REPO_ROOT, CANARY_SOURCE))
    planted = method_line(source, PLANTED_METHOD)
    waived = method_line(source, WAIVED_METHOD)

    good = (
        f"/repo/{CANARY_SOURCE}({planted},9): error TUnitAssertions0005: "
        f"Assert.That(...) should not be used with a constant value "
        f"[/repo/{CANARY_PROJECT}]\n"
    )
    cases = [
        ("the clean log must pass", good, True),
        ("a log with no diagnostic must be rejected", "", False),
        (
            "a log with the waived site reported must be rejected",
            good + f"/repo/{CANARY_SOURCE}({waived},9): error TUnitAssertions0005: x\n",
            False,
        ),
        (
            "a log with the diagnostic on another line must be rejected",
            f"/repo/{CANARY_SOURCE}({waived + 40},9): error TUnitAssertions0005: x\n",
            False,
        ),
        (
            "a log with two diagnostics must be rejected",
            good + f"/repo/{CANARY_SOURCE}({waived + 40},9): error TUnitAssertions0005: x\n",
            False,
        ),
    ]

    failures = 0
    for name, log, should_pass in cases:
        problems = analyse(log, planted, waived)
        passed = not problems
        if passed != should_pass:
            failures += 1
            print(f"FAIL selftest: {name} (expected "
                  f"{'pass' if should_pass else 'failure'}, got "
                  f"{'pass' if passed else 'failure'})")
            for problem in problems:
                print(f"       {problem}")
        else:
            print(f"ok   selftest: {name}")

    if failures:
        print(f"\n{failures} selftest case(s) failed — the analysis is not trustworthy")
        return 1
    print("\nOK: the analysis rejects every broken log it was shown")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--selftest",
        action="store_true",
        help="run the analysis over synthetic broken logs and require it to reject them",
    )
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()

    if args.selftest:
        return selftest()

    source = read(os.path.join(REPO_ROOT, CANARY_SOURCE))
    planted = method_line(source, PLANTED_METHOD)
    waived = method_line(source, WAIVED_METHOD)

    print(f"building the canary: {CANARY_PROJECT}")
    print(f"  planted offender: line {planted}")
    print(f"  waived offender:  line {waived}")

    code, log = build_canary(args.configuration)

    if code == 0:
        print()
        print("FAIL the canary build SUCCEEDED.")
        print("     The planted offender was not an error, so the gate is not armed:")
        print("     either TUnit's analyzer stopped shipping, or the promotion to")
        print("     an error in tests/Directory.Build.props stopped applying.")
        print("     (this is the failure mode a control exists to catch)")
        return 1

    problems = analyse(log, planted, waived)
    if problems:
        print()
        print("FAIL the canary build did not produce the expected diagnostic.")
        for problem in problems:
            print(f"  - {problem}")
        print()
        print("build output (last 60 lines):")
        for line in log.splitlines()[-60:]:
            print(f"  {line}")
        return 1

    print()
    print("OK: the analyzer reported the planted offender as an error, and the")
    print("    waiver suppressed exactly its own site.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
