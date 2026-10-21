#!/usr/bin/env python3
"""The coverage model: the cobertura files, the product tree, the registry.

Stdlib only — no pip, no network, no dotnet. This module is the plumbing
behind the two coverage tools:

    coverage_report.py          the REPORTER: the per-file coverage table
    check_coverage_registry.py  the GATE: the registry against the measurement

WHY A MODEL AND NOT A SCRIPT

Issue #1753 asks for the honest form of a coverage number. The form the
repository had was a percentage over everything the instrument happened to
see, and it was wrong twice over: the percentage was over a SUM of suites
whose per-suite columns OVERLAP on the shared `src/` assemblies, and the
number was a percentage of the whole tree while the measurement only ever
touched a part of it. A reader could not tell which of those two numbers
they were looking at.

The honest form is per FILE, with the scope written down, and a registry of
the files that are consciously not covered — each row with a reason. That is
what this module models.

THE THREE THINGS THE MODEL KNOWS

  1. The MEASUREMENT: the cobertura files the nightly lane produced. Every
     line of every class, with the number of times it was hit.

  2. The PRODUCT: the source files under `src/` and `apps/`. This is the
     universe the registry talks about. The test tree is not product code
     and is never in the registry.

  3. The REGISTRY: `tools/coverage-registry.txt`, the rows that say which
     product files are consciously not covered, and why.

WHAT "COVERED" MEANS HERE

A file is COVERED when at least one of its lines was hit at least once by
the measurement. A file that is not covered is one of four things, and the
model keeps them apart because they call for different answers:

  * never-ran      the engine REPORTED the file and not one of its lines
                   was hit. The code never ran. That is the shape a DEAD
                   CODE CANDIDATE has (the finding class of #772 and #755),
                   and the report lists it as one.
  * not-loaded     the file belongs to a project no suite of this lane
                   loads at all, so the measurement never saw it.
  * not-instrumented  the engine reported NOTHING for this file, while it
                   did report other files of the same project. The engine
                   instruments whole modules, so a file it says nothing
                   about in a module it did instrument holds no code to
                   cover: an interface, an enum, a global-using file, a
                   source-generated context. There is nothing in it for a
                   test to cover, so it is NOT a hole.
  * covered        at least one line was hit.

THE ORACLE IS THE ENGINE, NOT A GUESS

The third case is the one that could have been a heuristic and is not. The
model does not parse C# to decide whether a file has code in it: it asks
the coverage engine, which instrumented the module and reported every
file of it that has anything to instrument. A file it did not report has
nothing to cover.

The report still CROSS-CHECKS that answer against the source, because a
silent skip is the failure mode this repository keeps meeting: the files
the engine did not report are read, and if one of them carries code, the
report says so. On the tree this registry was measured on, the answer was
zero such files.

WHAT THE MODEL DOES NOT DO

It does not produce a single percentage, and it does not decide what the
coverage should be. The gate is a ratchet: the registry says what is
accepted, and the measurement says what is there. When the two disagree,
the gate is red. The number is never the gate — see #99, which deferred a
global percentage on purpose.

EXIT CODES (the CLI)

    0  the report was written
    1  a floor was not met (the measurement was empty or too small)
    2  bad invocation
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

# The product roots. A file under any other root is not product code: the
# test tree is measured too (the coverage report says so), but it is never
# in the registry, because a test file that no test runs is a different
# problem with a different gate (`tools/check-test-projects.py`).
PRODUCT_ROOTS = ("src", "apps")

# The registry file. The name follows the two files that already have this
# shape: `tools/test-project-baseline.txt` and `tools/merge-loss-exceptions.txt`.
DEFAULT_REGISTRY = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "coverage-registry.txt"
)

# The kinds a registry row may carry. The kind is not a judgement the gate
# makes: it is the reason CLASS, so a reader can sort the registry and see
# at a glance what kind of debt the tree carries. The two classes are the
# two shapes a hole has (see the model below).
KINDS = (
    "NEVER-RAN",      # the measurement saw the file and ran none of its lines
    "NOT-MEASURED",   # the file's project is not in the measurement at all
)

# A reason is prose, not a link: the two existing registries say the same,
# and a URL says where the debt is, not why it is allowed to stay.
MIN_REASON_LENGTH = 40


# ---------------------------------------------------------------- the model


@dataclass
class FileCoverage:
    """What the measurement says about one file.

    The line numbers are kept, not just the counts: the suites overlap on
    the shared product assemblies, so the merge has to be the UNION of the
    line numbers. A line two suites report is ONE line, and a line one
    suite hit and another missed is covered."""

    path: str
    lines: dict[int, bool] = field(default_factory=dict)
    suites: set[str] = field(default_factory=set)

    @property
    def total(self) -> int:
        return len(self.lines)

    @property
    def covered(self) -> int:
        return sum(1 for hit in self.lines.values() if hit)

    @property
    def uncovered_lines(self) -> list[int]:
        return sorted(number for number, hit in self.lines.items() if not hit)

    @property
    def rate(self) -> float:
        return 1.0 if self.total == 0 else self.covered / self.total


@dataclass
class RegistryRow:
    """One row of `tools/coverage-registry.txt`."""

    path: str
    kind: str
    reason: str
    owner: str
    line: int = 0

    @property
    def is_directory(self) -> bool:
        return self.path.endswith("/")


@dataclass
class Model:
    """The measurement, the product tree and the registry, together."""

    files: dict[str, FileCoverage] = field(default_factory=dict)
    product: list[str] = field(default_factory=list)
    registry: list[RegistryRow] = field(default_factory=list)
    suites: list[str] = field(default_factory=list)
    unmapped: list[str] = field(default_factory=list)
    # The reports that could not be read at all. A suite whose report is
    # unreadable produced no measurement, and the files it would have
    # covered look uncovered — so the measurement is partial.
    unreadable: list[str] = field(default_factory=list)
    # The files the engine reported nothing for, while it reported other
    # files of the same project. The engine instruments whole modules, so
    # these hold no code to cover.
    not_instrumented: list[str] = field(default_factory=list)
    # The files the engine did not report that nevertheless carry code. The
    # cross-check of the engine's answer; empty on the measured tree.
    not_instrumented_with_code: list[str] = field(default_factory=list)
    # The cache for `loaded_projects`, which the per-file loop asks for once
    # per file.
    _loaded_projects: set[str] | None = None

    # -- the product tree -------------------------------------------------

    def product_files(self) -> list[str]:
        return list(self.product)

    def covered_product_files(self) -> list[str]:
        return [p for p in self.product if self.files.get(p) and self.files[p].covered > 0]

    def loaded_projects(self) -> set[str]:
        """The projects the measurement loaded at all.

        A project is loaded when the engine reported at least one of its
        files: the engine instrumented the module and it was in the test
        process."""
        if self._loaded_projects is None:
            self._loaded_projects = {project_of(p) for p in self.files}
        return self._loaded_projects

    def holes(self) -> list[str]:
        """The product files this measurement cannot account for.

        Two shapes, and only two:
          * `never-ran`   the engine saw the code and ran none of it;
          * `not-loaded`  the project is not in this lane at all.

        The files the engine reported nothing for are NOT holes: there is
        no code in them to cover."""
        return [
            p for p in self.product
            if self.status_of(p) in ("never-ran", "not-loaded")
        ]

    def is_covered(self, path: str) -> bool:
        entry = self.files.get(path)
        return bool(entry and entry.covered > 0)

    def status_of(self, path: str) -> str:
        entry = self.files.get(path)
        if entry is not None:
            return "covered" if entry.covered > 0 else "never-ran"
        if project_of(path) in self.loaded_projects():
            return "not-instrumented"
        return "not-loaded"

    # -- the registry -----------------------------------------------------

    def row_for(self, path: str) -> RegistryRow | None:
        """The row that covers `path`, if any.

        A row names a file or a directory (a path that ends in `/`). A
        directory row covers every product file under it. The most specific
        row wins, so a file row can override a directory row."""
        best: RegistryRow | None = None
        for row in self.registry:
            if row.is_directory:
                if path.startswith(row.path) and (best is None or len(row.path) > len(best.path)):
                    best = row
            elif row.path == path:
                best = row
        return best

    def accepted_files(self) -> list[str]:
        return [p for p in self.holes() if self.row_for(p) is not None]

    def unaccepted_files(self) -> list[str]:
        return [p for p in self.holes() if self.row_for(p) is None]


# Tokens that say a file carries code. Used only as a CROSS-CHECK of the
# engine's answer, never as the answer itself: a file the engine did not
# report but that carries one of these is printed in the report, so a
# silent skip cannot hide.
#
# The tokens are the ones a BODY has. A method DECLARATION has none of
# them: `public Task PublishAsync(AgentEvent e);` is an interface member,
# not code, and reading it as code would put every interface in the tree
# into the cross-check.
CODE_TOKENS = re.compile(
    r"=>|\breturn\b|\bawait\b|\basync\b|\bnew\s|\bif\s*\(|\bforeach\b"
    r"|\bwhile\b|\bthrow\b|\bvar\s|\byield\b|\btry\b|\bswitch\b|\bcase\b"
)

# A `{` that opens a BODY. A record's parameter list is followed by a `{`
# too, so the line the `)` sits on is read: a type declaration there is
# not a body.
BODY_OPEN_RE = re.compile(r"\)\s*\{")
TYPE_DECL_RE = re.compile(r"\b(class|struct|record|interface|enum)\b")

_COMMENT_RE = re.compile(r"//[^\n]*")
_BLOCK_COMMENT_RE = re.compile(r"/\*.*?\*/", re.S)
_STRING_RE = re.compile(r'"(?:\\.|[^"\\])*"')


def strip_source(text: str) -> str:
    """The source without its comments and its string literals."""
    text = _BLOCK_COMMENT_RE.sub(" ", text)
    text = _COMMENT_RE.sub(" ", text)
    text = _STRING_RE.sub('""', text)
    return text


def carries_code(path: str) -> bool:
    """Whether the file at `path` looks like it carries code.

    The cross-check, not the oracle: the engine's answer is the answer."""
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            source = strip_source(handle.read())
    except OSError:
        return False
    if CODE_TOKENS.search(source):
        return True
    for match in BODY_OPEN_RE.finditer(source):
        start = source.rfind("\n", 0, match.start()) + 1
        line = source[start:match.start()]
        if not TYPE_DECL_RE.search(line):
            return True
    return False


# ------------------------------------------------------------ the tree


def project_dirs(source_root: str) -> dict[str, str]:
    """Map a PROJECT NAME to its repo-relative directory.

    The cobertura report names a file by the path the compiler saw, which
    for a project-local compile is relative to the project directory
    ("Foo.cs"). This map is what turns that into "src/Harbor.Foo/Foo.cs"."""
    mapping: dict[str, str] = {}
    for root in PRODUCT_ROOTS:
        base = os.path.join(source_root, root)
        if not os.path.isdir(base):
            continue
        for entry in sorted(os.listdir(base)):
            directory = os.path.join(base, entry)
            if os.path.isdir(directory):
                mapping[entry] = f"{root}/{entry}"
    return mapping


def product_source_files(source_root: str) -> list[str]:
    """Every product source file, repo-relative.

    `.cs` only: the language of the product. The `bin/` and `obj/`
    directories are skipped — they are build output, and a file in them is
    not a file of the tree."""
    found: list[str] = []
    for root in PRODUCT_ROOTS:
        base = os.path.join(source_root, root)
        if not os.path.isdir(base):
            continue
        for directory, subdirs, files in os.walk(base):
            subdirs[:] = sorted(d for d in subdirs if d not in ("bin", "obj"))
            for name in sorted(files):
                if name.endswith(".cs"):
                    full = os.path.join(directory, name)
                    found.append(os.path.relpath(full, source_root).replace(os.sep, "/"))
    return sorted(found)


def repo_roots(source_root: str) -> set[str]:
    """The top-level directories of the repository.

    The cobertura report names a file by the path the COMPILER saw, and
    that path is absolute on a CI runner. The repository's own top-level
    directories are the anchor: the first segment that is one of them
    starts the repo-relative path."""
    try:
        entries = os.listdir(source_root)
    except OSError:
        return set(PRODUCT_ROOTS)
    return {name for name in entries if os.path.isdir(os.path.join(source_root, name))}


def normalize_source_path(raw: str, projects: dict[str, str], roots: set[str]) -> str:
    """Turn a path from a cobertura report into a repo-relative path.

    The report carries the path the COMPILER saw, and that path depends on
    how the build was invoked. Four shapes occur, and all four are handled:

      * the deterministic-source-root form the CI build produces
        (`/_/src/Harbor.Foo/Bar.cs` — `ContinuousIntegrationBuild=true`
        maps the build machine's paths onto `/_/`);
      * an absolute path (`/home/runner/work/.../src/Harbor.Foo/Bar.cs`);
      * a path relative to the repository (`src/Harbor.Foo/Bar.cs`);
      * a path relative to the PROJECT directory (`Bar.cs`), which only the
        project-name map can resolve.

    A path that resolves to nothing is returned empty, and the caller
    records it as unmapped rather than dropping it silently."""
    if not raw:
        return ""
    text = raw.replace("\\", "/").strip()
    if text.startswith("file://"):
        text = text[len("file://"):]
    parts = [part for part in text.split("/") if part and part != "."]
    if not parts:
        return ""
    # A top-level directory of the repository starts the relative path.
    for index, part in enumerate(parts):
        if part in roots:
            return "/".join(parts[index:])
    # The product root wins for a path that names no top-level directory:
    # the first `src` or `apps` segment.
    for index, part in enumerate(parts):
        if part in PRODUCT_ROOTS:
            return "/".join(parts[index:])
    # The project directory form: "Bar.cs" or "Sub/Bar.cs" inside a project.
    if parts[0] in projects:
        return f"{projects[parts[0]]}/" + "/".join(parts[1:])
    if len(parts) > 1 and parts[-2] in projects:
        return f"{projects[parts[-2]]}/{parts[-1]}"
    return ""


# ------------------------------------------------------------ the parsing


def read_cobertura(path: str, projects: dict[str, str], roots: set[str]) -> tuple[dict[str, FileCoverage], list[str], bool]:
    """One cobertura file: the coverage per source file, what it could not
    map, and whether the file could be read at all.

    A report that cannot be parsed is a measurement that did not happen, and
    the caller has to know: the files it would have covered look uncovered,
    and a gate that called them holes would be reporting a broken report.
    """
    files: dict[str, FileCoverage] = {}
    unmapped: list[str] = []
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return files, unmapped, False
    suite = os.path.basename(path)
    for node in root.iter("class"):
        raw = node.get("filename") or ""
        relative = normalize_source_path(raw, projects, roots)
        if not relative:
            if raw:
                unmapped.append(raw)
            continue
        entry = files.setdefault(relative, FileCoverage(path=relative))
        entry.suites.add(suite)
        lines_node = node.find("lines")
        if lines_node is None:
            continue
        for line in lines_node.findall("line"):
            try:
                number = int(line.get("number", "0"))
            except ValueError:
                continue
            try:
                count = int(float(line.get("hits", "0")))
            except ValueError:
                count = 0
            hit = count > 0
            entry.lines[number] = entry.lines.get(number, False) or hit
    return files, unmapped, True


def load_coverage(coverage_dir: str, source_root: str) -> Model:
    """Read every cobertura file in `coverage_dir` and merge them."""
    projects = project_dirs(source_root)
    roots = repo_roots(source_root)
    model = Model()
    model.product = product_source_files(source_root)
    if not os.path.isdir(coverage_dir):
        return model
    names = sorted(
        name for name in os.listdir(coverage_dir)
        if name.endswith(".cobertura.xml") or name.endswith(".xml")
    )
    for name in names:
        full = os.path.join(coverage_dir, name)
        if not os.path.isfile(full):
            continue
        suite = name.removesuffix(".cobertura.xml").removesuffix(".xml")
        model.suites.append(suite)
        files, unmapped, readable = read_cobertura(full, projects, roots)
        if not readable:
            model.unreadable.append(name)
            continue
        for path, entry in files.items():
            merged = model.files.setdefault(path, FileCoverage(path=path))
            # The suites overlap on the shared product assemblies, so the
            # merge is a UNION of the line numbers, never a sum: a line two
            # suites hit is one line, and a line one suite hit and another
            # missed is covered.
            for number, hit in entry.lines.items():
                merged.lines[number] = merged.lines.get(number, False) or hit
            merged.suites.add(suite)
        model.unmapped.extend(unmapped)
    model.unmapped = sorted(set(model.unmapped))
    # The files the engine reported nothing for, while it reported other
    # files of the same project: they hold no code to cover.
    loaded = model.loaded_projects()
    model.not_instrumented = [
        path for path in model.product
        if path not in model.files and project_of(path) in loaded
    ]
    model.not_instrumented_with_code = [
        path for path in model.not_instrumented
        if carries_code(os.path.join(source_root, path))
    ]
    return model


# ------------------------------------------------------------ the registry


def parse_registry(path: str) -> list[RegistryRow]:
    """Read the registry. A malformed row is an error, not a warning."""
    rows: list[RegistryRow] = []
    if not os.path.exists(path):
        return rows
    with open(path, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, start=1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            fields = [field.strip() for field in line.split("|")]
            if len(fields) != 4:
                raise ValueError(
                    f"{path}:{number}: a row needs four fields "
                    "(path | kind | reason | owner)"
                )
            row = RegistryRow(
                path=fields[0],
                kind=fields[1],
                reason=fields[2],
                owner=fields[3],
                line=number,
            )
            if row.kind not in KINDS:
                raise ValueError(
                    f"{path}:{number}: unknown kind {row.kind!r}; the kinds are "
                    + ", ".join(KINDS)
                )
            if len(row.reason) < MIN_REASON_LENGTH:
                raise ValueError(
                    f"{path}:{number}: the reason is {len(row.reason)} characters; "
                    f"it needs at least {MIN_REASON_LENGTH} — a URL says where the "
                    "debt is, not why it is allowed to stay"
                )
            rows.append(row)
    return rows


def load_registry(path: str) -> list[RegistryRow]:
    try:
        return parse_registry(path)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise


# ------------------------------------------------------------ the report


def project_of(path: str) -> str:
    """The project a product file belongs to: the directory under the root."""
    parts = path.split("/")
    if len(parts) >= 2 and parts[0] in PRODUCT_ROOTS:
        return f"{parts[0]}/{parts[1]}"
    return parts[0] if parts else "?"


def render_report(model: Model, top: int = 25) -> str:
    """The per-file report.

    There is no single percentage in here, on purpose. The tables are per
    project and per file, and the scope is written into the header, so a
    reader cannot mistake a number for the coverage of the whole tree."""
    lines: list[str] = []
    lines.append("## Coverage per file (report-only, issue #99 and #1753)")
    lines.append("")
    product = model.product_files()
    covered = model.covered_product_files()
    holes = model.holes()
    accepted = model.accepted_files()
    unaccepted = model.unaccepted_files()
    lines.append(
        "_No global percentage: the numbers below are per file and per project, "
        "and the scope is the 20 core+platform unit suites the nightly lane runs "
        "(ui/e2e/load are excluded). The suites OVERLAP on the shared `src/` "
        "assemblies, so the file union is what is counted, never a sum of the "
        "suites._"
    )
    lines.append("")

    # The per-project table.
    projects: dict[str, list[str]] = {}
    for path in product:
        projects.setdefault(project_of(path), []).append(path)
    lines.append("| project | files | covered | never ran | not loaded | no code to cover |")
    lines.append("|---|---|---|---|---|---|")
    for name in sorted(projects):
        members = projects[name]
        never_ran = sum(1 for p in members if model.status_of(p) == "never-ran")
        not_loaded = sum(1 for p in members if model.status_of(p) == "not-loaded")
        no_code = sum(1 for p in members if model.status_of(p) == "not-instrumented")
        lines.append(
            f"| `{name}` | {len(members)} | {len(members) - never_ran - not_loaded - no_code} | "
            f"{never_ran} | {not_loaded} | {no_code} |"
        )
    never_ran_total = sum(1 for p in product if model.status_of(p) == "never-ran")
    not_loaded_total = sum(1 for p in product if model.status_of(p) == "not-loaded")
    no_code_total = len(model.not_instrumented)
    lines.append(
        f"| **total** | **{len(product)}** | **{len(covered)}** | "
        f"**{never_ran_total}** | **{not_loaded_total}** | **{no_code_total}** |"
    )
    lines.append("")

    # The registry split: what is consciously uncovered and what is not.
    lines.append(
        f"**Holes: {len(holes)} of {len(product)} product files** "
        f"({100.0 * len(holes) / len(product):.1f}%) — "
        f"{never_ran_total} the measurement saw and never ran, "
        f"{not_loaded_total} in a project this lane does not load. "
        f"{len(accepted)} are in the registry with a reason, "
        f"{len(unaccepted)} are not."
    )
    lines.append("")

    # The dead-code candidates: the files the measurement SAW and that ran
    # not one line.
    candidates = [p for p in holes if model.status_of(p) == "never-ran"]
    lines.append(f"### The {len(candidates)} files that never ran a line (deletion candidates)")
    lines.append("")
    if not candidates:
        lines.append("_None: every file the measurement touched ran at least one line._")
    else:
        lines.append("| file | lines | uncovered | in the registry |")
        lines.append("|---|---|---|---|")
        for path in sorted(candidates, key=lambda p: (-len(model.files[p].uncovered_lines), p))[:top]:
            entry = model.files[path]
            row = model.row_for(path)
            lines.append(
                f"| `{path}` | {entry.total} | {len(entry.uncovered_lines)} | "
                f"{('`' + row.kind + '`') if row else '**NO**'} |"
            )
        if len(candidates) > top:
            lines.append(f"| … {len(candidates) - top} more | | | |")
    lines.append("")

    # The projects the lane does not load.
    not_loaded = [p for p in holes if model.status_of(p) == "not-loaded"]
    lines.append(f"### The {len(not_loaded)} files in projects this lane does not load")
    lines.append("")
    if not_loaded:
        for path in sorted(not_loaded)[:top]:
            row = model.row_for(path)
            lines.append(f"- `{path}`" + (f" — `{row.kind}`" if row else " — **NO REGISTRY ROW**"))
        if len(not_loaded) > top:
            lines.append(f"- … {len(not_loaded) - top} more")
    else:
        lines.append("_None: every product file belongs to a project this lane loads._")
    lines.append("")

    # The files with no code to cover, and the cross-check of that answer.
    lines.append(
        f"_{len(model.not_instrumented)} product file(s) carry no code to cover: the "
        "engine instrumented their module and reported no line for them, which is "
        "what an interface, an enum, a global-using file or a source-generated "
        "context looks like._"
    )
    if model.not_instrumented_with_code:
        lines.append("")
        lines.append(
            f"**{len(model.not_instrumented_with_code)} of them carry code anyway** "
            "— the engine did not instrument them, so the measurement is blind "
            "there: "
            + ", ".join(f"`{p}`" for p in model.not_instrumented_with_code[:10])
        )
    lines.append("")

    if model.unmapped:
        lines.append(
            f"_{len(model.unmapped)} path(s) in the reports could not be mapped to "
            "the tree and were left out: "
            + ", ".join(f"`{p}`" for p in model.unmapped[:5])
            + "._"
        )
        lines.append("")
    if model.unreadable:
        lines.append(
            f"**{len(model.unreadable)} report(s) could not be read**: "
            + ", ".join(f"`{p}`" for p in model.unreadable)
            + ". The measurement is PARTIAL: the files those suites would have "
            "covered look uncovered, so the registry is compared only in the "
            "direction a partial measurement supports."
        )
        lines.append("")
    return "\n".join(lines)


def render_file_table(model: Model) -> str:
    """The FULL per-file table, one row per product file.

    This is the artifact, not the job summary: it is the thing a reader
    opens to see which file is uncovered and by how much."""
    lines = ["| file | lines | covered | share | uncovered lines | suites |"]
    lines.append("|---|---|---|---|---|---|")
    for path in model.product_files():
        entry = model.files.get(path)
        if entry is None:
            lines.append(f"| `{path}` | — | — | — | — | not measured |")
            continue
        share = 100.0 * entry.covered / entry.total if entry.total else 0.0
        numbers = ", ".join(str(n) for n in entry.uncovered_lines[:40])
        if len(entry.uncovered_lines) > 40:
            numbers += f", … (+{len(entry.uncovered_lines) - 40})"
        lines.append(
            f"| `{path}` | {entry.total} | {entry.covered} | {share:.1f}% | "
            f"{numbers or '—'} | {len(entry.suites)} |"
        )
    return "\n".join(lines) + "\n"


def render_uncovered_locations(model: Model) -> str:
    """Every uncovered line, as `file:line` — the deletion-candidate list."""
    lines = []
    for path in model.product_files():
        entry = model.files.get(path)
        if entry is None or not entry.uncovered_lines:
            continue
        for number in entry.uncovered_lines:
            lines.append(f"{path}:{number}")
    return "\n".join(lines) + ("\n" if lines else "")


# ---------------------------------------------------------------- self-test


def _write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def _cobertura(path: str, filename: str, lines: dict[int, int]) -> None:
    """A minimal cobertura file: one class, the given line hits."""
    body = "".join(
        f'        <line number="{number}" hits="{hits}" branch="false" />\n'
        for number, hits in sorted(lines.items())
    )
    _write(path, f"""<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<coverage line-rate="0.5" branch-rate="0" complexity="1" version="1.9" timestamp="0">
  <sources><source>/_</source></sources>
  <packages>
    <package name="Foo" line-rate="0.5" branch-rate="0" complexity="1">
      <classes>
        <class name="Foo.{os.path.basename(filename).replace('.cs', '')}" filename="{filename}" line-rate="0.5" branch-rate="0" complexity="1">
          <lines>
{body}          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
""")


def self_test() -> int:
    """Prove the reader still reads a coverage report.

    The fixture is a throwaway tree with three product files and one
    coverage report: a file that ran, a file the engine saw and never ran,
    and a file the engine said nothing about. The three are three
    different things — a covered file, a hole, and a file with no code to
    cover — and a reader that cannot tell them apart produces a number
    nobody can act on."""
    failures: list[str] = []
    tmp = tempfile.mkdtemp(prefix="harbor-coverage-report-")
    try:
        root = os.path.join(tmp, "repo")
        coverage_dir = os.path.join(root, "Coverage")
        _write(os.path.join(root, "src", "Foo", "Covered.cs"), "// Covered\n")
        _write(os.path.join(root, "src", "Foo", "Dead.cs"), "// Dead\n")
        _write(os.path.join(root, "src", "Foo", "Contract.cs"),
               "namespace Foo;\npublic interface IContract\n{\n    void Run();\n}\n")
        _cobertura(
            os.path.join(coverage_dir, "Harbor.Foo.Tests.cobertura.xml"),
            "src/Foo/Covered.cs",
            {1: 1, 2: 0},
        )
        _cobertura(
            os.path.join(coverage_dir, "Harbor.Foo.Other.Tests.cobertura.xml"),
            "src/Foo/Dead.cs",
            {1: 0},
        )
        model = load_coverage(coverage_dir, root)
        if len(model.product_files()) != 3:
            failures.append(
                f"the product tree has {len(model.product_files())} file(s), expected 3"
            )
        if model.status_of("src/Foo/Covered.cs") != "covered":
            failures.append(
                "the file that ran is not reported as covered: "
                f"{model.status_of('src/Foo/Covered.cs')}"
            )
        if model.status_of("src/Foo/Dead.cs") != "never-ran":
            failures.append(
                "the file the engine saw and never ran is not reported as "
                f"never-ran: {model.status_of('src/Foo/Dead.cs')}"
            )
        if model.status_of("src/Foo/Contract.cs") != "not-instrumented":
            failures.append(
                "the file the engine said nothing about is not reported as "
                f"not-instrumented: {model.status_of('src/Foo/Contract.cs')}"
            )
        holes = model.holes()
        if holes != ["src/Foo/Dead.cs"]:
            failures.append(f"the holes are {holes}, expected only src/Foo/Dead.cs")
        report = render_report(model)
        for wanted in ("src/Foo/Dead.cs", "never ran a line"):
            if wanted not in report:
                failures.append(f"the report does not mention {wanted!r}")
        if "100.0%" in report:
            failures.append("the report carries a global percentage")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        for failure in failures:
            print(f"self-test FAILED: {failure}", file=sys.stderr)
        return 1
    print(
        "self-test OK: the reader tells a covered file, a hole and a file "
        "with no code to cover apart, and the report carries no global percentage"
    )
    return 0


# ------------------------------------------------------------ the CLI


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="coverage_report.py",
        description="The per-file coverage report of the nightly lane.",
    )
    parser.add_argument("--coverage-dir", default="Coverage",
                        help="the directory with the cobertura files")
    parser.add_argument("--source-root", default=".",
                        help="the repository root")
    parser.add_argument("--registry", default=DEFAULT_REGISTRY,
                        help="the registry of consciously uncovered files")
    parser.add_argument("--summary", default="",
                        help="append the report to this file (the job summary)")
    parser.add_argument("--out", default="",
                        help="write the FULL per-file table to this file")
    parser.add_argument("--uncovered-out", default="",
                        help="write the uncovered lines (file:line) to this file")
    parser.add_argument("--json", default="",
                        help="write the model as JSON to this file")
    parser.add_argument("--print-missing", action="store_true",
                        help="print the registry rows the tree still needs "
                             "(the holes no row names), with a placeholder reason")
    parser.add_argument("--print-stale", action="store_true",
                        help="print the registry rows the tree no longer needs "
                             "(the rows whose file is covered now)")
    parser.add_argument("--min-files", type=int, default=0,
                        help="fail unless the product tree has at least this many files")
    parser.add_argument("--min-covered", type=int, default=0,
                        help="fail unless at least this many product files are covered")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the reader still reads a coverage report")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    if not os.path.isdir(args.coverage_dir):
        print(f"error: no coverage directory: {args.coverage_dir}", file=sys.stderr)
        return 2

    model = load_coverage(args.coverage_dir, args.source_root)
    model.registry = load_registry(args.registry)

    product = model.product_files()
    covered = model.covered_product_files()
    if len(product) < args.min_files:
        print(
            f"error: the product tree has {len(product)} file(s), the floor is "
            f"{args.min_files}",
            file=sys.stderr,
        )
        return 1
    if len(covered) < args.min_covered:
        print(
            f"error: {len(covered)} product file(s) are covered, the floor is "
            f"{args.min_covered}",
            file=sys.stderr,
        )
        return 1

    report = render_report(model)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(report + "\n")
    else:
        print(report)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(render_file_table(model))
    if args.uncovered_out:
        with open(args.uncovered_out, "w", encoding="utf-8") as handle:
            handle.write(render_uncovered_locations(model))
    if args.json:
        payload = {
            "suites": model.suites,
            "product_files": len(product),
            "covered_files": len(covered),
            "uncovered_files": len(model.holes()),
            "accepted_files": len(model.accepted_files()),
            "unaccepted_files": len(model.unaccepted_files()),
            "unmapped_paths": model.unmapped,
            "files": {
                path: {
                    "covered": entry.covered,
                    "total": entry.total,
                    "suites": sorted(entry.suites),
                    "uncovered_lines": entry.uncovered_lines,
                }
                for path, entry in sorted(model.files.items())
            },
        }
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)

    if args.print_missing:
        print()
        print("# the rows the registry still needs (the reason is yours to write)")
        for path in model.unaccepted_files():
            kind = "NEVER-RAN" if model.status_of(path) == "never-ran" else "NOT-MEASURED"
            print(f"{path} | {kind} | <why is this hole allowed to stay?> | @refusedguy")
    if args.print_stale:
        print()
        print("# the rows the registry no longer needs (the file is covered now)")
        for row in model.registry:
            if row.is_directory:
                still = [p for p in model.holes() if p.startswith(row.path)]
                if still:
                    continue
                print(f"{row.path} | {row.kind} | <the directory needs no row any more> | {row.owner}")
            elif not model.is_covered(row.path):
                continue
            else:
                print(f"{row.path} | {row.kind} | <the file is covered now> | {row.owner}")

    print(
        f"coverage: {len(product)} product file(s), {len(covered)} covered, "
        f"{len(model.holes())} uncovered "
        f"({len(model.accepted_files())} in the registry), "
        f"{len(model.suites)} suite(s)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
