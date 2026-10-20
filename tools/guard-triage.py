#!/usr/bin/env python3
"""Classify guard firings into defect / style / noise / unknown (issue #1726).

Stdlib only - no pip, no network, no dotnet, no build. Reads what a CI run
already produced (a TRX file, or the raw job log of a shard step) and writes a
machine-readable report of every guard that FIRED, with the evidence that
made it fire and the class it belongs to.

  guard-triage.py --trx TestResults/core --out guard-firings.json
  guard-triage.py --log logs/39447321149.log --markdown report.md
  guard-triage.py --self-test

Exit codes: 0 clean, 1 the input was unusable, 2 the self-test failed.

WHAT THIS FILE IS FOR

  Issue #1724 wants to shrink the architecture-guard set. It cannot, safely,
  without knowing which guards ever caught something real: cutting a guard
  that caught a real defect is a silent hole, and cutting a guard that only
  ever fired on a legitimate refactor is pure win. Today the answer is not
  written down anywhere - the only trace of a firing is a red CI run that
  gets re-run and forgotten.

  So the repository needs the missing half of the audit in #1725: a count of
  firings per rule, split by what the firing actually MEANT. This file is
  the counter. It does not decide the class - a human does, in
  `tools/guard-triage-classes.json` - but it does the part that is mechanical:
  it finds the firings, it attaches the evidence (which rule, which file,
  which line, what the assertion said), and it keeps the human decision in a
  file a reviewer can read.

THE FOUR CLASSES

  defect   the rule caught something that is genuinely broken. The code the
           rule names violates an invariant that the repository states
           somewhere else (a banned API, a layer that must not reference
           another, an unreachable seam).
  style    the rule is bound to the FORM of the code rather than to its
           meaning: a line number, a member order, a name, a regex over the
           text of a file. A firing here is a preference, not a bug.
  noise    the rule fired on code the repository EXPLICITLY ALLOWS. The
           allowance is a fact (a ledger entry with a reason, an exceptions
           file), so the firing carries no new information.
  unknown  the firing was not classified. This is the worklist.

  `unknown` is a first-class answer, not a failure: it says the feedback
  loop has not visited this rule yet. A report where everything is
  `unknown` is a report that says nothing, and the tool prints that fact
  instead of hiding it.

HOW A FIRING IS FOUND

  Two input shapes, because CI produces both:

    --trx DIR   a directory of TRX files (what `--report-trx` writes). Each
                failed `UnitTestResult` is one firing; its `<Message>` is
                the evidence and its stack trace names the rule.
    --log FILE  a CI job log. The test platform prints one block per failed
                test: the test name, the assertion message, and the stack
                frame `at <Namespace>.<Class>.<Method>()`. The frame is what
                identifies the rule; the job log is the shape that survives
                for the whole retention window, which is what a retrospective
                needs.

HOW A FIRING IS CLASSIFIED

  In this order, and the order is the point:

    1. A per-firing entry in the registry, matched by rule AND by an
       evidence substring. This is where a single rule that means different
       things in different situations is expressed.
    2. A per-rule entry in the registry: the class of the rule, with the
       reason and the concrete firings it was decided from.
    3. Two mechanical detectors, because these two situations are decidable
       from the text alone:
         ledger-retirement   the rule's own source lists the fired-on path
                            AND the assertion says the allowance is stale
                            ("retired debt", "no longer an orphan"). The rule
                            fired on its own allowance: that is noise by
                            definition.
         allowance-listed    the fired-on path is named in one of the
                            allowance files the registry declares. The
                            repository allows that code; the rule fired on
                            allowed code: noise.
    4. `unknown`.

  The two mechanical detectors are the only automatic decisions the tool
  makes, and both of them can only ever produce `noise` - the class that
  says "the rule told us nothing new". Everything else is a human decision
  recorded in a file, with the firings it was derived from.

WHAT THE REPORT IS FOR

  The output is a per-rule table with the noise share, so the reader can
  answer the question #1726 asks: which rules only ever fired on allowed
  code? Those are the candidates for the Tier C list in #1727, and the
  report prints them, together with the ratchet the tool proposes for each:

    fix-the-rule   the rule is right and the code was wrong - keep it.
    retire-the-rule the rule is bound to form, and the form was allowed -
                   retire it, or record the allowance with a reason.
    ledger-edit    the rule is a ledger and the entry is stale - the ledger
                   entry is what has to change, and that edit is the record.

  The ratchet is proposed, not applied. `tools/test-package-exceptions.txt`
  is the shape it follows: one line per allowance, with the reason and the
  owner, so an allowance nobody can justify is visible as such.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

SCHEMA = "harbor.guard-triage/1"
REGISTRY_SCHEMA = "harbor.guard-triage/registry/1"

# The four classes, in the order the report prints them.
CLASSES = ("defect", "style", "noise", "unknown")

# The part of an assertion message that names the thing that broke. TUnit
# renders it as `Expected ..., because <rule>. Found: <the thing>` or as
# `Undeclared: <the thing>` / `Stale entries: <the thing>`. The paths after
# the marker are what the rule CITES; the paths before it are the rule
# explaining itself, and are only mentions.
CITED_MARKERS = (
    "Found:",
    "Undeclared:",
    "Stale entries:",
    "Violations:",
    "Offenders:",
    "Actual:",
    "Received:",
)

# `path:line` in an assertion message. `file.cs:Member` is a member, not a
# line, so the line part must be digits.
PATH_LINE_RE = re.compile(
    r"(?P<path>(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.[A-Za-z0-9]+):(?P<line>\d+)"
)

# A bare repository-relative path in an assertion message.
BARE_PATH_RE = re.compile(
    r"(?P<path>(?:src|apps|tests|tools|docs|build|samples|contrib|\.github)/"
    r"[A-Za-z0-9_./+-]+\.[A-Za-z0-9]+)"
)

# The test platform's failure block. The name is followed by the duration in
# parentheses; the assertion message and the stack frame follow on the next
# lines. The line is matched with `search`, not `match`: the build job wraps
# the suite in the build tool's logger, which prefixes every line with a
# level marker, and the failure block is still a failure block.
FAILED_TEST_RE = re.compile(r"failed\s+(?P<name>[A-Za-z0-9_.\-]+)\s+\((?P<dur>[^)]*)\)")

# The build tool's logger prefix: `05:15:38 [DBG] `.
NUKE_PREFIX_RE = re.compile(r"^(?:\d{2}:\d{2}:\d{2}\s+)?\[(?:DBG|INF|WRN|ERR)\]\s*")

# How many lines of a failure block are evidence. A build log repeats the
# suite output and then prints thousands of MSBuild warnings; the assertion
# and the stack frame are in the first dozen lines - but an assertion that
# quotes a whole YAML file pushes the frame further down.
MAX_EVIDENCE_LINES = 120

# The stack frame that names the rule: `at Harbor.Architecture.Tests.Class.Method()`.
STACK_FRAME_RE = re.compile(
    r"at\s+(?P<ns>[A-Za-z0-9_.]+)\.(?P<cls>[A-Za-z0-9_]+)\.(?P<method>[A-Za-z0-9_]+)\(\)"
)

# The `::group::dotnet exec <project>` marker the shard step prints before
# every project, so a log alone says which assembly a failure came from.
# The marker reaches the log in two shapes: the raw `::group::` echo and the
# `##[group]` rendering GitHub writes into the stored log.
PROJECT_GROUP_RE = re.compile(
    r"(?:##\[group\]|::group::)dotnet exec\s+(?P<project>[A-Za-z0-9_.]+)"
)

# Two log shapes reach the tool. `gh run view --log` prints
# `<job>\t<step>\t<timestamp> <line>`; the job-logs API prints
# `<timestamp> <line>`. Both have to lose their prefix, or every line reads
# as one long sentence.
GH_PREFIX_RE = re.compile(
    r"^(?:[^\t]*\t[^\t]*\t)?\d{4}-\d{2}-\d{2}T[\d:.]+Z\s?"
)

# The `gh` CLI writes ANSI escapes in the caret form (`^[[31m`) when it
# prints a job log, while a log copied out of the browser carries the raw
# ESC byte. Both have to go, or every colour-coded line reads as text.
ANSI_RE = re.compile(r"(?:\x1b\[|\^\[\[)[0-9;]*[a-zA-Z]")

# The two situations the tool decides on its own. Both can only be noise.
LEDGER_STALE_RE = re.compile(
    r"(stale entr|retired debt|no longer (an )?orphan|outlived|"
    r"is stale|stopped being an orphan)",
    re.IGNORECASE,
)


# --------------------------------------------------------------------------
# The registry
# --------------------------------------------------------------------------


@dataclass
class Registry:
    """The human decisions: which class a rule's firings belong to."""

    rules: dict[str, dict[str, Any]] = field(default_factory=dict)
    firings: list[dict[str, Any]] = field(default_factory=list)
    path: str = ""

    @classmethod
    def load(cls, path: Path) -> "Registry":
        raw = json.loads(path.read_text(encoding="utf-8"))
        schema = raw.get("schema")
        if schema != REGISTRY_SCHEMA:
            raise ValueError(f"{path}: unknown schema {schema!r}, expected {REGISTRY_SCHEMA!r}")
        rules: dict[str, dict[str, Any]] = {}
        for entry in raw.get("rules", []):
            rule = entry.get("rule")
            if not rule:
                raise ValueError(f"{path}: a rule entry has no 'rule' key")
            if entry.get("class") not in CLASSES:
                raise ValueError(
                    f"{path}: rule {rule} has class {entry.get('class')!r}, "
                    f"expected one of {CLASSES}"
                )
            rules[rule] = entry
        return cls(
            rules=rules,
            firings=list(raw.get("firings", [])),
            path=str(path),
        )

    def allowance_text(self, repo_root: Path, rule: str) -> dict[str, str]:
        """The content of the allowance files the rule declares."""
        entry = self.rules.get(rule, {})
        out: dict[str, str] = {}
        for pattern in entry.get("allowance_files", []):
            for path in sorted(repo_root.glob(pattern)):
                if path.is_file():
                    try:
                        out[str(path.relative_to(repo_root))] = path.read_text(
                            encoding="utf-8", errors="replace"
                        )
                    except OSError:
                        continue
        return out


# --------------------------------------------------------------------------
# Guard source index
# --------------------------------------------------------------------------


@dataclass
class GuardIndex:
    """Where a rule lives: the guard file, and the line of the test method."""

    class_line: dict[str, tuple[str, int]] = field(default_factory=dict)
    method_line: dict[tuple[str, str], tuple[str, int]] = field(default_factory=dict)
    method_line_any: dict[str, tuple[str, int]] = field(default_factory=dict)
    source: dict[str, str] = field(default_factory=dict)

    @classmethod
    def build(cls, guard_dir: Path, repo_root: Path | None = None) -> "GuardIndex":
        index = cls()
        if not guard_dir.is_dir():
            return index
        for path in sorted(guard_dir.rglob("*.cs")):
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            rel = str(path)
            if repo_root is not None:
                try:
                    rel = str(path.relative_to(repo_root))
                except ValueError:
                    rel = str(path)
            index.source[rel] = text
            lines = text.splitlines()
            class_re = re.compile(
                r"^\s*(?:public|internal|private|protected)?\s*"
                r"(?:sealed\s+|static\s+|abstract\s+|partial\s+|file\s+)*class\s+([A-Za-z0-9_]+)"
            )
            method_re = re.compile(
                r"^\s*(?:public|internal|private|protected)\s+"
                r"(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|partial\s+)*"
                r"(?:Task|void|ValueTask|Task<[^>]+>|bool|int|string|"
                r"IReadOnlyList<[^>]+>|IEnumerable<[^>]+>)\s+([A-Za-z0-9_]+)\s*\("
            )
            current_class: str | None = None
            for number, line in enumerate(lines, start=1):
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("///"):
                    continue
                m = class_re.match(line)
                if m:
                    current_class = m.group(1)
                    index.class_line.setdefault(current_class, (rel, number))
                    continue
                m = method_re.match(line)
                if m:
                    index.method_line.setdefault((current_class, m.group(1)), (rel, number))
                    # A file can declare helper types between the class and
                    # its methods (the non-vacuity controls do), so the
                    # nearest preceding class is not always the right
                    # owner. The first occurrence of the method name in the
                    # file is the fallback.
                    index.method_line_any.setdefault(m.group(1), (rel, number))
        return index

    def locate(self, class_name: str, method_name: str) -> tuple[str | None, int | None]:
        if (class_name, method_name) in self.method_line:
            path, line = self.method_line[(class_name, method_name)]
            return path, line
        if method_name in self.method_line_any:
            return self.method_line_any[method_name]
        if class_name in self.class_line:
            path, line = self.class_line[class_name]
            return path, line
        return None, None

    def file_of_class(self, class_name: str) -> str | None:
        entry = self.class_line.get(class_name)
        return entry[0] if entry else None


# --------------------------------------------------------------------------
# Firings
# --------------------------------------------------------------------------


@dataclass
class Firing:
    rule: str
    test_name: str
    project: str
    evidence: str
    source: str  # "trx" or "log"
    origin: str  # the file the firing was read out of
    run_id: str = ""
    guard_file: str | None = None
    guard_line: int | None = None
    locations: list[tuple[str, int | None]] = field(default_factory=list)
    classification: str = "unknown"
    classified_by: str = "none"
    reason: str = ""
    ratchet: str = ""
    run: dict[str, Any] = field(default_factory=dict)

    def to_json(self) -> dict[str, Any]:
        return {
            "rule": self.rule,
            "test_name": self.test_name,
            "project": self.project,
            "class": self.classification,
            "classified_by": self.classified_by,
            "reason": self.reason,
            "ratchet": self.ratchet,
            "guard_file": self.guard_file,
            "guard_line": self.guard_line,
            "locations": [
                {"path": p, "line": l} for p, l in self.locations
            ],
            "evidence": self.evidence,
            "source": self.source,
            "origin": self.origin,
            "run_id": self.run_id,
            "run": self.run,
        }


def extract_locations(evidence: str) -> list[tuple[str, int | None]]:
    """The file:line pairs an assertion message points at.

    Only the part of the message after a citation marker counts as a
    citation. A rule that explains itself mentions other files - the tool
    that slices the shards, the issue that owns the debt - and a mention is
    not a place where something is broken.
    """
    cited = evidence
    for marker in CITED_MARKERS:
        index = evidence.find(marker)
        if index >= 0:
            cited = evidence[index + len(marker) :]
            break
    found: list[tuple[str, int | None]] = []
    seen: set[tuple[str, int | None]] = set()
    for m in PATH_LINE_RE.finditer(cited):
        path = m.group("path")
        # A path like `Foo.cs:12` is only a repository path when it has a
        # directory part or a known root; otherwise it is a file name in the
        # working directory, which is not a citation we can chase.
        if "/" not in path and not path.endswith(".cs"):
            continue
        item = (path, int(m.group("line")))
        if item not in seen:
            seen.add(item)
            found.append(item)
    for m in BARE_PATH_RE.finditer(cited):
        path = m.group("path").rstrip(".,;:)")
        item = (path, None)
        if item not in seen:
            seen.add(item)
            found.append(item)
    return found


def _rule_from_frame(ns: str, cls: str, method: str) -> str:
    if ns.endswith("Architecture.Tests") or ns.endswith(".Tests"):
        return f"{cls}.{method}"
    return f"{ns}.{cls}.{method}"


def firings_from_trx(path: Path) -> list[Firing]:
    """One firing per failed test in a TRX file."""
    out: list[Firing] = []
    try:
        tree = ET.parse(path)
    except (ET.ParseError, OSError):
        return out
    root = tree.getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = root.findall(".//t:UnitTestResult", ns)
    if not results:
        results = root.findall(".//UnitTestResult")
    for result in results:
        if (result.get("outcome") or "").lower() != "failed":
            continue
        test_name = result.get("testName") or ""
        class_name = result.get("className") or ""
        message = _trx_text(result, "Message")
        stack = _trx_text(result, "StackTrace")
        stdout = _trx_text(result, "Output/StdOut")
        evidence = "\n".join(part for part in (message, stdout) if part).strip()
        cls_name, method = _split_test_name(test_name, class_name)
        rule = f"{cls_name}.{method}" if cls_name else test_name
        frame = STACK_FRAME_RE.search(stack)
        if frame:
            rule = _rule_from_frame(frame.group("ns"), frame.group("cls"), frame.group("method"))
        project = _project_of(class_name, cls_name)
        out.append(
            Firing(
                rule=rule,
                test_name=test_name,
                project=project,
                evidence=evidence,
                source="trx",
                origin=str(path),
                locations=extract_locations(evidence),
            )
        )
    return out


def _project_of(class_name: str, cls_name: str) -> str:
    """The assembly the rule lives in: the namespace without the class."""
    if not class_name:
        return ""
    if cls_name and class_name.endswith("." + cls_name):
        return class_name[: -(len(cls_name) + 1)]
    return class_name.rsplit(".", 1)[0] if "." in class_name else class_name


def _trx_text(element: ET.Element, tag: str) -> str:
    """The text of the first descendant element with this local name."""
    for node in element.iter():
        if node.tag.rsplit("}", 1)[-1] == tag:
            if node.text:
                return node.text.strip()
            return (node.text or "").strip()
    return ""


def _split_test_name(test_name: str, class_name: str) -> tuple[str, str]:
    if "." in test_name:
        cls, method = test_name.rsplit(".", 1)
        return cls.split(".")[-1], method
    if class_name:
        return class_name.split(".")[-1], test_name
    return "", test_name


def firings_from_log(path: Path) -> list[Firing]:
    """One firing per failed test block in a CI job log."""
    out: list[Firing] = []
    try:
        raw = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return out
    project = ""
    lines = [_clean_line(line) for line in raw.splitlines()]
    index = 0
    while index < len(lines):
        line = lines[index]
        group = PROJECT_GROUP_RE.search(line)
        if group:
            project = group.group("project")
        m = FAILED_TEST_RE.search(line) if "failed" in line else None
        if not m or "failed:" in line:
            index += 1
            continue
        test_name = m.group("name")
        block: list[str] = []
        index += 1
        while index < len(lines) and len(block) < MAX_EVIDENCE_LINES:
            nxt = lines[index]
            if "failed" in nxt and FAILED_TEST_RE.search(nxt) and "failed:" not in nxt:
                break
            if nxt.startswith("[slow]") or nxt.startswith("In process file artifacts"):
                break
            if nxt.strip():
                block.append(nxt)
            index += 1
        evidence = "\n".join(block).strip()
        frame = None
        for candidate in block:
            frame = STACK_FRAME_RE.search(candidate)
            if frame:
                break
        cls_name = test_name.rsplit(".", 1)[0].split(".")[-1] if "." in test_name else ""
        method = test_name.rsplit(".", 1)[-1]
        rule = f"{cls_name}.{method}" if cls_name else test_name
        if frame:
            rule = _rule_from_frame(frame.group("ns"), frame.group("cls"), frame.group("method"))
            # The stack frame names the assembly the rule lives in, which is
            # the only way to attribute a firing in a log that has no
            # "dotnet exec <project>" marker - the build job runs the suite
            # from the build tool, not from the shard loop.
            if not project and frame.group("ns").endswith(".Tests"):
                project = frame.group("ns")
        out.append(
            Firing(
                rule=rule,
                test_name=test_name,
                project=project,
                evidence=evidence,
                source="log",
                origin=str(path),
                locations=extract_locations(evidence),
            )
        )
    return out


def _clean_line(line: str) -> str:
    """A log line without its colour codes, timestamp and logger prefix."""
    text = ANSI_RE.sub("", line)
    text = GH_PREFIX_RE.sub("", text)
    text = NUKE_PREFIX_RE.sub("", text)
    return text.rstrip()


# --------------------------------------------------------------------------
# Classification
# --------------------------------------------------------------------------


def classify(
    firing: Firing,
    registry: Registry,
    index: GuardIndex,
    allowance_text: dict[str, str],
) -> None:
    """Fill in classification / reason / ratchet for one firing."""
    # 1. A per-firing entry wins over everything else.
    for entry in registry.firings:
        if entry.get("rule") != firing.rule:
            continue
        needle = entry.get("evidence_contains")
        if needle and needle not in firing.evidence:
            continue
        firing.classification = entry.get("class", "unknown")
        firing.classified_by = "registry:firing"
        firing.reason = entry.get("reason", "")
        firing.ratchet = entry.get("ratchet", "")
        return

    # 2. A per-rule entry.
    rule_entry = registry.rules.get(firing.rule)
    if rule_entry:
        firing.classification = rule_entry.get("class", "unknown")
        firing.classified_by = "registry:rule"
        firing.reason = rule_entry.get("reason", "")
        firing.ratchet = rule_entry.get("ratchet", "")
        return

    # 3. The two mechanical detectors.
    guard_source = ""
    if firing.guard_file:
        guard_source = index.source.get(firing.guard_file, "")
    for path, _line in firing.locations:
        if not guard_source:
            continue
        if path in guard_source and LEDGER_STALE_RE.search(firing.evidence):
            firing.classification = "noise"
            firing.classified_by = "detector:ledger-retirement"
            firing.reason = (
                f"the guard's own source lists {path} as allowed code and the assertion says "
                "the allowance is stale: the rule fired on its own allowance"
            )
            firing.ratchet = "ledger-edit"
            return

    for path, _line in firing.locations:
        for allowance_path, text in allowance_text.items():
            if path in text:
                firing.classification = "noise"
                firing.classified_by = "detector:allowance-listed"
                firing.reason = (
                    f"{path} is named in {allowance_path}: the repository allows that code, "
                    "so the firing carries no new information"
                )
                firing.ratchet = "ledger-edit"
                return

    # 4. Unknown: the worklist.
    firing.classification = "unknown"
    firing.classified_by = "none"
    firing.reason = "no registry entry and no mechanical signal: needs a human decision"


# --------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------


def _head_of(firing: Firing) -> str:
    """The assertion line, normalized.

    The first line of a failure block is the assertion itself: what was
    expected, what was received, and the rule's own statement of the
    invariant. Everything after it is the stack frame and the repeat the
    build tool prints, which differ between the two places the same firing
    is visible.
    """
    first = firing.evidence.splitlines()[0] if firing.evidence else ""
    return " ".join(first.split())


def dedupe(firings: list[Firing]) -> list[Firing]:
    """One firing per (run, rule, evidence).

    The same guard failure is seen twice in a run that fails both places: the
    in-build architecture gate (the `build` job) and the shard that runs the
    suite again (the `test` job). Both see the same firing, and it is ONE
    firing - counting it twice would double every number in the report.
    """
    merged: dict[tuple[str, str, str], Firing] = {}
    for firing in firings:
        head = _head_of(firing)
        key = (firing.run_id, firing.rule, head)
        existing = merged.get(key)
        if existing is None:
            merged[key] = firing
            continue
        existing.origin = f"{existing.origin}, {firing.origin}"
        if not existing.guard_file and firing.guard_file:
            existing.guard_file = firing.guard_file
            existing.guard_line = firing.guard_line
    return list(merged.values())


def build_report(
    firings: list[Firing],
    registry: Registry,
    index: GuardIndex,
    sources: dict[str, list[str]],
    window: dict[str, str],
) -> dict[str, Any]:
    by_class = {name: 0 for name in CLASSES}
    per_rule: dict[str, dict[str, Any]] = {}
    distinct: set[tuple[str, str]] = set()
    for firing in firings:
        by_class[firing.classification] = by_class.get(firing.classification, 0) + 1
        head = _head_of(firing)
        distinct.add((firing.rule, head))
        rule = per_rule.setdefault(
            firing.rule,
            {
                "rule": firing.rule,
                "count": 0,
                "by_class": {name: 0 for name in CLASSES},
                "guard_file": firing.guard_file,
                "guard_line": firing.guard_line,
                "classified_by": firing.classified_by,
                "reason": firing.reason,
                "ratchet": firing.ratchet,
                "evidence": [],
            },
        )
        rule["count"] += 1
        rule["by_class"][firing.classification] = (
            rule["by_class"].get(firing.classification, 0) + 1
        )
        rule.setdefault("_distinct", set()).add(head)
        if not rule["guard_file"] and firing.guard_file:
            rule["guard_file"] = firing.guard_file
            rule["guard_line"] = firing.guard_line
        if len(rule["evidence"]) < 5:
            rule["evidence"].append(
                {
                    "run_id": firing.run_id,
                    "run": firing.run,
                    "origin": firing.origin,
                    "guard_file": firing.guard_file,
                    "guard_line": firing.guard_line,
                    "locations": [{"path": p, "line": l} for p, l in firing.locations],
                    "evidence_head": firing.evidence.splitlines()[0] if firing.evidence else "",
                }
            )
    for rule in per_rule.values():
        count = rule["count"] or 1
        rule["distinct"] = len(rule.pop("_distinct", set()))
        rule["noise_share"] = round(rule["by_class"].get("noise", 0) / count, 4)
        rule["unknown_share"] = round(rule["by_class"].get("unknown", 0) / count, 4)
    ordered = sorted(per_rule.values(), key=lambda r: (-r["noise_share"], -r["count"], r["rule"]))
    total = len(firings)
    return {
        "schema": SCHEMA,
        "window": window,
        "sources": sources,
        "totals": {
            "firings": total,
            "distinct_firings": len(distinct),
            "rules": len(per_rule),
            "by_class": by_class,
            "noise_share": round(by_class.get("noise", 0) / total, 4) if total else 0.0,
            "unknown_share": round(by_class.get("unknown", 0) / total, 4) if total else 0.0,
            "classified_share": (
                round((total - by_class.get("unknown", 0)) / total, 4) if total else 0.0
            ),
        },
        "rules": ordered,
        "firings": [f.to_json() for f in firings],
    }

def render_markdown(report: dict[str, Any]) -> str:
    totals = report["totals"]
    lines: list[str] = []
    lines.append("# Guard firings, classified")
    lines.append("")
    window = report.get("window", {})
    lines.append(f"- window: {window.get('from', '?')} .. {window.get('to', '?')}")
    lines.append(f"- firings: {totals['firings']} across {totals['rules']} rules")
    lines.append(
        f"- distinct firings (rule + evidence): {totals.get('distinct_firings', totals['firings'])}"
    )
    lines.append(
        "- by class: "
        + ", ".join(f"{name} {totals['by_class'].get(name, 0)}" for name in CLASSES)
    )
    lines.append(f"- noise share: {totals['noise_share']:.1%}")
    lines.append(f"- unknown share: {totals['unknown_share']:.1%}")
    lines.append("")
    lines.append("| rule | firings | distinct | defect | style | noise | unknown | noise share | ratchet |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---|")
    for rule in report["rules"]:
        counts = rule["by_class"]
        lines.append(
            f"| `{rule['rule']}` | {rule['count']} | {rule.get('distinct', rule['count'])} | "
            f"{counts.get('defect', 0)} | "
            f"{counts.get('style', 0)} | {counts.get('noise', 0)} | {counts.get('unknown', 0)} | "
            f"{rule['noise_share']:.0%} | {rule.get('ratchet') or '-'} |"
        )
    lines.append("")
    return "\n".join(lines)


def render_summary(report: dict[str, Any]) -> str:
    """The table that goes into a CI job summary."""
    totals = report["totals"]
    lines = [
        "| class | firings |",
        "|---|---:|",
    ]
    for name in CLASSES:
        lines.append(f"| {name} | {totals['by_class'].get(name, 0)} |")
    lines.append(f"| **total** | **{totals['firings']}** |")
    lines.append("")
    if report["rules"]:
        lines.append("| rule | firings | noise | unknown |")
        lines.append("|---|---:|---:|---:|")
        for rule in report["rules"][:15]:
            lines.append(
                f"| `{rule['rule']}` | {rule['count']} | "
                f"{rule['by_class'].get('noise', 0)} | {rule['by_class'].get('unknown', 0)} |"
            )
    return "\n".join(lines)


# --------------------------------------------------------------------------
# Inputs
# --------------------------------------------------------------------------


def collect_trx(paths: Iterable[str]) -> tuple[list[Firing], list[str]]:
    firings: list[Firing] = []
    used: list[str] = []
    for raw in paths:
        files = _expand(raw, "*.trx")
        for file in files:
            try:
                found = firings_from_trx(file)
            except Exception:  # a malformed artifact must not stop the report
                continue
            if found:
                used.append(str(file))
                firings.extend(found)
    return firings, used


def collect_logs(paths: Iterable[str]) -> tuple[list[Firing], list[str]]:
    firings: list[Firing] = []
    used: list[str] = []
    for raw in paths:
        files = _expand(raw, "*.log")
        for file in files:
            try:
                found = firings_from_log(file)
            except Exception:  # a malformed artifact must not stop the report
                continue
            if found:
                used.append(str(file))
                firings.extend(found)
    return firings, used


def _expand(raw: str, pattern: str) -> list[Path]:
    """A path, a directory or a glob pattern -> the files to read."""
    if any(ch in raw for ch in "*?["):
        import glob

        return [Path(p) for p in sorted(glob.glob(raw, recursive=True)) if Path(p).is_file()]
    path = Path(raw)
    if path.is_dir():
        return sorted(path.rglob(pattern))
    if path.is_file():
        return [path]
    return []


def guess_run_id(origin: str) -> str:
    match = re.search(r"(?<!\d)(\d{8,})(?!\d)", Path(origin).name)
    return match.group(1) if match else ""


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Classify guard firings into defect / style / noise / unknown."
    )
    parser.add_argument("--trx", action="append", default=[], help="a TRX file or a directory of them")
    parser.add_argument("--log", action="append", default=[], help="a CI job log or a directory of them")
    parser.add_argument(
        "--classes",
        default="tools/guard-triage-classes.json",
        help="the classification registry (default: tools/guard-triage-classes.json)",
    )
    parser.add_argument(
        "--guard-dir",
        default="tests/Harbor.Architecture.Tests",
        help="the guard project to index for file:line evidence",
    )
    parser.add_argument("--repo-root", default=".", help="the repository root")
    parser.add_argument("--out", help="write the JSON report here")
    parser.add_argument("--markdown", help="write the Markdown report here")
    parser.add_argument("--summary", help="append the summary table to this file")
    parser.add_argument(
        "--project",
        action="append",
        default=[],
        help="keep only the firings of this test project (repeatable). The guard "
        "population of issue #1726 is Harbor.Architecture.Tests.",
    )
    parser.add_argument(
        "--min-guard-files",
        type=int,
        default=0,
        help="floor on the number of guard files the index found. A renamed guard "
        "project would otherwise make the tool report an empty tree, which reads "
        "as 'no guard fired'",
    )
    parser.add_argument(
        "--runs",
        help="a JSON file with the metadata of the scanned runs (id -> branch/sha/title), "
        "so every firing in the report names the run it came from",
    )
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    repo_root = Path(args.repo_root).resolve()

    if args.self_test:
        return self_test()

    registry_path = Path(args.classes)
    if not registry_path.is_absolute():
        registry_path = repo_root / registry_path
    if not registry_path.is_file():
        print(f"guard-triage: no registry at {registry_path}", file=sys.stderr)
        return 1
    try:
        registry = Registry.load(registry_path)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"guard-triage: {exc}", file=sys.stderr)
        return 1

    guard_dir = Path(args.guard_dir)
    if not guard_dir.is_absolute():
        guard_dir = repo_root / guard_dir
    index = GuardIndex.build(guard_dir, repo_root)
    if args.min_guard_files and len(index.source) < args.min_guard_files:
        print(
            f"guard-triage: indexed {len(index.source)} guard file(s) under "
            f"{guard_dir}, the floor is {args.min_guard_files}. A guard project "
            "that moved would otherwise make this tool report an empty tree, "
            "which reads as 'no guard fired'.",
            file=sys.stderr,
        )
        return 1

    firings, trx_used = collect_trx(args.trx)
    log_firings, logs_used = collect_logs(args.log)
    firings.extend(log_firings)

    if args.project:
        wanted = set(args.project)
        firings = [f for f in firings if f.project in wanted]
    firings = dedupe(firings)

    run_index: dict[str, dict[str, Any]] = {}
    if args.runs:
        run_path = Path(args.runs)
        if not run_path.is_absolute():
            run_path = repo_root / run_path
        raw_runs = json.loads(run_path.read_text(encoding="utf-8"))
        if isinstance(raw_runs, dict):
            run_index = {str(k): v for k, v in raw_runs.items()}
        else:
            for entry in raw_runs:
                key = str(entry.get("run_id") or entry.get("id") or "")
                if key:
                    run_index[key] = entry

    for firing in firings:
        if not firing.run_id:
            firing.run_id = guess_run_id(firing.origin)
        if firing.run_id and firing.run_id in run_index:
            firing.run = run_index[firing.run_id]
        cls_name = firing.rule.split(".")[0] if "." in firing.rule else firing.rule
        method = firing.rule.rsplit(".", 1)[-1]
        path, line = index.locate(cls_name, method)
        if path:
            firing.guard_file = path
            firing.guard_line = line
        classify(firing, registry, index, registry.allowance_text(repo_root, firing.rule))

    firings.sort(key=lambda f: (f.rule, f.run_id))
    dates = sorted({f.run_id for f in firings})
    stamps = sorted(
        f.run.get("created_at", "") for f in firings if f.run.get("created_at")
    )
    window = {
        "from": stamps[0] if stamps else "",
        "to": stamps[-1] if stamps else "",
        "runs": len(dates),
    }
    report = build_report(
        firings,
        registry,
        index,
        {"trx": trx_used, "logs": logs_used},
        window,
    )

    payload = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    if args.out:
        Path(args.out).write_text(payload, encoding="utf-8")
    else:
        sys.stdout.write(payload)
    if args.markdown:
        Path(args.markdown).write_text(render_markdown(report) + "\n", encoding="utf-8")
    if args.summary:
        with Path(args.summary).open("a", encoding="utf-8") as handle:
            handle.write(render_summary(report) + "\n")
    if not args.quiet and args.out:
        totals = report["totals"]
        print(
            f"guard-triage: {totals['firings']} firings, {totals['rules']} rules, "
            f"noise {totals['by_class'].get('noise', 0)}, "
            f"unknown {totals['by_class'].get('unknown', 0)} -> {args.out}",
            file=sys.stderr,
        )
    return 0


# --------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------


def self_test() -> int:
    """The tool must still fail on what it claims to catch."""
    failures: list[str] = []

    trx = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="TheLedgerHolds" outcome="Failed" className="Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule">
      <Output><ErrorInfo><Message>Expected to contain "stale entries", because a ledger entry that is stale is an allowance that outlived its target. Found: src/Foo.cs:12</Message></ErrorInfo></Output>
      <Output><StdOut>ledger scan output</StdOut></Output>
      <StackTrace>   at Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule.TheLedgerHolds()</StackTrace>
    </UnitTestResult>
    <UnitTestResult testName="PassingTest" outcome="Passed" className="Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule" />
  </Results>
</TestRun>
"""
    log = (
        "test (core)\tRun shard core\t2026-10-20T15:49:53.5Z ##[group]dotnet exec Harbor.Architecture.Tests\n"
        "test (core)\tRun shard core\t2026-10-20T15:49:53.6Z \x1b[31mfailed\x1b[m TheLedgerHolds \x1b[90m(12ms)\x1b[m\n"
        "test (core)\tRun shard core\t2026-10-20T15:49:53.6Z   TUnit.Engine.Exceptions.TestFailedException: Expected to contain \"stale entries\". Found: src/Foo.cs:12\n"
        "test (core)\tRun shard core\t2026-10-20T15:49:53.6Z     at Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule.TheLedgerHolds()\n"
    )

    with _temp_dir() as tmp:
        trx_path = tmp / "sample.trx"
        trx_path.write_text(trx, encoding="utf-8")
        log_path = tmp / "sample.log"
        log_path.write_text(log, encoding="utf-8")

        found = firings_from_trx(trx_path)
        if len(found) != 1:
            failures.append(f"trx: expected 1 firing, got {len(found)}")
        else:
            if found[0].rule != "PanelKeyRouteReachabilityRule.TheLedgerHolds":
                failures.append(f"trx: rule is {found[0].rule!r}")
            if ("src/Foo.cs", 12) not in found[0].locations:
                failures.append(f"trx: locations are {found[0].locations}")

        log_firings = firings_from_log(log_path)
        if len(log_firings) != 1:
            failures.append(f"log: expected 1 firing, got {len(log_firings)}")
        else:
            if log_firings[0].rule != "PanelKeyRouteReachabilityRule.TheLedgerHolds":
                failures.append(f"log: rule is {log_firings[0].rule!r}")
            if log_firings[0].project != "Harbor.Architecture.Tests":
                failures.append(f"log: project is {log_firings[0].project!r}")

        # The registry must be loadable and must reject a bad class.
        registry_path = tmp / "registry.json"
        registry_path.write_text(
            json.dumps(
                {
                    "schema": REGISTRY_SCHEMA,
                    "rules": [
                        {
                            "rule": "PanelKeyRouteReachabilityRule.TheLedgerHolds",
                            "class": "noise",
                            "reason": "the ledger fired on its own allowance",
                            "ratchet": "ledger-edit",
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        registry = Registry.load(registry_path)
        if registry.rules["PanelKeyRouteReachabilityRule.TheLedgerHolds"]["class"] != "noise":
            failures.append("registry: class not read")

        bad_path = tmp / "bad-registry.json"
        bad_path.write_text(
            json.dumps({"schema": REGISTRY_SCHEMA, "rules": [{"rule": "X.Y", "class": "bogus"}]}),
            encoding="utf-8",
        )
        try:
            Registry.load(bad_path)
            failures.append("registry: a bad class was accepted")
        except ValueError:
            pass

        # A firing with no registry entry must stay `unknown`, not be guessed.
        unknown = Firing(
            rule="SomeRule.SomeTest",
            test_name="SomeTest",
            project="Harbor.Architecture.Tests",
            evidence="something went wrong",
            source="log",
            origin="x.log",
        )
        classify(unknown, registry, GuardIndex.build(tmp), {})
        if unknown.classification != "unknown":
            failures.append(f"unregistered firing classified as {unknown.classification!r}")

    for failure in failures:
        print(f"guard-triage self-test: FAIL {failure}", file=sys.stderr)
    if failures:
        return 2
    print("guard-triage self-test: ok")
    return 0


class _temp_dir:
    def __enter__(self) -> Path:
        import tempfile

        self._dir = tempfile.TemporaryDirectory()
        return Path(self._dir.name)

    def __exit__(self, *exc: Any) -> None:
        self._dir.cleanup()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
