#!/usr/bin/env python3
"""Reflection-JSON gate for the tests/ tree (#1822).

Stdlib only — no pip, no network, no dotnet. It lives in `tools/` next to
check-test-dup.py / the check-md-links and md-lint gates and asks the question the
other gates do not:

  check-test-dup.py             is a test body a copy of another test body?
  THIS ONE                     does a test serialize through a SOURCE-GENERATED
                                context, or through the reflection resolver?

WHY THIS FILE EXISTS (issue #1822)

  The product side of System.Text.Json already rides source generation: the
  `src/` tree carries 21 `JsonSerializerContext`s (#1565, #1779). The TEST
  side did not. Measured on `origin/dev` at the time this gate was written,
  `tests/` still carried 62 `JsonSerializer.Serialize/Deserialize/
  SerializeToElement/SerializeToUtf8Bytes` calls that went through the
  REFLECTION resolver, while 22 sibling calls in the same files already rode
  `JsonlCodecContext.Default.*`, `RemoteJsonContext.Default.*` or
  `McpJsonSerializerContext.Default.*`.

  Two things are wrong with a reflection call in a test, and only the second
  is visible in a CI log:

    1. It is slow. Reflection collects (and caches) the type's metadata at
       run time, on every fixture that serializes.
    2. It is NOT CHECKED. A misspelled member name compiles, serializes to a
       DIFFERENT key, and the test then asserts on the wrong field and stays
       green. A source-generated context names the type and its members, so
       the compiler sees them.

  The second one is the reason this is a gate and not a cleanup: without it,
  the next test that reaches for `JsonSerializer.Serialize(payload)` puts the
  reflection path back, and nothing in the repository notices.

WHY THE GATE IS IN `tools/` AND NOT IN `tests/Harbor.Architecture.Tests/`

  The architecture suite is the zone of #1724 and it judges the COMPILED
  assemblies. This rule judges the SOURCE TEXT of the test tree, and it is
  the same shape as `check-test-dup.py` (#1778): a stdlib script that reads
  the tree and answers one question. #1822 says so explicitly.

THE RULE

  A `JsonSerializer.Serialize` / `Deserialize` / `SerializeToElement` /
  `SerializeToUtf8Bytes` call under `tests/` must name a source-generated
  type info. The scan accepts a call when its ARGUMENTS mention one of:

    <Something>Context.Default        the generated JsonTypeInfo property
    <Something>Context.Options        the context's own JsonSerializerOptions
    <Something>Context.JsonOptions    a context-backed options object
    GetTypeInfo(                      a lookup through a context

  A call that mentions none of those takes the reflection resolver, and the
  gate fails.

  `tests/Harbor.Architecture.Tests/` is excluded: the arch-guard tree is
  #1724's zone, and it is not a test tree in the sense this rule means.

THE EXCEPTIONS FILE

  `tools/test-json-reflection-exceptions.txt` carries the calls that cannot
  be moved yet, one per line:

    <path relative to the repo root> | <text that must appear in the call> | <reason>

  It is a RATCHET, not a target: an exception the tree no longer produces is
  a failure, exactly like `tools/test-project-baseline.txt`. A gate whose
  exception list only grows is a gate that stops being a gate.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS_DIR = os.path.join(REPO_ROOT, "tests")
EXCEPTIONS_FILE = os.path.join(REPO_ROOT, "tools", "test-json-reflection-exceptions.txt")

# The arch-guard tree belongs to #1724; this rule is about the test tree.
EXCLUDED_DIRS = ("Harbor.Architecture.Tests",)
SKIP_DIR_PARTS = ("obj", "bin")

CALL_RE = re.compile(
    r"JsonSerializer\.(Serialize|Deserialize|SerializeToElement|SerializeToUtf8Bytes)\s*\("
)

# A call names a source-generated type info when its arguments mention one.
SOURCE_GEN_MARKERS = (
    re.compile(r"\b[A-Za-z0-9_]*Context\.Default\b"),
    re.compile(r"\b[A-Za-z0-9_]*Context\.Options\b"),
    re.compile(r"\b[A-Za-z0-9_]*Context\.JsonOptions\b"),
    re.compile(r"\bGetTypeInfo\s*\("),
)

# A call that names NO arguments at all, or only a JsonSerializerOptions, is
# the reflection shape. Kept for the report so the message can say which.
OPTIONS_RE = re.compile(r"\bJsonSerializerOptions\b|\b[A-Za-z0-9_]*Options\b")


def _hole_span(src: str, start: int, dollars: int) -> int:
    """Return the index just past an interpolation hole that starts at `start`.

    The hole's content is CODE: it may hold braces, and it may hold nested
    string literals. The hole closes with `dollars` braces at brace depth 0.
    """
    n = len(src)
    depth = 0
    i = start
    while i < n:
        c = src[i]
        if c == '"' or c == "'":
            i = _skip_literal(src, i)
            continue
        if c == "{":
            depth += 1
            i += 1
            continue
        if c == "}":
            if depth == 0:
                if dollars == 1:
                    return i + 1
                run = 0
                j = i
                while j < n and src[j] == "}" and run < dollars:
                    run += 1
                    j += 1
                if run == dollars:
                    return j
                i = j
                continue
            depth -= 1
            i += 1
            continue
        i += 1
    return n


def _dollars_before(src: str, index: int) -> int:
    """How many `$` signs precede the literal that starts at `index`?"""
    dollars = 0
    back = index - 1
    while back >= 0 and src[back] == "$":
        dollars += 1
        back -= 1
    return dollars


def _skip_literal(src: str, i: int) -> int:
    """Return the index just past the string or char literal that starts at `i`.

    Handles the four shapes C# has: a regular literal, a verbatim literal, a
    raw literal, and a char literal. An interpolated literal is skipped HOLE
    BY HOLE, because the hole's content is code and may hold a nested string.
    """
    n = len(src)
    if src[i] == "'":
        j = i + 1
        while j < n:
            if src[j] == "\\":
                j += 2
                continue
            if src[j] == "'":
                return j + 1
            j += 1
        return n
    dollars = _dollars_before(src, i)
    if src.startswith('"""', i):
        j = i + 3
        while j < n:
            if src.startswith('"""', j):
                return j + 3
            if dollars >= 2 and src.startswith("{{", j):
                j = _hole_span(src, j + 2, dollars)
                continue
            if dollars == 1 and src[j] == "{" and src[j + 1 : j + 2] != "{":
                j = _hole_span(src, j + 1, 1)
                continue
            j += 1
        return n
    j = i + 1
    while j < n:
        c = src[j]
        if c == "\\":
            j += 2
            continue
        if dollars >= 1 and c == "{":
            if dollars == 1 and src[j + 1 : j + 2] == "{":
                j += 2
                continue
            j = _hole_span(src, j + 1, dollars)
            continue
        if c == '"':
            return j + 1
        j += 1
    return n


def _is_code(src: str, index: int) -> bool:
    """Is the character at `index` code rather than comment or string text?

    Walks the source from the start, skipping comments and literals. The
    content of an interpolation hole is CODE, so the walk treats a hole as
    code rather than as text.
    """
    i = 0
    n = len(src)
    while i < n:
        if i == index:
            return True
        c = src[i]
        if c == "/" and src[i + 1 : i + 2] == "/":
            j = src.find("\n", i)
            j = n if j < 0 else j
            if j > index:
                return False
            i = j + 1
            continue
        if c == "/" and src[i + 1 : i + 2] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            if j > index:
                return False
            i = j
            continue
        if c == '"' or c == "'":
            end = _skip_literal(src, i)
            if c == '"' and _inside_hole(src, i, end, index):
                return True
            if end > index:
                return False
            i = end
            continue
        i += 1
    return False


def _inside_hole(src: str, start: int, end: int, index: int) -> bool:
    """Is `index` inside an interpolation hole of the literal at `start`?"""
    dollars = _dollars_before(src, start)
    if dollars == 0:
        return False
    i = start
    while i < end:
        if src.startswith('"""', start):
            if dollars >= 2 and src.startswith("{{", i):
                span = _hole_span(src, i + 2, dollars)
                if i + 2 <= index < span - dollars:
                    return True
                i = span
                continue
            if dollars == 1 and src[i] == "{" and src[i + 1 : i + 2] != "{":
                span = _hole_span(src, i + 1, 1)
                if i + 1 <= index < span - 1:
                    return True
                i = span
                continue
            i += 1
            continue
        if src[i] == "{":
            span = _hole_span(src, i + 1, dollars)
            if i + 1 <= index < span - dollars:
                return True
            i = span
            continue
        i += 1
    return False


def extract_call(src: str, open_paren_index: int) -> tuple[str, int]:
    """Return the text of a call's argument list and the index after its ')'.

    The argument list may span lines, so the scan works on the whole file
    rather than line by line.
    """
    depth = 1
    i = open_paren_index + 1
    start = i
    n = len(src)
    while i < n:
        c = src[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return src[start:i], i + 1
        elif c == '"' or c == "'":
            i = _skip_literal(src, i)
            continue
        i += 1
    return src[start:], n


def line_of(src: str, index: int) -> int:
    return src.count("\n", 0, index) + 1


def iter_source_files(root: str):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [
            d for d in dirnames if d not in SKIP_DIR_PARTS and d not in EXCLUDED_DIRS
        ]
        for name in sorted(filenames):
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def scan(root: str) -> list[dict]:
    """Return every JsonSerializer call under `root` and whether it is
    source-generated."""
    findings = []
    for path in iter_source_files(root):
        with open(path, encoding="utf-8") as handle:
            raw = handle.read()
        for match in CALL_RE.finditer(raw):
            if not _is_code(raw, match.start()):
                continue
            args, _ = extract_call(raw, match.end() - 1)
            source_generated = any(marker.search(args) for marker in SOURCE_GEN_MARKERS)
            findings.append(
                {
                    "path": os.path.relpath(path, REPO_ROOT).replace(os.sep, "/"),
                    "line": line_of(raw, match.start()),
                    "method": match.group(1),
                    "args": " ".join(args.split()),
                    "source_generated": source_generated,
                    "call": "JsonSerializer.%s(%s)" % (match.group(1), " ".join(args.split())),
                }
            )
    return findings


def load_exceptions(path: str) -> list[dict]:
    exceptions = []
    if not os.path.exists(path):
        return exceptions
    with open(path, encoding="utf-8") as handle:
        for number, line in enumerate(handle, 1):
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            parts = [part.strip() for part in text.split("|")]
            if len(parts) != 3:
                raise SystemExit(
                    "%s:%d: an exception needs three fields "
                    "(path | text-in-the-call | reason), got %d" % (path, number, len(parts))
                )
            exceptions.append(
                {"path": parts[0], "needle": parts[1], "reason": parts[2], "line": number}
            )
    return exceptions


def match_exception(finding: dict, exceptions: list[dict]) -> dict | None:
    for exception in exceptions:
        if exception["path"] != finding["path"]:
            continue
        if exception["needle"] in finding["call"]:
            return exception
    return None


def report(findings: list[dict], exceptions: list[dict], as_json: bool) -> int:
    violations = []
    used = set()
    for finding in findings:
        if finding["source_generated"]:
            continue
        exception = match_exception(finding, exceptions)
        if exception is not None:
            used.add(exception["line"])
            continue
        violations.append(finding)

    if as_json:
        import json

        print(
            json.dumps(
                {
                    "calls": len(findings),
                    "source_generated": sum(1 for f in findings if f["source_generated"]),
                    "reflection": sum(1 for f in findings if not f["source_generated"]),
                    "violations": [
                        {
                            "path": v["path"],
                            "line": v["line"],
                            "call": v["call"],
                        }
                        for v in violations
                    ],
                },
                indent=2,
            )
        )
        return 1 if violations else 0

    print("reflection-JSON census (tests/, excluding tests/Harbor.Architecture.Tests)")
    print("  JsonSerializer calls      : %d" % len(findings))
    print("  source-generated          : %d" % sum(1 for f in findings if f["source_generated"]))
    print("  reflection (grandfathered): %d" % (len(findings) - sum(1 for f in findings if f["source_generated"]) - len(violations)))
    print("  reflection (VIOLATION)    : %d" % len(violations))
    for violation in violations:
        print("    %s:%d  %s" % (violation["path"], violation["line"], violation["call"][:110]))
    stale = [e for e in exceptions if e["line"] not in used]
    for exception in stale:
        print(
            "  STALE EXCEPTION %s:%d — %s"
            % (exception["path"], exception["line"], exception["reason"])
        )
    return 1 if violations or stale else 0


def self_test() -> int:
    """Prove the gate still fails on a planted reflection call."""
    with tempfile.TemporaryDirectory() as tmp:
        tests = os.path.join(tmp, "tests")
        os.makedirs(os.path.join(tests, "Harbor.Sample.Tests"))
        with open(
            os.path.join(tests, "Harbor.Sample.Tests", "Sample.cs"), "w", encoding="utf-8"
        ) as handle:
            handle.write(
                "namespace Harbor.Sample.Tests;\n"
                "public class Sample\n"
                "{\n"
                "    // JsonSerializer.Serialize(payload) in a comment is not a call.\n"
                "    public string Reflect() => JsonSerializer.Serialize(payload);\n"
                "}\n"
            )
        with open(
            os.path.join(tests, "Harbor.Sample.Tests", "Hole.cs"), "w", encoding="utf-8"
        ) as handle:
            # The call lives INSIDE the interpolation hole of a raw
            # interpolated string. A scan that blanks the literal whole
            # would miss it — which is exactly the bug this fixture exists
            # for: the mock's own hot path builds its JSON this way.
            handle.write(
                "namespace Harbor.Sample.Tests;\n"
                "public class Hole\n"
                "{\n"
                "    public string Reflect(string path) =>\n"
                '        $"""{{\"path\":{{JsonSerializer.Serialize(path)}}}}""";\n'
                "}\n"
            )
        with open(
            os.path.join(tests, "Harbor.Sample.Tests", "Good.cs"), "w", encoding="utf-8"
        ) as handle:
            handle.write(
                "namespace Harbor.Sample.Tests;\n"
                "public class Good\n"
                "{\n"
                "    public string Ok() => JsonSerializer.Serialize(payload, SampleContext.Default.Payload);\n"
                "}\n"
            )
        # The arch-guard tree is excluded from the rule.
        os.makedirs(os.path.join(tests, "Harbor.Architecture.Tests"))
        with open(
            os.path.join(tests, "Harbor.Architecture.Tests", "Arch.cs"), "w", encoding="utf-8"
        ) as handle:
            handle.write(
                "namespace Harbor.Architecture.Tests;\n"
                "public class Arch\n"
                "{\n"
                "    public string Reflect() => JsonSerializer.Serialize(payload);\n"
                "}\n"
            )

        global REPO_ROOT, EXCEPTIONS_FILE
        saved_root, saved_exceptions = REPO_ROOT, EXCEPTIONS_FILE
        REPO_ROOT = tmp
        EXCEPTIONS_FILE = os.path.join(tmp, "exceptions.txt")
        try:
            findings = scan(os.path.join(tmp, "tests"))
            violations = [f for f in findings if not f["source_generated"]]
            if len(findings) != 3:
                print("self-test FAILED: expected 3 calls, saw %d" % len(findings))
                return 1
            if len(violations) != 2:
                print(
                    "self-test FAILED: expected exactly 2 reflection calls, saw %d"
                    % len(violations)
                )
                return 1
            names = sorted(v["path"].rsplit("/", 1)[-1] for v in violations)
            if names != ["Hole.cs", "Sample.cs"]:
                print("self-test FAILED: the wrong calls were flagged: %s" % names)
                return 1
        finally:
            REPO_ROOT, EXCEPTIONS_FILE = saved_root, saved_exceptions

    print("self-test ok: the gate still fails on a planted reflection call.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Fail when a test serializes through the reflection resolver."
    )
    parser.add_argument(
        "--root",
        default=TESTS_DIR,
        help="the tests tree to scan (default: the repository's tests/)",
    )
    parser.add_argument(
        "--exceptions",
        default=EXCEPTIONS_FILE,
        help="the exceptions file (default: tools/test-json-reflection-exceptions.txt)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the census and exit 0 even when there are violations",
    )
    parser.add_argument(
        "--json-out",
        action="store_true",
        help="print the census as JSON",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="prove the gate still fails on a planted reflection call",
    )
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = os.path.abspath(args.root)
    if not os.path.isdir(root):
        print("no tests tree at %s" % root, file=sys.stderr)
        return 2

    findings = scan(root)
    exceptions = load_exceptions(args.exceptions)

    if args.report or args.json_out:
        return report(findings, exceptions, args.json_out)

    violations = []
    for finding in findings:
        if finding["source_generated"]:
            continue
        if match_exception(finding, exceptions) is not None:
            continue
        violations.append(finding)

    used = {
        e["line"]
        for e in exceptions
        for f in findings
        if not f["source_generated"] and match_exception(f, exceptions) is not None
    }
    stale = [e for e in exceptions if e["line"] not in used]

    if violations:
        print(
            "reflection JSON in tests/: %d call(s) do not name a source-generated "
            "type info." % len(violations),
            file=sys.stderr,
        )
        for violation in violations:
            print(
                "  %s:%d  %s" % (violation["path"], violation["line"], violation["call"][:110]),
                file=sys.stderr,
            )
        print(
            "\nPass the type info from a source-generated context "
            "(JsonSerializer.Serialize(value, SomeContext.Default.SomeType)), or add a "
            "documented exception to tools/test-json-reflection-exceptions.txt.",
            file=sys.stderr,
        )
    if stale:
        print(
            "reflection JSON in tests/: %d exception(s) no longer apply — delete them."
            % len(stale),
            file=sys.stderr,
        )
        for exception in stale:
            print(
                "  %s (tools/test-json-reflection-exceptions.txt:%d)"
                % (exception["path"], exception["line"]),
                file=sys.stderr,
            )

    return 1 if violations or stale else 0


if __name__ == "__main__":
    sys.exit(main())
