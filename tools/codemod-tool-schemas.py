#!/usr/bin/env python3
"""The codemod that turned 21 hand-written JSON parameter schemas into
declarative [ToolParam] attributes, and the writer of the pin they are
pinned with (issue #1780).

WHAT THIS TOOL IS
------------------
  Before #1780 every builtin tool carried its own copy of a JSON Schema
  literal: 21 files, 281 lines of hand-written JSON, and a second copy of
  the same 21 documents in the test project's golden file.  The schema is
  the tool's contract with the model, so it may not drift by a byte, and
  two copies of it is exactly how it drifted.

  The schema now lives ONCE, as a typed declaration: a [ToolSchema] marker
  and one [ToolParam] per argument on the tool class.  The source generator
  Harbor.CodeGen.ToolSchemaGenerator renders that declaration into the
  canonical JSON Schema and emits the tool's ParameterSchema.

  This script is the migration (--migrate) and the pin writer (--write-pin).
  The pin is the SHA-256 of the canonical schema of every tool; the
  generator refuses to compile when a schema differs from its pin, so a
  change to a tool's arguments is a build failure until the pin is
  re-signed in the same commit.

  The gate that says the pin still agrees with the declarations is
  tools/check-tool-schema-pin.py; the reading of the declaration is
  tools/tool_schema_model.py, which both use.

WHY PYTHON: stdlib only, no pip, no network, no dotnet.  It rewrites
  source files and hashes JSON; the SDK is not needed and must not be
  needed, because the gate that reads what this tool writes is the
  compiler, and a control that needs the compiler cannot prove the
  compiler is broken.

Exit codes: 0 ok, 1 the check failed.
"""
from __future__ import annotations

import argparse
import os
import sys

import tool_schema_model as model  # noqa: E402  — sibling module; sys.path[0] is this directory


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--migrate",
        action="store_true",
        help="rewrite the legacy JSON literals into [ToolParam] attributes",
    )
    parser.add_argument(
        "--write-pin",
        action="store_true",
        help="write the pin file from the declared schemas",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print every declared schema and its hash",
    )
    args = parser.parse_args(argv)

    if args.migrate:
        migrated = 0
        for path in model.tool_files():
            if model.migrate_file(path) is not None:
                migrated += 1
        print(f"migrated {migrated} tool files")

    entries = model.collect()
    if not entries:
        print("no tool declares a schema", file=sys.stderr)
        return 1

    if args.write_pin:
        model.write_pin(entries)
        print(f"wrote {len(entries)} pins to {os.path.relpath(model.PIN_PATH, model.REPO_ROOT)}")

    if args.report:
        for class_name, schema in entries:
            print(f"{class_name}: {model.digest(schema)}")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
