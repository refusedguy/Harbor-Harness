#!/usr/bin/env python3
"""The `selftest` job's step list has ONE source, and this gate keeps it honest.

Stdlib only -- no pip, no network, no dotnet. Wired into the `workflows` job of
.github/workflows/docs.yml, where it guards ONE thing: the list of self-test
steps between the markers in the `selftest` job says what
tools/ci-gate-selftests.json says.

WHY THIS FILE EXISTS (issue #2205)

  The `selftest` job used to be a hand-written list that every new gate
  appended to at ONE point. Two agents adding two gates in the same night
  collided there 32 times: the list had 46 steps, one insertion point, and
  each entry carried a 4-8 line comment, so a conflict was 48 lines of
  prose to read for a two-line change.

  The steps now come from ONE data file. Adding a gate adds an entry there
  (next to the comment that says what the self-test proves), and this tool
  regenerates the job's list. The generated list is an artefact: nobody
  hand-edits it, and a merge that touches it is resolved by re-running
  --write, not by reading a diff of 46 steps.

HOW IT WORKS

  --write     rewrites the marked block in docs.yml from the source. This is
              the generator; a human runs it after editing the source.

  (default)  the gate. It reads the source and the marked block and fails
              when they disagree, naming the first line that differs. It
              also fails when a marker is missing (a block that cannot be
              found is a block that is not checked) or the source is too
              small for the --min-steps floor.

  --self-test plants a desync in a temporary tree (one edited `run:` line,
              one removed step, one missing marker) and asserts the gate
              catches each, then asserts the clean tree is green. A gate
              that cannot fail is indistinguishable from a gate that found
              nothing.

Usage:
  python3 tools/ci-selftests.py
  python3 tools/ci-selftests.py --write
  python3 tools/ci-selftests.py --self-test
  python3 tools/ci-selftests.py --min-steps 47

Exit codes:  0 = clean, 1 = findings, 2 = bad invocation / no input.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)

SOURCE_FILE = "tools/ci-gate-selftests.json"
WORKFLOW = ".github/workflows/docs.yml"

START = "      # BEGIN GENERATED SELFTEST STEPS (tools/ci-gate-selftests.json; do not hand-edit)"
END = "      # END GENERATED SELFTEST STEPS"


class SourceError(Exception):
    """The source is missing or malformed; the gate cannot see what it guards."""


# ---------------------------------------------------------------------------
# The source
# ---------------------------------------------------------------------------


def load_source(root):
    path = os.path.join(root, SOURCE_FILE)
    if not os.path.isfile(path):
        raise SourceError(f"{SOURCE_FILE} is not in the tree — the step list has no source")
    try:
        with open(path, encoding="utf-8") as handle:
            source = json.load(handle)
    except (OSError, ValueError) as error:
        raise SourceError(f"{SOURCE_FILE} could not be read: {error}") from error
    if not isinstance(source, dict) or source.get("version") != 1:
        raise SourceError(f"{SOURCE_FILE} must be an object with version 1")
    steps = source.get("steps")
    if not isinstance(steps, list) or not steps:
        raise SourceError(f"{SOURCE_FILE} has no `steps`")
    seen = set()
    for index, step in enumerate(steps):
        if not isinstance(step, dict):
            raise SourceError(f"step {index} of {SOURCE_FILE} is not an object")
        for field in ("key", "name", "run", "comment"):
            if field not in step:
                raise SourceError(f"step {index} of {SOURCE_FILE} has no `{field}`")
        if not isinstance(step["key"], str) or not step["key"]:
            raise SourceError(f"step {index} of {SOURCE_FILE} has no key")
        if step["key"] in seen:
            raise SourceError(f"{SOURCE_FILE} names the key `{step['key']}` twice")
        seen.add(step["key"])
        if not isinstance(step["name"], str) or not step["name"]:
            raise SourceError(f"step {step['key']} of {SOURCE_FILE} has no name")
        if not isinstance(step["run"], str) or "--self-test" not in step["run"] and "--selftest" not in step["run"]:
            raise SourceError(
                f"step {step['key']} of {SOURCE_FILE} is not a self-test "
                f"(its `run` has no --self-test flag)")
        if not isinstance(step["comment"], list) or not all(
                isinstance(line, str) and line.startswith("      #") for line in step["comment"]):
            raise SourceError(
                f"step {step['key']} of {SOURCE_FILE} has a comment that is not "
                f"a list of 6-space-indented YAML comment lines")
    return source


# ---------------------------------------------------------------------------
# The generated block
# ---------------------------------------------------------------------------


def generate_lines(source):
    """The marked block, as the lines docs.yml must carry."""
    out = [START]
    for index, step in enumerate(source["steps"]):
        if index:
            out.append("")
        out.extend(step["comment"])
        out.append("      - name: " + step["name"])
        out.append("        run: " + step["run"])
    out.append(END)
    return out


def read_workflow(root):
    path = os.path.join(root, WORKFLOW)
    try:
        with open(path, encoding="utf-8") as handle:
            return handle.read().split("\n")
    except OSError as error:
        raise SourceError(f"{WORKFLOW} could not be read: {error}") from error


def markers(lines):
    starts = [i for i, line in enumerate(lines) if line == START]
    ends = [i for i, line in enumerate(lines) if line == END]
    return starts, ends


def write(root):
    source = load_source(root)
    lines = read_workflow(root)
    starts, ends = markers(lines)
    if len(starts) != 1 or len(ends) != 1 or starts[0] >= ends[0]:
        print(f"::error::the generated self-test block in {WORKFLOW} has no clean pair of markers", file=sys.stderr)
        return 1
    generated = generate_lines(source)
    new = lines[:starts[0]] + generated + lines[ends[0] + 1:]
    path = os.path.join(root, WORKFLOW)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(new))
    print(f"rewrote the generated self-test steps in {WORKFLOW} ({len(source['steps'])} steps)")
    return 0


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------


def check(root, min_steps=0):
    try:
        source = load_source(root)
        lines = read_workflow(root)
    except SourceError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    problems = []
    if min_steps and len(source["steps"]) < min_steps:
        problems.append(
            f"the source names {len(source['steps'])} self-test step(s) and the floor is "
            f"{min_steps} — the gate is not looking at the whole set")
    starts, ends = markers(lines)
    if len(starts) != 1 or len(ends) != 1 or starts[0] >= ends[0]:
        problems.append(
            f"{WORKFLOW} does not carry exactly one BEGIN and one END marker for the "
            f"generated self-test block (found {len(starts)} BEGIN, {len(ends)} END)")
    else:
        committed = lines[starts[0]:ends[0] + 1]
        expected = generate_lines(source)
        for offset, (found, wanted) in enumerate(zip(committed, expected)):
            if found != wanted:
                problems.append(
                    f"{WORKFLOW} line {starts[0] + offset + 1} of the generated block is "
                    f"{found!r}, the source says {wanted!r} — run "
                    f"`python3 tools/ci-selftests.py --write`")
                break
        else:
            if len(committed) != len(expected):
                problems.append(
                    f"{WORKFLOW} carries {len(committed)} line(s) in the generated block, "
                    f"the source generates {len(expected)} — run "
                    f"`python3 tools/ci-selftests.py --write`")
    for problem in problems:
        print(f"::error::{problem}", file=sys.stderr)
    if problems:
        return 1
    print(f"OK: {len(source['steps'])} self-test step(s) in {WORKFLOW} match their source")
    return 0


# ---------------------------------------------------------------------------
# The self-test
# ---------------------------------------------------------------------------


def self_test():
    """Prove every rule can fire, on a tree that is otherwise clean."""
    with tempfile.TemporaryDirectory() as tmp:
        tree = os.path.join(tmp, "tree")
        os.makedirs(os.path.join(tree, "tools"))
        os.makedirs(os.path.join(tree, ".github", "workflows"))
        source = {
            "version": 1,
            "steps": [
                {"key": "a", "name": "A fails", "run": "python3 tools/a.py --self-test", "comment": ["      # A"]},
                {"key": "b", "name": "B fails", "run": "python3 tools/b.py --self-test", "comment": ["      # B"]},
            ],
        }
        src_path = os.path.join(tree, SOURCE_FILE)
        wf_path = os.path.join(tree, WORKFLOW)
        json.dump(source, open(src_path, "w"), indent=2)
        open(wf_path, "w").write("\n".join([
            "name: docs", "jobs:", "  selftest:", "    steps:",
        ] + generate_lines(source) + [""]))
        if check(tree) != 0:
            print("FAIL: the clean fixture is not clean", file=sys.stderr)
            return 1

        # 1. One hand-edited run line.
        text = open(wf_path, encoding="utf-8").read()
        open(wf_path, "w").write(text.replace("python3 tools/b.py --self-test", "python3 tools/b.py", 1))
        if check(tree) == 0:
            print("FAIL: an edited step was not caught", file=sys.stderr)
            return 1
        open(wf_path, "w").write(text)

        # 2. One step removed from the source without regenerating.
        two = json.loads(json.dumps(source))
        two["steps"].pop()
        json.dump(two, open(src_path, "w"), indent=2)
        if check(tree) == 0:
            print("FAIL: a removed step was not caught", file=sys.stderr)
            return 1
        json.dump(source, open(src_path, "w"), indent=2)

        # 3. A missing marker: a block that cannot be found is not checked.
        open(wf_path, "w").write(text.replace(END, "      # gone", 1))
        if check(tree) == 0:
            print("FAIL: a missing marker was not caught", file=sys.stderr)
            return 1
        open(wf_path, "w").write(text)

        # 4. The non-vacuity floor.
        if check(tree, min_steps=3) == 0:
            print("FAIL: a source below the --min-steps floor was not caught", file=sys.stderr)
            return 1
    print("OK: the self-test-list gate fires on an edited step, a removed step, a missing marker and a weak floor")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", default=REPO_ROOT)
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--min-steps", type=int, default=0)
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.write:
        return write(args.root)
    return check(args.root, args.min_steps)


if __name__ == "__main__":
    sys.exit(main())
