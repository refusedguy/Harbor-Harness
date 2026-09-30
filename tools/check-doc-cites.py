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
  DOC-CITE-TABLE-UNDECLARED
                     a markdown table that asserts `| path/File.cs | 12 |` —
                     an inventory of members, the shape every generated audit
                     table has — in a document that declares NEITHER
                     `Status: normative` NOR a dated `Status (YYYY-MM-DD):`
                     banner. See "THE THIRD SHAPE" below: this rule is the one
                     that would have caught #807 on the day the table was
                     written.
  DOC-SAMPLE-API-UNDECLARED
                     a backticked type name in the `## Public API` section of a
                     `samples/plugins/*/README.md` that no tracked `.cs` file
                     under src/, apps/ or samples/ declares. See "THE FOURTH
                     SHAPE" below: this rule is the one that would have caught
                     #794 the day the sample README was written.

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

THE THIRD SHAPE — WHY #807 HAPPENED AT ALL (#807)

  The two fence rules above only match the PROSE form `path/File.cs:12`. A
  markdown table row puts the path and the number in separate cells —
  `| Harbor.Application/Sessions/CompactionService.cs | 209 |` — and that
  matches nothing. Measured on this tree, the shape split is:

      prose `path/File.cs:12`    199 citations, all fenced, all green
      table `| path | line |`   2842 rows in ONE document, ZERO fenced

  So the document with by far the densest concentration of `file:line` claims
  in the repository sat entirely outside the only gate that checks whether a
  `file:line` is real, and rotted for a month without a single build noticing.
  That is the actual class: not "three audits went stale" but "the fence reads
  one shape of citation and the other shape is where the claims pile up".

  The fix is not a wider scan — the prose rules are deliberately not global,
  for the reason above. The fix is to make the SHAPE declare itself, which is
  what DOC-CITE-TABLE-UNDECLARED does:

      `Status: normative`      the rows are claims about today; they get fenced
      `Status (YYYY-MM-DD):`   the document says out loud that it is a dated
                               record, and a record is ALLOWED to name code
                               that has since moved. That is the whole point of
                               a запись.
      neither                  a table of pointers that never says which it is.
                               This is the undeclared middle, and it is the
                               defect: it reads as current truth while rotting
                               at 672 rows out of 2842.

  Making the label mandatory is the only version of this that lands. Fencing
  the table rows directly is not available: the one document in that shape is
  a dated snapshot, so a fence over it is red on 672 rows on day one, and a
  gate red on day one is a gate that gets switched off (see SCOPE).

  What this rule deliberately does NOT do: verify that a declared record's
  numbers are right. A запись is allowed to be wrong about today. It only has
  to stop pretending to be an эталон.

THE FOURTH SHAPE — THE NAME THAT NO TREE DECLARES (#794)

  #807 was one fence reading one shape. This is the same disease in the type
  rules, and it is worse because the type rules are supposed to be the ones
  that see past the fence.

  DOC-TYPE-UNWIRED answers "this type EXISTS and nothing constructs it". It
  cannot answer "this type DOES NOT EXIST", because it looks the name up in
  `declared_in` and then:

      decls = declared_in.get(name)
      if not decls:
          continue          # <-- the exact case that needs reporting

  So a normative document could name a type that no file in the repository has
  ever declared and the type rule passed it silently. Measured on this tree,
  the three axes of the table below are not equal, and only the middle column
  was fenced:

      declared in src//apps/, wired      -> DOC-TYPE-UNWIRED  (#664)
      declared in src//apps/, unwired    -> DOC-TYPE-UNWIRED
      declared NOWHERE                     -> nothing.   <- this rule

  A second, independent blind spot fed it: `scan()` indexed declarations from
  `production` alone (`src/` and `apps/`), so a type declared in `samples/`
  was not in `declared_in` at all and could not be found by either type rule.

  DOC-SAMPLE-API-UNDECLARED closes both, on the one shape where the claim is
  structural rather than rhetorical. A `## Public API` section is a CONTRACT
  INVENTORY: every bullet says "this component declares this type", which is a
  claim about the tree in exactly the way a `file:line` citation is, and it is
  checkable with no interpretation. The perimeter is
  `samples/plugins/*/README.md` — four files, listed in the glob below.

  Measured before the fix, on the four sample READMEs: SEVEN of the 23 names
  in those sections were types no tree has ever declared, and ALL FOUR of the
  READMEs were affected. The TodoWrite one was born lying — commit 487a68a0
  added the README and its false `TodoPanelPlugin` / `ITuiPlugin` claims in
  the same commit, and that sample's source has never contained a panel. #794
  reported one of the seven.

WHY THIS RULE IS NOT OPT-IN, EITHER

  Same reasoning as DOC-CITED-TABLE-UNDECLARED, and it is the second rule to
  earn it: a table of `| path | line |` and a list of `## Public API` type
  names LOOK like the prose and table claims the other rules cover, so leaving
  them out is what let 2842 unchecked rows and 8 phantom types accumulate
  while every other fence stayed green. Neither rule asks a document to
  declare anything — the answer is one edit to the README, and there is
  nothing here to go red on day one once the READMEs are corrected.

  What this rule deliberately does NOT do, stated rather than implied:

  * It checks the `## Public API` SECTION, not the whole README. A name in
    "How it works" is prose about a mechanism, and a doc that names an
    illustrative type there is not making a claim about the tree. Pinned by a
    self-test, because a whole-file version of this rule is the obvious wrong
    answer and would fire on every `HttpClient` in the repository.
  * It checks EXISTENCE, not locality. A real type attributed to the wrong
    component passes. The FileTree README claimed `TreeNode` — a real type in
    `Harbor.Ui.Framework.Rendering` — for a sample that has no such record.
    That is a different defect with a different fix, and this rule does not
    see it.
  * It does not check tool NAMES. All four sample READMEs also misname their
    own tool (`file_tree` for `tree`, `git_status`/`git_diff`/`git_log` for
    one `git` tool, `todo_write` for `todo`, `web_search` for `websearch`),
    and `ToolNameInventory.cs` already extracts the truth from
    `ToolName.Create("…")`. The claim lives in free prose on line 3, not in a
    structured field, so matching it would need a heuristic and a heuristic in
    a docs gate is how a gate starts guessing. Measured and reported instead.

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
#
# ANCHORED TO THE STATUS BANNER, not a bare substring (#807). A plain
# `"Status: normative" in text` test means a document that merely MENTIONS the
# marker opts itself in: docs/XML_DOC_AUDIT.md says in prose "this file is not
# marked `Status: normative`", and the substring check read that sentence as
# consent, pulling all 2842 of its table rows into the fence and turning the
# gate red on 672 historical rows. Self-selection that can be triggered by
# discussing the rule is not self-selection. The marker has to be a status
# banner in the position the convention puts it — the leading `> Status: ...`
# blockquote — and the three documents that declare it today all match.
NORMATIVE_RE = re.compile(r"^[ \t]*>[ \t]*Status: normative\b", re.M)
NORMATIVE = "Status: normative"  # prose references; matching is NORMATIVE_RE

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

# THE THIRD SHAPE (#807). A markdown table row whose first cell is a source
# path and whose second cell is a line — `| Harbor.Application/.../X.cs | 209 |`.
# The path and the number live in separate cells, so CITATION above cannot see
# them: the fence that exists for exactly this failure mode is blind to the one
# shape a generated inventory table uses.
#
# Anchored at the start of a row and requiring the line cell to be digits, so
# a prose mention, a link cell and the archive README's `| file.md | date |`
# rows all stay out. `spec` reuses CITATION's range grammar (209, 209-215,
# 209,215-230) so the two shapes describe a claim the same way.
TABLE_CITATION = re.compile(
    r"^\|\s*(?P<target>[A-Za-z0-9_][A-Za-z0-9_./\-]*\."
    r"(?:cs|json|yml|yaml|csproj|slnx|props|targets|py|axaml|xaml|editorconfig))"
    r"\s*\|\s*(?P<spec>\d+(?:-\d*)?(?:,\d+(?:-\d*)?)*)\s*\|",
    re.M,
)

# THE FOURTH SHAPE (#794). A component README whose `## Public API` section is
# an inventory of the types that component declares — the sample-plugin
# version of a contract table, and the one shape where "does this name exist?"
# is a question about the tree rather than a question about prose.
#
# The perimeter is four files and is written as a glob rather than a list so a
# new sample plugin is covered by existing, not by remembering to edit a list
# here (the same self-selecting property NORMATIVE_RE has).
SAMPLE_README = re.compile(r"^samples/plugins/[^/]+/README\.md$")

# `## Public API`, optionally with a parenthetical suffix. `^##\s` cannot match
# `### Public API`, and not requiring end-of-line means a section titled
# `## Public API (highlights)` is still the section — which is how the
# `src/*/README.md` spell it, and those are outside the perimeter for a
# different reason.
PUBLIC_API_HEADING = re.compile(r"^##[ \t]+Public API\b", re.M)

# The section ends at the next `##` heading, or at end of file.
SECTION_END = re.compile(r"^##[ \t]+", re.M)

# Where a type may be DECLARED for this rule's purposes. `src/` and `apps/` are
# the product; `samples/` is here because a sample README's claim is about the
# sample, and a name the sample itself declares is the one thing that must
# resolve. `tests/` is deliberately absent: a name that exists only in a test is
# not a type a sample plugin ships, so a README claiming it is wrong.
# `contrib/` is absent for the reason index_sources gives.
DECLARATION_SCOPE = ("src/", "apps/", "samples/")

# A dated record: the `> **Status (2026-08-27):**` banner that
# docs/audit-archive/README.md already prescribes for "a snapshot" kept in
# docs/ proper. Two spellings are accepted — the parenthetical form the archive
# rule documents, and `Status: as-of <date>` — so a document can declare
# itself in whichever reads better. A DATE is required, not just the word
# "Status": a table of pointers labelled only "Status: draft" is exactly the
# undeclared middle this rule exists to close.
DATED_RECORD = re.compile(
    r"Status\s*(?:\(\s*(?P<paren>\d{4}-\d{2}-\d{2})\s*\)|:\s*as-of\s+(?P<asof>\d{4}-\d{2}-\d{2}))",
    re.I,
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


def check_table_shape(
    text: str, rel: str, normative: bool
) -> tuple[int, list[tuple[str, str, int]]]:
    """The third shape (#807): does this document SAY which kind it is?

    Returns (row count, problems). A row count is reported for every document
    that carries one, including documents the rule passes, because a rule that
    only ever prints a number when it is about to fail is a rule nobody can
    tell apart from a rule that never fires.

    This runs over EVERY tracked markdown file, not just the normative ones.
    That is the one place this script is deliberately not opt-in, and the
    asymmetry is the point: a table of `file:line` claims is a document that
    LOOKS like the prose the fence covers, so leaving it opt-in is what let
    2842 unchecked rows accumulate in the densest claim table in the repo. The
    rule asks one cheap question — normative, or dated, or neither — and the
    answer is one line of prose, so there is nothing here to go red on day one.
    """
    rows = list(TABLE_CITATION.finditer(text))
    if not rows:
        return 0, []
    if normative or DATED_RECORD.search(text):
        # Declared. A normative document's rows are fenced as citations by
        # check_citations' sibling rule below; a dated record is ALLOWED to
        # name code that has since moved, which is what being a record means.
        return len(rows), []
    first = rows[0]
    return len(rows), [
        (
            "DOC-CITED-TABLE-UNDECLARED",
            f"{len(rows)} row(s) assert `| path | line |` against source files "
            f"(first at L{text.count(chr(10), 0, first.start()) + 1}), and this "
            f"document declares neither `Status: normative` nor a dated "
            f"`Status (YYYY-MM-DD):` banner. A table of `file:line` that does "
            f"not say which kind of document it is reads as current truth while "
            f"its numbers rot — the failure #807 is. Add one banner: "
            f"`> **Status: normative**` if the rows claim to be about today, or "
            f"`> **Status (YYYY-MM-DD):**` if this is a dated record, in which "
            f"case the rows are allowed to name code that has since moved",
            text.count("\n", 0, first.start()) + 1,
        )
    ]


def check_table_citations(
    text: str, repo: str, by_base: dict[str, list[str]], cache: dict[str, int]
) -> tuple[int, list[tuple[str, str, int]]]:
    """Fence the table rows of a NORMATIVE document.

    Only normative: for a dated record these numbers are the snapshot, and
    policing them would be the gate demanding a history be corrected. Paths in
    these tables are project-relative (`Harbor.Abstractions/...`, the column
    header a project sweep writes), so `src/` and `apps/` are tried after the
    repo-relative form.
    """
    found = 0
    problems: list[tuple[str, str, int]] = []
    for m in TABLE_CITATION.finditer(text):
        target, spec = m.group("target"), m.group("spec")
        line_no = text.count("\n", 0, m.start()) + 1
        full = None
        for candidate in (os.path.join(repo, target), os.path.join(repo, "src", target),
                          os.path.join(repo, "apps", target)):
            if os.path.isfile(candidate):
                full = candidate
                break
        if full is None:
            problems.append(
                ("DOC-CITE-MISSING", f"{target}:{spec} — no such file in the tree", line_no)
            )
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


def check_sample_api(
    text: str, rel: str, declared: set[str]
) -> tuple[int, list[tuple[str, str, int]]]:
    """The fourth shape (#794): does a `## Public API` list name a real type?

    Returns (name count, [(code, message, doc line), ...]). A name count is
    returned even for a document that passes, for the reason
    check_table_shape gives: a rule that only prints a number when it is about
    to fail is indistinguishable from a rule that never fires.

    The section boundary is the point of the rule, not a convenience. A
    `## Public API` bullet asserts "this component declares this type"; the
    same name in "How it works" is prose about a mechanism, and a document is
    entitled to describe an illustrative type there without shipping it. So the
    scan is bounded at the next `##` and the count is of names INSIDE it.
    """
    heading = PUBLIC_API_HEADING.search(text)
    if heading is None:
        return 0, []

    body_start = heading.end()
    nxt = SECTION_END.search(text, body_start)
    body_end = nxt.start() if nxt else len(text)

    seen: set[str] = set()
    problems: list[tuple[str, str, int]] = []
    for m in BACKTICK_TYPE.finditer(text):
        if not (body_start <= m.start() < body_end):
            continue
        name = m.group(1)
        if name in seen:
            continue
        seen.add(name)
        if name in declared:
            continue
        line_no = text.count("\n", 0, m.start()) + 1
        problems.append(
            (
                "DOC-SAMPLE-API-UNDECLARED",
                f"`{name}` is listed in this README's `## Public API` section but no "
                f".cs file under src/, apps/ or samples/ declares it — the section is "
                f"a contract inventory, so the name is a claim about the tree. Either "
                f"the type is missing from {rel.rsplit('/', 1)[0]}/ or the bullet "
                f"describes something this sample does not ship",
                line_no,
            )
        )
    return len(seen), problems


class Scan:
    """One pass over the normative documents, plus the counts the floors need."""

    def __init__(self) -> None:
        self.files = 0
        self.citations = 0
        self.type_names = 0
        self.table_rows = 0
        self.table_docs = 0
        self.api_names = 0
        self.api_docs = 0
        self.hits: dict[str, list[tuple[str, str, int]]] = defaultdict(list)

    @property
    def documents_with_hits(self) -> int:
        return sum(1 for v in self.hits.values() if v)

    @property
    def violations(self) -> int:
        return sum(len(v) for v in self.hits.values())


def scan(repo: str, verbose: bool) -> Scan:
    """Scan the normative documents once and return everything the gate needs."""
    files, by_base, production = index_sources(repo)

    declared_in: dict[str, set[str]] = defaultdict(set)
    # The union index DOC-SAMPLE-API-UNDECLARED needs. A SET rather than the
    # dict above because the question it asks is only "does this name exist
    # anywhere a product or a sample could declare it" — WHERE it is declared is
    # not what makes a README bullet true. Scoped to DECLARATION_SCOPE so the
    # answer stays a statement about shipped code, and read over `files` rather
    # than `production` so a type declared in samples/ is findable at all —
    # which, before this rule, no code path could do.
    declared_anywhere: set[str] = set()
    production_text: dict[str, str] = {}
    for rel in files:
        if not rel.startswith(DECLARATION_SCOPE):
            continue
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                body = strip_comments(fh.read())
        except OSError:
            continue
        names = {m.group(1) for m in DECLARATION.finditer(body)}
        declared_anywhere |= names
        if rel in production:
            production_text[rel] = body
            for name in names:
                declared_in[name].add(rel)

    cache: dict[str, int] = {}
    result = Scan()
    for rel in md_gate.tracked_md(repo):
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except OSError:
            continue
        normative = NORMATIVE_RE.search(text) is not None

        # The shape question runs on every tracked document, normative or not:
        # that is the whole of #807, which happened in a document that never
        # wrote the marker.
        rows, shape_problems = check_table_shape(text, rel, normative)
        if rows:
            result.table_docs += 1
            result.table_rows += rows

        # ... and so does the fourth shape (#794), for the same reason and with
        # the same one-line fix. A sample README's `## Public API` list is a
        # contract table, so a name in it is a claim about the tree whether or
        # not the document ever writes a status banner.
        if SAMPLE_README.match(rel):
            api_names, api_problems = check_sample_api(text, rel, declared_anywhere)
            result.api_docs += 1
            result.api_names += api_names
            shape_problems = shape_problems + api_problems

        if not normative:
            if shape_problems:
                result.hits[rel] = shape_problems
            continue
        result.files += 1
        cites, cite_problems = check_citations(text, repo, by_base, cache)
        table_cites, table_cite_problems = check_table_citations(text, repo, by_base, cache)
        allowance, allowance_problems = read_allowances(text)
        names, type_problems = check_types(text, declared_in, production_text, set(allowance))
        result.citations += cites + table_cites
        result.type_names += names
        result.hits[rel] = (
            cite_problems + table_cite_problems + type_problems + allowance_problems
        )
        if verbose:
            print(f"  {rel}: {cites + table_cites} citations, {names} type names, {rows} table rows")
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

    # ---- THE THIRD SHAPE (#807) ------------------------------------------------
    # The shape the prose fence cannot see: `| path/File.cs | 12 |`. Every case
    # below is a document with NO `Status: normative` marker, which is the
    # state XML_DOC_AUDIT.md was in for a month.
    table_row = "| Demo/Live.cs | 4 | public sealed class LiveThing | YES | HIGH |\n"

    code, out = run(
        {
            **live,
            "docs/TABLE.md": "# T\n\nMembers:\n\n| File | Line | Member |\n|---|---|---|\n" + table_row,
        }
    )
    st.expect(
        "an UNDECLARED table of `| path | line |` fails (#807 — the shape the prose fence is blind to)",
        code == 1 and "DOC-CITED-TABLE-UNDECLARED" in out,
        out[-400:],
    )

    # The three "must pass" fixtures below carry `clean_norm` because the
    # non-vacuity floors require at least one normative document: a fixture
    # with zero would exit 1 on the floors and the case would pass for the
    # wrong reason. That is the same trap the floors exist to catch.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/TABLE.md": "# T\n\n> **Status (2026-08-27):** a dated record.\n\n"
            "Members:\n\n| File | Line | Member |\n|---|---|---|\n" + table_row,
        }
    )
    st.expect(
        "the SAME table with a dated `Status (YYYY-MM-DD):` banner passes — a запись may name code that has since moved",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/TABLE.md": "# T\n\n> **Status: as-of 2026-08-27.**\n\n"
            "Members:\n\n| File | Line | Member |\n|---|---|---|\n" + table_row,
        }
    )
    st.expect(
        "the `Status: as-of <date>` spelling also declares a record",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/TABLE.md": "# T\n\n> **Status: draft.**\n\n"
            "Members:\n\n| File | Line | Member |\n|---|---|---|\n" + table_row,
        }
    )
    st.expect(
        "a dateless `Status: draft` banner does NOT declare anything (#807's undeclared middle)",
        code == 1 and "DOC-CITED-TABLE-UNDECLARED" in out,
        out[-400:],
    )

    # The regression that made #807 expensive to find: the marker used to be a
    # bare substring, so a document EXPLAINING that it is not normative opted
    # itself in. Both fixtures carry `clean_norm` so the non-vacuity floors are
    # satisfied and each case can only pass for the reason it names.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/NOTE.md": "# N\n\nFor the same reason this file is not marked\n"
            "`Status: normative`: its numbers are a snapshot. See `src/Demo/Gone.cs:4`.\n",
        }
    )
    st.expect(
        "a document that merely MENTIONS the marker in prose does not opt itself in (#807)",
        code == 0,
        out.strip()[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm.replace("`src/Demo/Live.cs:4` and `Live.cs:4`", "`src/Demo/Gone.cs:4`"),
            "docs/NOTE.md": "# N\n\nThe convention is a `Status: normative` banner; "
            "see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "and a mention is still only a mention: the broken citation is in the NON-normative doc, so only the real banner fails",
        code == 1 and "DOC-CITE-MISSING" in out,
        out[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/TABLE.md": "# T\n\n> Status: normative for the current implementation.\n\n"
            "Members:\n\n| File | Line | Member |\n|---|---|---|\n"
            "| Demo/Gone.cs | 4 | public sealed class Gone | YES | HIGH |\n",
        }
    )
    st.expect(
        "a table row in a NORMATIVE document is fenced, not merely labelled",
        code == 1 and "DOC-CITE-MISSING" in out,
        out[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/TABLE.md": "# T\n\n> Status: normative for the current implementation.\n\n"
            "Members:\n\n| File | Line | Member |\n|---|---|---|\n"
            "| Demo/Live.cs | 9999 | public sealed class LiveThing | YES | HIGH |\n",
        }
    )
    st.expect(
        "a normative table row past EOF fails (the #664 rule, in the table shape)",
        code == 1 and "DOC-CITE-EOF" in out,
        out[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/ARCHIVE.md": "# A\n\n| File | Date | Note |\n|---|---|---|\n"
            "| design-system-audit.json | 2026-08-27 | theme sweep |\n"
            "| report.html | 2026-08-27 | HDS v1 |\n",
        }
    )
    st.expect(
        "the archive README's own `| file | date |` rows are not mistaken for citations",
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

    # ---- THE FOURTH SHAPE (#794) ------------------------------------------------
    # A name no tree declares, in a section that is a contract inventory. Each
    # "must pass" fixture carries `clean_norm` so the floors are satisfied and
    # the case can only pass for the reason it names — the same trap as above.
    #
    # The fixture tree needs `ITool` declared, because the README's bullets name
    # the contract the sample implements and the rule checks EVERY backticked
    # name in the section. A fixture whose own bullets are undeclared would fail
    # for a reason the case is not about, which is how a self-test ends up
    # asserting nothing.
    tool_contract = "namespace Demo;\npublic interface ITool { }\n"
    tool_plugin = "namespace Demo;\npublic interface IToolPlugin { }\n"
    sample_src = (
        "samples/plugins/Harbor.Plugin.Sample/Sample.cs",
        "namespace Harbor.Plugin.Sample;\n"
        "public sealed class SamplePlugin : Demo.IToolPlugin { }\n"
        "public sealed class SampleTool : Demo.ITool { }\n",
    )
    sample_readme = (
        "# Harbor.Plugin.Sample\n\n"
        "Sample plugin. Demonstrates `IToolPlugin`.\n\n"
        "## Public API\n\n"
        "- `SamplePlugin` — implements `IToolPlugin`\n"
        "- `SampleTool` — the `ITool` implementation\n"
        "- `NAME_PLACEHOLDER` — the panel that renders the list\n\n"
        "## How it works\n\n"
        "The tool keeps its state in a `NAME_PLACEHOLDER` and emits an event.\n"
    )

    def sample_fixture(name: str, **extra: str) -> dict[str, str]:
        """A tree whose sample README names `name` in BOTH positions."""
        return {
            **live,
            "src/Demo/ITool.cs": tool_contract,
            "src/Demo/IToolPlugin.cs": tool_plugin,
            sample_src[0]: sample_src[1],
            "docs/NORM.md": clean_norm,
            "samples/plugins/Harbor.Plugin.Sample/README.md": sample_readme.replace(
                "NAME_PLACEHOLDER", name
            ),
            **extra,
        }

    code, out = run(sample_fixture("GhostPanelPlugin"))
    st.expect(
        "a `## Public API` bullet naming a type NO tree declares fails (#794)",
        code == 1 and "DOC-SAMPLE-API-UNDECLARED" in out,
        out[-400:],
    )

    # The SAME fixture with the type added to the sample. Everything else is
    # byte-identical, so the only thing that moved the verdict is whether the
    # name exists — which is the whole claim the rule makes.
    code, out = run(
        sample_fixture(
            "GhostPanelPlugin",
            **{
                "samples/plugins/Harbor.Plugin.Sample/Panel.cs":
                    "namespace Harbor.Plugin.Sample;\npublic sealed class GhostPanelPlugin { }\n"
            },
        )
    )
    st.expect(
        "the SAME bullet passes once the sample declares the type — the rule asks "
        "whether the name exists, not whether the README is pretty",
        code == 0,
        out.strip()[-400:],
    )

    # The discrimination case, and the reason the rule is not a whole-file scan.
    # `LiveThing` is a REAL type in the fixture tree and it is named in the same
    # two positions, so this pair differs from the two above only in whether the
    # name resolves: section mention reported, prose mention not.
    code, out = run(sample_fixture("LiveThing"))
    st.expect(
        "the same bullet naming a REAL type passes — and a real type is not a phantom",
        code == 0,
        out.strip()[-400:],
    )
    code, out = run(
        {
            **live,
            "src/Demo/ITool.cs": tool_contract,
            "src/Demo/IToolPlugin.cs": tool_plugin,
            sample_src[0]: sample_src[1],
            "docs/NORM.md": clean_norm,
            "samples/plugins/Harbor.Plugin.Sample/README.md": (
                "# S\n\n## Public API\n\n- `SamplePlugin` — implements `IToolPlugin`\n\n"
                "## How it works\n\nIt builds a `GhostPanelPlugin` forest.\n"
            ),
        }
    )
    st.expect(
        "an undeclared name OUTSIDE `## Public API` is prose and is not reported (#794)",
        code == 0,
        out.strip()[-400:],
    )

    # The perimeter. An undeclared name in a NORMATIVE document must still pass:
    # the new rule narrows nothing the existing rules do, and a version of it
    # that ran on every document would be a second, undeclared fence.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm + "A recipe may name an illustrative `GhostPanelPlugin`.\n",
        }
    )
    st.expect(
        "an undeclared name in a NORMATIVE document is still out of scope for the "
        "new rule — it did not widen the fence",
        code == 0,
        out.strip()[-400:],
    )

    # The ratchet. The surface is present and clean, and a floor above today's
    # count still fails — otherwise a rule that quietly stopped matching would
    # look exactly like a rule that is passing.
    code, out = run(sample_fixture("LiveThing"), "--min-api", "9")
    st.expect(
        "the `--min-api` ratchet fails when the sample surface shrinks",
        code == 1 and "floor" in out,
        out[-400:],
    )
    code, out = run({**live, "docs/NORM.md": clean_norm})
    st.expect(
        "and a fixture with no sample README is not failed by it — the floor is "
        "opt-in, like --min-cites (a 'must pass' case that fails on the floors "
        "proves nothing about the rule)",
        code == 0,
        out.strip()[-400:],
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
        "--min-api",
        type=int,
        default=0,
        help="fail unless at least this many `## Public API` type names were examined "
        "in samples/plugins/*/README.md (0 = off)",
    )
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
    if result.table_rows:
        print(
            f"and declared {result.table_rows} `| path | line |` table rows in "
            f"{result.table_docs} document(s) (#807: the shape the prose fence is blind to)"
        )
    print(
        f"and checked {result.api_names} `## Public API` type names in "
        f"{result.api_docs} sample README(s) (#794: names no tree declares)"
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
    # Engaged ONLY by the flag, like the two above. require_non_vacuous treats a
    # zero count as a failure whatever the floor is, so running it
    # unconditionally would make every self-test fixture that happens to have
    # no sample README exit 1 — which is the trap #826 recorded when three of
    # its own "must pass" fixtures were passing on the floors rather than on
    # the rule they were written to test. The rule itself is unconditional;
    # only the ratchet is opt-in.
    if args.min_api > 0:
        problems += md_gate.require_non_vacuous(
            "doc-cites/sample-api",
            result.api_docs,
            result.api_names,
            "`## Public API` type names",
            args.min_files,
            args.min_api,
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
    print(
        "OK: every normative file:line citation resolves to a line that exists, "
        "and every `## Public API` name in a sample README is a type the tree declares."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
