#!/usr/bin/env python3
"""Gate: does every test project in the solution actually run in CI?

Stdlib only. No pip, no network, no dotnet build, no restore. It reads the
tree and the workflow files and answers one question per file.

WHY THIS FILE EXISTS (issue #1823)

    The test shards in `.github/workflows/ci.yml` are hand-written project
    lists. There are six of them across two workflow files (four shard
    lists in the `test` job matrix, the `coverage` job's list and the
    `perf-gate` job), plus a derived list in the NUKE `Test` target. None is
    derived from another, and nothing compared them against the tree.

    The first draft of this gate assumed the obvious thing: a test project
    that no list names does not run. That assumption was WRONG, and the
    repository's own source says so.

    `tests/Harbor.E2E.App.Avalonia/E2eShards.cs` states it: "TUnit
    discovers and runs a referenced test project's classes in every project
    that references it, so those 26 tests are part of this process today".
    A test project therefore has TWO ways to reach a CI run:

      1. a list names it, and its own assembly is executed; or
      2. a project that a list names REFERENCES it, and its test classes
         are discovered inside that referencing project's process.

    So the completeness question is not "is the project in a list". It is
    "does any CI execution context discover its test classes". A project
    with no list entry AND no incoming reference from a listed project is
    the hole.

    And the same mechanic creates a second hole, in the opposite
    direction. A referenced test project's classes are discovered in EVERY
    project that references it, so a test project referenced by six listed
    projects runs its tests six times per CI run. Measured on
    dev@8d09f569:

        Harbor.E2E.Framework   26 [Test] methods, referenced by 6 listed
                                projects -> 156 executions per CI run

    Six of the referencing projects use the framework's types. One does
    not: `Harbor.Ipc.Tests` references `Harbor.E2E.Framework` and names
    none of its types, so the reference buys nothing and costs the IPC
    test project 14 assemblies in its closure (22 -> 8) and 26 duplicated
    test executions in the `platform` shard.

WHAT THE GATE ANSWERS

    PROJECT-UNCLAIMED     A test project with tests that no CI execution
                          context discovers. Its tests never run.
    PROJECT-EMPTY         A test project with no `[Test]` method. Allowed,
                          but only with a declared reason and an owner.
    TEST-DUPLICATED       A test project's test classes are discovered in
                          more than one CI execution context, because
                          several listed projects reference it.
    TEST-REFERENCE-UNUSED A test project references another test project
                          at runtime and uses none of its types. The
                          reference duplicates the referenced project's
                          tests and drags its product closure in for
                          nothing.
    SHARD-UNWIRED         A project defines a treenode-filter partition on
                          disk and no CI list runs it with a filter, so the
                          partition is dead code.
    LIST-STALE            A CI list names a project the tree does not have.
    CLAIM-CONDITIONAL     The only list that names a project runs only
                          under a condition, so the project may not
                          execute.
    PROJECT-NOT-IN-SOLUTION
                          A test project on disk that the solution does
                          not carry, so no CI job can even build it.
    SOLUTION-DRIFT        The solution names a project the tree does not
                          have.
    BASELINE-STALE        A baseline row the tree no longer produces.
    BASELINE-UNJUSTIFIED  A baseline row with no reason or no owner.
    SCAN-BLIND            Something the gate needed to read was not there.

WHAT THE GATE CANNOT ANSWER, STATED RATHER THAN IMPLIED

    It cannot know whether a job actually RAN a project. That is a fact
    about a run, and a run is not in the tree. The gate reads what the
    workflow files SAY; the CI run is what proves the rest. A green gate
    means "every test project's classes are discovered by some CI
    execution context", not "every test project executed on the last
    push".

    For the same reason it reports CLAIM-CONDITIONAL rather than pretending
    to know the outcome: a list whose job carries an `if:` may or may not
    have run, and the gate quotes the condition so a reader can decide.

    It cannot prove that TUnit discovers a referenced test project's
    classes. That behaviour is TUnit's, and the repository states it in
    `tests/Harbor.E2E.App.Avalonia/E2eShards.cs` and in the TRX of run
    39399653820 (133 tests in `Harbor.E2E.App.Avalonia`, of which 26 are
    `Harbor.E2E.Framework.StateTestRunnerTests`). The gate takes the
    statement as the repository's own and reasons from it.

    It also cannot see a project that is missing from the tree entirely. A
    list entry for a deleted project is caught (LIST-STALE), but a project
    that was deleted along with its tests leaves nothing to catch.

THE LISTS, AND WHICH OF THEM COUNT

    A CI list is a place in a workflow that NAMES a test project to run it.

      ci:test:core        ci.yml, `test` job, `core` shard        (primary)
      ci:test:platform    ci.yml, `test` job, `platform` shard    (primary)
      ci:test:ui          ci.yml, `test` job, `ui` shard          (primary)
      ci:test:e2e         ci.yml, `test` job, `e2e` shard         (primary)
      ci:coverage         ci.yml, `coverage` job's list
      ci:perf-gate        ci.yml, `perf-gate` job
      os:test-os          test-os.yml, `test-os` job's matrix list

    The four shard lists are PRIMARY. They are the repository's definition
    of "a test project runs in CI", and the `test` job's own `if:` is the
    repository-wide change filter (a docs-only PR does not run the test
    job, and that is by design). That filter is reported ONCE, per list,
    instead of once per project: a condition every project shares says
    something about the job, not about any project.

    Two more lists exist and are reported but do not count as CI claims,
    because neither is a gate:

      nuke:TestTarget     build/_build/Targets/TestTarget.cs. DERIVED from
                          the solution, not hand-written: it runs every
                          project whose name ends in `Tests` minus the ones
                          whose name contains E2E / LoadTests / PerfTests /
                          Benchmarks / TestKit / PtyTests. A new project
                          cannot be forgotten by it, so it is a census, not
                          a claim.
      goldens:default     goldens.yml's default suite. Manual dispatch
                          only, and the workflow says so itself.

    The `contrib/` tree holds ten more test projects (Blazor, Maui, Wpf,
    Scripting, the contrib E2E suites). `contrib/` is not in `Harbor.slnx`,
    is not compiled by CI, and AGENTS.md calls it unmaintained. This gate
    does not require them anywhere, and it says so rather than implying the
    tree has only the projects under `tests/`.

HOW A PROJECT IS CLASSIFIED

    The universe is the projects under `tests/` that the solution carries.

      IsTestProject       Read from the project's own `.csproj`. `false`
                          there wins. Otherwise the value is inherited from
                          `tests/Directory.Build.props`, which sets it to
                          `true` for every project under `tests/`. The
                          default is READ, not assumed: if that props file
                          stops setting it, the gate follows the file.
      has tests           At least one `[Test]` / `[TestAttribute]` in the
                          project's `.cs` files, with comments and string
                          literals stripped first so a `[Test]` in a doc
                          comment is not counted.
      runtime reference   A `<ProjectReference>` from one test project to
                          another, WITHOUT `ReferenceOutputAssembly=false`
                          and WITHOUT `OutputItemType=Analyzer`. A
                          build-time reference drags no test classes into
                          a test process, so it is not a reference for this
                          gate's purposes.

    `Harbor.Benchmarks` and `Harbor.TestKit.Generators` set
    `IsTestProject=false` and are not test projects. `Harbor.TestKit` is a
    fixture library that inherits `true` and has no `[Test]`: it is a
    PROJECT-EMPTY finding, carried in the baseline with a reason.

THE BASELINE IS A RATCHET

    `tools/test-project-baseline.txt` carries the findings the tree is
    allowed to have. A finding the file does not carry is RED. A row the
    tree no longer produces is RED. The file can only shrink, so the gate
    gets stricter as the tree is fixed, and a fixed hole cannot stay
    blessed.

    Every row carries a reason and an owner, and the reason has a length
    floor and may not be a bare URL. A row is a permission, and a
    permission nobody can justify is the hole this file exists to close.

    A row is keyed on the RULE and the SUBJECT, not on the message. A
    finding whose detail changed (a duplication count went from six to
    five, say) still matches its row; a finding that disappeared does not,
    and the row has to go with it.

NON-VACUITY

    `--self-test` builds throwaway trees and asserts the failure path. The
    fixtures cover: a clean tree staying clean; a project that no list and
    no referencing project reaches; a project that no list names but a
    LISTED project references, which is NOT a hole (this is the case the
    first draft of this gate got wrong); a project reached only through an
    UNLISTED project's reference, which IS a hole; a stale list entry; a
    project claimed only behind a condition, with that condition quoted; the
    primary list's own job condition not condemning every project that
    shares it; a project whose tests run in more than one context; a
    reference that uses none of the referenced project's types, and one that
    does use them; a build-time reference (an analyzer, or one with no
    output assembly) duplicating nothing; a treenode partition that no list
    applies, and one that a list does apply; a test project with no tests;
    a baseline row that matches nothing; a baseline row with no reason; a
    finding the baseline does not carry failing; a scan floor firing on a
    truncated list; a list that names nothing; a workflow with no primary
    list; and a test project missing from the solution.

    Every fixture added after the first version was checked by mutation:
    it fails when its own fix is reverted.
"""
import argparse
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

# The files the gate reads. Narrowing any of them is a reviewable diff.
SOLUTION_FILE = "Harbor.slnx"
TESTS_DIR = "tests"
TESTS_PROPS = os.path.join(TESTS_DIR, "Directory.Build.props")
CI_WORKFLOW = os.path.join(".github", "workflows", "ci.yml")
OS_WORKFLOW = os.path.join(".github", "workflows", "test-os.yml")
NUKE_TEST_TARGET = os.path.join(
    "build", "_build", "Targets", "TestTarget.cs"
)
BASELINE_FILE = os.path.join("tools", "test-project-baseline.txt")

# The rules a baseline row may carry. A row with any other rule is a
# malformed row, not an exception.
BASELINE_RULES = (
    "PROJECT-UNCLAIMED",
    "PROJECT-EMPTY",
    "TEST-DUPLICATED",
    "TEST-REFERENCE-UNUSED",
    "SHARD-UNWIRED",
    "CLAIM-CONDITIONAL",
    "LIST-STALE",
)

# The reason has to say something. The floor and the URL rule are the ones
# tools/aot-publish-gate.py uses for its baseline, for the same reason: a
# URL says where the debt is, not why it is allowed here.
MIN_REASON_LENGTH = 40
_URL_ONLY = re.compile(r"^\W*https?://\S+[.,;]?\W*$")

# `[Test]` and `[TestAttribute]`, and nothing that merely starts with
# "Test" (`[TestFixture]` does not match: `\b` fails before the "F").
_TEST_ATTRIBUTE = re.compile(r"\[\s*Test(?:Attribute)?\b")

# Comments and string literals, blanked out before the attributes are
# counted. A `[Test]` inside a doc comment or a string is not a test.
_NOT_CODE = re.compile(
    r"//[^\n]*"
    r"|/\*.*?\*/"
    r'|@"[^"]*(?:""[^"]*)*"'
    r'|"(?:\\.|[^"\\])*"'
    r"|'(?:\\.|[^'\\])*'",
    re.S,
)

# Comments only, with the string literals KEPT. A type name or a filter
# expression lives in a string, and this pass is about what the code says.
# The scanner below is a scanner rather than a regex because a regex for
# block comments eats the `/*` inside a string literal: the treenode filter
# expression the partition marker looks for IS a string literal that starts
# with `/*`.

# The treenode filter expression a test project uses to split itself into
# shards: `/*/*/(ClassA)|(ClassB)/*`. Its presence in code (not in a
# comment) is the marker that the project defines a partition.
_TREENODE_PARTITION = re.compile(r'"(/\*/\*/\()')

# A type declaration: the name of a class, record, struct, interface or
# enum the project declares.
_TYPE_DECLARATION = re.compile(
    r"^\s*(?:public|internal|file)\s+"
    r"(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*"
    r"(?:class|record|struct|interface|enum)\s+([A-Za-z0-9_]+)",
    re.M,
)

_PROJECT_IN_PATH = re.compile(
    r"tests/([A-Za-z0-9_.]+)/bin/Release/net10\.0/([A-Za-z0-9_.]+)\.dll"
)

# A workflow key that opens a folded block scalar: `projects: >-`.
_FOLDED_KEY = re.compile(r"^(\s*)([A-Za-z0-9_.\-]+):\s*(?:>|\|)[-+]?\s*$")
# The same, as a sequence entry: `- projects: >-`.
_DASH_FOLDED_KEY = re.compile(
    r"^(\s*)-\s*([A-Za-z0-9_.\-]+):\s*(?:>|\|)[-+]?\s*$"
)
# A `for p in \` ... `; do` list, the shape the coverage job uses.
_FOR_LIST = re.compile(r"^\s*for\s+([A-Za-z0-9_]+)\s+in\s+\\?\s*$")
# A line that opens a new mapping key, used to end a folded block.
_OPENS_KEY = re.compile(r"^[A-Za-z0-9_.\-]+:")


class Finding:
    """One thing the gate found.

    `rule` names the class of the finding, `subject` the thing it is about,
    and the two together are the key a baseline row has to match.
    """

    def __init__(self, rule, subject, message, blocking=True):
        self.rule = rule
        self.subject = subject
        self.message = message
        self.blocking = blocking

    @property
    def key(self):
        return (self.rule, self.subject)

    def __str__(self):
        return f"{self.rule}  {self.subject}: {self.message}"


def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


def indent_of(line):
    return len(line) - len(line.lstrip())


def strip_comments(text):
    """The text with its comments blanked out and its strings left alone.

    A scanner and not a regex: the treenode filter expression this file
    looks for is a STRING literal that starts with `/*`, and a regex for
    block comments would eat it and everything after it.
    """
    out = []
    index = 0
    size = len(text)
    while index < size:
        char = text[index]
        if char == "/" and index + 1 < size and text[index + 1] == "/":
            end = text.find("\n", index)
            end = size if end == -1 else end
            out.append(" " * (end - index))
            index = end
            continue
        if char == "/" and index + 1 < size and text[index + 1] == "*":
            end = text.find("*/", index + 2)
            end = size if end == -1 else end + 2
            out.append(
                "".join(c if c == "\n" else " " for c in text[index:end])
            )
            index = end
            continue
        if char == "@" and index + 1 < size and text[index + 1] == '"':
            end = index + 2
            while end < size:
                if text[end] == '"':
                    if end + 1 < size and text[end + 1] == '"':
                        end += 2
                        continue
                    end += 1
                    break
                end += 1
            out.append(text[index:end])
            index = end
            continue
        if char in ('"', "'"):
            end = index + 1
            while end < size:
                if text[end] == "\\":
                    end += 2
                    continue
                if text[end] == char:
                    end += 1
                    break
                if text[end] == "\n":
                    break
                end += 1
            out.append(text[index:end])
            index = end
            continue
        out.append(char)
        index += 1
    return "".join(out)


# ---------------------------------------------------------------------------
# The projects
# ---------------------------------------------------------------------------


def tracked_test_projects(root):
    """Every `.csproj` under `tests/`, repository-relative.

    The INDEX (`git ls-files`) rather than a glob, for the reason
    `tools/check-test-packages.py` states: an untracked scratch project must
    not be able to change what the gate sees, and a tracked project must not
    be able to hide from it. The walk is the fallback for a throwaway
    fixture tree, which is not a git repository.
    """
    try:
        proc = subprocess.run(
            ["git", "ls-files", "--", TESTS_DIR],
            cwd=root,
            capture_output=True,
            text=True,
            check=True,
        )
        tracked = sorted(
            line for line in proc.stdout.splitlines() if line.endswith(".csproj")
        )
        if tracked:
            return tracked
    except (OSError, subprocess.CalledProcessError):
        pass

    found = []
    base = os.path.join(root, TESTS_DIR)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if name.endswith(".csproj"):
                full = os.path.join(dirpath, name)
                found.append(
                    os.path.relpath(full, root).replace(os.sep, "/")
                )
    return sorted(found)


def solution_projects(root):
    """Every project path the solution carries, repository-relative."""
    text = read(os.path.join(root, SOLUTION_FILE))
    return sorted(
        set(re.findall(r'<Project\s+Path="([^"]+)"', text))
    )


def default_is_test_project(root):
    """The `IsTestProject` value a project under `tests/` inherits.

    Read out of `tests/Directory.Build.props` rather than assumed, so a
    change to that file changes the gate's universe with it.
    """
    text = read(os.path.join(root, TESTS_PROPS))
    if re.search(r"<IsTestProject>\s*false", text, re.I):
        return False
    if re.search(r"<IsTestProject>\s*true", text, re.I):
        return True
    return None


def explicit_is_test_project(csproj_text):
    """`True` or `False` when the project says so, `None` when it does not."""
    if re.search(r"<IsTestProject>\s*false", csproj_text, re.I):
        return False
    if re.search(r"<IsTestProject>\s*true", csproj_text, re.I):
        return True
    return None


def project_references(csproj_text):
    """The projects a `.csproj` references, with their attributes.

    The attributes matter: `ReferenceOutputAssembly=false` and
    `OutputItemType=Analyzer` mark a BUILD-TIME reference, and a build-time
    reference drags no test classes into a test process.
    """
    try:
        root = ET.fromstring(csproj_text)
    except ET.ParseError:
        return []
    found = []
    for element in root.iter():
        if not element.tag.endswith("ProjectReference"):
            continue
        include = (element.get("Include") or "").replace("\\", "/")
        name = os.path.basename(include)
        if not name.endswith(".csproj"):
            continue
        found.append(
            (
                name[: -len(".csproj")],
                {
                    key.lower(): (value or "").lower()
                    for key, value in element.attrib.items()
                    if key != "Include"
                },
            )
        )
    return found


def is_runtime_reference(attributes):
    """Whether a reference pulls the referenced assembly into the process."""
    if attributes.get("referenceoutputassembly") == "false":
        return False
    if attributes.get("outputitemtype") == "analyzer":
        return False
    return True


def project_graph(root):
    """Every project in the tree and the projects it references."""
    base = pathlib.Path(root)
    graph = {}
    for pattern in ("src/*/*.csproj", "apps/*/*.csproj", "tests/*/*.csproj"):
        for path in sorted(base.glob(pattern)):
            graph[path.stem] = project_references(read(path))
    return graph


def assembly_closure(graph, start, skip=()):
    """The projects reachable from `start`, following runtime references."""
    seen = set()
    stack = [start]
    while stack:
        current = stack.pop()
        for name, attributes in graph.get(current, []):
            if name in skip or not is_runtime_reference(attributes):
                continue
            if name not in seen:
                seen.add(name)
                stack.append(name)
    return seen


def count_tests(root, project_dir):
    """How many `[Test]` methods the project carries.

    Comments and string literals are blanked out first, so a `[Test]` in a
    doc comment or in a message string is not counted as a test.
    """
    total = 0
    base = os.path.join(root, project_dir)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            text = read(os.path.join(dirpath, name))
            total += len(_TEST_ATTRIBUTE.findall(_NOT_CODE.sub(" ", text)))
    return total


def declared_types(root, project_dir):
    """The names of the types a project declares."""
    names = set()
    base = os.path.join(root, project_dir)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            text = _NOT_CODE.sub(" ", read(os.path.join(dirpath, name)))
            for match in _TYPE_DECLARATION.finditer(text):
                names.add(match.group(1))
    return names


def uses_types(root, project_dir, type_names):
    """Whether a project's sources name any of `type_names`.

    Comments and string literals are blanked out, so a type named in a
    comment is not a use. The check errs towards silence: a type name that
    collides with a local declaration counts as a use.
    """
    if not type_names:
        return False
    pattern = re.compile(
        r"\b(?:" + "|".join(re.escape(name) for name in sorted(type_names)) + r")\b"
    )
    base = os.path.join(root, project_dir)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            text = _NOT_CODE.sub(" ", read(os.path.join(dirpath, name)))
            if pattern.search(text):
                return True
    return False


def defines_partition(root, project_dir):
    """Whether a project defines a treenode-filter partition of itself.

    The marker is the filter expression itself, in code rather than in a
    comment: a project that writes `"/*/*/("` has a partition it means to
    apply.
    """
    base = os.path.join(root, project_dir)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            text = strip_comments(read(os.path.join(dirpath, name)))
            if _TREENODE_PARTITION.search(text):
                return True
    return False


def collect_projects(root):
    """The projects the gate reasons about, and what it knows about them."""
    inherited = default_is_test_project(root)
    projects = {}
    for relative in tracked_test_projects(root):
        name = os.path.splitext(os.path.basename(relative))[0]
        text = read(os.path.join(root, relative))
        explicit = explicit_is_test_project(text)
        is_test = bool(inherited) if explicit is None else explicit
        project_dir = os.path.dirname(relative)
        projects[name] = {
            "name": name,
            "path": relative,
            "dir": project_dir,
            "is_test_project": is_test,
            "tests": count_tests(root, project_dir) if is_test else 0,
        }
    return projects, inherited


# ---------------------------------------------------------------------------
# The workflow lists
# ---------------------------------------------------------------------------


def workflow_jobs(text):
    """The jobs of a workflow, as {name: [body lines]}.

    The body of a job is every line between its header and the next job's
    header. A workflow file is YAML; no YAML parser is in the standard
    library, so this reads the shape the repository's own workflows use:
    two-space job headers under a top-level `jobs:` key.
    """
    lines = text.splitlines()
    jobs = {}
    in_jobs = False
    current = None
    for line in lines:
        if re.match(r"^jobs:\s*$", line):
            in_jobs = True
            continue
        if not in_jobs:
            continue
        header = re.match(r"^  ([A-Za-z0-9_.\-]+):\s*$", line)
        if header:
            current = header.group(1)
            jobs[current] = []
            continue
        if current is not None:
            jobs[current].append(line)
    return jobs


def job_condition(job_lines):
    """The job-level `if:` of a job, as written, or "" when it has none."""
    for index, line in enumerate(job_lines):
        match = re.match(r"^    if:\s*(.*)$", line)
        if not match:
            continue
        value = match.group(1).strip()
        if value in (">-", ">", "|", "|-", "|+"):
            parts = []
            base_indent = indent_of(line)
            for follow in job_lines[index + 1:]:
                if not follow.strip():
                    continue
                if indent_of(follow) <= base_indent:
                    break
                parts.append(follow.strip())
            return " ".join(parts)
        return value
    return ""


def job_applies_filter(job_lines):
    """Whether a job passes a treenode filter to the projects it runs."""
    return any("--treenode-filter" in line for line in job_lines)


def folded_block(lines, index):
    """The tokens of the folded block scalar that starts at `lines[index]`.

    `lines[index]` opens the block, either as `projects: >-` or as the
    sequence entry `- projects: >-`. The block runs until a line at the
    opening line's own indentation, or a line that opens a new key.
    """
    match = _FOLDED_KEY.match(lines[index]) or _DASH_FOLDED_KEY.match(lines[index])
    if not match:
        return []
    base_indent = len(match.group(1))
    tokens = []
    for follow in lines[index + 1:]:
        if not follow.strip():
            continue
        if indent_of(follow) <= base_indent:
            break
        stripped = follow.strip()
        if _OPENS_KEY.match(stripped):
            break
        tokens.append(stripped)
    return tokens


def shell_list(lines, index):
    """The tokens of the `for p in \\ ... ; do` list at `lines[index]`."""
    tokens = []
    for follow in lines[index + 1:]:
        stripped = follow.strip()
        if stripped.endswith("; do"):
            tokens.append(stripped[: -len("; do")].strip())
            break
        if stripped.endswith("\\"):
            stripped = stripped[:-1].strip()
        if stripped:
            tokens.append(stripped)
    return [token for token in tokens if token]


def matrix_projects(job_lines):
    """The `projects:` lists of a job's matrix `include:` block.

    Returns [(label, tokens, line index)], where the label is the shard name
    when the entry carries one and the list's position otherwise.
    """
    include_at = None
    for index, line in enumerate(job_lines):
        if re.match(r"^\s*include:\s*$", line):
            include_at = index
            break
    if include_at is None:
        return []

    results = []
    label = None
    for index in range(include_at + 1, len(job_lines)):
        line = job_lines[index]
        if not line.strip():
            continue
        if indent_of(line) <= indent_of(job_lines[include_at]):
            break
        shard = re.match(r"^\s*-\s*shard:\s*([A-Za-z0-9_.\-]+)\s*$", line)
        if shard:
            label = shard.group(1)
            continue
        opened = _FOLDED_KEY.match(line) or _DASH_FOLDED_KEY.match(line)
        if opened:
            if opened.group(2) != "projects":
                continue
            tokens = folded_block(job_lines, index)
            if tokens:
                results.append((label, tokens, index))
            label = None
    return results


def coverage_list(job_lines):
    """The `for p in \\ ... ; do` list of the coverage job."""
    for index, line in enumerate(job_lines):
        if _FOR_LIST.match(line):
            tokens = shell_list(job_lines, index)
            if tokens:
                return tokens
    return []


def perf_gate_projects(job_lines):
    """The projects the perf-gate job runs, read out of its dll paths."""
    found = []
    for line in job_lines:
        for name, dll in _PROJECT_IN_PATH.findall(line):
            if name == dll and name not in found:
                found.append(name)
    return found


def project_conditions(text, project):
    """Every `if:` in a workflow whose text names this project.

    A job-level `if:` applies to a whole list. This is the narrower shape:
    a condition that mentions one project by name, so that one project in a
    list may be gated while the rest of the list is not.
    """
    found = []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped.startswith("if:"):
            continue
        value = stripped[3:].strip()
        if project in value:
            found.append(value)
    return found


def per_project_conditions(text, projects):
    """The conditions that name one project, keyed by project name."""
    return {
        project: project_conditions(text, project) for project in projects
    }


def collect_lists(root):
    """Every list in the tree that names test projects to run.

    Returns (lists, notes). A list is a dict with an id, the file it lives
    in, the job, the tokens it names, the condition it runs under, whether
    it applies a treenode filter, and whether it is primary.
    """
    lists = []
    notes = []

    def add(list_id, path, job, projects, condition, primary, applies_filter):
        lists.append(
            {
                "id": list_id,
                "file": path,
                "job": job,
                "kind": "ci",
                "primary": primary,
                "projects": projects,
                "condition": condition,
                "applies_filter": applies_filter,
            }
        )

    ci_text = read(os.path.join(root, CI_WORKFLOW))
    if not ci_text:
        notes.append(
            f"{CI_WORKFLOW} could not be read, so the shard, coverage and "
            "perf-gate lists were not seen at all. A gate that examined "
            "nothing cannot report that everything is fine."
        )
    else:
        jobs = workflow_jobs(ci_text)
        test_job = jobs.get("test")
        if test_job is None:
            notes.append(
                f"{CI_WORKFLOW} has no `test` job, so the shard lists were "
                "not seen. A list that moves to another workflow file has to "
                "be added to the table in this file."
            )
            add(
                "ci.yml::test", CI_WORKFLOW, "test", [], "", True, False
            )
        else:
            condition = job_condition(test_job)
            applies = job_applies_filter(test_job)
            shards = matrix_projects(test_job)
            if not shards:
                notes.append(
                    f"{CI_WORKFLOW}'s `test` job carries no `projects:` "
                    "list, so no shard list was seen."
                )
                add(
                    "ci.yml::test",
                    CI_WORKFLOW,
                    "test",
                    [],
                    condition,
                    True,
                    applies,
                )
            for label, tokens, _ in shards:
                shard = label or "matrix"
                add(
                    f"ci:test:{shard}",
                    CI_WORKFLOW,
                    "test",
                    tokens,
                    condition,
                    True,
                    applies,
                )

        coverage_job = jobs.get("coverage")
        if coverage_job is None:
            notes.append(
                f"{CI_WORKFLOW} has no `coverage` job. A list that moves to "
                "another workflow file has to be added to the table in this "
                "file."
            )
        else:
            # The list is a shell `for p in` loop today. A matrix-shaped
            # list is accepted too, because the shape of the job is the
            # workflow's business and the gate's question is which
            # projects the job names.
            tokens = coverage_list(coverage_job)
            if not tokens:
                for _label, block, _ in matrix_projects(coverage_job):
                    tokens.extend(block)
            add(
                "ci:coverage",
                CI_WORKFLOW,
                "coverage",
                tokens,
                job_condition(coverage_job),
                False,
                job_applies_filter(coverage_job),
            )

        perf_job = jobs.get("perf-gate")
        if perf_job is None:
            notes.append(f"{CI_WORKFLOW} has no `perf-gate` job.")
        else:
            add(
                "ci:perf-gate",
                CI_WORKFLOW,
                "perf-gate",
                perf_gate_projects(perf_job),
                job_condition(perf_job),
                False,
                job_applies_filter(perf_job),
            )

    os_text = read(os.path.join(root, OS_WORKFLOW))
    if not os_text:
        notes.append(
            f"{OS_WORKFLOW} could not be read, so the OS matrix list was "
            "not seen."
        )
    else:
        jobs = workflow_jobs(os_text)
        os_job = jobs.get("test-os")
        if os_job is None:
            notes.append(
                f"{OS_WORKFLOW} has no `test-os` job. A list that moves to "
                "another workflow file has to be added to the table in this "
                "file."
            )
        else:
            tokens = []
            for _label, block, _ in matrix_projects(os_job):
                tokens.extend(block)
            add(
                "os:test-os",
                OS_WORKFLOW,
                "test-os",
                tokens,
                job_condition(os_job),
                False,
                job_applies_filter(os_job),
            )

    return lists, notes


def nuke_test_target(root):
    """What the NUKE `Test` target would run, and what it excludes.

    The target is DERIVED from the solution: every project whose name ends
    in `Tests`, minus the ones whose name contains one of the excluded
    substrings. The substrings are read out of the source rather than
    restated here, so a change to the exclusion list changes this census
    with it.
    """
    path = os.path.join(root, NUKE_TEST_TARGET)
    text = read(path)
    if not text:
        return None
    excluded = []
    match = re.search(r"ExcludedSubstrings\s*=\s*\[(.*?)\]", text, re.S)
    if match:
        excluded = re.findall(r'"([^"]+)"', match.group(1))
    suffix = "Tests"
    suffix_match = re.search(r'Name\.EndsWith\("([^"]+)"', text)
    if suffix_match:
        suffix = suffix_match.group(1)
    return {"excluded": excluded, "suffix": suffix}


def nuke_selected(projects, target):
    """The projects the NUKE `Test` target would run."""
    if target is None:
        return []
    selected = []
    for name in sorted(projects):
        if not name.endswith(target["suffix"]):
            continue
        if any(part in name for part in target["excluded"]):
            continue
        selected.append(name)
    return selected


# ---------------------------------------------------------------------------
# The baseline
# ---------------------------------------------------------------------------


class BaselineRow:
    """One row of the baseline file: a rule, a subject, a reason, an owner."""

    def __init__(self, rule, subject, reason, owner, line_no):
        self.rule = rule
        self.subject = subject
        self.reason = reason
        self.owner = owner
        self.line_no = line_no

    @property
    def key(self):
        return (self.rule, self.subject)


def parse_baseline(root):
    """(rows, findings) read from the baseline file.

    A row with the wrong number of fields, an unknown rule, an empty
    reason, an empty owner or a reason that is only a URL is a finding and
    not a row: the file is the one place in this repository where a
    permission is granted, and a permission nobody can justify is the hole
    this gate closes.
    """
    rows = []
    findings = []
    path = os.path.join(root, BASELINE_FILE)
    if not os.path.exists(path):
        findings.append(
            Finding(
                "BASELINE-UNJUSTIFIED",
                BASELINE_FILE,
                "the baseline file does not exist. A baseline that is absent "
                "is indistinguishable from a baseline that forgives "
                "everything, so its absence is reported rather than read as "
                "'no known holes'.",
            )
        )
        return rows, findings

    for number, raw in enumerate(read(path).splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        fields = [field.strip() for field in line.split("|")]
        if len(fields) != 4:
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    f"expected 4 '|'-separated fields "
                    f"(<rule> | <subject> | <reason> | <owner>), got "
                    f"{len(fields)}: {raw!r}",
                )
            )
            continue
        rule, subject, reason, owner = fields
        if rule not in BASELINE_RULES:
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    f"{rule!r} is not a rule this gate produces. The rules "
                    f"are: {', '.join(BASELINE_RULES)}.",
                )
            )
            continue
        if not subject or not reason or not owner:
            missing = [
                name
                for name, value in (
                    ("subject", subject),
                    ("reason", reason),
                    ("owner", owner),
                )
                if not value
            ]
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    f"the {', '.join(missing)} field is empty. A row without "
                    "a reason is a permission nobody can justify.",
                )
            )
            continue
        if len(reason) < MIN_REASON_LENGTH:
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    f"the reason is {len(reason)} characters; the floor is "
                    f"{MIN_REASON_LENGTH}. A reason that short says nothing "
                    "a reviewer can check.",
                )
            )
            continue
        if _URL_ONLY.match(reason):
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    "the reason is a URL. A URL says where the debt is, not "
                    "why it is allowed here.",
                )
            )
            continue
        if not owner.startswith("@"):
            findings.append(
                Finding(
                    "BASELINE-UNJUSTIFIED",
                    f"{BASELINE_FILE}:{number}",
                    f"the owner {owner!r} is not a '@handle'. A row without "
                    "an accountable owner is an allowance with no one to ask.",
                )
            )
            continue
        rows.append(BaselineRow(rule, subject, reason, owner, number))
    return rows, findings


# ---------------------------------------------------------------------------
# The analysis
# ---------------------------------------------------------------------------


def analyse(root):
    """Return (census, findings)."""
    findings = []
    notes = []

    projects, inherited = collect_projects(root)
    solution = solution_projects(root)
    solution_tests = {
        os.path.splitext(os.path.basename(path))[0]
        for path in solution
        if path.startswith(TESTS_DIR + "/") and path.endswith(".csproj")
    }

    lists, list_notes = collect_lists(root)
    notes.extend(list_notes)

    workflow_text = {}
    for entry in lists:
        path = entry["file"]
        if path not in workflow_text:
            workflow_text[path] = read(os.path.join(root, path))
        entry["project_conditions"] = per_project_conditions(
            workflow_text[path], entry["projects"]
        )

    if inherited is None:
        findings.append(
            Finding(
                "SCAN-BLIND",
                TESTS_PROPS,
                f"{TESTS_PROPS} does not say what IsTestProject inherits, so "
                "the gate cannot tell a test project from a fixture "
                "library. The props file is where that default lives; read "
                "it there rather than assuming it.",
            )
        )

    # The universe: a project under tests/ that the solution carries.
    universe = {}
    for name, project in projects.items():
        if not project["is_test_project"]:
            continue
        if name not in solution_tests:
            findings.append(
                Finding(
                    "PROJECT-NOT-IN-SOLUTION",
                    name,
                    f"{project['path']} is a test project but {SOLUTION_FILE} "
                    "does not carry it, so no CI job builds it and no CI job "
                    "can run it. Add it to the solution, or delete it.",
                )
            )
            continue
        universe[name] = project

    for name in sorted(solution_tests - set(projects)):
        findings.append(
            Finding(
                "SOLUTION-DRIFT",
                name,
                f"{SOLUTION_FILE} carries {name}, but there is no such "
                "project under tests/. The solution names a project the tree "
                "does not have.",
            )
        )

    # The claims.
    claims = {name: [] for name in universe}
    for entry in lists:
        if not entry["projects"]:
            findings.append(
                Finding(
                    "SCAN-BLIND",
                    entry["id"],
                    f"the list {entry['id']} in {entry['file']} parsed to "
                    "zero entries. A list that names nothing was either "
                    "emptied on purpose or stopped matching, and the gate "
                    "cannot tell the two apart.",
                )
            )
        for token in entry["projects"]:
            if token in claims:
                claims[token].append(entry)
            elif token in projects and not projects[token]["is_test_project"]:
                notes.append(
                    f"{entry['id']} names {token}, which is not a test "
                    "project (IsTestProject is false there). It is not "
                    "counted as a claim."
                )
            else:
                findings.append(
                    Finding(
                        "LIST-STALE",
                        f"{entry['id']}:{token}",
                        f"the list {entry['id']} in {entry['file']} names "
                        f"{token}, and there is no such test project in the "
                        "tree. A stale entry is a list that lies about what "
                        "it runs.",
                    )
                )

    # The reference graph between test projects.
    graph = project_graph(root)
    incoming = {name: [] for name in universe}
    for name in universe:
        for target, attributes in graph.get(name, []):
            if target not in universe:
                continue
            if not is_runtime_reference(attributes):
                continue
            incoming[target].append(name)

    # The execution contexts each test project's classes are discovered in.
    contexts = {}
    for name, project in universe.items():
        found = []
        if claims.get(name):
            found.append(name)
        for referrer in incoming.get(name, []):
            if claims.get(referrer):
                found.append(referrer)
        contexts[name] = found

    # A project named by two lists is executed by two jobs. That is waste,
    # not a hole, so it is reported and does not fail the gate.
    repeated = sorted(
        name for name, entries in claims.items() if len(entries) > 1
    )
    if repeated:
        notes.append(
            f"{len(repeated)} project(s) are named by more than one list, so "
            f"they are executed by more than one job: {', '.join(repeated)}"
        )

    # The holes.
    for name, project in sorted(universe.items()):
        entries = claims.get(name, [])
        found_in = contexts.get(name, [])
        if project["tests"] == 0:
            findings.append(
                Finding(
                    "PROJECT-EMPTY",
                    name,
                    f"{name} is a test project (IsTestProject is not false "
                    "there) but carries no [Test] method. If that is "
                    "deliberate, a fixture library for instance, record it "
                    f"in {BASELINE_FILE} with a reason and an owner; if it "
                    "is not, this project has tests that no list will run.",
                )
            )
            continue
        if not found_in:
            findings.append(
                Finding(
                    "PROJECT-UNCLAIMED",
                    name,
                    f"no CI list names {name} and no project that CI runs "
                    f"references it, so TUnit never discovers its "
                    f"{project['tests']} [Test] method(s). CI would stay "
                    "green. Add it to a shard list in "
                    f"{CI_WORKFLOW}, or record why it may stay out.",
                )
            )
            continue
        if len(found_in) > 1:
            referrers = [entry for entry in found_in if entry != name]
            product = sorted(
                assembly_closure(graph, name) - set(universe)
            )
            product_note = (
                f" The referenced project's own closure carries "
                f"{len(product)} product assembl"
                f"{'y' if len(product) == 1 else 'ies'} "
                f"({', '.join(product)}), and every project that references "
                "it inherits them."
                if product
                else ""
            )
            findings.append(
                Finding(
                    "TEST-DUPLICATED",
                    name,
                    f"TUnit discovers a referenced test project's classes in "
                    f"every project that references it, so {name}'s "
                    f"{project['tests']} [Test] method(s) run "
                    f"{len(found_in)} times per CI run "
                    f"({project['tests'] * len(found_in)} executions) "
                    f"instead of once. The contexts are: "
                    f"{', '.join(found_in)}.{product_note} Reference the "
                    "project from one project only, or apply a treenode "
                    "filter that names its classes in one of them.",
                )
            )
        unconditional = [
            entry
            for entry in entries
            if entry["primary"]
            or (
                not entry["condition"]
                and not entry["project_conditions"].get(name)
            )
        ]
        if entries and not unconditional:
            conditions = "; ".join(
                f"{entry['id']}: {entry['condition']}"
                + (
                    "; "
                    + "; ".join(entry["project_conditions"][name])
                    if entry["project_conditions"].get(name)
                    else ""
                )
                for entry in entries
            )
            findings.append(
                Finding(
                    "CLAIM-CONDITIONAL",
                    name,
                    f"the only CI list that names {name} runs only under a "
                    f"condition, so the project may not execute at all. "
                    f"{conditions}. Claim it from a list that always runs, "
                    f"or record the deliberate scoping in {BASELINE_FILE}.",
                )
            )

    # A reference from one test project to another that uses none of its
    # types: the referenced project's tests are duplicated for nothing and
    # its product closure comes along for the ride.
    for name in sorted(universe):
        for target, _attributes in graph.get(name, []):
            if target not in universe or target == name:
                continue
            if not is_runtime_reference(dict(_attributes)):
                continue
            types = declared_types(root, universe[target]["dir"])
            if uses_types(root, universe[name]["dir"], types):
                continue
            without = assembly_closure(graph, name, skip=(target,))
            dragged = sorted(
                assembly_closure(graph, target) - without
            )
            product = [item for item in dragged if not item.endswith("Tests")]
            findings.append(
                Finding(
                    "TEST-REFERENCE-UNUSED",
                    f"{name} -> {target}",
                    f"{name} references the test project {target} and names "
                    "none of its types, so the reference buys nothing. It "
                    f"drags {len(product)} product assembl"
                    f"{'y' if len(product) == 1 else 'ies'} into "
                    f"{name}'s closure ({', '.join(product)}) and it makes "
                    f"{target}'s test classes run inside {name} as well. "
                    "Drop the reference, after checking that it carries no "
                    "package or analyzer the project still needs.",
                )
            )

    # A treenode partition on disk that no CI list applies.
    for name, project in sorted(universe.items()):
        if not defines_partition(root, project["dir"]):
            continue
        for entry in claims.get(name, []):
            if entry["applies_filter"]:
                continue
            findings.append(
                Finding(
                    "SHARD-UNWIRED",
                    name,
                    f"{name} defines a treenode-filter partition of itself, "
                    f"but the list {entry['id']} in {entry['file']} runs it "
                    "without a filter, so the partition is dead code: the "
                    "classes it separates run in one process, and the tests "
                    "it would have moved out of the other processes run "
                    "here too. Wire the partition, or delete it.",
                )
            )
            break

    # The census of the local NUKE gate, which is derived and therefore
    # reported rather than enforced.
    target = nuke_test_target(root)
    if target is None:
        notes.append(
            f"{NUKE_TEST_TARGET} could not be read, so the local NUKE "
            "`Test` target's coverage was not seen."
        )
        nuke_runs = []
    else:
        nuke_runs = nuke_selected(universe, target)
        missed = [
            name
            for name, project in sorted(universe.items())
            if project["tests"] > 0 and name not in nuke_runs
        ]
        if missed:
            notes.append(
                f"the local NUKE `Test` target does not run "
                f"{len(missed)} project(s) that carry tests "
                f"({', '.join(missed)}); it selects by name suffix minus "
                f"{', '.join(target['excluded'])}."
            )

    # The baseline decides what is allowed and what is a new hole.
    rows, baseline_findings = parse_baseline(root)
    findings.extend(baseline_findings)
    allowed = {row.key for row in rows}

    current = {finding.key for finding in findings}
    for finding in findings:
        if finding.rule in ("BASELINE-STALE", "BASELINE-UNJUSTIFIED"):
            continue
        if finding.key in allowed:
            finding.blocking = False
            notes.append(
                f"{finding.rule} {finding.subject} is carried by "
                f"{BASELINE_FILE}, so it is a known hole and not a new one."
            )

    for row in rows:
        if row.key in current:
            continue
        findings.append(
            Finding(
                "BASELINE-STALE",
                f"{row.rule}:{row.subject}",
                f"the baseline row on line {row.line_no} carries {row.rule} "
                f"for {row.subject}, and the tree produces no such finding "
                "any more. A row that outlives its finding is a permission "
                "with no referent. Delete the row in the same diff that "
                "closed the hole.",
            )
        )

    census = {
        "projects": projects,
        "universe": universe,
        "lists": lists,
        "claims": claims,
        "contexts": contexts,
        "incoming": incoming,
        "notes": notes,
        "inherited": inherited,
        "nuke": {"target": target, "runs": nuke_runs},
        "solution_tests": sorted(solution_tests),
        "totals": {
            "csproj": len(projects),
            "test_projects": len(universe),
            "with_tests": sum(
                1 for p in universe.values() if p["tests"] > 0
            ),
            "lists": len(lists),
            "claims": sum(1 for entries in claims.values() if entries),
            "contexts": sum(
                1 for found in contexts.values() if found
            ),
            "nuke_runs": len(nuke_runs),
        },
    }
    return census, findings


# ---------------------------------------------------------------------------
# Self-test — the failure path, on every run of the gate in CI.
# ---------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
"""

CSPROJ_REFERENCES = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\{target}\\{target}.csproj"/>
  </ItemGroup>
</Project>
"""

CSPROJ_ANALYZER = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\{target}\\{target}.csproj"
                      OutputItemType="Analyzer" />
  </ItemGroup>
</Project>
"""

CSPROJ_NO_OUTPUT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\{target}\\{target}.csproj"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
"""

CSPROJ_NOT_TEST = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>false</IsTestProject>
  </PropertyGroup>
</Project>
"""

TEST_SOURCE = """namespace Fixture;

public class Tests
{
    [Test]
    public void Works() { }
}
"""

EMPTY_SOURCE = """namespace Fixture;

public class Helpers
{
    public int Value => 1;
}
"""

HARNESS_SOURCE = """namespace Fixture;

public class Helper
{
    public int Value => 1;
}

public class HarnessTests
{
    [Test]
    public void Works() { }
}
"""

USES_HELPER_SOURCE = """namespace Fixture;

public class Consumer
{
    public Helper Helper { get; } = new Helper();
}
"""

USES_NOTHING_SOURCE = """namespace Fixture;

public class Consumer
{
}
"""

PARTITION_SOURCE = """namespace Fixture;

public static class E2eShards
{
    public static string FilterFor(string[] names)
    {
        return "/*/*/(" + string.Join(")|(", names) + ")/*";
    }
}
"""

TESTS_PROPS_FIXTURE = """<Project>
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
"""

NUKE_FIXTURE = """namespace Harbor.Build.Targets;

public static class TestTarget
{
    private static readonly string[] ExcludedSubstrings =
    [
        "E2E",
        "LoadTests",
        "PerfTests",
        "Benchmarks",
        "TestKit",
        "PtyTests",
    ];

    public static void Execute()
    {
        var projects = solution.AllProjects
            .Where(p => p.Name.EndsWith("Tests", StringComparison.Ordinal)
                && !ExcludedSubstrings.Any(s => p.Name.Contains(s, StringComparison.Ordinal)));
    }
}
"""

BASELINE_CLEAN = "# no known holes\n"

BASELINE_WITH_HOLE = (
    "PROJECT-UNCLAIMED | Orphan.Tests | the project is a placeholder that "
    "carries no tests yet and must not be scheduled | @refusedguy\n"
)


def ci_workflow(shards=None, coverage=None, perf_gate=None, test_if=None,
                coverage_if=None, perf_if=None, coverage_step_if=None,
                test_filter=None):
    """A ci.yml fixture with the shape the real one has."""
    lines = ["name: ci", "on:", "  push:", "jobs:"]
    lines.append("  test:")
    if test_if:
        lines.append(f"    if: {test_if}")
    lines.append("    runs-on: ubuntu-latest")
    lines.append("    strategy:")
    lines.append("      fail-fast: false")
    lines.append("      matrix:")
    lines.append("        shard: [core]")
    lines.append("        include:")
    for name, tokens in (shards or {}).items():
        lines.append(f"          - shard: {name}")
        lines.append("            guard: true")
        lines.append("            projects: >-")
        for token in tokens:
            lines.append(f"              {token}")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    lines.append("      - name: Run shard")
    if test_filter:
        lines.append(f"        if: {test_filter}")
    lines.append("        run: |")
    lines.append("          for p in ${{ matrix.projects }}; do")
    if test_filter:
        lines.append(
            '            dotnet exec "tests/$p/bin/Release/net10.0/$p.dll" '
            "--treenode-filter"
        )
    else:
        lines.append(
            '            dotnet exec "tests/$p/bin/Release/net10.0/$p.dll"'
        )
    lines.append("          done")

    lines.append("  coverage:")
    if coverage_if:
        lines.append(f"    if: {coverage_if}")
    lines.append("    runs-on: ubuntu-latest")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    lines.append("      - name: Collect coverage")
    if coverage_step_if:
        lines.append(f"        if: {coverage_step_if}")
    lines.append("        run: |")
    lines.append("          for p in \\")
    for token in coverage if coverage is not None else ["Clean.Tests"]:
        lines.append(f"            {token} \\")
    lines.append("            ; do")
    lines.append("            dotnet exec tests/$p/bin/Release/net10.0/$p.dll")
    lines.append("          done")

    lines.append("  perf-gate:")
    if perf_if:
        lines.append(f"    if: {perf_if}")
    lines.append("    runs-on: ubuntu-latest")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    lines.append("      - name: Run renderer performance gate")
    lines.append("        run: |")
    for token in perf_gate if perf_gate is not None else ["Clean.Tests"]:
        lines.append(
            f"          taskset -c 1 dotnet exec tests/{token}/bin/Release/"
            f"net10.0/{token}.dll --output Detailed"
        )
    return "\n".join(lines) + "\n"


def os_workflow(projects):
    """A test-os.yml fixture with the shape the real one has."""
    lines = [
        "name: test-os",
        "on:",
        "  push:",
        "jobs:",
        "  test-os:",
        "    if: >-",
        "      needs.gate.outputs.proceed == 'true' &&",
        "      (needs.changes.outputs.dotnet == 'true' ||",
        "       github.event_name == 'schedule')",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      fail-fast: false",
        "      matrix:",
        "        os: [windows-latest, macos-latest]",
        "        include:",
        "          - projects: >-",
    ]
    for token in projects:
        lines.append(f"              {token}")
    lines.append("    steps:")
    lines.append("      - uses: actions/checkout@v7")
    lines.append("      - name: Test critical suites")
    lines.append("        shell: pwsh")
    lines.append("        run: |")
    lines.append('          $projects = @("${{ matrix.projects }}" -split \'\\s+\')')
    lines.append("          foreach ($p in $projects) { }")
    return "\n".join(lines) + "\n"


def solution(paths):
    """A Harbor.slnx fixture."""
    lines = ["<Solution>", '    <Folder Name="/tests/">']
    for path in paths:
        lines.append(f'        <Project Path="{path}"/>')
    lines.append("    </Folder>")
    lines.append("</Solution>")
    return "\n".join(lines) + "\n"


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="test-project-gate-")
    for relative, body in files.items():
        path = os.path.join(root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def base_fixture(files=None):
    """The clean baseline every fixture starts from."""
    merged = {
        SOLUTION_FILE: solution(["tests/Clean.Tests/Clean.Tests.csproj"]),
        TESTS_PROPS: TESTS_PROPS_FIXTURE,
        CI_WORKFLOW: ci_workflow(shards={"core": ["Clean.Tests"]}),
        OS_WORKFLOW: os_workflow(["Clean.Tests"]),
        NUKE_TEST_TARGET: NUKE_FIXTURE,
        BASELINE_FILE: BASELINE_CLEAN,
        "tests/Clean.Tests/Clean.Tests.csproj": CSPROJ,
        "tests/Clean.Tests/Clean.Tests.cs": TEST_SOURCE,
    }
    if files:
        merged.update(files)
    return fixture(merged)


def selftest():
    failures = []
    ran = []

    def check(name, condition, detail=""):
        ran.append(name)
        if condition:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name} {detail}")
            failures.append(name)

    def shown(found):
        return " | ".join(str(f) for f in found)

    # 1. A clean tree stays clean. The false-positive control.
    root = base_fixture()
    _, found = analyse(root)
    check("a clean tree stays green", not found, shown(found))
    shutil.rmtree(root, ignore_errors=True)

    # 2. A project in the tree and in the solution, but in no list and
    #    referenced by nothing. The hole this gate exists for.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Orphan.Tests/Orphan.Tests.csproj",
                ]
            ),
            "tests/Orphan.Tests/Orphan.Tests.csproj": CSPROJ,
            "tests/Orphan.Tests/Orphan.Tests.cs": TEST_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a project missing from every list is reported",
        any(
            f.rule == "PROJECT-UNCLAIMED" and f.subject == "Orphan.Tests"
            for f in found
        ),
        shown(found),
    )
    check(
        "the unclaimed project is reported as a blocking finding",
        any(f.rule == "PROJECT-UNCLAIMED" and f.blocking for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 3. A project that no list names, but that a listed project
    #    REFERENCES. TUnit discovers its classes inside the referencing
    #    project, so its tests DO run, and the gate must not call that a
    #    hole. This is the fixture for the premise the first draft of this
    #    gate got wrong.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Harness"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": TEST_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a listed project is not reported as unclaimed",
        not any(f.rule == "PROJECT-UNCLAIMED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 4. A project that no list names and that only an UNLISTED project
    #    references. Nothing runs it.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Runner/Runner.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(shards={"core": ["Clean.Tests"]}),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": TEST_SOURCE,
            "tests/Runner/Runner.csproj": CSPROJ_REFERENCES.format(
                target="Harness"
            ),
            "tests/Runner/Runner.cs": TEST_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a project reached only through an unlisted reference is reported",
        any(
            f.rule == "PROJECT-UNCLAIMED" and f.subject == "Harness"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 5. A stale list entry: a list names a project the tree does not have.
    root = base_fixture(
        {
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Ghost.Tests"]}
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a stale list entry is reported",
        any(
            f.rule == "LIST-STALE" and f.subject.endswith("Ghost.Tests")
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 6. A project claimed only by a list whose job carries a condition.
    #    The condition has to be quoted, because whether the project ran is
    #    a fact about a run and not about the tree.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Skipped.Tests/Skipped.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests"]},
                coverage=["Skipped.Tests"],
                coverage_if="needs.changes.outputs.dotnet == 'true'",
            ),
            "tests/Skipped.Tests/Skipped.Tests.csproj": CSPROJ,
            "tests/Skipped.Tests/Skipped.Tests.cs": TEST_SOURCE,
        }
    )
    _, found = analyse(root)
    conditional = [f for f in found if f.rule == "CLAIM-CONDITIONAL"]
    check(
        "a project claimed only by a conditional list is reported",
        any(f.subject == "Skipped.Tests" for f in conditional),
        shown(found),
    )
    check(
        "the conditional finding quotes the condition",
        any(
            "needs.changes.outputs.dotnet == 'true'" in f.message
            for f in conditional
        ),
        shown(conditional),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 7. The PRIMARY list's own job condition does not condemn every
    #    project that shares it.
    root = base_fixture(
        {
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests"]},
                test_if="needs.changes.outputs.dotnet == 'true'",
            ),
        }
    )
    _, found = analyse(root)
    check(
        "the primary list's job condition does not condemn its projects",
        not any(f.rule == "CLAIM-CONDITIONAL" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 8. A test project with no [Test] method.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Fixtures/Fixtures.csproj",
                ]
            ),
            "tests/Fixtures/Fixtures.csproj": CSPROJ,
            "tests/Fixtures/Fixtures.cs": EMPTY_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a test project with no tests is reported",
        any(
            f.rule == "PROJECT-EMPTY" and f.subject == "Fixtures"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 9. A baseline row that matches nothing.
    root = base_fixture({BASELINE_FILE: BASELINE_WITH_HOLE})
    _, found = analyse(root)
    check(
        "a baseline row that matches nothing is stale",
        any(f.rule == "BASELINE-STALE" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 10. A baseline row with no reason.
    root = base_fixture(
        {
            BASELINE_FILE: "PROJECT-UNCLAIMED | Orphan.Tests | too short | \n"
        }
    )
    _, found = analyse(root)
    check(
        "a baseline row without a reason is unjustified",
        any(f.rule == "BASELINE-UNJUSTIFIED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 11. A row that DOES match its finding silences it.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Orphan.Tests/Orphan.Tests.csproj",
                ]
            ),
            "tests/Orphan.Tests/Orphan.Tests.csproj": CSPROJ,
            "tests/Orphan.Tests/Orphan.Tests.cs": TEST_SOURCE,
            BASELINE_FILE: BASELINE_WITH_HOLE,
        }
    )
    _, found = analyse(root)
    orphan = [f for f in found if f.subject == "Orphan.Tests"]
    check(
        "a baseline row silences the finding it covers",
        any(f.rule == "PROJECT-UNCLAIMED" and not f.blocking for f in orphan),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 12. A test project on disk that the solution does not carry.
    root = base_fixture(
        {
            "tests/Stray.Tests/Stray.Tests.csproj": CSPROJ,
            "tests/Stray.Tests/Stray.Tests.cs": TEST_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a test project missing from the solution is reported",
        any(
            f.rule == "PROJECT-NOT-IN-SOLUTION" and f.subject == "Stray.Tests"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 13. A project that declares IsTestProject=false is not a test
    #     project, and the gate must not demand it anywhere.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                ]
            ),
            "tests/Harness/Harness.csproj": CSPROJ_NOT_TEST,
            "tests/Harness/Harness.cs": EMPTY_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a project with IsTestProject=false is not a test project",
        not any(f.subject == "Harness" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 14. A [Test] in a comment or a string is not a test.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Docs.Tests/Docs.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Docs.Tests"]}
            ),
            "tests/Docs.Tests/Docs.Tests.csproj": CSPROJ,
            "tests/Docs.Tests/Docs.Tests.cs": (
                "namespace F;\n"
                "// [Test]\n"
                "/* [Test] */\n"
                "public class C { public string S = \"[Test]\"; }\n"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a [Test] in a comment or a string is not counted",
        any(
            f.rule == "PROJECT-EMPTY" and f.subject == "Docs.Tests"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 15. A test project's classes discovered in more than one context.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Other.Tests/Other.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Other.Tests", "Harness"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": HARNESS_SOURCE,
            "tests/Other.Tests/Other.Tests.csproj": CSPROJ_REFERENCES.format(
                target="Harness"
            ),
            "tests/Other.Tests/Other.Tests.cs": USES_HELPER_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a project whose classes run in two contexts is reported",
        any(
            f.rule == "TEST-DUPLICATED" and f.subject == "Harness"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 16. A reference that names none of the referenced project's types.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Other.Tests/Other.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Other.Tests"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": HARNESS_SOURCE,
            "tests/Other.Tests/Other.Tests.csproj": CSPROJ_REFERENCES.format(
                target="Harness"
            ),
            "tests/Other.Tests/Other.Tests.cs": USES_NOTHING_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a reference that uses none of the referenced types is reported",
        any(
            f.rule == "TEST-REFERENCE-UNUSED"
            and f.subject == "Other.Tests -> Harness"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 17. A reference that DOES use the referenced project's types is
    #     load-bearing and is not a finding.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Other.Tests/Other.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Other.Tests"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": HARNESS_SOURCE,
            "tests/Other.Tests/Other.Tests.csproj": CSPROJ_REFERENCES.format(
                target="Harness"
            ),
            "tests/Other.Tests/Other.Tests.cs": USES_HELPER_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a reference that uses the referenced types is not reported",
        not any(f.rule == "TEST-REFERENCE-UNUSED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 18. A treenode partition on disk that no CI list applies.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Sharded.Tests/Sharded.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Sharded.Tests"]}
            ),
            "tests/Sharded.Tests/Sharded.Tests.csproj": CSPROJ,
            "tests/Sharded.Tests/Sharded.Tests.cs": (
                PARTITION_SOURCE + "\n" + TEST_SOURCE
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a treenode partition no list applies is reported",
        any(
            f.rule == "SHARD-UNWIRED" and f.subject == "Sharded.Tests"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 18. The same partition, applied by the list that runs it. No
    #     finding: the partition is wired.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Sharded.Tests/Sharded.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Sharded.Tests"]},
                test_filter="matrix.shard == 'core'",
            ),
            "tests/Sharded.Tests/Sharded.Tests.csproj": CSPROJ,
            "tests/Sharded.Tests/Sharded.Tests.cs": (
                PARTITION_SOURCE + "\n" + TEST_SOURCE
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a wired treenode partition is not reported",
        not any(f.rule == "SHARD-UNWIRED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 19. A BUILD-TIME reference drags no test classes into a test process.
    #     An analyzer reference must not create a duplication finding.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Other.Tests/Other.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Other.Tests"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": HARNESS_SOURCE,
            "tests/Other.Tests/Other.Tests.csproj": CSPROJ_ANALYZER.format(
                target="Harness"
            ),
            "tests/Other.Tests/Other.Tests.cs": USES_NOTHING_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "an analyzer reference does not duplicate the referenced tests",
        not any(f.rule == "TEST-DUPLICATED" for f in found),
        shown(found),
    )
    check(
        "an analyzer reference is not an unused runtime reference",
        not any(f.rule == "TEST-REFERENCE-UNUSED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 20. The other build-time shape: a reference whose output assembly is
    #     not referenced at all.
    root = base_fixture(
        {
            SOLUTION_FILE: solution(
                [
                    "tests/Clean.Tests/Clean.Tests.csproj",
                    "tests/Harness/Harness.csproj",
                    "tests/Other.Tests/Other.Tests.csproj",
                ]
            ),
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests", "Other.Tests"]}
            ),
            "tests/Harness/Harness.csproj": CSPROJ,
            "tests/Harness/Harness.cs": HARNESS_SOURCE,
            "tests/Other.Tests/Other.Tests.csproj": CSPROJ_NO_OUTPUT.format(
                target="Harness"
            ),
            "tests/Other.Tests/Other.Tests.cs": USES_NOTHING_SOURCE,
        }
    )
    _, found = analyse(root)
    check(
        "a reference with no output assembly does not duplicate the tests",
        not any(f.rule == "TEST-DUPLICATED" for f in found),
        shown(found),
    )
    check(
        "a reference with no output assembly is not an unused runtime one",
        not any(f.rule == "TEST-REFERENCE-UNUSED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 21. A list that names nothing is blindness, not cleanliness.
    root = base_fixture(
        {
            CI_WORKFLOW: ci_workflow(
                shards={"core": ["Clean.Tests"]},
                coverage=[],
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a list that names nothing is reported",
        any(
            f.rule == "SCAN-BLIND" and f.subject == "ci:coverage"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 22. The scan floors.
    root = base_fixture()
    census, _ = analyse(root)
    shutil.rmtree(root, ignore_errors=True)
    breached = check_floors(
        census,
        [
            ("projects", 40, census["totals"]["csproj"]),
            ("CI lists", 7, census["totals"]["lists"]),
            ("claimed projects", 36, census["totals"]["claims"]),
        ],
    )
    check(
        "a scan floor fires on a truncated list",
        len(breached) == 3,
        str(breached),
    )
    check(
        "a scan floor passes on a full list",
        not check_floors(
            census,
            [
                ("projects", 1, census["totals"]["csproj"]),
                ("CI lists", 4, census["totals"]["lists"]),
                ("claimed projects", 1, census["totals"]["claims"]),
            ],
        ),
        str(census["totals"]),
    )

    # 23. A workflow without the primary list.
    root = base_fixture(
        {
            CI_WORKFLOW: "name: ci\njobs:\n  coverage:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n",
        }
    )
    _, found = analyse(root)
    check(
        "a workflow without the primary list is reported",
        any(
            f.rule == "SCAN-BLIND" and f.subject == "ci.yml::test"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 24. The gate must not be able to count itself: this file lives under
    #     tools/, and the scope is tests/, so the two cannot overlap.
    here = os.path.realpath(__file__)
    inside = here.startswith(
        os.path.realpath(os.path.join(here, "..", "..", TESTS_DIR)) + os.sep
    )
    check("the gate's own source is outside the scope", not inside, here)

    if failures:
        print(
            f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): "
            f"{', '.join(failures)}"
        )
        return 1
    print(f"\nself-test passed: {len(ran)} fixtures")
    return 0


# ---------------------------------------------------------------------------


def check_floors(census, floors):
    """The scan floors this tree breached.

    A floor is a count of things that are EXPECTED to be there. It is what
    stops the gate from being switched off by shrinking what it looks at:
    a gate that was handed three projects instead of forty cannot report
    that the other thirty-seven are covered, and a floor says so.
    """
    return [
        (name, floor, actual)
        for name, floor, actual in floors
        if floor and actual < floor
    ]


def report(census, findings):
    """The truth table: every project, and where its tests actually run."""
    print(
        f"universe: {census['totals']['test_projects']} test project(s) in "
        f"{SOLUTION_FILE}, {census['totals']['with_tests']} of them with "
        "[Test] methods"
    )
    print(f"csproj under {TESTS_DIR}/: {census['totals']['csproj']}")
    print()
    for entry in census["lists"]:
        condition = entry["condition"] or "(always)"
        print(
            f"list {entry['id']}  [{entry['file']} :: {entry['job']}]  "
            f"{len(entry['projects'])} entr(ies)"
        )
        print(f"  condition: {condition}")
        print(f"  primary: {'yes' if entry['primary'] else 'no'}")
        print(f"  treenode filter: {'yes' if entry['applies_filter'] else 'no'}")
        print()
    print(f"{'project':40s} {'tests':>5s}  {'lists':44s} runs in")
    for name, project in sorted(census["universe"].items()):
        entries = census["claims"].get(name, [])
        ids = ", ".join(entry["id"] for entry in entries) or "-"
        where = ", ".join(census["contexts"].get(name, [])) or "-"
        print(f"{name:40s} {project['tests']:5d}  {ids:44s} {where}")
    print()
    if census["nuke"]["target"] is not None:
        print(
            f"local NUKE `Test` target runs {len(census['nuke']['runs'])} "
            f"project(s): {', '.join(census['nuke']['runs']) or '-'}"
        )
        print()
    for note in census["notes"]:
        print(f"note  {note}")
    print()
    if findings:
        print("findings:")
        for finding in findings:
            print(f"  {finding}")


def main():
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n")[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--root",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        help="repository root (default: the root of this checkout)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the derived census and the truth table, and exit 0",
    )
    parser.add_argument(
        "--min-projects",
        type=int,
        default=0,
        help="scan floor: the number of .csproj files under tests/",
    )
    parser.add_argument(
        "--min-tested",
        type=int,
        default=0,
        help="scan floor: the number of test projects that carry tests",
    )
    parser.add_argument(
        "--min-lists",
        type=int,
        default=0,
        help="scan floor: the number of CI lists the gate read",
    )
    parser.add_argument(
        "--min-claims",
        type=int,
        default=0,
        help="scan floor: the number of distinct projects the CI lists name",
    )
    parser.add_argument(
        "--min-contexts",
        type=int,
        default=0,
        help="scan floor: the number of test projects some CI run discovers",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    census, findings = analyse(root)
    totals = census["totals"]

    if args.report:
        report(census, findings)
        return 0

    breached = check_floors(
        census,
        [
            ("projects", args.min_projects, totals["csproj"]),
            ("test projects with tests", args.min_tested, totals["with_tests"]),
            ("CI lists", args.min_lists, totals["lists"]),
            ("claimed projects", args.min_claims, totals["claims"]),
            ("discovered projects", args.min_contexts, totals["contexts"]),
        ],
    )

    blocking = [f for f in findings if f.blocking]
    advisory = [f for f in findings if not f.blocking]

    if blocking:
        print("Test-project completeness gate — findings:\n")
        for finding in blocking:
            print(f"  {finding}")
        print()
    if advisory:
        print("carried by the baseline (known holes):\n")
        for finding in advisory:
            print(f"  {finding}")
        print()
    if breached:
        for name, floor, actual in breached:
            print(
                f"  SCAN-BLIND  scan floor: {name} compared {actual}, floor "
                f"is {floor}. The gate saw less than the tree holds, so it "
                "cannot report that the tree is complete."
            )
        print()

    if blocking or breached:
        return 1

    known = len(advisory)
    print(
        f"OK: {totals['test_projects']} test project(s) in {SOLUTION_FILE} "
        f"({totals['with_tests']} with tests), {totals['lists']} CI list(s), "
        f"{totals['claims']} claimed project(s), {totals['contexts']} "
        f"discovered by a CI run, {known} known hole(s) carried by the "
        "baseline"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
