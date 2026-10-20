#!/usr/bin/env python3
"""Arch-anchor gate: an architecture rule may not anchor on a line number (#1728).

Stdlib only — no pip, no network, no dotnet. It lives in `tools/` next to
check-doc-cites.py / check-test-dup.py / check-test-json-reflection.py and
answers the one question those gates do not:

  check-doc-cites.py           does a `file:line` a TEST writes still point at
                               the line its author meant?
  THIS ONE                    may an architecture rule anchor on a line number
                               AT ALL?

WHY THIS FILE EXISTS (issue #1728)

  `docs/CITATION_DRIFT_MEASUREMENT.md` priced the alternative and the answer
  was 90%: of the 66 citations in `tests/**/*.cs` that were provably WRONG on
  the day they were measured, 58 named a line that a symbol name would have
  survived. A line number is a coordinate, not a name, and every line inserted
  above it moves it. The measured cost of the coordinate form was three
  "repair the citation" incidents in one shift (#1701: `:549→:561`, #1658,
  #1502) and the standing re-run tax of the gate that watches them.

  The architecture tree was the worst offender: 50 rule files, 237 `file:line`
  anchors on `dev` at `56e24932`, of which 197 sat in a rule's PROSE (a
  comment that says where a fact lives) and 40 in a rule's FAILURE MESSAGE (a
  string that says the same thing to whoever reads a red build). Every one of
  them was a claim about a NUMBER, and a number is the one thing a refactor
  moves without asking.

  So the tree was converted to SYMBOLIC anchors — `Type.Member`, plus the
  condition that makes the site the one being talked about — and this gate is
  what keeps it converted.

THE RULE

  A `path/File.cs:123` shape inside `tests/Harbor.Architecture.Tests/**` is a
  violation. The scan reads the WHOLE file, not just its comments, because a
  rule's failure message is the same claim as its prose: both name the place
  a reader should look, and both are wrong the moment a line moves above it.

  The grammar is the same one `check-doc-cites.py` fences with (the CITATION
  pattern there), so "the anchor this gate refuses" and "the anchor the
  citation gate watches" are the same shape and cannot drift apart by
  accident.

  A symbolic anchor is not a magic word: `Type.Member` survives a line being
  inserted above it, and it survives the member being MOVED to another file.
  What it does not survive is the member being RENAMED — and a rename is a
  deliberate, reviewable act that a compiler catches, unlike a line shifting
  by one because somebody added a blank line.

THE EXCEPTIONS FILE

  `tools/arch-anchor-exceptions.txt` carries the anchors that are part of the
  CHECKED FACT rather than a pointer to it, one per line:

    <path relative to the repo root> | <the anchor text> | <reason>

  The key is the ANCHOR TEXT, never a line number. A line-number key would be
  the very defect this file exists to stop: it would go stale on the next
  insert and the exception would quietly stop excusing anything.

  It is a RATCHET in both directions, like `tools/test-json-reflection-
  exceptions.txt`: a row the tree no longer produces is RED, so a closed hole
  cannot stay blessed. Every row carries a reason, and a row without one is a
  row the gate rejects.

  Today the file carries seven rows, all of them synthetic fixtures: a
  rule's own test data that NAMES a line on purpose (a report format under
  test, a fake source file fed to a scanner, a Roslyn diagnostic location).

THE FLOORS

  `--min-files` is the anti-vacuity guard, the same one every gate here
  carries: a scanner that looked at nothing must not exit 0. A walker that
  stopped matching `*.cs`, or a path filter that stopped resolving, drives
  the count down and the job goes red instead of quiet.

  `--min-anchors` is NOT a floor on the number of anchors found — that number
  is expected to be ZERO, and a floor on it would be a floor on the defect.
  It is a floor on the number of files the scan READ, which is the only count
  that must not shrink silently.

NON-VACUITY

  `--self-test` builds a throwaway git repo with a planted `file:line` anchor
  in an architecture rule and asserts the gate goes RED naming it; then adds
  the matching exception row and asserts the gate goes GREEN; then deletes
  the anchor and asserts the STALE row goes RED. A gate whose failure path is
  not tested is a gate that is green because it is blind.
"""

from __future__ import annotations

import argparse
import os
import re
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCH_DIR = os.path.join("tests", "Harbor.Architecture.Tests")
EXCEPTIONS_FILE = os.path.join(REPO_ROOT, "tools", "arch-anchor-exceptions.txt")

# The same grammar `check-doc-cites.py` fences with (its CITATION pattern).
# Copied rather than imported: the module name is hyphenated, so importing it
# would need importlib gymnastics in a gate that must stay trivial to read.
# The two must agree — the citation gate watches the anchors this one forbids
# in the architecture tree, and a shape one of them can see and the other
# cannot is a shape nobody watches.
CITATION = re.compile(
    r"(?<![A-Za-z0-9_./\-])"
    r"([A-Za-z0-9_][A-Za-z0-9_./\-]*\.(?:cs|json|yml|yaml|csproj|slnx|props|targets|py|axaml|xaml|editorconfig))"
    r":(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)"
)


def iter_arch_files(root: str) -> list[str]:
    """Every tracked `*.cs` under the architecture tree, relative to `root`.

    The INDEX (`git ls-files`) and not a glob: an untracked scratch file must
    not be able to fail the build, and a tracked file must not be able to
    hide from it.
    """
    import subprocess

    proc = subprocess.run(
        ["git", "ls-files", "*.cs"],
        cwd=root,
        capture_output=True,
        text=True,
        check=True,
        env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
    )
    prefix = ARCH_DIR.replace(os.sep, "/") + "/"
    return sorted(f for f in proc.stdout.split() if f.startswith(prefix))


def scan(root: str) -> list[dict]:
    """Every line-number anchor in the architecture tree."""
    findings: list[dict] = []
    for rel in iter_arch_files(root):
        path = os.path.join(root, rel)
        try:
            with open(path, encoding="utf-8", errors="replace") as fh:
                body = fh.read()
        except OSError:
            continue
        for m in CITATION.finditer(body):
            line = body.count("\n", 0, m.start()) + 1
            findings.append(
                {
                    "file": rel,
                    "line": line,
                    "anchor": m.group(0),
                    "text": body.split("\n")[line - 1].strip(),
                }
            )
    return findings


def load_exceptions(path: str) -> tuple[list[dict], list[str]]:
    """Return (rows, malformed). A row is a dict with file/anchor/reason."""
    rows: list[dict] = []
    malformed: list[str] = []
    if not os.path.isfile(path):
        return rows, malformed
    with open(path, encoding="utf-8") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            parts = [p.strip() for p in line.split("|")]
            if len(parts) != 3 or not all(parts):
                malformed.append(line)
                continue
            rows.append({"file": parts[0], "anchor": parts[1], "reason": parts[2]})
    return rows, malformed


def match_exception(finding: dict, rows: list[dict]) -> dict | None:
    for row in rows:
        if row["file"] == finding["file"] and row["anchor"] == finding["anchor"]:
            return row
    return None


def report(findings: list[dict], rows: list[dict], malformed: list[str]) -> int:
    """Print the verdict; return the process exit code."""
    excused = []
    violations = []
    for finding in findings:
        row = match_exception(finding, rows)
        if row:
            excused.append((finding, row))
        else:
            violations.append(finding)

    used: set[tuple[str, str]] = set()
    for finding, row in excused:
        used.add((row["file"], row["anchor"]))

    stale = [row for row in rows if (row["file"], row["anchor"]) not in used]

    if violations:
        print(f"=== LINE-NUMBER ANCHORS ({len(violations)}) ===")
        for finding in violations:
            print(
                f"  {finding['file']}:{finding['line']}  {finding['anchor']}\n"
                f"      {finding['text'][:150]}"
            )
        print(
            "\nAn architecture rule may not anchor on a line number: a line is a\n"
            "coordinate, and every line inserted above it moves the coordinate.\n"
            "Name the SYMBOL instead — `Type.Member` plus the condition that\n"
            "makes the site the one being talked about — or search the content\n"
            "instead of the position. If the number IS the checked fact (a\n"
            "fixture that names a line on purpose), record it in\n"
            "tools/arch-anchor-exceptions.txt with a reason."
        )

    if stale:
        print(f"=== STALE EXCEPTIONS ({len(stale)}) ===")
        for row in stale:
            print(
                f"  {row['file']}  {row['anchor']}\n"
                f"      the tree no longer carries this anchor — delete the row in\n"
                f"      the same diff that removed it. Reason on file: {row['reason']}"
            )

    if malformed:
        print(f"=== MALFORMED EXCEPTION ROWS ({len(malformed)}) ===")
        for row in malformed:
            print(f"  {row}\n      expected  <path> | <anchor> | <reason>")

    if violations or stale or malformed:
        print(f"\nFAIL: {len(violations)} line-number anchor(s), {len(stale)} stale row(s).")
        return 1

    print(
        f"OK: {len(findings)} line-number anchor(s) in the architecture tree, "
        f"all of them recorded in tools/arch-anchor-exceptions.txt with a reason."
    )
    return 0


def self_test() -> int:
    """Prove the gate fails on a planted anchor, passes on an excused one, and
    fails on a stale row."""
    import shutil
    import subprocess
    import tempfile

    here = os.path.dirname(os.path.abspath(__file__))
    failures: list[str] = []
    checks = 0

    def expect(name: str, ok: bool, detail: str = "") -> None:
        nonlocal checks
        checks += 1
        if ok:
            print(f"  ok   {name}")
        else:
            failures.append(f"{name}: {detail}" if detail else name)
            print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))

    root = tempfile.mkdtemp(prefix="harbor-arch-anchor-")
    try:
        tools = os.path.join(root, "tools")
        os.makedirs(tools, exist_ok=True)
        for name in ("check-arch-anchors.py",):
            shutil.copy2(os.path.join(here, name), os.path.join(tools, name))
        arch = os.path.join(root, ARCH_DIR)
        os.makedirs(arch, exist_ok=True)
        rule = os.path.join(arch, "SomeRule.cs")
        with open(rule, "w", encoding="utf-8") as fh:
            fh.write(
                "// The rule expects the anchor to live in WidgetFactory.BuildWidget.\n"
                "// src/Harbor.Core/WidgetFactory.cs:42 does the thing.\n"
                "internal sealed class SomeRule { }\n"
            )
        exceptions = os.path.join(tools, "arch-anchor-exceptions.txt")
        with open(exceptions, "w", encoding="utf-8") as fh:
            fh.write("# no exceptions yet\n")
        subprocess.run(["git", "init", "-q", "-b", "main"], cwd=root, check=True)
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)

        def run() -> tuple[int, str]:
            proc = subprocess.run(
                [sys.executable, os.path.join("tools", "check-arch-anchors.py")],
                cwd=root,
                capture_output=True,
                text=True,
            )
            return proc.returncode, proc.stdout + proc.stderr

        code, out = run()
        expect(
            "a planted file:line anchor is RED",
            code == 1 and "WidgetFactory.cs:42" in out,
            f"exit {code}: {out[-400:]}",
        )

        with open(exceptions, "w", encoding="utf-8") as fh:
            fh.write(
                f"{ARCH_DIR.replace(os.sep, '/')}/SomeRule.cs | src/Harbor.Core/WidgetFactory.cs:42 | "
                "the fixture names a line on purpose\n"
            )
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)
        code, out = run()
        expect(
            "the same anchor with a recorded exception is GREEN",
            code == 0,
            f"exit {code}: {out[-400:]}",
        )

        # The anchor goes away: the exception must go red, not linger.
        with open(rule, "w", encoding="utf-8") as fh:
            fh.write(
                "// The rule expects the anchor to live in WidgetFactory.BuildWidget.\n"
                "internal sealed class SomeRule { }\n"
            )
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)
        code, out = run()
        expect(
            "an exception the tree no longer produces is RED",
            code == 1 and "STALE" in out,
            f"exit {code}: {out[-400:]}",
        )

        # A row without a reason is rejected.
        with open(exceptions, "w", encoding="utf-8") as fh:
            fh.write(
                f"{ARCH_DIR.replace(os.sep, '/')}/SomeRule.cs | src/Harbor.Core/WidgetFactory.cs:42\n"
            )
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)
        code, out = run()
        expect(
            "an exception row without a reason is RED",
            code == 1 and "MALFORMED" in out,
            f"exit {code}: {out[-400:]}",
        )
    finally:
        shutil.rmtree(root, ignore_errors=True)

    if failures:
        print(f"\nFAIL: check-arch-anchors.py self-test — {len(failures)} of {checks} checks failed.")
        return 1
    print(f"\nOK: check-arch-anchors.py self-test — {checks} checks passed.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="floor on the number of architecture files the scan reads",
    )
    parser.add_argument(
        "--exceptions",
        default=EXCEPTIONS_FILE,
        help="path to the exceptions file",
    )
    parser.add_argument("--self-test", action="store_true", help="prove the gate still fails on a planted anchor")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = REPO_ROOT
    files = iter_arch_files(root)
    if args.min_files and len(files) < args.min_files:
        print(
            f"FAIL: scanned {len(files)} architecture files, floor is {args.min_files} — "
            f"the file set shrank or the path filter is wrong; lower the floor in "
            f".github/workflows/docs.yml only if the removal was intended."
        )
        return 1

    findings = scan(root)
    rows, malformed = load_exceptions(args.exceptions)
    code = report(findings, rows, malformed)
    print(
        f"and scanned {len(files)} architecture rule file(s) for line-number anchors "
        f"({len(findings)} anchor(s) found)"
    )
    return code


if __name__ == "__main__":
    sys.exit(main())
