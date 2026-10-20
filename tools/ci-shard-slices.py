#!/usr/bin/env python3
"""The CI project lists have ONE source, and this gate keeps the tree honest about it.

Stdlib only — no pip, no network, no dotnet build. Wired into the `build`
job of .github/workflows/ci.yml, and it guards ONE thing:

    every place in the tree that NAMES a test project to run it says what
    the single source (tools/ci-project-lists.json) says.

WHY THIS FILE EXISTS

  The name is historical. The gate started as the comparison between the
  `test` job's shard lists and the build job's upload slices, because a
  project in one and not in the other fails the build with a "Missing
  …dll" that points at the download rather than at the list that caused
  it. That comparison is still here, and it is still the first thing the
  gate checks.

  But the comparison was never the whole problem. The project lists were
  written by hand in FOUR places, in THREE files:

    1. ci.yml, the `test` job's matrix — four shard lists, 37 projects;
    2. ci.yml, the build job's uploads — four slices, the same 37
       projects again, one glob per file kind;
    3. nightly.yml, the `coverage` job — 20 projects;
    4. test-os.yml, the OS matrix — 6 projects.

  None was derived from another, and nothing compared the whole set. The
  lists drifted silently: a comment in ci.yml said the OS matrix ran nine
  projects while the matrix ran ten; the coverage list carried a project
  that was being deleted from the tree while missing one that was in the
  `core` shard; and a new test project had to be added to three places,
  with nothing that could notice one of them being missed (issue #1800).

  So the lists now come from ONE source, tools/ci-project-lists.json, and
  this gate is what keeps them honest. It has two halves:

    --write    rewrites every derived place from the source. This is the
               generator; a human runs it after editing the source.

    (default)  the gate. It reads the source and every derived place, and
               fails when they disagree — naming the place, the project and
               the side it is on. This is what runs in CI.

  A change to the source that is not regenerated is red here. A change to
  a derived place that was not made in the source is red here. The two
  cannot both be true at once, which is the property the issue asked for.

WHAT IT CHECKS

  1. Every derived place agrees with the source: the four shard lists, the
     four upload slices, the coverage list and the OS list.
  2. The source is internally consistent: a project is in exactly one
     shard, the coverage list is the union of the shards it names minus
     the projects it excludes, the OS list is a subset of the shards, and
     the shards that must carry the CLI's own output directory do.
  3. Every project in a shard's list appears in that shard's slice, and
     the slice carries the file kinds the shard's runner needs (the
     project's own assemblies, the native assets for its RID, Harbor's own
     satellite assembly).
  4. The slices that start the CLI carry the CLI's own output directory
     (`apps/Harbor.App.Cli/bin/Release/net10.0/`). Two test helpers start
     the CLI from THERE, not from the test project's copy of the assembly:
     tests/Harbor.E2E.Framework/CliDriver.cs (the e2e shard) and
     tests/Harbor.Tui.CellForge.PtyTests/CellForgePtyScenarioBase.cs
     (the ui shard).
  5. A shard whose `mirror:` key names another shard runs the SAME
     project list as that shard. The arm64 spike (issue #1919) writes the
     four shard lists a second time, and the two copies are the only
     thing standing between the shards and a silent drift.
  6. A shard that runs on an arm64 runner gets a slice that carries the
     linux-arm64 NATIVE ASSETS as well as the linux-x64 ones. The build
     is RID-agnostic, so the arm64 assets exist in the build output; the
     upload globs pick them per RID. A slice that carries only the x64
     ones runs on x64 and fails on arm64 with a DllNotFoundException —
     a failure that names the missing library, not the missing glob.
  7. Every shard says WHICH RUNNER it runs on (`runner:`), because the
     job's `runs-on` is `${{ matrix.runner }}` — a shard without one has
     no runner at all, and, worse, check 6 would silently stop asking
     for the native assets that runner needs.

  The floors (--min-shards, --min-projects) are the non-vacuity proof: a
  gate that was handed a source with one shard and no projects must not
  report "clean".

HOW IT READS THE FILES

  A regex over the workflow text, not a YAML parser: PyYAML is not on the
  runner image, and the shapes it looks for are fixed by this file's own
  formatting (`- shard: <name>` … `projects: >-` … one project per line,
  and `name: solution-binaries…` … `path: |` … one glob per line).

  If it cannot find what it expects, it says so and exits non-zero. A
  checker that quietly checks nothing is the failure mode this file
  exists to prevent.

Usage:
  python3 tools/ci-shard-slices.py
  python3 tools/ci-shard-slices.py --write
  python3 tools/ci-shard-slices.py --self-test
  python3 tools/ci-shard-slices.py --min-shards 4 --min-projects 37

Exit codes:  0 = clean, 1 = findings, 2 = bad invocation / no input.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)

# The one source. Every list in the tree is derived from it.
SOURCE_FILE = "tools/ci-project-lists.json"
CI_WORKFLOW = ".github/workflows/ci.yml"
NIGHTLY_WORKFLOW = ".github/workflows/nightly.yml"
OS_WORKFLOW = ".github/workflows/test-os.yml"

# The CLI's own output directory. Two test helpers start the CLI from
# THERE, not from the test project's copy of the assembly:
# tests/Harbor.E2E.Framework/CliDriver.cs (the e2e shard) and
# tests/Harbor.Tui.CellForge.PtyTests/CellForgePtyScenarioBase.cs
# (the ui shard). Those two shards' slices must carry it.
CLI_OUTPUT_DIR = "apps/Harbor.App.Cli/bin/Release/net10.0"
CLI_CARRYING_SHARDS = ("ui", "e2e")

# The kinds of file a project's slice must carry: the project's own
# assemblies and JSON, the linux-x64 native assets, and Harbor's own
# satellite assembly.
REQUIRED_KINDS = {"files", "native:x64", "satellite"}

# The arm64 runners (issue #1919) load the linux-arm64 native assets, so a
# slice that an arm64 shard consumes must carry those too.
ARM_REQUIRED_KINDS = {"native:arm64"}

# The kinds of file the generator writes for one project into a slice, and
# the file kind the CLI's own output directory needs.
PROJECT_SLICE_GLOBS = (
    "{base}/*.*",
    "{base}/runtimes/linux-x64/native/*",
    "{base}/runtimes/linux-arm64/native/*",
    "{base}/*/Harbor.*.resources.dll",
)

# The lines every slice ends with: the debug symbols and the XML docs are
# not needed to run a test.
SLICE_TAIL = ("!**/*.pdb", "!**/*.xml")

# The root marker. One root-level path is enough to move the artifact
# root (the least common ancestor of the globs) up to the workspace root;
# without it a slice rooted at tests/ extracts to <workspace>/<Project>/…
# instead of <workspace>/tests/<Project>/…, and the DLLs would not be
# where the shard looks for them.
SLICE_ROOT_MARKER = "Harbor.slnx"

# The em dash the generated OS notes use, the same one the hand-written
# comment used.
EM_DASH = "—"


# ---------------------------------------------------------------------------
# The source
# ---------------------------------------------------------------------------


class SourceError(Exception):
    """The source is missing or malformed. The gate cannot see what it guards."""


def load_source(root):
    """The one source, as a dict. Raises SourceError when it is unusable."""
    path = os.path.join(root, SOURCE_FILE)
    if not os.path.isfile(path):
        raise SourceError(f"{SOURCE_FILE} is not in the tree — the lists have no source")
    try:
        with open(path, encoding="utf-8") as handle:
            source = json.load(handle)
    except (OSError, ValueError) as error:
        raise SourceError(f"{SOURCE_FILE} could not be read: {error}") from error
    if not isinstance(source, dict):
        raise SourceError(f"{SOURCE_FILE} is not an object")
    shards = source.get("shards")
    if not isinstance(shards, dict) or not shards:
        raise SourceError(f"{SOURCE_FILE} has no `shards`")
    for name, entry in shards.items():
        if not isinstance(entry, dict):
            raise SourceError(f"the `{name}` shard of {SOURCE_FILE} is not an object")
        projects = entry.get("projects")
        if not isinstance(projects, list) or not projects:
            raise SourceError(f"the `{name}` shard of {SOURCE_FILE} names no project")
        if not all(isinstance(project, str) and project for project in projects):
            raise SourceError(f"the `{name}` shard of {SOURCE_FILE} names a project that is not a name")
        if not isinstance(entry.get("cli"), bool):
            raise SourceError(f"the `{name}` shard of {SOURCE_FILE} does not say whether it carries the CLI")
    coverage = source.get("coverage")
    if not isinstance(coverage, dict):
        raise SourceError(f"{SOURCE_FILE} has no `coverage`")
    if not isinstance(coverage.get("from"), list) or not coverage["from"]:
        raise SourceError(f"the `coverage` of {SOURCE_FILE} names no shard")
    if not isinstance(coverage.get("exclude"), list):
        raise SourceError(f"the `coverage` of {SOURCE_FILE} has no `exclude` list")
    for entry in coverage["exclude"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("project"), str):
            raise SourceError(f"the `coverage` of {SOURCE_FILE} has an exclusion without a project")
        if not isinstance(entry.get("note"), str) or not entry["note"]:
            raise SourceError(
                f"the `coverage` of {SOURCE_FILE} excludes `{entry.get('project')}` "
                f"without saying why — the reason is the point of naming it")
    os_list = source.get("os")
    if not isinstance(os_list, list) or not os_list:
        raise SourceError(f"{SOURCE_FILE} has no `os` list")
    for entry in os_list:
        if not isinstance(entry, dict) or not isinstance(entry.get("project"), str):
            raise SourceError(f"the `os` list of {SOURCE_FILE} has an entry without a project")
        if not isinstance(entry.get("note"), str) or not entry["note"]:
            raise SourceError(f"the `os` list of {SOURCE_FILE} has a project without a note")
    return source


def derive(source):
    """The lists the source means, as the tree must say them.

    Returns {"shards": {name: [projects]}, "cli": [shard names],
             "coverage": [projects], "os": [projects]}.
    """
    shards = {
        name: list(entry["projects"])
        for name, entry in source["shards"].items()
    }
    cli = [name for name, entry in source["shards"].items() if entry.get("cli")]
    coverage = []
    excluded = {entry["project"] for entry in source["coverage"]["exclude"]}
    for name in source["coverage"]["from"]:
        for project in shards.get(name, []):
            if project not in excluded:
                coverage.append(project)
    return {
        "shards": shards,
        "cli": cli,
        "coverage": coverage,
        "os": [entry["project"] for entry in source["os"]],
    }


# ---------------------------------------------------------------------------
# The places the lists are written down in
# ---------------------------------------------------------------------------


def indent_of(line):
    return len(line) - len(line.lstrip())


def read_lines(root, relative):
    path = os.path.join(root, relative)
    if not os.path.isfile(path):
        raise SourceError(f"{relative} is not in the tree — the gate cannot see what it guards")
    with open(path, encoding="utf-8") as handle:
        return handle.read().splitlines()


def block_span(lines, key_index):
    """The span of a block scalar's content: (start, end), end exclusive.

    The content of a folded (`>-`) or literal (`|`) block is every line
    indented deeper than the key that opens it.
    """
    base = indent_of(lines[key_index])
    start = key_index + 1
    end = start
    while end < len(lines):
        line = lines[end]
        if not line.strip():
            break
        if indent_of(line) <= base:
            break
        end += 1
    return start, end


def job_range(lines, job_name):
    """The body of one job: (first line, one past its last line).

    A job header is two spaces, a name and a colon; the next such header
    ends the job. The shard lists live in the `test` job's matrix, and
    reading them out of the WHOLE file would let another job's
    `- shard:` entry pass for one of ours.
    """
    start = None
    for index, line in enumerate(lines):
        if re.match(rf"^  {re.escape(job_name)}:\s*$", line):
            start = index + 1
            break
    if start is None:
        return None
    end = len(lines)
    for index in range(start, len(lines)):
        if re.match(r"^  [A-Za-z0-9_.\-]+:\s*$", lines[index]):
            end = index
            break
    return start, end


def find_shard_entries(lines):
    """The `projects:` lists of the `test` job's matrix.

    Returns [(shard, entry index, key index, content start, content
    end)]. The shard name is the one the `- shard:` entry carries; an
    entry without a `projects:` list is skipped, and the gate notices it
    later.
    """
    span = job_range(lines, "test")
    if span is None:
        return []
    offset, limit = span
    entries = []
    for index in range(offset, limit):
        line = lines[index]
        match = re.match(r"^(\s*)- shard: (\S+)\s*$", line)
        if not match:
            continue
        key_index = None
        for probe in range(index + 1, limit):
            candidate = lines[probe]
            if re.match(r"^(\s*)- shard: ", candidate):
                break
            if re.match(r"^(\s*)projects: >-\s*$", candidate):
                key_index = probe
                break
        if key_index is None:
            continue
        start, end = block_span(lines, key_index)
        entries.append((match.group(2), index, key_index, start, end))
    return entries


def find_slice_blocks(lines):
    """The `path: |` blocks of the build job's upload steps.

    Returns [(artifact name, key index, content start, content end)].
    """
    blocks = []
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)name: (solution-binaries(?:-[a-z0-9-]+)?)\s*$", line)
        if not match:
            continue
        key_index = None
        for probe in range(index + 1, len(lines)):
            candidate = lines[probe]
            if re.match(r"^\s*name: ", candidate):
                break
            if re.match(r"^(\s*)path: \|\s*$", candidate):
                key_index = probe
                break
        if key_index is None:
            continue
        start, end = block_span(lines, key_index)
        blocks.append((match.group(2), key_index, start, end))
    return blocks


def find_coverage_list(lines):
    """The `for p in \\ … ; do` list of the coverage job, or None."""
    for index, line in enumerate(lines):
        if not re.match(r"^\s*for p in \\\s*$", line):
            continue
        projects = []
        for follow in lines[index + 1:]:
            stripped = follow.strip()
            if stripped.endswith("; do"):
                projects.append(stripped[: -len("; do")].strip())
                break
            if stripped.endswith("\\"):
                stripped = stripped[:-1].strip()
            if stripped:
                projects.append(stripped)
        return index, projects
    return None, None


def find_os_list(lines):
    """The `projects:` list of the OS matrix's `include:` block, or None."""
    for index, line in enumerate(lines):
        if not re.match(r"^(\s*)- projects: >-\s*$", line):
            continue
        start, end = block_span(lines, index)
        return index, start, end
    return None, None, None


def find_os_notes(lines, list_key_index):
    """The generated `#   <project> — <note>` lines above the OS list.

    Returns (first line index, last line index exclusive), or None. The
    notes are the lines that name a project of the OS list and say why it
    is there; they are generated from the source, and a hand-written copy
    of them is a fourth copy of the list.

    They sit above the matrix's `include:` key, not directly above the
    `projects:` key, so the walk back skips that key.
    """
    notes = []
    for index in range(list_key_index - 1, -1, -1):
        line = lines[index]
        if re.match(r"^\s*include:\s*$", line):
            continue
        match = re.match(r"^(\s*)#\s{3}(\S+)\s+—\s+\S", line)
        if not match:
            break
        notes.append(index)
    if not notes:
        return None
    return (min(notes), max(notes) + 1)


# ---------------------------------------------------------------------------
# What the generator writes
# ---------------------------------------------------------------------------


def shard_list_lines(projects):
    """The content of a shard's `projects: >-` list, one project per line."""
    return list(projects)


def slice_lines(projects, carries_cli):
    """The `path: |` content of a shard's upload slice."""
    body = [SLICE_ROOT_MARKER]
    for project in projects:
        base = f"tests/{project}/bin/Release/net10.0"
        for glob in PROJECT_SLICE_GLOBS:
            body.append(glob.format(base=base))
    if carries_cli:
        for glob in PROJECT_SLICE_GLOBS:
            body.append(glob.format(base=CLI_OUTPUT_DIR))
    body.extend(SLICE_TAIL)
    return body


def coverage_list_lines(projects):
    """The `for p in \\ … ; do` list of the coverage job.

    The shell loop puts its `; do` on the LAST project's line, the way
    the hand-written list did.
    """
    body = []
    for position, project in enumerate(projects):
        tail = "; do" if position == len(projects) - 1 else " \\"
        body.append(f"{project}{tail}")
    return body


def os_note_lines(notes):
    """The `#   <project> — <note>` lines above the OS list."""
    width = max(len(project) for project, _ in notes)
    return [
        f"#   {project.ljust(width)} {EM_DASH} {note}"
        for project, note in notes
    ]


# ---------------------------------------------------------------------------
# The edits the generator makes
# ---------------------------------------------------------------------------


class Edit:
    """One derived place: the lines it holds now and the lines it must hold."""

    def __init__(self, path, label, start, end, lines, indent):
        self.path = path
        self.label = label
        self.start = start
        self.end = end
        self.lines = lines
        self.indent = indent


def plan_edits(root, source, lists):
    """Every derived place, with the lines it must hold.

    Returns (edits, problems). A problem is a place the generator cannot
    find at all — the gate reports it rather than rewriting a file it does
    not understand.
    """
    edits = []
    problems = []

    ci_lines = read_lines(root, CI_WORKFLOW)
    ci_shards = find_shard_entries(ci_lines)
    if not ci_shards:
        problems.append(f"{CI_WORKFLOW} carries no shard list — the gate cannot see what it guards")
    # A shard of the source that the matrix does not carry is a hole the
    # text comparison cannot see: there is no list to compare. The shard
    # would simply not run.
    for shard in lists["shards"]:
        if shard not in {name for name, _e, _k, _s, _en in ci_shards}:
            problems.append(
                f"the source names the `{shard}` shard, and the `test` job's matrix in "
                f"{CI_WORKFLOW} does not — the shard would not run at all")
    for shard, _entry, key_index, start, end in ci_shards:
        indent = indent_of(ci_lines[start]) if start < end else indent_of(ci_lines[key_index]) + 2
        edits.append(Edit(
            CI_WORKFLOW,
            f"the `{shard}` shard's project list in {CI_WORKFLOW}",
            start, end,
            shard_list_lines(lists["shards"].get(shard, [])),
            indent,
        ))

    ci_slices = find_slice_blocks(ci_lines)
    if not ci_slices:
        problems.append(f"{CI_WORKFLOW} carries no upload slice — the gate cannot see what it guards")
    for artifact, key_index, start, end in ci_slices:
        shard = artifact[len("solution-binaries-"):] if artifact != "solution-binaries" else None
        if shard is None or shard not in lists["shards"]:
            # The perf slice is not a shard's slice; it is the perf-gate
            # job's own artifact, and it is not derived from the source.
            continue
        indent = indent_of(ci_lines[start]) if start < end else indent_of(ci_lines[key_index]) + 2
        edits.append(Edit(
            CI_WORKFLOW,
            f"the `{artifact}` slice in {CI_WORKFLOW}",
            start, end,
            slice_lines(lists["shards"][shard], shard in lists["cli"]),
            indent,
        ))

    nightly_lines = read_lines(root, NIGHTLY_WORKFLOW)
    index, projects = find_coverage_list(nightly_lines)
    if index is None:
        problems.append(
            f"{NIGHTLY_WORKFLOW} carries no coverage list — the gate cannot see what it guards")
    else:
        indent = indent_of(nightly_lines[index + 1]) if index + 1 < len(nightly_lines) else 12
        edits.append(Edit(
            NIGHTLY_WORKFLOW,
            f"the coverage list in {NIGHTLY_WORKFLOW}",
            index + 1, index + 1 + len(projects),
            coverage_list_lines(lists["coverage"]),
            indent,
        ))

    os_lines = read_lines(root, OS_WORKFLOW)
    key_index, start, end = find_os_list(os_lines)
    if key_index is None:
        problems.append(f"{OS_WORKFLOW} carries no project list — the gate cannot see what it guards")
    else:
        indent = indent_of(os_lines[start]) if start < end else indent_of(os_lines[key_index]) + 2
        edits.append(Edit(
            OS_WORKFLOW,
            f"the OS list in {OS_WORKFLOW}",
            start, end,
            shard_list_lines(lists["os"]),
            indent,
        ))
        notes = [(entry["project"], entry["note"]) for entry in source["os"]]
        span = find_os_notes(os_lines, key_index)
        if span is None:
            problems.append(
                f"{OS_WORKFLOW} carries no generated note lines above the OS list — "
                f"the gate cannot see what it guards")
        else:
            note_indent = indent_of(os_lines[span[0]])
            edits.append(Edit(
                OS_WORKFLOW,
                f"the OS notes in {OS_WORKFLOW}",
                span[0], span[1],
                os_note_lines(notes),
                note_indent,
            ))
    return edits, problems


def apply_edits(root, edits):
    """Write the edits into the files. Returns the paths that changed."""
    by_path = {}
    for edit in edits:
        by_path.setdefault(edit.path, []).append(edit)
    changed = []
    for path, path_edits in by_path.items():
        full = os.path.join(root, path)
        with open(full, encoding="utf-8") as handle:
            lines = handle.read().splitlines()
        for edit in sorted(path_edits, key=lambda item: item.start, reverse=True):
            lines[edit.start:edit.end] = [f"{' ' * edit.indent}{line}" for line in edit.lines]
        with open(full, "w", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
        changed.append(path)
    return changed


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------


def source_problems(source, lists):
    """What is wrong with the source itself."""
    problems = []
    shards = lists["shards"]

    seen = {}
    for name, projects in shards.items():
        for project in projects:
            if project in seen:
                problems.append(
                    f"`{project}` is in the `{name}` shard and in the `{seen[project]}` shard — "
                    f"a project runs in one shard, and the gate cannot say which")
            else:
                seen[project] = name

    for name in source["coverage"]["from"]:
        if name not in shards:
            problems.append(
                f"the coverage list is the union of the `{name}` shard, and there is no such shard")
    covered = set()
    for name in source["coverage"]["from"]:
        covered.update(shards.get(name, []))
    for entry in source["coverage"]["exclude"]:
        project = entry["project"]
        if project not in covered:
            problems.append(
                f"the coverage list excludes `{project}`, and no shard the coverage list is "
                f"the union of runs it — the exclusion names nothing")
    if len(set(lists["coverage"])) != len(lists["coverage"]):
        problems.append("the coverage list names a project twice")

    every = set()
    for projects in shards.values():
        every.update(projects)
    for project in lists["os"]:
        if project not in every:
            problems.append(
                f"the OS list runs `{project}`, and no shard runs it — the OS matrix would "
                f"build a project the shards do not")
    if len(set(lists["os"])) != len(lists["os"]):
        problems.append("the OS list names a project twice")

    cli = set(lists["cli"])
    for shard in CLI_CARRYING_SHARDS:
        if shard not in cli:
            problems.append(
                f"the `{shard}` shard starts the CLI from `{CLI_OUTPUT_DIR}` "
                f"(tests/Harbor.E2E.Framework/CliDriver.cs, "
                f"tests/Harbor.Tui.CellForge.PtyTests/CellForgePtyScenarioBase.cs), "
                f"but the source says its slice does not carry it")
    for shard in cli:
        if shard not in CLI_CARRYING_SHARDS:
            problems.append(
                f"the source says the `{shard}` shard's slice carries `{CLI_OUTPUT_DIR}`, "
                f"and no helper starts the CLI from there — the slice carries a directory "
                f"nothing reads")
    return problems


def slice_kinds(lines):
    """{output directory: the kinds of file the slice's globs carry}."""
    carried = {}
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or line.startswith("!"):
            continue
        glob = re.match(r"((?:tests|apps|src)/.+?/bin/Release/net10\.0)/(.*)$", line)
        if not glob:
            continue
        base, tail = glob.group(1), glob.group(2)
        if tail == "*.*":
            kind = "files"
        else:
            native = re.fullmatch(r"runtimes/linux-([^/]+)/native/\*", tail)
            kind = f"native:{native.group(1)}" if native else tail
        if tail == "*/Harbor.*.resources.dll":
            kind = "satellite"
        carried.setdefault(base, set()).add(kind)
    return carried


def find_problems(root, min_shards=0, min_projects=0):
    """Every disagreement between the source and the tree. Empty means clean."""
    problems = []
    try:
        source = load_source(root)
    except SourceError as error:
        return [str(error)]
    lists = derive(source)

    problems.extend(source_problems(source, lists))

    try:
        edits, plan_problems = plan_edits(root, source, lists)
    except SourceError as error:
        return problems + [str(error)]
    problems.extend(plan_problems)

    # The committed text must be what the generator writes. This is the
    # check that makes "edit the source, then regenerate" the only way to
    # change a list: a hand edit of a derived place is a difference here.
    for edit in edits:
        path = os.path.join(root, edit.path)
        with open(path, encoding="utf-8") as handle:
            block_lines = handle.read().splitlines()
        found = [line.strip() for line in block_lines[edit.start:edit.end]]
        wanted = [line.strip() for line in edit.lines]
        if found == wanted:
            continue
        only_wanted = [line for line in wanted if line not in found]
        only_found = [line for line in found if line not in wanted]
        if only_wanted or only_found:
            detail = []
            if only_wanted:
                detail.append(f"the source says {only_wanted}")
            if only_found:
                detail.append(f"the file says {only_found}")
            problems.append(
                f"{edit.label} does not say what the source says — "
                f"{'; '.join(detail)}. "
                f"Run `python3 tools/ci-shard-slices.py --write` after editing "
                f"{SOURCE_FILE}")
        else:
            problems.append(
                f"{edit.label} says the same things as the source, but in a different "
                f"order or shape. "
                f"Run `python3 tools/ci-shard-slices.py --write` after editing "
                f"{SOURCE_FILE}")

    # The slice's contents, read as what they carry rather than as text:
    # a slice that dropped a project's native assets would satisfy a text
    # comparison only if the source dropped them too.
    ci_lines = read_lines(root, CI_WORKFLOW)
    slices = {
        artifact: slice_kinds(ci_lines[start:end])
        for artifact, _key, start, end in find_slice_blocks(ci_lines)
    }
    shard_entries = find_shard_entries(ci_lines)
    runners = {}
    for shard, entry_index, key_index, _start, _end in shard_entries:
        # The options belong to THIS entry: the scan stops at the entry's
        # own header, so a shard cannot inherit the runner of the shard
        # above it.
        for line in ci_lines[entry_index:key_index]:
            option = re.match(r"\s+(mirror|artifact|runner):\s*(\S+)\s*$", line)
            if option:
                runners.setdefault(shard, {})[option.group(1)] = option.group(2)

    for shard, entry in sorted(runners.items()):
        projects = lists["shards"].get(shard, [])
        artifact = entry.get("artifact") or f"solution-binaries-{shard}"
        carried = slices.get(artifact)
        if carried is None:
            problems.append(f"the `{shard}` shard has no `{artifact}` upload step")
            continue

        mirror = entry.get("mirror")
        if mirror:
            source_list = lists["shards"].get(mirror)
            if source_list is None:
                problems.append(
                    f"the `{shard}` shard mirrors `{mirror}`, which is not a shard — "
                    f"the mirror name does not match any matrix entry")
            elif source_list != projects:
                only_here = [p for p in projects if p not in source_list]
                only_there = [p for p in source_list if p not in projects]
                problems.append(
                    f"the `{shard}` shard's project list differs from the `{mirror}` shard's "
                    f"it mirrors — only in `{shard}`: {only_here}; only in `{mirror}`: {only_there}")

        runner = entry.get("runner")
        if not runner:
            problems.append(
                f"the `{shard}` shard does not say which runner it runs on — the job's "
                f"`runs-on` is `${{{{ matrix.runner }}}}`, so the entry needs a `runner:` key")
            continue

        required = set(REQUIRED_KINDS)
        if "arm" in runner:
            required |= ARM_REQUIRED_KINDS

        for project in projects:
            base = f"tests/{project}/bin/Release/net10.0"
            if base not in carried:
                problems.append(
                    f"`{project}` is in the `{shard}` shard's project list but not in the "
                    f"`{artifact}` slice — the shard would fail with 'Missing …dll'")
            else:
                missing_kinds = required - carried[base]
                if missing_kinds:
                    problems.append(
                        f"the `{artifact}` slice does not carry {sorted(missing_kinds)} for "
                        f"`{project}` — the shard would fail with 'Missing …dll'")

    for shard in lists["cli"]:
        artifact = f"solution-binaries-{shard}"
        carried = slices.get(artifact)
        if carried is None:
            problems.append(f"the `{artifact}` upload step is missing")
        elif CLI_OUTPUT_DIR not in carried:
            problems.append(
                f"the `{artifact}` slice does not carry `{CLI_OUTPUT_DIR}` — the CLI is "
                f"started from there by the e2e and PTY helpers")
        else:
            missing_kinds = (REQUIRED_KINDS | ARM_REQUIRED_KINDS) - carried[CLI_OUTPUT_DIR]
            if missing_kinds:
                problems.append(
                    f"the `{artifact}` slice does not carry {sorted(missing_kinds)} for "
                    f"`{CLI_OUTPUT_DIR}` — the CLI would not start")

    # The floors. A gate handed a source with one shard and no projects
    # must not report "clean".
    if min_shards and len(lists["shards"]) < min_shards:
        problems.append(
            f"the source names {len(lists['shards'])} shard(s), and the floor is "
            f"{min_shards} — the gate is not looking at the whole set")
    total = sum(len(projects) for projects in lists["shards"].values())
    if min_projects and total < min_projects:
        problems.append(
            f"the source names {total} project(s), and the floor is {min_projects} — "
            f"the gate is not looking at the whole set")
    return problems


def write(root):
    """Rewrite every derived place from the source."""
    source = load_source(root)
    lists = derive(source)
    edits, problems = plan_edits(root, source, lists)
    if problems:
        for problem in problems:
            print(f"::error::{problem}")
        return 1
    changed = apply_edits(root, edits)
    for path in sorted(changed):
        print(f"rewrote the derived lists in {path}")
    return 0


# ---------------------------------------------------------------------------
# The self-test
# ---------------------------------------------------------------------------


def fixture_source(shards=None, coverage_from=None, coverage_exclude=None, os_projects=None):
    """A source with the shape the real one has."""
    if shards is None:
        shards = {
            "core": {"projects": ["Harbor.Core.Tests"], "cli": False},
            "platform": {"projects": ["Harbor.Platform.Tests"], "cli": False},
            "ui": {"projects": ["Harbor.Tui.Tests"], "cli": True},
            "e2e": {"projects": ["Harbor.E2E.Tests"], "cli": True},
        }
    else:
        # A case that overrides one shard keeps the other three, because a
        # missing shard is a finding of its own.
        for name, entry in {
            "core": {"projects": ["Harbor.Core.Tests"], "cli": False},
            "platform": {"projects": ["Harbor.Platform.Tests"], "cli": False},
            "ui": {"projects": ["Harbor.Tui.Tests"], "cli": True},
            "e2e": {"projects": ["Harbor.E2E.Tests"], "cli": True},
        }.items():
            shards.setdefault(name, entry)
    if coverage_from is None:
        coverage_from = ["core", "platform"]
    if coverage_exclude is None:
        coverage_exclude = []
    coverage_exclude = [
        entry if isinstance(entry, dict)
        else {"project": entry, "note": "the fixture's reason"}
        for entry in coverage_exclude
    ]
    if os_projects is None:
        os_projects = [{"project": "Harbor.Core.Tests", "note": "the core"}]
    return {
        "version": 1,
        "shards": shards,
        "coverage": {"from": coverage_from, "exclude": coverage_exclude},
        "os": os_projects,
    }


def fixture_ci(source, lists):
    """A ci.yml with the shape the real one has, built from the source."""
    lines = [
        "name: ci",
        "on:",
        "  push:",
        "jobs:",
        "  build:",
        "    runs-on: ubuntu-latest",
        "    steps:",
    ]
    for shard, projects in lists["shards"].items():
        lines.extend([
            f"      - name: Stash build outputs — {shard} shard",
            "        uses: actions/upload-artifact@v7",
            "        with:",
            f"          name: solution-binaries-{shard}",
            "          path: |",
        ])
        for line in slice_lines(projects, shard in lists["cli"]):
            lines.append(f"            {line}")
    lines.extend([
        "  test:",
        "    runs-on: ${{ matrix.runner }}",
        "    strategy:",
        "      fail-fast: false",
        "      matrix:",
        f"        shard: [{', '.join(lists['shards'])}]",
        "        include:",
    ])
    for shard, projects in lists["shards"].items():
        lines.extend([
            f"          - shard: {shard}",
            "            runner: ubuntu-latest",
            "            guard: true",
            "            projects: >-",
        ])
        for line in shard_list_lines(projects):
            lines.append(f"              {line}")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    return "\n".join(lines) + "\n"


def fixture_nightly(lists):
    """A nightly.yml with the shape the real one has."""
    lines = [
        "name: nightly",
        "on:",
        "  push:",
        "jobs:",
        "  coverage:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: Collect coverage",
        "        run: |",
        "          for p in \\",
    ]
    for line in coverage_list_lines(lists["coverage"]):
        lines.append(f"            {line}")
    lines.append("            echo \"$p\"")
    lines.append("          done")
    return "\n".join(lines) + "\n"


def fixture_os(source, lists):
    """A test-os.yml with the shape the real one has."""
    lines = [
        "name: test-os",
        "on:",
        "  push:",
        "jobs:",
        "  test-os:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os: [windows-latest]",
    ]
    notes = [(entry["project"], entry["note"]) for entry in source["os"]]
    for line in os_note_lines(notes):
        lines.append(f"        {line}")
    lines.append("        include:")
    lines.append("          - projects: >-")
    for line in shard_list_lines(lists["os"]):
        lines.append(f"              {line}")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    return "\n".join(lines) + "\n"


def fixture_tree(source=None):
    """A throwaway tree whose every derived place says what the source says."""
    root = tempfile.mkdtemp(prefix="ci-lists-")
    source = source or fixture_source()
    lists = derive(source)
    files = {
        SOURCE_FILE: json.dumps(source, indent=2) + "\n",
        CI_WORKFLOW: fixture_ci(source, lists),
        NIGHTLY_WORKFLOW: fixture_nightly(lists),
        OS_WORKFLOW: fixture_os(source, lists),
    }
    for relative, body in files.items():
        path = os.path.join(root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def self_test():
    """Plant a desync in each place and prove the gate names it."""
    failures = []
    ran = 0

    def expect(label, condition, detail=""):
        nonlocal ran
        ran += 1
        if not condition:
            failures.append(label)
            print(f"  FAIL {label}: {detail}")

    # 1. The clean tree is clean.
    root = fixture_tree()
    problems = check_tree(root)
    expect("the generated tree is clean", not problems, str(problems))
    shutil.rmtree(root, ignore_errors=True)

    # 2. The source moved, the tree did not.
    root = fixture_tree()
    path = os.path.join(root, SOURCE_FILE)
    with open(path, encoding="utf-8") as handle:
        source = json.load(handle)
    source["shards"]["core"]["projects"].append("Harbor.New.Tests")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(json.dumps(source, indent=2) + "\n")
    problems = check_tree(root)
    expect(
        "a project added to the source and not regenerated is red",
        any("Harbor.New.Tests" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 3. The tree moved, the source did not.
    root = fixture_tree()
    path = os.path.join(root, CI_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace("Harbor.Core.Tests", "Harbor.Hand.Edited.Tests"))
    problems = check_tree(root)
    expect(
        "a hand edit of the shard list is red",
        any("Harbor.Hand.Edited.Tests" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 4. The slice and the shard list drifted apart.
    root = fixture_tree()
    path = os.path.join(root, CI_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace(
            "tests/Harbor.Core.Tests/bin/Release/net10.0/*.*\n", "", 1))
    problems = check_tree(root)
    expect(
        "a slice that dropped a project is red",
        any("Harbor.Core.Tests" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 5. The coverage list drifted.
    root = fixture_tree()
    path = os.path.join(root, NIGHTLY_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace("Harbor.Core.Tests", "Harbor.Other.Tests"))
    problems = check_tree(root)
    expect(
        "a hand edit of the coverage list is red",
        any("Harbor.Other.Tests" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 6. The OS list drifted.
    root = fixture_tree()
    path = os.path.join(root, OS_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace("Harbor.Core.Tests", "Harbor.Other.Tests"))
    problems = check_tree(root)
    expect(
        "a hand edit of the OS list is red",
        any("Harbor.Other.Tests" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 7. A project in two shards.
    root = fixture_tree(fixture_source(shards={
        "ui": {"projects": ["Harbor.Core.Tests"], "cli": True},
    }))
    problems = check_tree(root)
    expect(
        "a project in two shards is red",
        any("in one shard" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 8. The coverage exclusion names nothing.
    root = fixture_tree(fixture_source(coverage_exclude=["Harbor.Not.Here.Tests"]))
    problems = check_tree(root)
    expect(
        "a coverage exclusion that names nothing is red",
        any("excludes `Harbor.Not.Here.Tests`" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 9. The OS list runs a project no shard runs.
    root = fixture_tree(fixture_source(os_projects=[
        {"project": "Harbor.Not.Here.Tests", "note": "nothing"},
    ]))
    problems = check_tree(root)
    expect(
        "an OS list that runs an unknown project is red",
        any("no shard runs it" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 10. The source says a shard carries the CLI and the gate knows it does not.
    root = fixture_tree(fixture_source(shards={
        "core": {"projects": ["Harbor.Core.Tests"], "cli": True},
    }))
    problems = check_tree(root)
    expect(
        "a slice that claims the CLI where no helper starts it is red",
        any("no helper starts the CLI from there" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 11. The floors.
    root = fixture_tree()
    problems = find_problems(root, min_shards=4, min_projects=37)
    expect(
        "the floors fire on a source with too little in it",
        any("the floor is" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 12. The real tree, with a desync planted in it.
    real = os.path.join(REPO_ROOT, SOURCE_FILE)
    if os.path.isfile(real):
        root = tempfile.mkdtemp(prefix="ci-lists-real-")
        for relative in (SOURCE_FILE, CI_WORKFLOW, NIGHTLY_WORKFLOW, OS_WORKFLOW):
            path = os.path.join(root, relative)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            shutil.copyfile(os.path.join(REPO_ROOT, relative), path)
        path = os.path.join(root, SOURCE_FILE)
        with open(path, encoding="utf-8") as handle:
            source = json.load(handle)
        shard = sorted(source["shards"])[0]
        source["shards"][shard]["projects"].append("Harbor.Planted.Tests")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(json.dumps(source, indent=2) + "\n")
        problems = check_tree(root)
        expect(
            "a desync planted in the real tree is red",
            any("Harbor.Planted.Tests" in problem for problem in problems),
            str(problems),
        )
        shutil.rmtree(root, ignore_errors=True)

    # 13. A shard of the source that the matrix does not carry.
    root = fixture_tree(fixture_source(shards={
        "brandnew": {"projects": ["Harbor.Brand.New.Tests"], "cli": False},
    }))
    path = os.path.join(root, CI_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace("- shard: brandnew", "- shard: renamed"))
    problems = check_tree(root)
    expect(
        "a shard of the source that no matrix entry carries is red",
        any("would not run at all" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 14. The source is gone: the gate must say so, not pass.
    root = fixture_tree()
    os.remove(os.path.join(root, SOURCE_FILE))
    problems = check_tree(root)
    expect(
        "a tree without the source is red, and says why",
        any("no source" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 15. A workflow without the list: the gate must say so, not pass.
    root = fixture_tree()
    path = os.path.join(root, CI_WORKFLOW)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace("            projects: >-\n", "            runner: x\n"))
    problems = check_tree(root)
    expect(
        "a workflow without a shard list is red, and says why",
        any("cannot see what it guards" in problem for problem in problems),
        str(problems),
    )
    shutil.rmtree(root, ignore_errors=True)

    if failures:
        print(f"\nself-test FAILED: {len(failures)} of {ran} fixture(s): {', '.join(failures)}")
        return 1
    print(f"\nself-test passed: {ran} fixtures")
    return 0


def check_tree(root):
    return find_problems(root)


# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description="The CI project lists have one source; this gate keeps the tree honest about it.")
    parser.add_argument("--write", action="store_true",
                        help="rewrite every derived place from the source")
    parser.add_argument("--self-test", action="store_true",
                        help="plant a desync in each place and prove the gate names it")
    parser.add_argument("--min-shards", type=int, default=0,
                        help="floor: how many shards the source must name")
    parser.add_argument("--min-projects", type=int, default=0,
                        help="floor: how many projects the source must name")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.write:
        return write(REPO_ROOT)

    problems = find_problems(REPO_ROOT, args.min_shards, args.min_projects)
    if problems:
        print("::error::the CI project lists and their source disagree:")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    source = load_source(REPO_ROOT)
    lists = derive(source)
    total = sum(len(projects) for projects in lists["shards"].values())
    print(
        f"ok: {len(lists['shards'])} shards, {total} projects, "
        f"{len(lists['coverage'])} covered, {len(lists['os'])} on the OS matrix — "
        f"one source, every place says what it says")
    return 0


if __name__ == "__main__":
    sys.exit(main())
