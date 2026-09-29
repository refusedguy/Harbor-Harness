#!/usr/bin/env python3
"""Check that the repo's *normative* markdown still cites code that exists.

Stdlib only — no pip install, no network, no dotnet. Third sibling of
check-md-links.py and md-lint.py, wired by the same workflow
(.github/workflows/docs.yml, issue #509). The three gates must not overlap:

  check-md-links.py  does the link resolve?
  md-lint.py         is the file shaped the way the repo says it is?
  THIS ONE           does the `file:line` it asserts still point at a line?

WHY THIS FILE EXISTS (issue #664)

  docs/EVENT_TOPOLOGY.md cited `AgentLoop.cs:157,173,282,316,345,352,363,455,
  526,549,575`. AgentLoop.cs is 249 lines. Nine of those eleven line numbers
  were past EOF, and none of the two that were in range held a
  `PublishAsync` call. The document was "normative for the current
  implementation" and had not been true for a long time; ci.yml ignores
  `**.md`/`docs/**`, and the two existing docs gates check form, not
  content, so a file:line fence pointed at nothing read as a passing gate.

  The same document also named `EventBusAppStoreDispatcher` as a live
  subscriber, which is a third failure mode the line fence cannot see — the
  line exists, the file exists, the type exists, and nothing constructs it.
  So the fence rules below are necessary but NOT sufficient; rule
  DOC-TYPE-UNWIRED exists to close that half.

RULES

  DOC-CITE-MISSING   `path/File.cs:12` names a file that is not in the tree.
                     A bare `File.cs:12` must resolve to exactly one tracked
                     `.cs` outside contrib/ — prose legitimately abbreviates
                     after a full path was given, and an ambiguous basename
                     is treated as a miss rather than guessed.
  DOC-CITE-EOF       the cited line, or the end of a `a-b` range, is past
                     the last line of the file. This is the rule that catches
                     the AgentLoop case: the file is alive, the number is not.
  DOC-TYPE-UNWIRED   a backticked PascalCase name that IS declared as a type
                     in `src/` or `apps/`, but is referenced by no other
                     production file. A normative document that names it is
                     sending a reader down a branch nothing builds. This is the
                     half the line fence cannot see: the file exists, the line
                     exists, the type exists — nothing constructs it (#664).

  The escape hatch is a line IN THE SAME DOCUMENT, and it must carry a reason:

      <!-- check-doc-cites: allow-unwired TypeFilterMiddleware — <why> -->

  Per-document and reason-mandatory on purpose. A global allow-list in this
  script would be the "allow-list that verified zero files" failure the repo
  has already paid for; a per-document line shows up in the diff next to the
  sentence that needed it, so adding one is a reviewable act rather than an
  edit to a file nobody reads.

SCOPE — WHY THIS IS NOT A GLOBAL SCAN

  Only documents carrying the marker line `Status: normative` are checked.

  That is a pre-existing convention in this repo (EVENT_TOPOLOGY.md and
  EVENT_BUS_SINKS.md are the two that declare it today), and it is a
  self-selector rather than an allow-list: a new normative document is
  covered by writing the marker, and nobody has to remember to edit a list
  here. The alternative — scanning all 310 tracked .md files — is not
  available at this strength: measured over the whole tree, the two fence
  rules find 141 pre-existing violations in 23 files (docs/STATE_OWNERSHIP.md
  alone has 20), most of them in archived material under docs/.kilo-docs/.
  A gate red on 141 unrelated findings on day one is a gate that gets
  switched off, which is the exact failure md_gate.require_non_vacuous
  exists to prevent.

  Deleting the marker from a document is therefore not a bypass: it shrinks
  the scanned set, and the `--min-files` / `--min-cites` floors in
  docs.yml turn that into a red build.

KNOWN LIMITATIONS — READ THIS BEFORE TRUSTING A GREEN

  A fence proves a line exists. It does NOT prove the line is the right one.
  `EventBroadcaster.cs:124` in EVENT_BUS_SINKS.md is a real line of a real
  file and is the wrong line; nothing here can tell the difference, because
  the document does not state what it expects to find. Only DOC-TYPE-UNWIRED
  reaches past the fence, and only for types.

  And DOC-TYPE-UNWIRED has a matching blind spot: a name referenced only from
  another file's XML doc comment would count as wired. Comments are stripped
  before counting, so that specific case is handled — but a type referenced
  from a real (yet never-constructed) helper is still counted as wired. The
  rule is a floor on honesty, not a substitute for reading the diff.

  What it is good for: the two failure modes that actually happened — a
  deleted/renamed file, a number that drifted past the end of its file, and a
  type that only exists to be documented. See `--self-test`.

USAGE

  tools/check-doc-cites.py                      # gate (what docs.yml runs)
  tools/check-doc-cites.py --self-test          # prove the gate still bites
  tools/check-doc-cites.py --verbose            # per-document counts
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from collections import defaultdict

import md_gate

# The opt-in marker. A document that claims to be normative gets its
# `file:line` fences checked; nothing else is scanned.
NORMATIVE = "Status: normative"

# `path/to/File.cs:12`, `File.cs:12-30`, `File.cs:12,30-44`, `File.cs:12-`.
# The leading lookbehind keeps `xfoo.cs:1` from matching at `foo.cs:1`, and
# the extension allowlist keeps `1.2:3` and `localhost:8080` out.
CITATION = re.compile(
    r"(?<![A-Za-z0-9_./\-])"
    r"([A-Za-z0-9_][A-Za-z0-9_./\-]*\.(?:cs|json|yml|yaml|csproj|slnx|props|targets|py|axaml|xaml|editorconfig))"
    r":(\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)"
)

# Backticked `SomeType` or `SomeType.Member` — PascalCase head, at least 3
# chars. The dotted form matters: #664's dead subscriber was named in the
# prose as `EventBusAppStoreDispatcher.OnAgentEvent` and in the diagram with no
# backticks at all, so a rule that only matched a whole backticked identifier
# would have missed the very case it was written for. The head before the first
# dot is what gets checked; `Harbor.Abstractions` and `TimeSpan.Zero` resolve to
# a head that is not declared under src/ or apps/, so they are skipped rather
# than guessed at.
BACKTICK_TYPE = re.compile(r"`([A-Z][A-Za-z0-9_]{2,})(?:\.[A-Za-z_][A-Za-z0-9_]*)*`")

# `~~struck through~~` — a struck-through span is a historical record, not a
# live claim. EVENT_BUS_SINKS.md keeps its `TypeFilterMiddleware` row struck
# through after #478 deleted the filter; reporting that as "nothing constructs
# this type" would be the gate scolding a document for being accurate about
# its own history.
STRUCK = re.compile(r"~~[^\n]*?~~")

# The per-document escape hatch for DOC-TYPE-UNWIRED. A name plus a reason,
# on one line, inside the document that needs it. No global list exists.
ALLOW_UNWIRED = re.compile(
    r"<!--\s*check-doc-cites:\s*allow-unwired\s+(?P<names>[A-Za-z0-9_,\s]+?)"
    r"(?:\s+[-—:]\s*(?P<reason>.+?))?\s*-->"
)

# `public sealed partial record Foo` / `internal interface Foo` / `public enum
# Foo` / `public readonly record struct Foo`. Attribute lists and modifiers are
# allowed to repeat; `record struct` and `record class` put a second keyword
# between the keyword and the name.
DECLARATION = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*(?:\r?\n[ \t]*)?)*"
    r"(?:(?:public|internal|protected|private|sealed|abstract|static|partial|"
    r"readonly|ref|new|unsafe|file)[ \t]+)*"
    r"(?:class|interface|enum|struct|record)[ \t]+"
    r"(?:(?:class|struct)[ \t]+)?"
    r"([A-Z][A-Za-z0-9_]*)",
    re.M,
)

# Line and block comments, so that a type "referenced" only from a doc comment
# is not counted as wired.
LINE_COMMENT = re.compile(r"//[^\n]*")
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)


def strip_comments(text: str) -> str:
    return LINE_COMMENT.sub("", BLOCK_COMMENT.sub("", text))


def repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def line_counts(path: str, cache: dict[str, int]) -> int:
    """Number of lines in `path`, memoised. Counts newline-terminated lines."""
    if path not in cache:
        try:
            with open(path, "rb") as fh:
                cache[path] = sum(1 for _ in fh)
        except OSError:
            cache[path] = 0
    return cache[path]


def index_sources(repo: str) -> tuple[list[str], dict[str, list[str]], set[str]]:
    """Tracked `.cs` files, basename -> candidates, and the production subset.

    `contrib/` is excluded from basename resolution: it is unmaintained, not
    compiled by CI, and a doc citing `AppStore.cs:5` must not be satisfied by a
    file that nothing builds.
    """
    proc = subprocess.run(
        ["git", "ls-files", "*.cs"],
        cwd=repo,
        capture_output=True,
        text=True,
        check=True,
        env=md_gate.git_env(),
    )
    files = sorted(f for f in proc.stdout.split() if f)
    by_base: dict[str, list[str]] = defaultdict(list)
    for f in files:
        by_base[os.path.basename(f)].append(f)
    production = [f for f in files if f.startswith(("src/", "apps/"))]
    return files, dict(by_base), set(production)


def resolve_citation(
    target: str, repo: str, by_base: dict[str, list[str]]
) -> tuple[str | None, str]:
    """Return (absolute path, kind). `kind` is 'path', 'basename' or 'miss'."""
    if "/" in target:
        full = os.path.join(repo, target)
        if os.path.isfile(full):
            return full, "path"
        return None, "miss"
    candidates = [c for c in by_base.get(target, []) if not c.startswith("contrib/")]
    if len(candidates) == 1:
        return os.path.join(repo, candidates[0]), "basename"
    if len(candidates) > 1:
        return None, "ambiguous"
    return None, "miss"


def parse_ranges(spec: str) -> list[tuple[int, int]]:
    """`'12'`, `'12-'`, `'12-30'`, `'12,30-44'` -> [(start, end), ...].

    A bare trailing dash means "from here to the end of the file", which is
    what `IEventBus.cs:21-` in EVENT_TOPOLOGY.md means and what a reader
    assumes; the fence check then has to make sure 21 exists.
    """
    out: list[tuple[int, int]] = []
    for part in spec.split(","):
        if "-" in part:
            head, _, tail = part.partition("-")
            start = int(head)
            end = int(tail) if tail else start
        else:
            start = end = int(part)
        out.append((start, end))
    return out


def check_citations(
    text: str,
    repo: str,
    by_base: dict[str, list[str]],
    cache: dict[str, int],
) -> tuple[int, list[tuple[str, str, int]]]:
    """Return (citation count, [(code, message, doc line), ...])."""
    found = 0
    problems: list[tuple[str, str, int]] = []
    for m in CITATION.finditer(text):
        target, spec = m.group(1), m.group(2)
        line_no = text.count("\n", 0, m.start()) + 1
        full, kind = resolve_citation(target, repo, by_base)
        if full is None:
            if kind == "ambiguous":
                problems.append(
                    (
                        "DOC-CITE-MISSING",
                        f"{target}:{spec} — {len(by_base.get(target, []))} tracked files share this name",
                        line_no,
                    )
                )
            else:
                problems.append(
                    ("DOC-CITE-MISSING", f"{target}:{spec} — no such file in the tree", line_no)
                )
            continue
        if not full.endswith((".cs", ".json", ".yml", ".yaml", ".csproj", ".slnx")):
            # A path that exists is a path that exists; only the formats we
            # can count lines for get a fence.
            continue
        total = line_counts(full, cache)
        for start, end in parse_ranges(spec):
            found += 1
            if total == 0:
                problems.append(
                    ("DOC-CITE-EOF", f"{target}:{start} — file is empty or unreadable", line_no)
                )
            elif end > total:
                problems.append(
                    (
                        "DOC-CITE-EOF",
                        f"{target}:{start}-{end if end != start else ''} — file has {total} lines",
                        line_no,
                    )
                )
    return found, problems


def read_allowances(text: str) -> tuple[dict[str, str], list[tuple[str, str, int]]]:
    """Parse this document's `allow-unwired` lines.

    Returns (name -> reason, problems). A line without a reason is a problem,
    not a silent pass: the whole point of the hatch is that someone had to say
    why, in the diff, next to the sentence that needed it.
    """
    allowed: dict[str, str] = {}
    problems: list[tuple[str, str, int]] = []
    for m in ALLOW_UNWIRED.finditer(text):
        line_no = text.count("\n", 0, m.start()) + 1
        reason = (m.group("reason") or "").strip()
        for name in m.group("names").split(","):
            name = name.strip()
            if not name:
                continue
            if not reason:
                problems.append(
                    (
                        "DOC-ALLOW-NO-REASON",
                        f"`{name}` is allowed as unwired but the line carries no reason — "
                        f"write one: <!-- check-doc-cites: allow-unwired {name} — why -->",
                        line_no,
                    )
                )
            allowed[name] = reason
    return allowed, problems


def check_types(
    text: str,
    declared_in: dict[str, set[str]],
    production_text: dict[str, str],
    allowed: set[str],
) -> tuple[int, list[tuple[str, str, int]]]:
    """Return (name count, [(code, message, doc line), ...]).

    A backticked name that IS a type declared under src/ or apps/ must be
    referenced by at least one OTHER production file. Comments are stripped,
    so a `<see cref="..."/>` cannot make a dead type look wired.

    `allowed` is this document's own `allow-unwired` list; each entry had to
    arrive with a reason, so a name cannot be waved through silently.
    """
    seen: set[str] = set()
    problems: list[tuple[str, str, int]] = []
    # Offsets covered by a strikethrough span, so a struck-through mention of a
    # deleted type is not reported as a live claim.
    struck_spans = [m.span() for m in STRUCK.finditer(text)]
    for m in BACKTICK_TYPE.finditer(text):
        name = m.group(1)
        if name in seen:
            continue
        if any(start <= m.start() < end for start, end in struck_spans):
            continue
        seen.add(name)
        if name in allowed:
            continue
        decls = declared_in.get(name)
        if not decls:
            continue
        wired = [
            f
            for f, body in production_text.items()
            if f not in decls and re.search(rf"\b{re.escape(name)}\b", body)
        ]
        if not wired:
            problems.append(
                (
                    "DOC-TYPE-UNWIRED",
                    f"`{name}` is declared in {', '.join(sorted(decls))} but no other "
                    f"file under src/ or apps/ references it — nothing constructs it",
                    text.count("\n", 0, m.start()) + 1,
                )
            )
    return len(seen), problems


class Scan:
    """One pass over the normative documents, plus the counts the floors need."""

    def __init__(self) -> None:
        self.files = 0
        self.citations = 0
        self.type_names = 0
        self.hits: dict[str, list[tuple[str, str, int]]] = defaultdict(list)

    @property
    def documents_with_hits(self) -> int:
        return sum(1 for v in self.hits.values() if v)

    @property
    def violations(self) -> int:
        return sum(len(v) for v in self.hits.values())


def scan(repo: str, verbose: bool) -> Scan:
    """Scan the normative documents once and return everything the gate needs."""
    _, by_base, production = index_sources(repo)

    declared_in: dict[str, set[str]] = defaultdict(set)
    production_text: dict[str, str] = {}
    for rel in sorted(production):
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                body = strip_comments(fh.read())
        except OSError:
            continue
        production_text[rel] = body
        for m in DECLARATION.finditer(body):
            declared_in[m.group(1)].add(rel)

    cache: dict[str, int] = {}
    result = Scan()
    for rel in md_gate.tracked_md(repo):
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except OSError:
            continue
        if NORMATIVE not in text:
            continue
        result.files += 1
        cites, cite_problems = check_citations(text, repo, by_base, cache)
        allowance, allowance_problems = read_allowances(text)
        names, type_problems = check_types(text, declared_in, production_text, set(allowance))
        result.citations += cites
        result.type_names += names
        result.hits[rel] = cite_problems + type_problems + allowance_problems
        if verbose:
            print(f"  {rel}: {cites} citations, {names} type names")
    return result


def self_test() -> int:
    """Run the gate against fixtures that are broken in one specific way each.

    The last two cases matter most: a clean normative document and a *non*-normative
    document carrying the same broken citations. Together they prove the gate
    discriminates rather than failing on everything it is shown — which is the
    difference between a gate and a tripwire.
    """
    st = md_gate.SelfTest("check-doc-cites")

    good_src = (
        "namespace Demo;\n"
        "/// <summary>Live type.</summary>\n"
        "public sealed class LiveThing\n"
        "{\n"
        "    public int Value => 1;\n"
        "}\n"
    )
    dead_src = (
        "namespace Demo;\n"
        "/// <summary>Never constructed.</summary>\n"
        "public sealed class DeadThing\n"
        "{\n"
        "    public void Start() { }\n"
        "}\n"
    )
    # `LiveThing` needs a real production reference or the fixture is itself
    # unwired, which would make the "clean document" case pass for the wrong
    # reason. Every fixture document also names `LiveThing` in backticks, so the
    # type rule is exercised (and non-vacuously counted) in each case.
    user_src = "namespace Demo;\npublic sealed class User { public LiveThing T { get; } = new(); }\n"
    caller_src = "namespace Demo;\npublic static class Caller { public static void Go(LiveThing t) { } }\n"

    live = {
        "src/Demo/Live.cs": good_src,
        "src/Demo/User.cs": user_src,
        "src/Demo/Caller.cs": caller_src,
    }
    clean_norm = (
        "# N\n\n> Status: normative for the current implementation.\n\n"
        "The subscriber is `LiveThing`; see `src/Demo/Live.cs:4` and `Live.cs:4`.\n"
    )

    def run(files: dict[str, str], *args: str) -> tuple[int, str]:
        root = md_gate.make_fixture("check-doc-cites.py", files)
        code, out = md_gate.run_gate(root, "check-doc-cites.py", *args)
        return code, out

    code, out = run({**live, "docs/NORM.md": clean_norm})
    st.expect("a clean normative document passes", code == 0, out.strip()[-400:])

    code, out = run(
        {
            **live,
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `LiveThing`; see `src/Demo/Gone.cs:4`.\n",
        }
    )
    st.expect("a citation to a deleted file fails", code == 1 and "DOC-CITE-MISSING" in out, out[-400:])

    code, out = run(
        {
            **live,
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `LiveThing`; see `src/Demo/Live.cs:999` and `src/Demo/Live.cs:2-40`.\n",
        }
    )
    st.expect(
        "a citation past EOF fails (the #664 AgentLoop case)", code == 1 and "DOC-CITE-EOF" in out, out[-400:]
    )

    code, out = run(
        {
            **live,
            # Two tracked files share a basename, so a bare `Widget.cs:1` has
            # two answers. Guessing one of them is how a doc ends up citing a
            # file that is not the one it means.
            "src/Demo/Widget.cs": "namespace Demo;\npublic sealed class WidgetA { }\n",
            "src/Demo/Other/Widget.cs": "namespace Demo;\npublic sealed class WidgetB { }\n",
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `LiveThing`; see `Widget.cs:2`.\n",
        }
    )
    st.expect(
        "an ambiguous bare basename fails rather than guessing",
        code == 1 and "DOC-CITE-MISSING" in out,
        out[-400:],
    )

    code, out = run(
        {
            **live,
            "src/Demo/Dead.cs": dead_src,
            "src/Demo/User.cs": "namespace Demo;\npublic sealed class User { public DeadThing T { get; } = new(); }\n",
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `DeadThing`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a wired type passes (the UNWIRED rule is not a blanket type check)",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {
            "src/Demo/Dead.cs": dead_src,
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `DeadThing`.\n",
        }
    )
    st.expect(
        "a declared-but-unwired type fails (the #664 AppStore case)",
        code == 1 and "DOC-TYPE-UNWIRED" in out,
        out[-400:],
    )

    code, out = run(
        {
            "src/Demo/Dead.cs": dead_src,
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `DeadThing`; see `src/Demo/Dead.cs:4`.\n"
            "<!-- check-doc-cites: allow-unwired DeadThing — public API surface, no product caller -->\n",
        }
    )
    st.expect(
        "an allow-unwired line with a reason silences the type rule",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {
            "src/Demo/Dead.cs": dead_src,
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `DeadThing`.\n"
            "<!-- check-doc-cites: allow-unwired DeadThing -->\n",
        }
    )
    st.expect(
        "an allow-unwired line WITHOUT a reason fails — the hatch is not a mute button",
        code == 1 and "DOC-ALLOW-NO-REASON" in out,
        out[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/NOTE.md": "# Note\n\nNot normative. `LiveThing`; see `src/Demo/Gone.cs:4` and `Live.cs:999`.\n",
        }
    )
    st.expect(
        "the same broken citations in a NON-normative document are out of scope",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {**live, "docs/NORM.md": clean_norm},
        "--min-files",
        "9",
        "--min-cites",
        "9",
    )
    st.expect("a narrowed scan fails the floor instead of passing quietly", code == 1 and "floor" in out, out[-400:])

    # The real-world shape: a file is renamed and every citation to the old
    # name goes stale at once. The doc still parses, still has the same number
    # of citations, and the line count is irrelevant — the file is simply gone.
    renamed_src = good_src.replace("LiveThing", "Renamed")
    code, out = run(
        {
            "src/Demo/Renamed.cs": renamed_src,
            "src/Demo/User.cs": "namespace Demo;\npublic sealed class User { public Renamed T { get; } = new(); }\n",
            "src/Demo/Caller.cs": "namespace Demo;\npublic static class Caller { public static void Go(Renamed t) { } }\n",
            "docs/NORM.md": "# N\n\n> Status: normative for the current implementation.\n\n"
            "The subscriber is `Renamed`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a rename breaks the citation rather than passing silently",
        code == 1 and "DOC-CITE-MISSING" in out,
        out[-400:],
    )

    return st.finish()


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--verbose", action="store_true", help="print per-document counts")
    ap.add_argument("--min-files", type=int, default=0, help="fail unless at least this many normative docs were scanned (0 = off)")
    ap.add_argument("--min-cites", type=int, default=0, help="fail unless at least this many line citations were examined (0 = off)")
    ap.add_argument("--min-types", type=int, default=0, help="fail unless at least this many backticked type names were examined (0 = off)")
    ap.add_argument(
        "--self-test",
        action="store_true",
        help="run the gate against known-broken fixtures instead of this repo",
    )
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = repo_root()
    result = scan(repo, args.verbose)
    print(
        f"checked {result.citations} line citations and {result.type_names} backticked "
        f"type names across {result.files} normative markdown documents"
    )

    # Two calls, one per unit, so a parser that silently stops matching is
    # caught separately from a file set that shrank.
    problems = md_gate.require_non_vacuous(
        "doc-cites", result.files, result.citations, "line citations", args.min_files, args.min_cites
    )
    problems += md_gate.require_non_vacuous(
        "doc-cites/types",
        result.files,
        result.type_names,
        "backticked type names",
        args.min_files,
        args.min_types,
    )

    if result.violations:
        print(
            f"\n=== VIOLATIONS ({result.violations} in {result.documents_with_hits} documents) ==="
        )
        for rel in sorted(result.hits):
            if not result.hits[rel]:
                continue
            print(f"\n{rel}")
            for code, message, line_no in sorted(result.hits[rel], key=lambda t: (t[2], t[0])):
                print(f"  L{line_no}: [{code}] {message}")

    for problem in problems:
        print(f"\n{problem}", file=sys.stderr)

    if result.violations or problems:
        return 1
    print("OK: every normative file:line citation resolves to a line that exists.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
