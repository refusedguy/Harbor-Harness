#!/usr/bin/env python3
"""Lint the tracked .md files against the markdown invariants Harbor claims.

Stdlib only — no pip install, no network, no dotnet. Sibling of
check-md-links.py, and wired by the same workflow
(.github/workflows/docs.yml, issue #509). The two gates must not overlap:
check-md-links.py answers "does this link resolve?", this one answers "is
this file shaped the way the repo says it is?".

RULES (all currently green on the 310 tracked .md files)

  ENC-BOM        file starts with a UTF-8 BOM
  ENC-CRLF       file contains CR LF
  MD010          hard tab in indentation
  MD001          heading level jumps by more than one (h2 -> h4)
  MD003          setext heading (a `===` underline) instead of an ATX `#`
  MD018          no space after the `#`s of an ATX heading
  MD019          more than one space after the `#`s of an ATX heading
  MD047          file does not end with exactly one newline
  MD048          `~~~` fence instead of a ``` fence
  FENCE-UNCLOSED a fence that never closes — the lines after it are invisible
                 to this linter, so it is a blind spot, not a style nit

  The MD0xx codes are markdownlint's for the rules whose semantics match
  exactly; this is a deliberately small subset, not a markdownlint
  implementation. A code not listed above is not checked.

  KNOWN LIMITATION — no block-structure parser. An ATX heading is recognised
  only at column 0. A `   #foo` line indented 1-3 spaces is a heading in
  CommonMark, but in this repo every such line is the continuation of a list
  item (a `1. `/`- ` item indents its content by 3 or 2 columns), and
  reporting those as headings is a false-positive generator. So "indented
  heading" (MD023) is deliberately NOT checked, and MD001/MD018/MD019 skip
  indented lines rather than mis-read them.

RULES DELIBERATELY NOT ENFORCED (each is a real markdownlint rule this repo's
existing documents would fail, so a gate on it would be noise rather than
signal — measured, not assumed)

  MD009   trailing whitespace — 178 lines end in the two-space hard break,
          which this repo uses deliberately (.editorconfig's trim fights it).
  MD012   multiple consecutive blank lines — 1333 hits in 171 files.
  MD022 / MD031 / MD032  blank lines around headings / fences / lists —
          618 / 320 / 1709 hits.
  MD007   list indent width — 615 hits in 44 files (the repo uses 2 and 4).
  MD040   fence info string — 190 fences without a language, mostly shell and
          ASCII diagrams; retro-fitting a language would be cosmetic churn.
  MD041   first line must be an H1 — 23 files, almost all archived
          docs/.kilo-docs/ prompts that intentionally start with prose.
  MD025   exactly one H1 — docs/adr/DECISIONS.md holds 9 ADRs as 9 H1s, which
          is the correct shape for an ADR log, not a violation.
  MD004   dash-only bullets — 9 archived files mix `+` with `-`; rewriting
          archived sprint notes for zero reader value is not worth the diff.
  MD042   empty links — zero real occurrences; the naive `[]` regex matches
          `char[][]` in prose and would have been a false-positive generator.
  MD049 / MD050  emphasis style — the only underscore "hits" are identifiers
          like `NetArch_<Project>_DoesNotDependOn_<Layer>`.
  MD046   fenced over indented code — 3 hits, all inside a nested list where
          the indentation is structural.

  There is no exclusion list, and that is on purpose. A linter that skips 100
  files and reports "checked 210" is the same lie as a gate that scans
  nothing: the skipped set is invisible at review time. Every tracked file is
  linted or the run fails.

Usage:
  ./tools/md-lint.py                        # whole repo
  ./tools/md-lint.py --min-files 250 --min-lines 100000
  ./tools/md-lint.py --self-test            # prove it still fails on bad input

Exit codes:  0 = clean, 1 = violations found, 2 = bad invocation / no input.
"""

from __future__ import annotations

import argparse
import os
import re
import sys

import md_gate  # noqa: E402  — sibling module; sys.path[0] is this directory

BOM = b"\xef\xbb\xbf"
# Opening/closing fence: a run of 3+ backticks or 3+ tildes at line start.
FENCE_RUN = re.compile(r"^\s{0,3}(`{3,}|~{3,})(.*)$")
# ATX heading: 1-6 hashes at column 0. No leading spaces — see KNOWN LIMITATION.
ATX = re.compile(r"^(#{1,6})(.*)$")

RULE_TEXT = {
    "ENC-BOM": "file starts with a UTF-8 BOM",
    "ENC-CRLF": "file contains CR LF (repo is LF-only)",
    "MD010": "hard tab in indentation",
    "MD001": "heading level jumps by more than one",
    "MD003": "setext heading (`===` underline) instead of an ATX `#`",
    "MD018": "no space after the `#`s of an ATX heading",
    "MD019": "more than one space after the `#`s of an ATX heading",
    "MD047": "file must end with exactly one newline",
    "MD048": "use a ``` fence, not ~~~",
    "FENCE-UNCLOSED": "code fence is never closed — everything after it escapes this linter",
}


def lint_bytes(data: bytes) -> list[tuple[int, str, str]]:
    """Return (line, code, message) for one document. `line` is 1-based."""
    out: list[tuple[int, str, str]] = []

    if data.startswith(BOM):
        out.append((1, "ENC-BOM", RULE_TEXT["ENC-BOM"]))
    if b"\r\n" in data:
        out.append((data[: data.index(b"\r\n")].count(b"\n") + 1, "ENC-CRLF", RULE_TEXT["ENC-CRLF"]))

    if data == b"":
        return [(1, "MD047", "empty file — delete it or give it a heading")]

    if not data.endswith(b"\n"):
        out.append((data.count(b"\n") + 1, "MD047", RULE_TEXT["MD047"] + " (no newline at EOF)"))
    elif data.endswith(b"\n\n"):
        out.append((data.count(b"\n"), "MD047", RULE_TEXT["MD047"] + " (blank line at EOF)"))

    lines = data.decode("utf-8", errors="replace").split("\n")
    if lines and lines[-1] == "":
        lines.pop()  # the split artifact at EOF, not a real line

    fence_char: str | None = None
    fence_len = 0
    fence_line = 0
    last_level: int | None = None

    for idx, raw in enumerate(lines, start=1):
        fence = FENCE_RUN.match(raw)

        if fence_char is not None:
            # A closing fence is the same character, at least as long, and
            # carries nothing else. That is what lets a ``` block contain a
            # ```` block.
            if (
                fence
                and fence.group(1)[0] == fence_char
                and len(fence.group(1)) >= fence_len
                and not fence.group(2).strip()
            ):
                fence_char = None
            continue

        if fence:
            fence_char = fence.group(1)[0]
            fence_len = len(fence.group(1))
            fence_line = idx
            if fence_char == "~":
                out.append((idx, "MD048", RULE_TEXT["MD048"]))
            continue

        if "\t" in raw:
            out.append((idx, "MD010", RULE_TEXT["MD010"]))

        # Setext, unambiguous form only: `---` is also a thematic break and a
        # front-matter delimiter, so underlining a heading with dashes is not
        # checked (documented limitation, zero real occurrences today).
        if raw.strip() and set(raw.strip()) == {"="}:
            if idx > 1 and lines[idx - 2].strip():
                out.append((idx, "MD003", RULE_TEXT["MD003"]))
            continue

        atx = ATX.match(raw)
        if not atx:
            continue

        hashes, rest = atx.group(1), atx.group(2)
        if rest == "" or rest[0] not in " \t":
            out.append((idx, "MD018", RULE_TEXT["MD018"]))
        elif rest.startswith("  "):
            out.append((idx, "MD019", RULE_TEXT["MD019"]))

        level = len(hashes)
        if last_level is not None and level > last_level + 1:
            out.append((idx, "MD001", f"{RULE_TEXT['MD001']} (h{last_level} -> h{level})"))
        last_level = level

    if fence_char is not None:
        out.append((fence_line, "FENCE-UNCLOSED", RULE_TEXT["FENCE-UNCLOSED"]))

    return out


def self_test() -> int:
    """Run this gate against documents that are known-broken.

    Same contract as check-md-links.py's self-test: for each fixture we assert
    the exit code AND that the specific rule code appears, so a linter that
    degraded into "prints nothing, exits 0" cannot pass.
    """
    st = md_gate.SelfTest("md-lint")
    clean = "# Title\n\nBody with no tabs.\n\n```bash\ndotnet build\n```\n"

    def case(name: str, doc: str, expect_code: str | None) -> None:
        root = md_gate.make_fixture("md-lint.py", {"docs/x.md": doc})
        rc, out = md_gate.run_gate(root, "md-lint.py")
        if expect_code is None:
            st.expect(f"{name}: exits 0", rc == 0, f"rc={rc} out={out.strip()[-300:]}")
            return
        st.expect(f"{name}: exits 1", rc == 1, f"rc={rc} out={out.strip()[-300:]}")
        st.expect(f"{name}: reports {expect_code}", expect_code in out, out.strip()[-300:])

    case("clean document", clean, None)
    case("heading level jump", "# T\n\n#### Deep\n", "MD001")
    case("hard tab", "# T\n\n\tindented with a tab\n", "MD010")
    case("no newline at EOF", "# T\n\nbody", "MD047")
    case("blank line at EOF", "# T\n\nbody\n\n", "MD047")
    case("tilde fence", "# T\n\n~~~\ncode\n~~~\n", "MD048")
    case("no space after hash", "#T\n", "MD018")
    case("two spaces after hash", "#  T\n", "MD019")
    case("setext heading", "Title\n=====\n", "MD003")
    case("unclosed fence", "# T\n\n```bash\ndotnet build\n", "FENCE-UNCLOSED")
    case("empty file", "", "MD047")

    # An indented `#` is list content in every real case in this repo; it must
    # not be reported as a heading (false positives are how a lint dies).
    root = md_gate.make_fixture("md-lint.py", {"docs/list.md": "1. Do a thing\n   #249 is a PR\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py")
    st.expect("indented hash in a list item is not a heading", rc == 0, f"rc={rc} out={out.strip()[-300:]}")

    # Encoding rules need byte-level fixtures, so they bypass `case`.
    root = md_gate.make_fixture("md-lint.py", {"docs/bom.md": BOM + b"# T\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py")
    st.expect("BOM exits 1", rc == 1, f"rc={rc} out={out.strip()[-300:]}")
    st.expect("BOM reports ENC-BOM", "ENC-BOM" in out, out.strip()[-300:])

    root = md_gate.make_fixture("md-lint.py", {"docs/crlf.md": b"# T\r\n\r\nbody\r\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py")
    st.expect("CRLF exits 1", rc == 1, f"rc={rc} out={out.strip()[-300:]}")
    st.expect("CRLF reports ENC-CRLF", "ENC-CRLF" in out, out.strip()[-300:])

    # A fence must not be able to hide the rest of the file from the linter...
    root = md_gate.make_fixture("md-lint.py", {"docs/hide.md": "# T\n\n```\n#### buried jump\n```\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py")
    st.expect("fenced content is not linted as headings", rc == 0, f"rc={rc} out={out.strip()[-300:]}")

    # ...and an unclosed one must not be able to hide the whole document.
    root = md_gate.make_fixture("md-lint.py", {"docs/hide.md": "# T\n\n```\n#### buried jump\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py")
    st.expect("fenced-away heading is reported via the unclosed fence", "FENCE-UNCLOSED" in out, out.strip()[-300:])

    # Anti-vacuity: a real-but-tiny scan must not pass silently.
    root = md_gate.make_fixture("md-lint.py", {"docs/x.md": clean})
    rc, out = md_gate.run_gate(root, "md-lint.py", "--min-files", "250")
    st.expect("file floor below the count exits 1", rc == 1, f"rc={rc} out={out.strip()[-300:]}")
    st.expect("file floor breach says the scan got narrower", "floor is 250" in out, out.strip()[-300:])

    rc, out = md_gate.run_gate(root, "md-lint.py", "--min-lines", "100000")
    st.expect("line floor breach exits 1", rc == 1, f"rc={rc} out={out.strip()[-300:]}")
    st.expect("line floor breach says the scan got narrower", "floor is 100000" in out, out.strip()[-300:])

    # No tracked markdown at all is a blind gate, not a pass.
    root = md_gate.make_fixture("md-lint.py", {"src/only.cs": "// no markdown here\n"})
    rc, out = md_gate.run_gate(root, "md-lint.py", "--min-files", "250")
    st.expect("zero markdown files exits 2", rc == 2, f"rc={rc} out={out.strip()[-300:]}")

    return st.finish()


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument(
        "--min-files", type=int, default=0, help="fail unless at least this many files were linted (0 = off)"
    )
    ap.add_argument(
        "--min-lines", type=int, default=0, help="fail unless at least this many lines were read (0 = off)"
    )
    ap.add_argument("--self-test", action="store_true", help="run the gate against known-broken fixtures")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    listed = md_gate.tracked_md(repo)
    if not listed:
        print("no tracked markdown files found", file=sys.stderr)
        return 2

    findings: dict[str, list[tuple[int, str, str]]] = {}
    lines_read = 0
    unreadable: list[str] = []

    for rel in listed:
        try:
            with open(os.path.join(repo, rel), "rb") as fh:
                data = fh.read()
        except OSError:
            unreadable.append(rel)
            continue
        lines_read += data.count(b"\n")
        problems = lint_bytes(data)
        if problems:
            findings[rel] = problems

    n = sum(len(v) for v in findings.values())
    print(f"linted {lines_read} lines across {len(listed)} markdown files: {n} violation(s)")

    for rel in sorted(findings):
        print(f"\n{rel}")
        for line, code, message in findings[rel]:
            print(f"  L{line}: {code} {message}")

    if unreadable:
        print(f"\n=== UNREADABLE ({len(unreadable)}) ===")
        for rel in sorted(unreadable):
            print(f"  {rel}")

    guard = md_gate.require_non_vacuous(
        "md-lint", len(listed) - len(unreadable), lines_read, "lines", args.min_files, args.min_lines
    )
    if guard:
        print("\n=== VACUITY GUARD ===")
        for problem in guard:
            print(f"  {problem}")

    if guard or unreadable or n:
        print(f"\nFAIL: {n} violation(s), {len(unreadable)} unreadable, {len(guard)} vacuity-guard problem(s).")
        return 1
    print("\nOK: no markdown violations.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
