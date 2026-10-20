#!/usr/bin/env python3
"""Gate the architecture suite's execution count on the pull-request lane.

Stdlib only - no pip, no network, no dotnet. Wired into the `build` job of
.github/workflows/ci.yml, and it guards ONE thing:

    on a pull request, the architecture suite runs EXACTLY ONCE.

WHAT THE SUITE IS
-----------------
`tests/Harbor.Architecture.Tests` is the rule tree that holds the layering
rules. `Directory.Build.props` runs the whole assembly as an MSBuild target
(`HarborArchitectureGate`, `AfterTargets="Build"`), so a Release compile of
the solution executes the suite inside the build.

WHY THE COUNT IS A QUESTION
---------------------------
Issue #1920 measured THREE executions of the same suite on one pull request,
on the baseline run 39389070999:

    68.0 s   inside the build job (`dotnet build` runs the gate)
    65.9 s   in the `core` test shard
    63.3 s   again in the `coverage` job

197.2 s of CPU for one answer. #1928 switched the build-time gate off on the
pull-request lane and #1923 moved the coverage collection to the nightly
lane, so the suite runs once now, in the `core` shard, and the pull-request
lane pays 59.9 s for it (measured on run 39508977362: 1094 tests, 59.9 s).

WHAT THIS FILE GUARDS, AND WHY SOMETHING ELSE WAS NOT ENOUGH
--------------------------------------------------------------
Nothing held that count. The build-time gate is one environment variable
(`HarborArchGate: ${{ github.event_name == 'pull_request' && 'false' || 'true' }}`),
and a coverage-like job on the pull-request lane is one block of YAML. Both
are easy to bring back by accident, and nothing else in the repository would
say so: the suite would run twice, slowly, and the pull request would still
be green.

So this gate reads the workflow files and asks five questions.

  EXEC-ONCE      the suite is executed exactly once on the pull-request
                 lane, by the `core` shard's project list, and by nothing
                 else. Zero is a finding too: a lane that runs the suite
                 nowhere has no architecture gate at all.
  GATE-OFF-PR    every job that compiles the solution switches the
                 build-time gate off for `pull_request` events, either with
                 `-p:HarborArchGate=false` on the command line or with a
                 job-level `HarborArchGate` environment variable whose
                 expression yields `'false'` for that event.
  GATE-ON-MERGE  at least one of those jobs still runs the gate for a
                 `push` event, so a merge into dev/master keeps failing the
                 BUILD on a layering regression. The gate is worth keeping;
                 it is the DUPLICATION that costs.
  GATE-DEFAULT   `Directory.Build.props` defaults `HarborArchGate` to
                 `true`, so the gate is on unless a workflow says otherwise.
  GATE-CONDITION the target's own condition still asks the property before
                 it runs. Everything above rests on one sentence: the
                 target runs only when `HarborArchGate` is `true`, and an
                 environment variable of that name IS an MSBuild property.
                 A target that stopped asking would run inside every
                 Release build, and every other rule here would be green.

WHAT IT CANNOT SEE, AND SAYS SO
-------------------------------
The two executions are the same assembly and the same rules, but they are
not the same ENVIRONMENT: the shard runs on a fresh checkout with only its
own slice of the build output, so a rule that reads the built tree (the
Release/Debug copy check in `ReleaseBuildConfigurationRule`) has no tree to
read there and passes vacuously. That is why the gate stays ON for the merge
path: it is the only execution that sees the build output. This file counts
executions; it does not claim the two are interchangeable.

WHY A TEXT SCAN AND NOT A YAML PARSE
------------------------------------
PyYAML is not on the runner image, and the shapes this file looks for are
fixed by the workflows' own formatting. The same argument
`tools/ci-shard-slices.py` makes about the shard lists.

If it cannot find what it expects, it says so and exits non-zero. A checker
that quietly checks nothing is the failure mode this file exists to prevent.

Usage:
  ./tools/check-arch-suite-once.py
  ./tools/check-arch-suite-once.py --min-shards 4
  ./tools/check-arch-suite-once.py --self-test

Exit codes: 0 = clean, 1 = findings, 2 = bad invocation / no input.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
WORKFLOW = REPO / ".github" / "workflows" / "ci.yml"
PROPS = REPO / "Directory.Build.props"

# The suite, and the MSBuild property that runs it inside a build.
SUITE = "Harbor.Architecture.Tests"
GATE_PROPERTY = "HarborArchGate"

# A workflow job id: two spaces, an id, a colon.
JOB_RE = re.compile(r"^  ([A-Za-z0-9_.\-]+):\s*$")
# A `run:` key, with or without the list dash in front of it.
RUN_RE = re.compile(r"^(\s*)(?:-\s+)?run:\s*(.*)$")
# The block markers a `run:` key may carry instead of an inline command.
BLOCK_MARKERS = ("|", ">", "|-", ">-", "|+", ">+")
# The switch that turns the build-time gate off on the command line.
FLAG_OFF = "-p:HarborArchGate=false"
# The default the gate carries when nothing says otherwise.
DEFAULT_ON = "<HarborArchGate Condition=\"'$(HarborArchGate)' == ''\">true</HarborArchGate>"
# The clause in the target's condition that asks the property before it runs.
GATE_CONDITION = "'$(HarborArchGate)' == 'true'"


# --------------------------------------------------------------------- parsing


def split_jobs(text: str) -> dict[str, str]:
    """The workflow's jobs, as job id -> the job's own text."""
    jobs: dict[str, str] = {}
    in_jobs = False
    name: str | None = None
    body: list[str] = []
    for line in text.splitlines():
        if line and not line[0].isspace():
            in_jobs = line.strip() == "jobs:"
            if name is not None:
                jobs[name] = "\n".join(body)
                name, body = None, []
            continue
        if not in_jobs:
            continue
        match = JOB_RE.match(line)
        if match:
            if name is not None:
                jobs[name] = "\n".join(body)
            name, body = match.group(1), []
            continue
        if name is not None:
            body.append(line)
    if name is not None:
        jobs[name] = "\n".join(body)
    return jobs


def job_env(job_text: str) -> dict[str, str]:
    """The job's own `env:` block, as name -> value."""
    lines = job_text.splitlines()
    for index, line in enumerate(lines):
        if not re.match(r"^    env:\s*$", line):
            continue
        values: dict[str, str] = {}
        for entry in lines[index + 1:]:
            if entry.strip() and not entry.startswith("     "):
                break
            match = re.match(r"^      ([A-Za-z0-9_]+):\s*(.*?)\s*$", entry)
            if match:
                values[match.group(1)] = match.group(2)
        return values
    return {}


def run_blocks(job_text: str) -> list[str]:
    """The text of every `run:` block in the job."""
    blocks: list[str] = []
    lines = job_text.splitlines()
    index = 0
    while index < len(lines):
        match = RUN_RE.match(lines[index])
        if not match:
            index += 1
            continue
        indent = len(match.group(1))
        rest = match.group(2).strip()
        body: list[str] = []
        index += 1
        if rest and rest not in BLOCK_MARKERS:
            body.append(rest)
        else:
            while index < len(lines):
                entry = lines[index]
                if entry.strip() and (len(entry) - len(entry.lstrip())) <= indent:
                    break
                body.append(entry)
                index += 1
        blocks.append("\n".join(body))
    return blocks


def shard_lists(text: str) -> dict[str, list[str]]:
    """The `test` job's shard matrix: shard name -> the projects it runs."""
    shards: dict[str, list[str]] = {}
    for match in re.finditer(r"- shard: (\S+)\n(.*?)(?=\n\s+- shard: |\n\s+steps:)", text, re.S):
        shard, block = match.group(1), match.group(2)
        lines = block.splitlines()
        start = next((i for i, ln in enumerate(lines)
                      if ln.strip().startswith("projects:")), None)
        if start is None:
            continue
        projects: list[str] = []
        for line in lines[start + 1:]:
            if line.strip() and line.startswith(" " * 10):
                projects.append(line.strip())
            elif projects:
                break
        if projects:
            shards[shard] = projects
    return shards


def compiles_the_solution(block: str) -> bool:
    """Does this command compile the SOLUTION (and therefore the suite)?"""
    if "dotnet build" in block and "Harbor.slnx" in block:
        return True
    if "build/build.sh" in block and "Compile" in block:
        return True
    return False


def gate_value(expression: str, event: str) -> str | None:
    """The value of a `HarborArchGate` expression for one event.

    The shape the workflows use is the GitHub expression

        ${{ github.event_name == 'pull_request' && 'false' || 'true' }}

    which is read here as "the condition, the value when it holds, the value
    when it does not". A literal is a literal. Anything else is `None`: the
    gate cannot tell, and a gate that cannot tell treats that as a finding
    rather than as a pass.
    """
    text = expression.strip()
    if text.startswith("${{") and text.endswith("}}"):
        text = text[3:-2].strip()
    literal = re.fullmatch(r"'([A-Za-z]+)'|\"([A-Za-z]+)\"", text)
    if literal:
        return literal.group(1) or literal.group(2)
    match = re.fullmatch(r"(.+?)\s*&&\s*(.+?)\s*\|\|\s*(.+)", text)
    if not match:
        return None
    condition, when_true, when_false = (part.strip() for part in match.groups())
    event_match = re.fullmatch(
        r"github\.event_name\s*==\s*'([A-Za-z_]+)'", condition)
    if not event_match:
        return None
    chosen = when_true if event_match.group(1) == event else when_false
    return chosen.strip("'\"")


# ---------------------------------------------------------------------- checks


def check_once(workflow: str, shards: dict[str, list[str]]) -> list[str]:
    """EXEC-ONCE: the suite is executed exactly once on the pull-request lane."""
    executions: list[str] = []
    for shard, projects in sorted(shards.items()):
        if SUITE in projects:
            executions.append(f"the `{shard}` shard's project list")
    for job, job_text in sorted(split_jobs(workflow).items()):
        for block in run_blocks(job_text):
            if SUITE in block:
                executions.append(f"the `{job}` job's command")
    if len(executions) == 1:
        return []
    if not executions:
        return [
            f"EXEC-ONCE: the pull-request lane runs `{SUITE}` NOWHERE - the "
            f"architecture gate would not run at all on a pull request"
        ]
    return [
        f"EXEC-ONCE: the pull-request lane runs `{SUITE}` {len(executions)} "
        f"times - it must run exactly once: " + "; ".join(executions)
    ]


def check_gate(workflow: str) -> list[str]:
    """GATE-OFF-PR and GATE-ON-MERGE: the gate's placement on the lane."""
    problems: list[str] = []
    compiling: list[tuple[str, str]] = []
    for job, job_text in sorted(split_jobs(workflow).items()):
        blocks = run_blocks(job_text)
        if not any(compiles_the_solution(block) for block in blocks):
            continue
        compiling.append((job, job_text))

    if not compiling:
        return [
            "GATE-OFF-PR: no job in this workflow compiles the solution - the "
            "checker cannot see the build-time gate it guards"
        ]

    for job, job_text in compiling:
        blocks = run_blocks(job_text)
        if any(FLAG_OFF in block for block in blocks):
            # The command line says so, for every event. That is a job that
            # never runs the gate (the reference-trimmer job), which is
            # allowed: the build job is the one that has to keep it.
            continue
        value = job_env(job_text).get(GATE_PROPERTY)
        if value is None:
            problems.append(
                f"GATE-OFF-PR: the `{job}` job compiles the solution and says "
                f"nothing about `{GATE_PROPERTY}` - the suite would run inside "
                f"that build as well as in the shard"
            )
            continue
        on_pull_request = gate_value(value, "pull_request")
        if on_pull_request == "false":
            continue
        if on_pull_request == "true":
            problems.append(
                f"GATE-OFF-PR: the `{job}` job's `{GATE_PROPERTY}` is `{value}` "
                f"- the suite runs inside that build AND in the shard"
            )
            continue
        problems.append(
            f"GATE-OFF-PR: the `{job}` job's `{GATE_PROPERTY}` is `{value}`, and "
            f"this checker cannot tell what it yields for a pull_request event "
            f"- expected `${{{{ github.event_name == 'pull_request' && 'false' "
            f"|| 'true' }}`"
        )

    # The merge path keeps the gate.
    keeps_it = False
    for job, job_text in compiling:
        blocks = run_blocks(job_text)
        if any(FLAG_OFF in block for block in blocks):
            continue
        value = job_env(job_text).get(GATE_PROPERTY)
        if value is None:
            keeps_it = True
            break
        if gate_value(value, "push") == "true":
            keeps_it = True
            break
    if not keeps_it:
        problems.append(
            "GATE-ON-MERGE: no job in this workflow runs the architecture gate "
            "for a push event - a merge into dev/master would no longer fail "
            "the BUILD on a layering regression"
        )
    return problems


def check_default(props: str) -> list[str]:
    """GATE-DEFAULT: the gate is on unless a workflow says otherwise."""
    if DEFAULT_ON in props:
        return []
    return [
        f"GATE-DEFAULT: `{PROPS.name}` does not default `{GATE_PROPERTY}` to "
        f"`true` - the gate is off everywhere unless every job asks for it"
    ]


def check_condition(props: str) -> list[str]:
    """GATE-CONDITION: the target still asks the property before it runs.

    The whole design rests on one sentence: the MSBuild target runs only
    when `HarborArchGate` is `true`, and an environment variable of that
    name IS an MSBuild property. A target whose condition stopped asking
    would run inside every Release build whatever the workflow says, and
    every other rule here would still be green.
    """
    if "HarborArchitectureGate" not in props:
        return [
            f"GATE-CONDITION: `{PROPS.name}` has no `HarborArchitectureGate` "
            f"target - the architecture gate does not exist"
        ]
    if GATE_CONDITION not in props:
        return [
            f"GATE-CONDITION: the `HarborArchitectureGate` target's condition "
            f"no longer requires `{GATE_PROPERTY}` to be `true` - it would run "
            f"inside every Release build, whatever the workflow says"
        ]
    return []


def evaluate(workflow: str, props: str, min_shards: int) -> list[str]:
    """Every finding the tree has, in the order the rules are stated."""
    shards = shard_lists(workflow)
    problems: list[str] = []
    if len(shards) < min_shards:
        problems.append(
            f"the workflow declares {len(shards)} shard(s), the floor is "
            f"{min_shards} - the checker cannot see what it guards"
        )
        return problems
    problems += check_once(workflow, shards)
    problems += check_gate(workflow)
    problems += check_default(props)
    problems += check_condition(props)
    return problems


# ------------------------------------------------------------------ self-test


FIXTURE_WORKFLOW = """name: ci

on:
  pull_request:

jobs:
  build:
    runs-on: ubuntu-24.04
    env:
      HarborArchGate: ${{ github.event_name == 'pull_request' && 'false' || 'true' }}
    steps:
      - uses: actions/checkout@v7
      - name: Build solution via NUKE
        run: bash build/build.sh Compile --configuration Release
  reference-trimmer:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v7
      - name: Build the solution and collect the analyzer's findings
        run: |
          dotnet build Harbor.slnx -c Release --no-restore \\
            -p:HarborReferenceTrimmer=true \\
            -p:HarborArchGate=false \\
            -p:MaxCpuCount=0
  test:
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        shard: [core, platform, ui, e2e]
        include:
          - shard: core
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Abstractions.Tests
              Harbor.Architecture.Tests
              Harbor.Core.Tests
          - shard: platform
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Tools.Builtin.Tests
              Harbor.Storage.Tests
          - shard: ui
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.Tui.Tests
              Harbor.Ui.Framework.Tests
          - shard: e2e
            runner: ubuntu-24.04-arm
            guard: true
            projects: >-
              Harbor.E2E.Cli
              Harbor.App.Cli.Tests
    steps:
      - uses: actions/checkout@v7
      - name: Run shard ${{ matrix.shard }}
        run: |
          for p in ${{ matrix.projects }}; do
            dotnet exec "tests/$p/bin/Release/net10.0/$p.dll"
          done
"""

FIXTURE_PROPS = """<Project>
  <PropertyGroup>
    <HarborArchGate Condition="'$(HarborArchGate)' == ''">true</HarborArchGate>
  </PropertyGroup>
  <Target Name="HarborArchitectureGate"
          AfterTargets="Build"
          Condition="'$(Configuration)' == 'Release' AND '$(HarborArchGate)' == 'true' AND '$(MSBuildProjectName)' == 'Harbor.Architecture.Tests'">
  </Target>
</Project>
"""


def _edit(text: str, old: str, new: str) -> str:
    if old not in text:
        raise AssertionError(f"the fixture does not contain {old!r}")
    return text.replace(old, new, 1)


def self_test() -> int:
    """Each rule fires on a planted defect; the clean fixture stays clean."""
    failures: list[str] = []

    clean = evaluate(FIXTURE_WORKFLOW, FIXTURE_PROPS, 4)
    if clean:
        failures.append("the clean fixture is not clean: " + "; ".join(clean))

    cases: list[tuple[str, str, str]] = [
        (
            "EXEC-ONCE",
            "the suite listed in two shards runs it twice",
            lambda w: _edit(
                w,
                "              Harbor.Storage.Tests\n",
                "              Harbor.Storage.Tests\n              "
                "Harbor.Architecture.Tests\n"),
        ),
        (
            "EXEC-ONCE",
            "the suite listed in no shard leaves the lane with no gate",
            lambda w: w.replace("              Harbor.Architecture.Tests\n", ""),
        ),
        (
            "EXEC-ONCE",
            "a command that runs the suite is a second execution",
            lambda w: _edit(
                w,
                "      - name: Run shard ${{ matrix.shard }}\n",
                "      - name: The gate, again\n"
                "        run: dotnet exec "
                "tests/Harbor.Architecture.Tests/bin/Release/net10.0/"
                "Harbor.Architecture.Tests.dll\n"
                "      - name: Run shard ${{ matrix.shard }}\n"),
        ),
        (
            "GATE-OFF-PR",
            "a build job that says nothing about the gate runs it",
            lambda w: _edit(
                w,
                "      HarborArchGate: ${{ github.event_name == 'pull_request' "
                "&& 'false' || 'true' }}\n",
                ""),
        ),
        (
            "GATE-OFF-PR",
            "a build job whose gate is on for every event runs it twice",
            lambda w: _edit(
                w,
                "${{ github.event_name == 'pull_request' && 'false' || 'true' }}",
                "'true'"),
        ),
        (
            "GATE-OFF-PR",
            "a second job that compiles the solution without the flag runs it",
            lambda w: _edit(
                w,
                "            -p:HarborArchGate=false \\\n",
                ""),
        ),
        (
            "GATE-ON-MERGE",
            "a gate that is off for every event leaves the merge path bare",
            lambda w: _edit(
                w,
                "${{ github.event_name == 'pull_request' && 'false' || 'true' }}",
                "'false'"),
        ),
    ]

    for code, description, mutate in cases:
        findings = evaluate(mutate(FIXTURE_WORKFLOW), FIXTURE_PROPS, 4)
        if not any(finding.startswith(code) for finding in findings):
            failures.append(
                f"the planted defect ({description}) did not fire {code}: "
                + ("; ".join(findings) if findings else "the checker found nothing")
            )

    # GATE-DEFAULT and GATE-CONDITION read the props, not the workflow.
    findings = evaluate(FIXTURE_WORKFLOW, FIXTURE_PROPS.replace("true</HarborArchGate>",
                                                                "false</HarborArchGate>"), 4)
    if not any(finding.startswith("GATE-DEFAULT") for finding in findings):
        failures.append(
            "the planted defect (the gate's default turned off) did not fire "
            "GATE-DEFAULT: " + ("; ".join(findings) if findings else "the checker found nothing")
        )

    findings = evaluate(FIXTURE_WORKFLOW, FIXTURE_PROPS.replace(
        "AND '$(HarborArchGate)' == 'true' AND ", ""), 4)
    if not any(finding.startswith("GATE-CONDITION") for finding in findings):
        failures.append(
            "the planted defect (the target no longer asks the property) did "
            "not fire GATE-CONDITION: "
            + ("; ".join(findings) if findings else "the checker found nothing")
        )

    findings = evaluate(FIXTURE_WORKFLOW, FIXTURE_PROPS.replace(
        'Name="HarborArchitectureGate"', 'Name="SomethingElse"'), 4)
    if not any(finding.startswith("GATE-CONDITION") for finding in findings):
        failures.append(
            "the planted defect (the gate target is gone) did not fire "
            "GATE-CONDITION: "
            + ("; ".join(findings) if findings else "the checker found nothing")
        )

    # The floor: a workflow with fewer shards than the floor must be a
    # finding, not a quiet pass.
    findings = evaluate(FIXTURE_WORKFLOW, FIXTURE_PROPS, 9)
    if not any("floor" in finding for finding in findings):
        failures.append(
            "a workflow with fewer shards than the floor did not fire the floor: "
            + ("; ".join(findings) if findings else "the checker found nothing")
        )

    if failures:
        for failure in failures:
            print(f"::error::self-test failed: {failure}")
        return 1
    print("ok: the architecture-once gate still fails on every planted defect")
    return 0


# ----------------------------------------------------------------------- main


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Fail when the pull-request lane does not run the "
                    "architecture suite exactly once.")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the gate still fails on planted defects")
    parser.add_argument("--workflow", default=str(WORKFLOW),
                        help="the workflow to read (default: .github/workflows/ci.yml)")
    parser.add_argument("--props", default=str(PROPS),
                        help="the props file to read (default: Directory.Build.props)")
    parser.add_argument("--min-shards", type=int, default=0,
                        help="the floor on how many shards the matrix must declare")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    workflow_path = Path(args.workflow)
    props_path = Path(args.props)
    if not workflow_path.is_file():
        print(f"::error::workflow not found: {workflow_path}")
        return 2
    if not props_path.is_file():
        print(f"::error::props file not found: {props_path}")
        return 2

    workflow = workflow_path.read_text(encoding="utf-8")
    props = props_path.read_text(encoding="utf-8")

    findings = evaluate(workflow, props, args.min_shards)
    if findings:
        print("::error::the pull-request lane does not run the architecture suite once:")
        for finding in findings:
            print(f"  - {finding}")
        return 1

    shards = shard_lists(workflow)
    print(
        f"ok: {len(shards)} shards, the architecture suite runs once - in the "
        f"`{next(iter(shards))}` shard"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
