#!/usr/bin/env python3
"""Shared plumbing for the markdown gates in tools/ — stdlib only, no dotnet.

Two gates live next to this file and both are wired by
.github/workflows/docs.yml (issue #509):

  check-md-links.py   do internal links + GitHub anchors resolve?
  md-lint.py          do the files obey the repo's markdown invariants?

This module holds the two things they must not each reinvent:

  1. `require_non_vacuous` — the anti-vacuity guard. A checker that scans
     nothing (or nearly nothing) and exits 0 is indistinguishable from a
     checker that found nothing, and that is the failure mode this repo has
     already paid for twice: a link gate in a workflow whose `paths-ignore`
     made it unreachable, and an allow-list that "verified zero files". So
     every gate reports how many files and how many units it looked at, and
     a floor (`--min-files` / `--min-refs`, set in docs.yml) turns a silently
     narrowed scan into a red build.

  2. `SelfTest` + `make_fixture` — a throwaway git repo in which the gate is
     run against documents that are *known* to be broken. `--self-test` runs
     on every docs change in CI and fails if the checker stops reporting a
     deliberately broken link. Without it, "the gate passed" and "the gate was
     blind" are the same green check.

Imported by the sibling scripts as `import md_gate` (same directory), so the
file is copied alongside them by `make_fixture`.

No third-party packages, no network, no dotnet.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from collections.abc import Mapping

# Marker copied into every self-test fixture. If this string shows up in a
# gate's output, the gate is reporting its own input back instead of
# diagnosing it — a bug worth failing on.
FIXTURE_MARKER = "harbor-md-gate-selftest"


def git_env() -> dict[str, str]:
    """Environment for our own `git` calls: never prompt, never inherit a
    terminal. CI runners and interactive shells both get the same behaviour."""
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    return env


def tracked_md(repo: str) -> list[str]:
    """Every tracked `*.md` path, relative to `repo`, sorted.

    `git ls-files` (the index) rather than a glob: an untracked scratch file
    must not be able to break the build, and a tracked file must not be able
    to hide from it.
    """
    proc = subprocess.run(
        ["git", "ls-files", "*.md"],
        cwd=repo,
        capture_output=True,
        text=True,
        check=True,
        env=git_env(),
    )
    return sorted(proc.stdout.split())


def require_non_vacuous(
    label: str,
    files: int,
    units: int,
    unit_name: str,
    min_files: int,
    min_units: int,
) -> list[str]:
    """Return guard violations for a scan; empty list means the scan was real.

    `files` is how many documents were read, `units` how many things were
    examined inside them (link references, checked lines). The floors are a
    ratchet: they are set in .github/workflows/docs.yml to today's numbers,
    and lowering one is a deliberate act that a reviewer can see in the diff.
    """
    problems: list[str] = []
    if files == 0:
        problems.append(
            f"{label}: scanned ZERO markdown files — a gate that examined "
            f"nothing cannot report that everything is fine"
        )
        return problems
    if files < min_files:
        problems.append(
            f"{label}: scanned {files} markdown files, floor is {min_files} — "
            f"the file set shrank or the path filter is wrong; lower the floor "
            f"in .github/workflows/docs.yml only if the removal was intended"
        )
    if units == 0:
        problems.append(
            f"{label}: examined ZERO {unit_name} in {files} files — the parser "
            f"stopped matching, not the documents"
        )
    elif units < min_units:
        problems.append(
            f"{label}: examined {units} {unit_name}, floor is {min_units} — "
            f"same cause as the file count: the scan got narrower"
        )
    return problems


class SelfTest:
    """Tiny assertion recorder for the `--self-test` mode of a gate."""

    def __init__(self, gate: str) -> None:
        self.gate = gate
        self.failures: list[str] = []
        self.checks = 0

    def expect(self, name: str, ok: bool, detail: str = "") -> None:
        self.checks += 1
        if ok:
            print(f"  ok   {name}")
            return
        self.failures.append(f"{name}: {detail}" if detail else name)
        print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))

    def finish(self) -> int:
        if self.failures:
            print(
                f"\nFAIL: {self.gate} self-test — {len(self.failures)} of "
                f"{self.checks} checks failed. The gate is not trusted "
                f"(it may be broken, or broken-but-green)."
            )
            return 1
        print(f"\nOK: {self.gate} self-test — {self.checks} checks passed.")
        return 0


def make_fixture(script: str, files: Mapping[str, str]) -> str:
    """Build a throwaway git repo holding the gate under test + `files`.

    `script` is the file name of the gate (e.g. "check-md-links.py"); it and
    this module are copied into <root>/tools/ so the gate resolves its repo
    root the same way it does in Harbor. `files` maps repo-relative path to
    content; the tree is `git add`-ed (never committed) so `git ls-files`
    sees it.
    """
    root = tempfile.mkdtemp(prefix="harbor-md-gate-")
    tools = os.path.join(root, "tools")
    os.makedirs(tools, exist_ok=True)

    here = os.path.dirname(os.path.abspath(__file__))
    for name in (script, os.path.basename(__file__)):
        shutil.copy2(os.path.join(here, name), os.path.join(tools, name))

    for rel, content in files.items():
        path = os.path.join(root, rel)
        parent = os.path.dirname(path)
        if parent:
            os.makedirs(parent, exist_ok=True)
        payload = content if isinstance(content, bytes) else content.encode("utf-8")
        with open(path, "wb") as fh:
            fh.write(payload)

    subprocess.run(["git", "init", "-q", "-b", "main"], cwd=root, check=True, env=git_env())
    # The gates read the working tree, not the blob, so CRLF fixtures must
    # survive `git add` verbatim. Without this, a machine with
    # core.autocrlf=true rewrites them and the ENC-CRLF case tests nothing.
    subprocess.run(["git", "config", "core.autocrlf", "false"], cwd=root, check=True, env=git_env())
    subprocess.run(["git", "add", "-A"], cwd=root, check=True, env=git_env())
    return root


def run_gate(root: str, script: str, *args: str) -> tuple[int, str]:
    """Run `tools/<script>` inside fixture `root`; return (exit code, output)."""
    proc = subprocess.run(
        [sys.executable, os.path.join("tools", script), *args],
        cwd=root,
        capture_output=True,
        text=True,
        env=git_env(),
    )
    return proc.returncode, proc.stdout + proc.stderr
