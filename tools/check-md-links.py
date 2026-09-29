#!/usr/bin/env python3
"""Check internal markdown links and anchors across the tracked .md files.

Stdlib only — no pip install, no network, no dotnet. Safe to run in CI.

What it catches:
  - a link to a path that does not exist          -> "(target)"
  - a link to a path that exists but whose GitHub anchor does not
                                               -> "path#anchor"
  - a `path/File.cs:123` reference whose file is gone (GitHub line anchor)

What it deliberately does not check:
  - external URLs (http/https/mailto) — needs network, and a flaky gate is
    worse than no gate
  - images — an <img> tag with a missing file is usually a placeholder

Fenced code blocks and inline `code` are stripped before parsing, so a
`[text](url)` inside a code sample is not reported as a broken link.

Wired by .github/workflows/docs.yml (issue #509) — that workflow exists
because ci.yml lists `**.md` and `docs/**` in `paths-ignore`, so a docs-only
PR never starts a build and therefore never reached this script. Until it was
added the checker had never run in CI.

Usage:
  ./tools/check-md-links.py                # whole repo
  ./tools/check-md-links.py --verbose      # per-file counts
  ./tools/check-md-links.py --min-files 250 --min-refs 500   # CI floors
  ./tools/check-md-links.py --self-test    # prove it still fails on a bad link

Exit codes:  0 = clean, 1 = broken links found, 2 = bad invocation.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from collections import defaultdict

import md_gate  # noqa: E402  — sibling module; sys.path[0] is this directory

# [text](target) — target is the first non-space, non-'>' run inside the parens.
INLINE_LINK = re.compile(
    r"!?\[[^\]]*\]\(\s*<?([^)>\s]+)>?(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*\)"
)
# [id]: target — reference-style link definitions.
REF_DEF = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*<?([^\s>]+)>?", re.MULTILINE)
# <a href="target"> — used by the generated HTML reports in docs/.
HTML_HREF = re.compile(r'<a\s[^>]*href="([^"]+)"', re.IGNORECASE)
FENCE = re.compile(r"^\s{0,3}(```|~~~)")
HEADING = re.compile(r"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$")
# `src/File.cs:123` — the `:123` is a GitHub line anchor, not part of the path.
LINE_ANCHOR = re.compile(r"^(.*):\d+$")
EXTERNAL = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.\-]*:")


def strip_code(text: str) -> str:
    """Blank out fenced blocks and inline code, preserving line/offset count.

    Offsets must survive because they are used to report line numbers against
    the original file. Replaced spans keep their newlines.
    """
    out: list[str] = []
    fence: str | None = None
    for line in text.splitlines(keepends=True):
        mark = FENCE.match(line)
        if mark:
            if fence is None:
                fence = mark.group(1)
            elif mark.group(1) == fence:
                fence = None
            out.append("\n" if line.endswith("\n") else "")
            continue
        out.append("" if fence else re.sub(r"`[^`\n]*`", "``", line))
    return "".join(out)


def heading_anchors(path: str) -> set[str]:
    """GitHub heading slugs for a markdown file.

    GitHub strips non-word/non-space/non-dash characters, then turns spaces
    into dashes — without trimming, so a leading emoji leaves a leading dash
    ("📊 Metrics" -> "-metrics"). Duplicate slugs get a -1, -2 suffix.
    """
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except OSError:
        return set()

    anchors: set[str] = set()
    seen: dict[str, int] = {}
    for line in text.splitlines():
        m = HEADING.match(line)
        if not m:
            continue
        title = m.group(1).strip()
        title = re.sub(r"`([^`]*)`", r"\1", title)
        title = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", title)
        title = re.sub(r"[*_~]", "", title)
        slug = re.sub(r"[^\w\- ]+", "", title, flags=re.UNICODE)
        slug = slug.replace(" ", "-").lower()
        n = seen.get(slug, 0)
        seen[slug] = n + 1
        anchors.add(slug if n == 0 else f"{slug}-{n}")
    return anchors


def resolve(base_dir: str, target: str) -> tuple[bool, str, str]:
    """Return (ok, kind, fragment) for a link target relative to base_dir."""
    path, _, frag = target.partition("#")
    if not path:
        return True, "self", frag
    if EXTERNAL.match(path):
        return True, "external", frag

    resolved = os.path.normpath(os.path.join(base_dir, path))
    if os.path.isdir(resolved):
        # A directory is a valid target; GitHub renders its README.
        return True, "dir", frag
    if os.path.exists(resolved):
        return True, "file", frag
    m = LINE_ANCHOR.match(resolved)
    if m and os.path.exists(m.group(1)):
        return True, "file-line", frag
    return False, "missing", frag


def self_test() -> int:
    """Run this gate against documents that are known-broken.

    The failure this guards against is a checker that has quietly stopped
    finding anything: it would still be green on every PR, and the broken-link
    report above would be a lie. Each case below asserts a specific exit code
    and a specific diagnostic, so "returns 0" alone is not enough to pass.
    """
    st = md_gate.SelfTest("check-md-links")

    ok_doc = (
        "# Fixture\n\n"
        "A real link to [target](target.md) and an external [one](https://example.com).\n\n"
        "```\n"
        "[not a link](gone.md)\n"
        "```\n"
    )
    target_doc = "# Target\n\n## Real Anchor\n"

    # 1. A clean fixture is clean — otherwise every other case is meaningless.
    root = md_gate.make_fixture("check-md-links.py", {"docs/ok.md": ok_doc, "docs/target.md": target_doc})
    rc, out = md_gate.run_gate(root, "check-md-links.py")
    st.expect("clean fixture exits 0", rc == 0, f"rc={rc} out={out.strip()[:400]}")
    st.expect("clean fixture reports its scan size", "across 2 markdown files" in out, out.strip()[:400])

    # 2. A link to a file that is not there must fail, and must name the target.
    broken = ok_doc + "\nBroken: [gone](nope/missing.md)\n"
    root = md_gate.make_fixture("check-md-links.py", {"docs/ok.md": broken, "docs/target.md": target_doc})
    rc, out = md_gate.run_gate(root, "check-md-links.py")
    st.expect("missing target exits 1", rc == 1, f"rc={rc} out={out.strip()[:400]}")
    st.expect("missing target is named", "nope/missing.md" in out, out.strip()[:400])
    st.expect(
        "fenced code sample is not a link", "not a link" not in out.split("MISSING TARGETS")[-1], out[-400:]
    )

    # 3. An existing file with a heading that does not exist must fail.
    bad_anchor = ok_doc + "\nBad anchor: [target](target.md#no-such-anchor)\n"
    root = md_gate.make_fixture("check-md-links.py", {"docs/ok.md": bad_anchor, "docs/target.md": target_doc})
    rc, out = md_gate.run_gate(root, "check-md-links.py")
    st.expect("missing anchor exits 1", rc == 1, f"rc={rc} out={out.strip()[:400]}")
    st.expect("missing anchor is reported as an anchor", "MISSING ANCHORS (1)" in out, out.strip()[-400:])

    # 4. The anti-vacuity floor: a real-but-tiny scan must NOT pass silently.
    #    Without this the gate could be re-pointed at one file and stay green.
    root = md_gate.make_fixture("check-md-links.py", {"docs/ok.md": ok_doc, "docs/target.md": target_doc})
    rc, out = md_gate.run_gate(root, "check-md-links.py", "--min-files", "250", "--min-refs", "500")
    st.expect("floor below the file count exits 1", rc == 1, f"rc={rc} out={out.strip()[:400]}")
    st.expect("floor breach says the scan got narrower", "floor is 250" in out, out.strip()[-400:])

    # 5. A repo with no tracked markdown at all is a blind gate, not a pass.
    root = md_gate.make_fixture("check-md-links.py", {"src/only.cs": "// no markdown here\n"})
    rc, out = md_gate.run_gate(root, "check-md-links.py", "--min-files", "250")
    st.expect("zero markdown files exits 2", rc == 2, f"rc={rc} out={out.strip()[:400]}")

    return st.finish()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--verbose", action="store_true", help="print per-file link counts")
    ap.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="fail unless at least this many markdown files were scanned (0 = off)",
    )
    ap.add_argument(
        "--min-refs",
        type=int,
        default=0,
        help="fail unless at least this many link references were examined (0 = off)",
    )
    ap.add_argument(
        "--self-test",
        action="store_true",
        help="run the gate against known-broken fixtures instead of this repo",
    )
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    listed = md_gate.tracked_md(repo)
    if not listed:
        print("no tracked markdown files found", file=sys.stderr)
        return 2

    missing: dict[str, set[tuple[int, str]]] = defaultdict(set)
    bad_anchor: dict[str, set[tuple[int, str]]] = defaultdict(set)
    total = 0
    unreadable: list[str] = []

    for rel in listed:
        full = os.path.join(repo, rel)
        try:
            with open(full, encoding="utf-8", errors="replace") as fh:
                raw = fh.read()
        except OSError:
            unreadable.append(rel)
            continue
        text = strip_code(raw)
        base = os.path.dirname(rel)

        hits = [(m.group(1), m.start()) for m in INLINE_LINK.finditer(text)]
        hits += [(m.group(1), m.start()) for m in REF_DEF.finditer(text)]
        hits += [(m.group(1), m.start()) for m in HTML_HREF.finditer(text)]
        if not hits:
            continue
        if args.verbose:
            print(f"  {rel}: {len(hits)} refs")

        for target, pos in hits:
            total += 1
            ok, kind, frag = resolve(base, target.strip())
            line = raw.count("\n", 0, pos) + 1
            if not ok:
                missing[rel].add((line, target))
            elif frag and kind in ("file", "self"):
                dest = os.path.normpath(os.path.join(base, target.split("#")[0]))
                if dest.endswith(".md") and os.path.exists(dest):
                    if frag.lower() not in heading_anchors(dest):
                        bad_anchor[rel].add((line, target))

    n_missing = sum(len(v) for v in missing.values())
    n_anchor = sum(len(v) for v in bad_anchor.values())
    print(f"checked {total} link refs across {len(listed)} markdown files")

    for title, data in (("MISSING TARGETS", missing), ("MISSING ANCHORS", bad_anchor)):
        print(f"\n=== {title} ({sum(len(v) for v in data.values())}) ===")
        for rel in sorted(data):
            print(f"\n{rel}")
            for line, target in sorted(data[rel])[:20]:
                print(f"  L{line}: {target}")

    if unreadable:
        # A file the gate could not open is a hole in the scan, never a pass.
        print(f"\n=== UNREADABLE ({len(unreadable)}) ===")
        for rel in sorted(unreadable):
            print(f"  {rel}")

    guard = md_gate.require_non_vacuous(
        "check-md-links", len(listed), total, "link refs", args.min_files, args.min_refs
    )
    if guard:
        print("\n=== VACUITY GUARD ===")
        for problem in guard:
            print(f"  {problem}")

    if guard or unreadable or n_missing or n_anchor:
        print(
            f"\nFAIL: {n_missing} missing target(s), {n_anchor} missing anchor(s), "
            f"{len(unreadable)} unreadable, {len(guard)} vacuity-guard problem(s)."
        )
        return 1
    print("\nOK: no broken internal links.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
