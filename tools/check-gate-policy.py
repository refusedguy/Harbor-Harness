#!/usr/bin/env python3
"""Check that the gate table in docs/CI_GATES.md still describes this tree.

Stdlib only — no pip, no network, no dotnet. Wired into
.github/workflows/docs.yml as its own job, and it must not overlap any of the
other gates in that workflow:

  check-md-links.py   does the link resolve?
  md-lint.py          is the file shaped the way the repo says it is?
  check-doc-cites.py  does a `file:line` still point at a line, and a named type
                      still exist?
  check-doc-facts.py  does a COUNT a document states still equal the count of
                      tracked files it is counting?
  THIS ONE            does the TABLE of gates in docs/CI_GATES.md still describe
                      the gates this repository actually runs?

WHY THIS FILE EXISTS (issue #1888)

  #1888 asks for a POLICY, not for another gate: why the gates are python,
  what the compiler takes over, and what a gate owes the repository. A policy
  in prose decays the way every prose fact decays — a gate is added, the
  table is not, and a year later the table describes a repository that does
  not exist. #933 measured that class on this repository: the builtin tool
  count was stated 16 times with six different values, and every divergent
  copy was a document that had already declared itself historical.

  So the policy carries its own table, and this file is the thing that keeps
  the table honest. It reads three sources — the table, the scripts in
  tools/, and the workflow files — and fails when they disagree.

  The rules it enforces, and the reason each one earns its place:

    POLICY-SCRIPT-UNDOCUMENTED  a python script a workflow runs is not in the
                                table. This is the #933 shape: a gate nobody
                                wrote down is a gate nobody reviews, and the
                                first question about it ("is it a gate or a
                                leftover?") has no answer in the repository.
    POLICY-INVOCATION-DEAD       a workflow runs a script that is not in the
                                tree. A gate that cannot start is not a gate;
                                it is a green check that checks nothing. The
                                tree on which this file was written had exactly
                                this: ci.yml ran
                                tools/check-public-api-abstractions.py, which
                                #1845 deleted when it moved the public-API
                                freeze to the compiler.
    POLICY-WORKFLOW-UNDECLARED  a script is run by a workflow the table does
                                not name, or the table names a workflow that
                                does not run it.
    POLICY-JOB-UNKNOWN           the table names a job that does not exist.
                                A renamed job leaves the table pointing at
                                nothing, and the row still reads as a fact.
    POLICY-MODE-GATE-NO-RUN      a row claims to be a gate, but the only thing
                                CI does with it is run its --self-test. A
                                self-test nobody calls is a self-test nobody
                                reads, and the row reads as if the gate ran.
    POLICY-SELFTEST-NOT-RUN      a script that HAS a self-test, and no
                                workflow ever runs it. Same failure from the
                                other side.
    POLICY-SELFTEST-UNSUPPORTED  the table names a self-test flag the script
                                does not accept, or names none for a script
                                that has one.
    POLICY-FLOORS-DIVERGE        the floors the table claims are not the floors
                                CI passes. A floor is the non-vacuity proof
                                (a gate that scanned nothing must not report
                                "clean"), and a floor that is written down but
                                not passed is a floor that protects nothing.
    POLICY-BASELINE-MISSING      the table names a ratchet file that is not in
                                the tree, or one the script never reads.
    POLICY-HEADER-MISSING        a gate script does not say why it is python.
                                #1888 asks for that line in every header, and
                                without a checker the line is a convention
                                that erodes.
    POLICY-DOTNET-CALL           a gate script shells out to dotnet. The
                                reason the gates are python is that they must
                                run where there is no SDK and no built
                                solution; a gate that needs the SDK cannot run
                                on the PR that fixes the build.
    POLICY-TIME-CALL             a gate script measures time. A timing
                                threshold is a benchmark, and benchmarks live
                                in docs/BENCHMARKS.md with a methodology; a
                                gate that fails on a slow runner is a gate
                                that gets switched off.

  The rules that are NOT mechanical are stated in the document itself and
  reviewed by a human: what a gate checks, that its message names the
  violator, that it has no allow-list, and that it does not duplicate
  another gate. This file does not pretend to check prose.

NON-VACUITY

  --min-gates N is a floor on how many rows the table holds. Measured today:
  21. A table that lost rows because a gate was deleted would otherwise
  shrink quietly, and a policy that covers fewer gates than the repository
  runs is the #591 shape.

  --self-test runs the checker against a synthetic tree and asserts that each
  rule fires on a planted defect, and that the same tree without the defect
  is clean. A rule that cannot fire is indistinguishable from a rule that
  found nothing, which is the failure every other gate in this workflow is
  tested against.

Usage:
  ./tools/check-gate-policy.py
  ./tools/check-gate-policy.py --min-gates 21
  ./tools/check-gate-policy.py --self-test

Exit codes:  0 = clean, 1 = findings, 2 = bad invocation / no input.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile

# The table, and the shape the table must have. The header is pinned: a
# policy whose table can be reshaped without the checker noticing is a table
# nobody reads.
TABLE_DOC = "docs/CI_GATES.md"
TABLE_HEADER = (
    "Script",
    "Implementation",
    "What it checks",
    "Runs in",
    "Jobs",
    "Mode",
    "Self-test",
    "Baseline",
    "Floors",
)

# The modes a row may claim. "gate" fails a build, "advisory" reports and
# lets the build pass, "control" proves that a COMPILE-time gate is armed
# and therefore runs the compiler, "reporter" prints and never fails,
# "helper" is a tool the pipeline uses, "library" is imported rather than run.
MODES = ("gate", "advisory", "control", "reporter", "helper", "library")

# The self-test flags this repository uses. A row names one of them, or "—".
SELF_TEST_FLAGS = ("--self-test", "--selftest")

# The header marker every python tool carries, and the reason it is there:
# the tool runs where there is no SDK, no package manager and no network.
WHY_PYTHON_MARKER = "stdlib only"

# A COMPILED gate: one multi-call binary, one subcommand per gate, and the
# sources of that gate next to it. The binary is committed to the tree, so
# that a gate can run where there is no SDK and no build of Harbor, and the
# workflow rebuilds it from the sources and compares the bytes.
COMPILED_SOURCE_DIR = "tools/gates"
COMPILED_BINARY_DIR = "tools/bin"
COMPILED_MANIFEST = "tools/bin/SHA256SUMS"
COMPILED_BINARY_RE = re.compile(
    r"^tools/bin/harbor-gates-linux-(x64|arm64)$"
)

# An invocation of a compiled gate: the binary, then the gate's name.
COMPILED_INVOKE_RE = re.compile(
    r"tools/bin/harbor-gates-linux-(x64|arm64)\s+([A-Za-z0-9_.\-]+)"
)

# A workflow job id: two spaces, an id, a colon.
JOB_RE = re.compile(r"^  ([A-Za-z0-9_.\-]+):\s*$")

# A step of a job: six spaces and a dash.
STEP_RE = re.compile(r"^      - ")

# A `tools/<name>.py` mention.
TOOL_RE = re.compile(r"tools/([A-Za-z0-9_.\-]+\.py)")

# The INVOCATION, as opposed to a mention. A step can name a tool in a
# message without running it — ci.yml's shard step says, in an echo, that
# "tools/ci-shard-slices.py checks the two lists" — and counting that as an
# invocation would put a job in the table that does not exist. An invocation
# is a `python3` that is followed by the tool.
INVOKE_RE = re.compile(
    r"python3(?:\s+-[^\s]+)*\s+tools/([A-Za-z0-9_.\-]+\.py)"
)

# A subprocess call that would pull the SDK into a gate.
DOTNET_CALL_RE = re.compile(
    r"(?:subprocess\.(?:run|call|check_call|check_output|Popen)"
    r"|os\.(?:system|popen|execv|execvp|execve|execl|execlp))"
    r"\s*\(\s*\[?\s*[\"']dotnet"
)

# A measurement of a duration. A gate that compares durations is a benchmark
# with a threshold, and thresholds belong in docs/BENCHMARKS.md.
TIME_CALL_RE = re.compile(
    r"\btime\.(?:perf_counter|perf_counter_ns|monotonic|monotonic_ns|time)\b"
    r"|\bdatetime\.datetime\.(?:now|utcnow)\b"
)

# The self-test's fixtures are DATA, not the checker's code. A scan for a
# dotnet call or for a timer must not read them, or the checker would flag
# its own fixture and every other rule with it. The markers below delimit
# the data; a script under test has no markers, so it is read whole.
FIXTURE_RE = re.compile(
    r"^[ \t]*# --- fixture data, not code ---"
    r".*?"
    r"^[ \t]*# --- end fixture data ---",
    re.M | re.S,
)


def code_only(source: str) -> str:
    """The source with the self-test fixture data removed."""
    return FIXTURE_RE.sub("", source)


class Finding:
    """One disagreement between the table and the tree."""

    def __init__(self, code: str, message: str, where: str = ""):
        self.code = code
        self.message = message
        self.where = where

    def __str__(self) -> str:
        head = f"{self.code}: {self.message}"
        return f"{head} ({self.where})" if self.where else head


# ---------------------------------------------------------------- the table


def split_row(line: str) -> list[str]:
    """Split one markdown table row into its cells."""
    text = line.strip()
    if text.startswith("|"):
        text = text[1:]
    if text.endswith("|"):
        text = text[:-1]
    return [cell.strip() for cell in text.split("|")]


def unquote(cell: str) -> str:
    """Strip the backticks a table cell wraps its code in."""
    return cell.replace("`", "").strip()


def parse_table(text: str) -> tuple[list[list[str]] | None, str]:
    """Return the table's rows and the reason there is no table."""
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if not line.startswith("|"):
            continue
        if tuple(split_row(line)) != TABLE_HEADER:
            continue
        rows: list[list[str]] = []
        for candidate in lines[index + 1:]:
            if not candidate.startswith("|"):
                break
            cells = split_row(candidate)
            if all(set(cell) <= set("-: ") for cell in cells):
                continue  # the separator row
            rows.append(cells)
        return rows, ""
    return None, f"{TABLE_DOC} has no table with the header {TABLE_HEADER}"


# ------------------------------------------------------------- the workflows


def floor_pairs(tokens: list[str]) -> set[str]:
    """The `--min-*` arguments of a token list, as `flag value` strings.

    A set, not a list: two invocations of one script can pass the same floor
    (the package gate passes `--min-projects` in both of its steps), and the
    table states the floor once.
    """
    pairs: set[str] = set()
    index = 0
    while index < len(tokens):
        token = tokens[index]
        if token.startswith("--min-"):
            if "=" in token:
                pairs.add(token)
            elif index + 1 < len(tokens) and not tokens[index + 1].startswith("--"):
                pairs.add(f"{token} {tokens[index + 1]}")
                index += 1
            else:
                pairs.add(token)
        index += 1
    return pairs


class Invocation:
    """One `python3 tools/<name>.py ...` inside a workflow step.

    A COMPILED gate is invoked the same way, with the binary and the
    gate's name: `tools/bin/harbor-gates-linux-x64 gen-seam-graph --check`.
    The `script` is then the binary, and `gate` is the subcommand.
    """

    def __init__(self, workflow: str, job: str, script: str, args: str,
                 gate: str = ""):
        self.workflow = workflow
        self.job = job
        self.script = script
        self.args = args
        self.gate = gate

    @property
    def key(self) -> str:
        """What the table's Script cell names this invocation by."""
        if self.gate:
            return f"{self.script} {self.gate}"
        return self.script

    @property
    def is_self_test(self) -> bool:
        return any(flag in self.args for flag in SELF_TEST_FLAGS)

    @property
    def is_warn_only(self) -> bool:
        return "--warn-only" in self.args

    @property
    def floors(self) -> list[str]:
        """The `--min-*` arguments of this invocation, as `flag value` pairs."""
        return sorted(floor_pairs(self.args.split()))


def step_command(step: str) -> str:
    """The shell command of a workflow step, with continuations joined."""
    lines = step.splitlines()
    command: list[str] = []
    started = False
    run_indent = 0
    for line in lines:
        stripped = line.strip()
        indent = len(line) - len(line.lstrip())
        if not started:
            if stripped.startswith("run:"):
                started = True
                run_indent = indent
                rest = stripped[4:].strip()
                if rest not in ("|", ">", "|-", ">-", "|+", ">+"):
                    command.append(rest)
            continue
        if indent <= run_indent:
            break
        command.append(stripped)
    joined = " ".join(command)
    return joined.replace("\\ ", " ").replace("\\\n", " ").strip()


def parse_workflow(text: str) -> tuple[list[str], list[Invocation]]:
    """Return the job ids of a workflow and the invocations it makes."""
    jobs: list[str] = []
    invocations: list[Invocation] = []
    in_jobs = False
    job: str | None = None
    step: list[str] = []

    def flush_step() -> None:
        nonlocal step
        if job is None or not step:
            step = []
            return
        command = step_command("\n".join(step))
        matches = list(INVOKE_RE.finditer(command))
        for index, match in enumerate(matches):
            end = matches[index + 1].start() if index + 1 < len(matches) else len(command)
            invocations.append(
                Invocation(
                    workflow="",
                    job=job,
                    script=match.group(1),
                    args=command[match.end():end].strip(),
                )
            )
        # A compiled gate: the binary, then the gate's name.
        compiled = list(COMPILED_INVOKE_RE.finditer(command))
        for index, match in enumerate(compiled):
            end = compiled[index + 1].start() if index + 1 < len(compiled) else len(command)
            invocations.append(
                Invocation(
                    workflow="",
                    job=job,
                    script=f"harbor-gates-linux-{match.group(1)}",
                    gate=match.group(2),
                    args=command[match.end():end].strip(),
                )
            )
        step = []

    for line in text.splitlines():
        if line and not line[0].isspace():
            in_jobs = line.strip() == "jobs:"
            flush_step()
            job = None
            continue
        if not in_jobs:
            continue
        job_match = JOB_RE.match(line)
        if job_match:
            flush_step()
            job = job_match.group(1)
            jobs.append(job)
            continue
        if STEP_RE.match(line):
            flush_step()
            step = [line]
            continue
        if step:
            step.append(line)
    flush_step()
    return jobs, invocations


def read_workflows(repo: str) -> tuple[dict[str, list[str]], list[Invocation]]:
    """Read every workflow file: job ids per file, and every invocation."""
    directory = os.path.join(repo, ".github", "workflows")
    job_ids: dict[str, list[str]] = {}
    invocations: list[Invocation] = []
    if not os.path.isdir(directory):
        return job_ids, invocations
    for name in sorted(os.listdir(directory)):
        if not name.endswith((".yml", ".yaml")):
            continue
        path = os.path.join(directory, name)
        try:
            with open(path, encoding="utf-8") as handle:
                text = handle.read()
        except OSError:
            continue
        ids, found = parse_workflow(text)
        job_ids[name] = ids
        for invocation in found:
            invocation.workflow = name
            invocations.append(invocation)
    return job_ids, invocations


# ------------------------------------------------------------------- checks


def source_of(repo: str, script: str) -> str:
    try:
        with open(os.path.join(repo, "tools", script), encoding="utf-8") as handle:
            return handle.read()
    except OSError:
        return ""


def docstring_of(source: str) -> str:
    """The module docstring, which is where a tool's header lives.

    The rule the policy states is "the header says why it is python", and a
    header is the docstring. Reading the first N lines instead would make
    the rule depend on how long a tool's explanation is, and a tool with a
    good explanation would fail for having one.
    """
    text = source.lstrip("\ufeff")
    if text.startswith("#!"):
        _, _, text = text.partition("\n")
    text = text.lstrip()
    if not text.startswith('"""') and not text.startswith("'''"):
        return "\n".join(source.splitlines()[:40])
    quote = text[:3]
    end = text.find(quote, 3)
    return text if end < 0 else text[:end]


def evaluate(repo: str) -> tuple[list[Finding], int]:
    """Compare the table, the tools and the workflows.

    Returns the findings and the number of rows the table holds.
    """
    findings: list[Finding] = []

    table_path = os.path.join(repo, TABLE_DOC)
    if not os.path.isfile(table_path):
        return [Finding(
            "POLICY-TABLE-MISSING",
            f"{TABLE_DOC} does not exist, so the policy has no table to check",
            TABLE_DOC,
        )], 0
    with open(table_path, encoding="utf-8") as handle:
        table_text = handle.read()
    rows, reason = parse_table(table_text)
    if rows is None:
        return [Finding("POLICY-TABLE-MISSING", reason, TABLE_DOC)], 0

    job_ids, invocations = read_workflows(repo)

    # The scripts CI runs, and the scripts it runs that are not in the tree.
    invoked: dict[str, list[Invocation]] = {}
    for invocation in invocations:
        invoked.setdefault(invocation.key, []).append(invocation)
        if invocation.gate:
            # A compiled gate: the binary has to be in the tree.
            binary = f"{COMPILED_BINARY_DIR}/{invocation.script}"
            if not os.path.isfile(os.path.join(repo, binary)):
                findings.append(Finding(
                    "POLICY-INVOCATION-DEAD",
                    f"workflow {invocation.workflow} runs {binary} "
                    f"{invocation.gate}, which is not in the tree — a gate "
                    f"that cannot start checks nothing",
                    f"{invocation.workflow} · {invocation.job}",
                ))
            continue
        if not os.path.isfile(os.path.join(repo, "tools", invocation.script)):
            findings.append(Finding(
                "POLICY-INVOCATION-DEAD",
                f"workflow {invocation.workflow} runs tools/{invocation.script}, "
                f"which is not in the tree — a gate that cannot start checks "
                f"nothing",
                f"{invocation.workflow} · {invocation.job}",
            ))

    documented: dict[str, dict[str, str]] = {}
    for cells in rows:
        if len(cells) != len(TABLE_HEADER):
            findings.append(Finding(
                "POLICY-ROW-SHAPE",
                f"row has {len(cells)} cells, the table has {len(TABLE_HEADER)}: "
                f"{' | '.join(cells)}",
                TABLE_DOC,
            ))
            continue
        row = dict(zip(TABLE_HEADER, cells))
        script = unquote(row["Script"])
        if not script:
            findings.append(Finding(
                "POLICY-ROW-SHAPE", "row names no script", TABLE_DOC,
            ))
            continue
        if script in documented:
            findings.append(Finding(
                "POLICY-DUPLICATE-SCRIPT",
                f"{script} has more than one row in the table",
                TABLE_DOC,
            ))
            continue
        documented[script] = {key: unquote(value) for key, value in row.items()}

    # Every script CI runs is in the table, and every python script in
    # tools/ is one of the two: run by CI, or listed as a tool that is not.
    # A script that is neither is a script nobody runs and nobody reviews.
    tools_dir = os.path.join(repo, "tools")
    names = sorted(name for name in os.listdir(tools_dir)
                   if name.endswith(".py")) if os.path.isdir(tools_dir) else []
    for name in names:
        path = f"tools/{name}"
        if path in documented:
            continue
        if name in invoked:
            places = sorted({f"{i.workflow} · {i.job}" for i in invoked[name]})
            findings.append(Finding(
                "POLICY-SCRIPT-UNDOCUMENTED",
                f"{path} is run by CI ({', '.join(places)}) and is not in the "
                f"table in {TABLE_DOC}",
                path,
            ))
        else:
            findings.append(Finding(
                "POLICY-SCRIPT-UNDOCUMENTED",
                f"{path} is a python tool in tools/ that no workflow runs, and "
                f"the table does not list it — either wire it up or say in the "
                f"table that it is a tool a human runs",
                path,
            ))

    # The compiled gates in the tree, and the table's rows that name one.
    # BOTH directions: a gate that exists and is not in the table is a gate
    # nobody reviews, and a row that names a gate that does not exist is a
    # row that reads as a fact about a repository that is not this one.
    compiled_gates = compiled_gate_names(repo)
    documented_gates = set()
    for script in documented:
        _, gate = split_compiled(script)
        if gate:
            documented_gates.add(gate)
    for gate in sorted(compiled_gates - documented_gates):
        findings.append(Finding(
            "POLICY-GATE-UNDOCUMENTED",
            f"{COMPILED_SOURCE_DIR}/{gate} is a compiled gate, and the table "
            f"in {TABLE_DOC} does not list it — either wire it up or say in "
            f"the table that it is a gate a human runs",
            f"{COMPILED_SOURCE_DIR}/{gate}",
        ))

    for script in sorted(documented):
        row = documented[script]
        implementation = row.get("Implementation", "python")
        binary, gate = split_compiled(script)
        if binary:
            path = os.path.join(repo, binary)
            name = os.path.basename(binary)
            key = f"{name} {gate}"
            source = compiled_source(repo, implementation, gate)
        else:
            path = os.path.join(repo, script)
            name = os.path.basename(script)
            key = name
            source = ""
        if not os.path.isfile(path):
            findings.append(Finding(
                "POLICY-SCRIPT-MISSING",
                f"the table names {script}, which is not in the tree",
                TABLE_DOC,
            ))
            continue
        here = invoked.get(key, [])

        if binary:
            findings.extend(compiled_findings(repo, script, binary, gate,
                                              implementation, source))

        mode = row["Mode"]
        if mode not in MODES:
            findings.append(Finding(
                "POLICY-MODE-UNKNOWN",
                f"{script} claims mode {mode!r}; the modes are "
                f"{', '.join(MODES)}",
                TABLE_DOC,
            ))

        runs_in = [item.strip() for item in row["Runs in"].split(",")
                   if item.strip() and item.strip() != "—"]
        jobs = [item.strip() for item in row["Jobs"].split(",")
                if item.strip() and item.strip() != "—"]

        if not runs_in:
            # A library: imported by other tools, run by no workflow.
            if here:
                findings.append(Finding(
                    "POLICY-WORKFLOW-UNDECLARED",
                    f"the table says {script} runs nowhere, but CI runs it",
                    script,
                ))
        else:
            declared = set(runs_in)
            actual = {invocation.workflow for invocation in here}
            for workflow in sorted(actual - declared):
                findings.append(Finding(
                    "POLICY-WORKFLOW-UNDECLARED",
                    f"CI runs {script} in {workflow}, which the table does "
                    f"not name",
                    script,
                ))
            for workflow in sorted(declared - actual):
                findings.append(Finding(
                    "POLICY-WORKFLOW-UNDECLARED",
                    f"the table says {script} runs in {workflow}, and "
                    f"{workflow} does not run it",
                    script,
                ))
            # The job ids the table names must be exactly the job ids that
            # run the script, across the named workflows. The pairing of a
            # job to a workflow is not checked: the table is a summary, and a
            # script that runs in one job of two workflows would need a cell
            # per pair to say so.
            actual_jobs = {invocation.job for invocation in here}
            if set(jobs) != actual_jobs:
                missing = sorted(actual_jobs - set(jobs))
                extra = sorted(set(jobs) - actual_jobs)
                detail = []
                if missing:
                    detail.append(f"CI runs it in {', '.join(missing)}")
                if extra:
                    detail.append(
                        f"the table names {', '.join(extra)}, which does not run it"
                    )
                findings.append(Finding(
                    "POLICY-JOB-UNKNOWN",
                    f"the table's job list for {script} and the jobs that run it "
                    f"disagree: {'; '.join(detail)}",
                    script,
                ))
            for job in jobs:
                if not any(job in job_ids.get(w, []) for w in declared):
                    findings.append(Finding(
                        "POLICY-JOB-UNKNOWN",
                        f"the table names job {job}, which no named workflow has",
                        script,
                    ))

        # The mode, against what CI actually does with the script.
        real = [invocation for invocation in here if not invocation.is_self_test]
        if mode == "gate":
            if not real:
                findings.append(Finding(
                    "POLICY-MODE-GATE-NO-RUN",
                    f"the table calls {script} a gate, and the only thing "
                    f"CI does with it is run its self-test",
                    script,
                ))
            for invocation in real:
                if invocation.is_warn_only:
                    findings.append(Finding(
                        "POLICY-MODE-GATE-WARN-ONLY",
                        f"the table calls {script} a gate, and CI runs it "
                        f"with --warn-only in {invocation.workflow} · "
                        f"{invocation.job}",
                        script,
                    ))
        if mode == "advisory":
            for invocation in real:
                if not invocation.is_warn_only:
                    findings.append(Finding(
                        "POLICY-MODE-ADVISORY-NOT-WARN-ONLY",
                        f"the table calls {script} advisory, and CI runs "
                        f"it without --warn-only in {invocation.workflow} · "
                        f"{invocation.job}",
                        script,
                    ))

        # The self-test. A library is not run, so "CI must run its self-test"
        # does not apply to it, and a library that carries the flag as shared
        # plumbing is not a gate without a self-test.
        if binary:
            source = compiled_source_text(repo, implementation, gate)
        else:
            source = source_of(repo, name)
        flags = [flag for flag in SELF_TEST_FLAGS if flag in source]
        claimed = row["Self-test"]
        if mode == "library":
            pass
        elif claimed == "—":
            if flags:
                findings.append(Finding(
                    "POLICY-SELFTEST-UNSUPPORTED",
                    f"the table says {script} has no self-test, and the "
                    f"script accepts {', '.join(flags)}",
                    script,
                ))
        else:
            if claimed not in flags:
                findings.append(Finding(
                    "POLICY-SELFTEST-UNSUPPORTED",
                    f"the table names {claimed} as the self-test of "
                    f"{script}, and the script accepts "
                    f"{', '.join(flags) if flags else 'no self-test flag'}",
                    script,
                ))
            elif not any(invocation.is_self_test for invocation in here):
                findings.append(Finding(
                    "POLICY-SELFTEST-NOT-RUN",
                    f"{script} accepts {claimed} and no workflow ever "
                    f"runs it",
                    script,
                ))

        # The floors.
        floors = sorted({floor for invocation in here for floor in invocation.floors})
        claimed_floors = row["Floors"]
        claimed_list = sorted(floor_pairs(
            [] if claimed_floors == "—" else claimed_floors.split()))
        if claimed_list != floors:
            findings.append(Finding(
                "POLICY-FLOORS-DIVERGE",
                f"the table claims the floors {claimed_floors!r} for "
                f"{script}, and CI passes "
                f"{' '.join(floors) if floors else '(none)'}",
                script,
            ))
        for floor in floors:
            flag = floor.split()[0]
            if flag not in source:
                findings.append(Finding(
                    "POLICY-FLOORS-DIVERGE",
                    f"CI passes {floor} to {script}, and the script does "
                    f"not accept {flag}",
                    script,
                ))

        # The ratchet.
        baseline = row["Baseline"]
        if baseline != "—":
            if not os.path.exists(os.path.join(repo, baseline)):
                findings.append(Finding(
                    "POLICY-BASELINE-MISSING",
                    f"the table names {baseline} as the ratchet of "
                    f"{script}, and it is not in the tree",
                    script,
                ))
            else:
                name = os.path.basename(baseline)
                passed = any(name in invocation.args for invocation in here)
                if name not in source and not passed:
                    findings.append(Finding(
                        "POLICY-BASELINE-UNREAD",
                        f"the table names {baseline} as the ratchet of "
                        f"{script}, and neither the script nor its CI "
                        f"invocation ever mentions it",
                        script,
                    ))

        # The reason the tool is python, and the two things it may not do.
        # A "control" is exempt from the dotnet rule on purpose: it is the
        # proof that a COMPILE-time gate is armed, so running the compiler
        # is the whole of its job. A gate that needed the SDK would be a
        # different thing.
        # A COMPILED gate says why it is compiled instead, and the checker
        # reads the sources of the gate, not a python header.
        if binary:
            code = code_only(source)
            if mode in ("gate", "advisory") and DOTNET_CALL_RE.search(code):
                findings.append(Finding(
                    "POLICY-DOTNET-CALL",
                    f"{script} shells out to dotnet, and a gate that needs "
                    f"the SDK cannot run on the PR that fixes the build",
                    script,
                ))
            if mode in ("gate", "advisory") and TIME_CALL_RE.search(code):
                findings.append(Finding(
                    "POLICY-TIME-CALL",
                    f"{script} measures time, and a timing threshold is a "
                    f"benchmark, and benchmarks live in docs/BENCHMARKS.md",
                    script,
                ))
        elif mode in ("gate", "advisory", "control"):
            head = docstring_of(source)
            if WHY_PYTHON_MARKER not in head.lower():
                findings.append(Finding(
                    "POLICY-HEADER-MISSING",
                    f"{script} does not say why it is python in its header "
                    f"(the marker is {WHY_PYTHON_MARKER!r})",
                    script,
                ))
            code = code_only(source)
            if mode in ("gate", "advisory") and DOTNET_CALL_RE.search(code):
                findings.append(Finding(
                    "POLICY-DOTNET-CALL",
                    f"{script} shells out to dotnet, and a gate that needs "
                    f"the SDK cannot run on the PR that fixes the build",
                    script,
                ))
            if mode in ("gate", "advisory") and TIME_CALL_RE.search(code):
                findings.append(Finding(
                    "POLICY-TIME-CALL",
                    f"{script} measures time, and a timing threshold is a "
                    f"benchmark, and benchmarks live in docs/BENCHMARKS.md",
                    script,
                ))

    return findings, len(rows)


# ------------------------------------------------------- the compiled gates


def split_compiled(script: str) -> tuple[str, str]:
    """The binary and the gate's name, if the row names a compiled gate.

    A compiled row's Script cell is the command the workflow runs:
    `tools/bin/harbor-gates-linux-x64 gen-seam-graph`. A python row's cell
    is the path of the script.
    """
    parts = script.split()
    if len(parts) != 2:
        return "", ""
    if not COMPILED_BINARY_RE.match(parts[0]):
        return "", ""
    return parts[0], parts[1]


def compiled_gate_names(repo: str) -> set[str]:
    """The compiled gates in the tree: the directories under tools/gates/."""
    root = os.path.join(repo, COMPILED_SOURCE_DIR)
    if not os.path.isdir(root):
        return set()
    names = set()
    for entry in sorted(os.listdir(root)):
        path = os.path.join(root, entry)
        if not os.path.isdir(path):
            continue
        if entry == "internal":
            continue  # the shared helpers, not a gate
        names.add(entry)
    return names


def compiled_source(repo: str, implementation: str, gate: str) -> str:
    """The directory that holds the sources of a compiled gate."""
    if implementation.startswith("compiled:"):
        return implementation.split(":", 1)[1].strip()
    return f"{COMPILED_SOURCE_DIR}/{gate}"


def compiled_source_text(repo: str, implementation: str, gate: str) -> str:
    """The sources of a compiled gate, as one text."""
    directory = compiled_source(repo, implementation, gate)
    parts = []
    if not os.path.isdir(os.path.join(repo, directory)):
        return ""
    for name in sorted(os.listdir(os.path.join(repo, directory))):
        if not name.endswith(".go"):
            continue
        try:
            with open(os.path.join(repo, directory, name), encoding="utf-8") as handle:
                parts.append(handle.read())
        except OSError:
            continue
    return "\n".join(parts)


def compiled_findings(repo: str, script: str, binary: str, gate: str,
                      implementation: str, source: str) -> list[Finding]:
    """The rules that hold for a compiled gate."""
    findings: list[Finding] = []

    # The binary of every platform, and the manifest that names its hash.
    manifest = os.path.join(repo, COMPILED_MANIFEST)
    hashes: dict[str, str] = {}
    if os.path.isfile(manifest):
        with open(manifest, encoding="utf-8") as handle:
            for line in handle:
                parts = line.split()
                if len(parts) == 2:
                    hashes[parts[1]] = parts[0]
    else:
        findings.append(Finding(
            "POLICY-BINARY-MANIFEST-MISSING",
            f"{COMPILED_MANIFEST} is not in the tree, and it is where the "
            f"hash of every committed binary is written down",
            COMPILED_MANIFEST,
        ))

    for platform in ("x64", "arm64"):
        name = f"harbor-gates-linux-{platform}"
        path = os.path.join(repo, COMPILED_BINARY_DIR, name)
        if not os.path.isfile(path):
            findings.append(Finding(
                "POLICY-BINARY-MISSING",
                f"the compiled gate {script} needs {COMPILED_BINARY_DIR}/{name}, "
                f"and it is not in the tree",
                f"{COMPILED_BINARY_DIR}/{name}",
            ))
            continue
        expected = hashes.get(name)
        if expected is None:
            findings.append(Finding(
                "POLICY-BINARY-HASH-MISSING",
                f"{COMPILED_BINARY_DIR}/{name} has no entry in "
                f"{COMPILED_MANIFEST}",
                f"{COMPILED_BINARY_DIR}/{name}",
            ))
            continue
        actual = sha256_of(path)
        if actual != expected:
            findings.append(Finding(
                "POLICY-BINARY-HASH-DIVERGE",
                f"{COMPILED_BINARY_DIR}/{name} hashes to {actual[:12]}, and "
                f"{COMPILED_MANIFEST} says {expected[:12]} — rebuild it with "
                f"tools/build-gates.sh",
                f"{COMPILED_BINARY_DIR}/{name}",
            ))

    # The sources of the gate, and the gate's own directory.
    directory = compiled_source(repo, implementation, gate)
    if not os.path.isdir(os.path.join(repo, directory)):
        findings.append(Finding(
            "POLICY-GATE-SOURCE-MISSING",
            f"the table says {script} is implemented in {directory}, and that "
            f"directory is not in the tree",
            directory,
        ))

    # No python original and no compiled twin of the same gate: the twin is
    # how a drifted pair is caught.
    twin = os.path.join(repo, "tools", f"{gate}.py")
    if os.path.isfile(twin):
        findings.append(Finding(
            "POLICY-GATE-TWIN",
            f"tools/{gate}.py and the compiled gate {script} are two "
            f"implementations of one gate — a gate moves from one to the "
            f"other in one pull request, never both",
            f"tools/{gate}.py",
        ))
    return findings


def sha256_of(path: str) -> str:
    """The SHA-256 of a file, as the manifest writes it."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


# ---------------------------------------------------------------- self-test


def _write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def _fixture(root: str) -> None:
    """A synthetic tree that the checker must call clean."""
    _write(os.path.join(root, "tools", "alpha.py"), '''#!/usr/bin/env python3
"""Alpha.

Stdlib only — no pip, no network, no dotnet. A gate over the tree.
"""

import argparse


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--min-files", type=int, default=0)
    args = parser.parse_args()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
''')
    _write(os.path.join(root, "tools", "beta.py"), '''#!/usr/bin/env python3
"""Beta.

Stdlib only — no pip, no network, no dotnet. A reporter over the tree.
"""

import argparse


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
''')
    _write(os.path.join(root, ".github", "workflows", "wf.yml"), """name: wf
on:
  push:
    paths:
      - 'tools/alpha.py'
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - name: Self-test
        run: python3 tools/alpha.py --self-test
      - name: Check
        run: python3 tools/alpha.py --min-files 10
  report:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - name: Self-test
        run: python3 tools/beta.py --self-test
""")
    # A COMPILED gate: the binary, the manifest that names its hash, the
    # sources, and the workflow line that runs it.
    binary = b"harbor-gates fixture binary\n"
    _write(os.path.join(root, "tools", "bin", "harbor-gates-linux-x64"), binary.decode())
    _write(os.path.join(root, "tools", "bin", "harbor-gates-linux-arm64"), binary.decode())
    digest = hashlib.sha256(binary).hexdigest()
    _write(os.path.join(root, "tools", "bin", "SHA256SUMS"),
           f"{digest}  harbor-gates-linux-x64\n"
           f"{digest}  harbor-gates-linux-arm64\n")
    _write(os.path.join(root, "tools", "gates", "gamma", "gamma.go"), """// Package gamma is a compiled gate.
//
// A compiled gate: no interpreter, no SDK, one static binary per platform.
// The flags are --self-test and --min-files, the same names the python
// original had.
package gamma

import "flag"

func main() {
    selfTest := flag.Bool("self-test", false, "prove the checker can fail")
    minFiles := flag.Int("min-files", 0, "a floor over the tree")
    flag.Parse()
    _ = *selfTest
    _ = *minFiles
}
""")
    _write(os.path.join(root, ".github", "workflows", "wf-compiled.yml"), """name: wfc
on:
  push:
    paths:
      - 'tools/gates/**'
jobs:
  compiled:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - name: Self-test
        run: tools/bin/harbor-gates-linux-x64 gamma --self-test
      - name: Check
        run: tools/bin/harbor-gates-linux-x64 gamma --min-files 10
""")
    _write(os.path.join(root, TABLE_DOC), """# Gates

## The gate table

| Script | Implementation | What it checks | Runs in | Jobs | Mode | Self-test | Baseline | Floors |
|---|---|---|---|---|---|---|---|---|
| `tools/alpha.py` | python | a floor over the tree | wf.yml | check | gate | `--self-test` | — | `--min-files 10` |
| `tools/beta.py` | python | nothing, it prints | wf.yml | report | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 gamma` | compiled: `tools/gates/gamma` | a compiled floor over the tree | wf-compiled.yml | compiled | gate | `--self-test` | — | `--min-files 10` |
""")


def _codes(findings: list[Finding]) -> list[str]:
    return sorted(finding.code for finding in findings)


def self_test() -> int:
    """Each rule fires on a planted defect; the clean tree stays clean."""
    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix="gate-policy-") as tmp:
        root = os.path.join(tmp, "repo")
        _fixture(root)
        clean, gates = evaluate(root)
        if clean:
            failures.append(
                "the clean fixture is not clean: " + "; ".join(map(str, clean))
            )
        if gates != 3:
            failures.append(f"the clean fixture has {gates} rows, expected 3")

        # The planted defects below are DATA: the strings are what a broken
        # script would contain, and a scan for a dotnet call or for a timer
        # must not read them as the checker's own code.
        # --- fixture data, not code ---
        cases: list[tuple[str, str, object]] = [
            (
                "POLICY-SCRIPT-MISSING",
                "a row names a script that is not in the tree",
                lambda r: _edit_table(
                    r, "| `tools/alpha.py` |", "| `tools/ghost.py` |"),
            ),
            (
                "POLICY-SCRIPT-UNDOCUMENTED",
                "a script CI runs is not in the table",
                lambda r: _edit_table(
                    r, "| `tools/beta.py` | python | nothing, it prints | "
                       "wf.yml | report | reporter | `--self-test` | — | — |\n", ""),
            ),
            (
                "POLICY-INVOCATION-DEAD",
                "a workflow runs a script that is not in the tree",
                lambda r: _edit_workflow(
                    r, "python3 tools/beta.py --self-test",
                    "python3 tools/ghost.py --self-test"),
            ),
            (
                "POLICY-WORKFLOW-UNDECLARED",
                "the table names a workflow that does not run the script",
                lambda r: _edit_table(
                    r, "| wf.yml | check |", "| other.yml | check |"),
            ),
            (
                "POLICY-JOB-UNKNOWN",
                "the table names a job that does not exist",
                lambda r: _edit_table(
                    r, "| wf.yml | check |", "| wf.yml | nosuchjob |"),
            ),
            (
                "POLICY-MODE-GATE-NO-RUN",
                "a gate whose only CI use is its self-test",
                lambda r: _edit_workflow(
                    r, "        run: python3 tools/alpha.py --min-files 10\n",
                    ""),
            ),
            (
                "POLICY-MODE-GATE-WARN-ONLY",
                "a gate CI runs with --warn-only",
                lambda r: _edit_workflow(
                    r, "python3 tools/alpha.py --min-files 10",
                    "python3 tools/alpha.py --min-files 10 --warn-only"),
            ),
            (
                "POLICY-MODE-ADVISORY-NOT-WARN-ONLY",
                "an advisory gate CI runs without --warn-only",
                lambda r: _edit_table(
                    r, "| wf.yml | check | gate |",
                    "| wf.yml | check | advisory |"),
            ),
            (
                "POLICY-SELFTEST-UNSUPPORTED",
                "the table names a self-test the script does not accept",
                lambda r: _edit_table(
                    r, "| gate | `--self-test` |", "| gate | `--selftest` |"),
            ),
            (
                "POLICY-SELFTEST-NOT-RUN",
                "a script with a self-test that no workflow runs",
                lambda r: _edit_workflow(
                    r, "python3 tools/beta.py --self-test",
                    "python3 tools/beta.py"),
            ),
            (
                "POLICY-FLOORS-DIVERGE",
                "the floors in the table are not the floors CI passes",
                lambda r: _edit_table(
                    r, "| `--min-files 10` |", "| `--min-files 99` |"),
            ),
            (
                "POLICY-BASELINE-MISSING",
                "the table names a ratchet that is not in the tree",
                lambda r: _edit_table(
                    r, "| `--self-test` | — | `--min-files 10` |",
                    "| `--self-test` | `tools/nothing.json` | "
                    "`--min-files 10` |"),
            ),
            (
                "POLICY-HEADER-MISSING",
                "a gate that does not say why it is python",
                lambda r: _edit_tool(
                    r, "alpha.py", "Stdlib only — no pip, no network, no dotnet.",
                    "A gate over the tree."),
            ),
            (
                "POLICY-DOTNET-CALL",
                "a gate that shells out to dotnet",
                lambda r: _edit_tool(
                    r, "alpha.py", "import argparse",
                    'import argparse\nimport subprocess\n'
                    'subprocess.run(["dotnet", "build"], check=True)'),
            ),
            (
                "POLICY-TIME-CALL",
                "a gate that measures time",
                lambda r: _edit_tool(
                    r, "alpha.py", "import argparse",
                    "import argparse\nimport time\n"
                    "elapsed = time.perf_counter()"),
            ),
            (
                "POLICY-GATE-UNDOCUMENTED",
                "a compiled gate that the table does not list",
                lambda r: _edit_table(
                    r, "| `tools/bin/harbor-gates-linux-x64 gamma` | compiled: "
                       "`tools/gates/gamma` | a compiled floor over the tree | "
                       "wf-compiled.yml | compiled | gate | `--self-test` | — | "
                       "`--min-files 10` |\n", ""),
            ),
            (
                "POLICY-BINARY-MISSING",
                "a committed binary that is not in the tree",
                lambda r: _remove(
                    r, "tools/bin/harbor-gates-linux-arm64"),
            ),
            (
                "POLICY-BINARY-HASH-DIVERGE",
                "a committed binary that does not match the manifest",
                lambda r: _write(
                    os.path.join(r, "tools", "bin", "harbor-gates-linux-x64"),
                    "a binary that was edited by hand\n"),
            ),
            (
                "POLICY-GATE-SOURCE-MISSING",
                "a compiled gate whose sources are not in the tree",
                lambda r: shutil.rmtree(os.path.join(r, "tools", "gates", "gamma")),
            ),
            (
                "POLICY-GATE-TWIN",
                "a python original and a compiled twin of the same gate",
                lambda r: _write(
                    os.path.join(r, "tools", "gamma.py"),
                    '#!/usr/bin/env python3\n"""Gamma.\n\nStdlib only."""\n'),
            ),
        ]
        # --- end fixture data ---

        for code, what, plant in cases:
            case_root = os.path.join(tmp, "case")
            if os.path.exists(case_root):
                shutil.rmtree(case_root)
            os.makedirs(case_root)
            _fixture(case_root)
            plant(case_root)
            found, _ = evaluate(case_root)
            if code not in _codes(found):
                failures.append(
                    f"the {what} did not raise {code}; got "
                    f"{_codes(found) or 'no findings'}"
                )

    if failures:
        for failure in failures:
            print(f"FAIL: {failure}", file=sys.stderr)
        return 1
    print(f"self-test OK: {len(cases)} planted defects each raised their rule, "
          f"and the clean tree stayed clean.")
    return 0


def _edit_table(root: str, old: str, new: str) -> None:
    path = os.path.join(root, TABLE_DOC)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    if old not in text:
        raise AssertionError(f"fixture does not contain {old!r}")
    _write(path, text.replace(old, new, 1))


def _remove(root: str, relative: str) -> None:
    """Take a file out of the fixture tree."""
    path = os.path.join(root, relative)
    if os.path.isfile(path):
        os.remove(path)


def _edit_workflow(root: str, old: str, new: str) -> None:
    path = os.path.join(root, ".github", "workflows", "wf.yml")
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    if old not in text:
        raise AssertionError(f"fixture does not contain {old!r}")
    _write(path, text.replace(old, new, 1))


def _edit_tool(root: str, name: str, old: str, new: str) -> None:
    path = os.path.join(root, "tools", name)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    if old not in text:
        raise AssertionError(f"fixture does not contain {old!r}")
    _write(path, text.replace(old, new, 1))


# --------------------------------------------------------------------- main


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Check the gate table in docs/CI_GATES.md against the tree.")
    parser.add_argument("--repo", default=".", help="the repository root")
    parser.add_argument("--min-gates", type=int, default=0,
                        help="fail unless the table holds at least this many rows")
    parser.add_argument("--self-test", action="store_true",
                        help="prove each rule still fires on a planted defect")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    repo = os.path.abspath(args.repo)
    if not os.path.isdir(repo):
        print(f"no such repository: {repo}", file=sys.stderr)
        return 2
    if not os.path.isdir(os.path.join(repo, ".github", "workflows")):
        print(f"{repo} has no .github/workflows — nothing to check",
              file=sys.stderr)
        return 2

    findings, gates = evaluate(repo)

    if args.min_gates and gates < args.min_gates:
        findings.append(Finding(
            "POLICY-TABLE-EMPTY",
            f"the table holds {gates} row(s), the floor is {args.min_gates}",
            TABLE_DOC,
        ))

    if findings:
        for finding in findings:
            print(finding)
        print(f"\n{len(findings)} finding(s): the gate table in {TABLE_DOC} "
              f"does not describe this tree.", file=sys.stderr)
        return 1

    print(f"OK: the {gates} row(s) of the gate table in {TABLE_DOC} describe "
          f"the gates this repository runs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
