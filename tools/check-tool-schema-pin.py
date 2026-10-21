#!/usr/bin/env python3
"""Gate: does the pinned tool schema still agree with the declarations?

Stdlib only — no pip, no network, no dotnet.  It reads the tool files and
the pin, and answers one question per tool.

WHAT THIS TOOL IS
-----------------
  A builtin tool's parameter schema is its contract with the model: the
  JSON the model may send.  Since #1780 the schema is declared once, as
  [ToolParam] attributes on the tool class, and rendered by
  Harbor.CodeGen.ToolSchemaGenerator.  The rendered schema is hashed and
  pinned in src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json, and
  the generator refuses to compile a tool whose schema differs from its
  pin.

  So the compiler already enforces this.  This gate asks the SAME question
  in a place that needs no SDK: the generator is the enforcement, and a
  rule whose only reader is the compiler never runs on a pull request that
  does not build.  The pin is what the two share, so the two cannot
  disagree about what the schema is.

  The gate therefore proves two things:

    * the pin and the declarations still agree, and
    * a PLANTED DIVERGENCE is rejected by the same analysis — the
      self-test plants one and requires the analysis to find it.

  The analysis is shared with the migration (tools/tool_schema_model.py),
  so the gate reads the declaration exactly the way the pin was written
  from it.  If the two ever disagreed, the build would fail on the same
  disagreement.

WHY THIS FILE IS PYTHON: stdlib only, no pip, no network, and no dotnet.
  The gate has to run where the SDK and the build are not: a pull request
  that fixes the build cannot be checked by a gate that needs the build.
  It reads files, and files are all it needs.

  The failure paths are the point.  A tool that checks nothing is green
  forever, so:

    * the pin is missing          -> FAIL
    * a tool has no pin           -> FAIL
    * a pin names a tool that is
      not there                   -> FAIL
    * a schema hashes to another
      value than its pin          -> FAIL

  --selftest runs the same analysis over synthetic inputs that each break
  one of those rules, and requires the analysis to REJECT every one of
  them.  An analysis that cannot fail is not an analysis.

Exit codes: 0 the gate holds, 1 it does not.
"""
from __future__ import annotations

import argparse
import os
import sys

import tool_schema_model as model  # noqa: E402  — sibling module; sys.path[0] is this directory

PIN_RELATIVE = os.path.join(
    "src", "Harbor.Tools.Builtin", "Schemas", "tool-schemas.sha256.json"
)


def analyze(entries: list[tuple[str, dict]], pins: dict[str, str]) -> list[str]:
    """The problems with one (declarations, pin) pair.

    An empty list means the two agree: every declared tool is pinned with
    the hash its declaration renders to, and every pin names a tool that is
    there.
    """
    problems: list[str] = []

    if not pins:
        problems.append(
            f"the pin file {PIN_RELATIVE} pins no schema at all: either it is "
            f"missing, or it is empty, and a gate that reads nothing protects "
            f"nothing"
        )
        return problems

    declared = {name for name, _schema in entries}
    for name, schema in entries:
        rendered = model.digest(schema)
        if name not in pins:
            problems.append(
                f"the tool '{name}' declares a schema, but no hash is pinned "
                f"for it: its schema would change unnoticed"
            )
            continue
        if pins[name] != rendered:
            problems.append(
                f"the tool '{name}' renders as {rendered} and is pinned as "
                f"{pins[name]}: its parameter schema changed. The schema is "
                f"the tool's contract with the model — re-sign the pin "
                f"(python3 tools/check-tool-schema-pin.py --write-pin) in the "
                f"same commit, or revert the change"
            )

    for name in sorted(pins):
        if name not in declared:
            problems.append(
                f"a hash is pinned for the tool '{name}', but no tool declares "
                f"that schema: either the tool was renamed or it is gone"
            )

    return problems


def selftest() -> int:
    """The analysis must REJECT every synthetic input that breaks a rule."""
    failures: list[str] = []

    def expect_reject(what: str, entries: list[tuple[str, dict]], pins: dict[str, str]) -> None:
        if not analyze(entries, pins):
            failures.append(what)

    def expect_accept(what: str, entries: list[tuple[str, dict]], pins: dict[str, str]) -> None:
        problems = analyze(entries, pins)
        if problems:
            failures.append(f"{what} ({problems[0]})")

    schema = {"type": "object", "properties": {"path": {"type": "string"}}}
    good = model.digest(schema)

    # The honest tree: one tool, one pin, the same hash.
    expect_accept("a tool and its pin agree", [("ReadTool", schema)], {"ReadTool": good})

    # A planted divergence: the declaration moved, the pin did not.
    moved = {"type": "object", "properties": {"path": {"type": "string", "description": "moved"}}}
    expect_reject("a schema that diverged from its pin", [("ReadTool", moved)], {"ReadTool": good})

    # A tool with no pin.
    expect_reject("a tool without a pin", [("ReadTool", schema)], {})

    # A pin that names a tool that is not there.
    expect_reject("a pin without a tool", [("ReadTool", schema)], {"ReadTool": good, "GoneTool": good})

    # An empty pin: the gate that reads nothing protects nothing.
    expect_reject("an empty pin", [("ReadTool", schema)], {})

    if failures:
        for what in failures:
            print(f"selftest: the analysis accepted {what}", file=sys.stderr)
        return 1
    print("selftest: every broken input was rejected")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--min-tools", type=int, default=0, help="a floor on how many tools declare a schema")
    parser.add_argument("--write-pin", action="store_true", help="write the pin from the declarations")
    parser.add_argument("--report", action="store_true", help="print every declared schema and its hash")
    parser.add_argument("--selftest", action="store_true", help="prove the analysis rejects broken input")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    entries = model.collect()

    if args.min_tools and len(entries) < args.min_tools:
        print(
            f"::error::only {len(entries)} tools declare a schema, and the floor is "
            f"{args.min_tools}: a scan that measured less than it should cannot say "
            f"that the tree is fine",
            file=sys.stderr,
        )
        return 1

    if args.write_pin:
        model.write_pin(entries)
        print(f"wrote {len(entries)} pins to {PIN_RELATIVE}")
        return 0

    if args.report:
        for class_name, schema in entries:
            print(f"{class_name}: {model.digest(schema)}")
        return 0

    problems = analyze(entries, model.read_pins())
    for problem in problems:
        print(f"::error::{problem}", file=sys.stderr)
    if problems:
        return 1

    print(f"the pin agrees with the {len(entries)} declared tool schemas")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
