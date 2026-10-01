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
                     in `src/` or `apps/`, but that nothing REACHES. This is the
                     half the line fence cannot see: the file exists, the line
                     exists, the type exists — nothing constructs it (#664).
                     Three shapes, three witnesses (see `unreachable`): a
                     `static` class is exempt because C# forbids `new` on it; a
                     `private`/`file` type is witnessed by its OWN file outside
                     its declaration; a top-level public/internal type is
                     witnessed by another production file or by an `.axaml`
                     `x:Class`. Comments and string literals are not witnesses
                     (#905).
  DOC-CITE-TABLE-UNDECLARED
                     a markdown table that asserts `| path/File.cs | 12 |` —
                     an inventory of members, the shape every generated audit
                     table has — in a document that declares NEITHER
                     `Status: normative` NOR a dated `Status (YYYY-MM-DD):`
                     banner. See "THE THIRD SHAPE" below: this rule is the one
                     that would have caught #807 on the day the table was
                     written.
  DOC-COUNT-STALE    a document that states a total for a table it contains —
                     "Total members audited: 2861" over 2842 rows — and never
                     says which of the two is historical. NOT the same class as
                     the rules above, and the distinction is the whole point:
                     every other rule asks whether a number is inside a FILE,
                     and this one asks whether a number agrees with the ROWS
                     BESIDE IT, so a file:line fence cannot see it at all. See
                     "THE ONE THING A `file:line` FENCE CANNOT SEE".
  DOC-SAMPLE-API-UNDECLARED
                     a backticked type name in the `## Public API` section of a
                     `samples/plugins/*/README.md` that no tracked `.cs` file
                     under src/, apps/ or samples/ declares. See "THE FOURTH
                     SHAPE" below: this rule is the one that would have caught
                     #794 the day the sample README was written.
  TEST-CITE-MISSING / TEST-CITE-AMBIGUOUS
                     the `file:line` shape above, in a TEST's prose rather than
                     a document's. `Status: normative` cannot select a .cs file,
                     so the markdown fence above is structurally blind to every
                     one of them (#947).
  TEST-CITE-EOF / TEST-CITE-BLANK
                     likewise, and mechanical: past the last line, or the cited
                     line is empty.
  TEST-CITE-DRIFT  the cited line resolves, is not blank, and is the WRONG
                     line: what the commit that wrote the citation meant by N is
                     not what N holds today. It is the only one of these five
                     that catches the class the others cannot see — over the
                     whole tests/ population they catch 4 of the 66 that are
                     provably wrong. See "THE SIXTH SHAPE" below for the
                     measurement and the cost.
  TEST-CITE-NO-HISTORY
                     not a finding about a sentence: the clone is truncated, so
                     the DRIFT comparison cannot be made and would silently
                     report nothing. Reported so that "fewer findings" and "the
                     anchored rule did not run" are never the same sentence.

  The escape hatch is a line IN THE SAME DOCUMENT, and it must carry a reason:

      <!-- check-doc-cites: allow-unwired TypeFilterMiddleware — <why> -->
      <!-- check-doc-cites: allow-stale-count "Total members audited" — <why> -->

  Per-document and reason-mandatory on purpose. A global allow-list in this
  script would be the "allow-list that verified zero files" failure the repo
  has already paid for; a per-document line shows up in the diff next to the
  sentence that needed it, so adding one is a reviewable act rather than an
  edit to a file nobody reads.

  The `allow-stale-count` form additionally requires a QUOTED LABEL naming the
  figure it covers. `allow-unwired` did not need one because a type name is
  already unambiguous; a document's figures are not, and a waiver that does not
  say which number it is excusing would silence every number the document will
  ever print. That is the hole #921 found in the list #902 grew and closed.

SCOPE — WHY THIS IS NOT A GLOBAL SCAN

  Only documents carrying the marker line `Status: normative` are checked.

  That is a pre-existing convention in this repo (four documents declare it
  today), and it is a self-selector rather than an allow-list: a new normative
  document is covered by writing the marker, and nobody has to remember to edit
  a list here. The alternative — scanning all tracked .md files — is not
  available at this strength: measured over the whole tree on dev at 787c777d,
  the fence rules find 85 pre-existing violations in 24 non-normative documents,
  most of them in archived material under docs/.kilo-docs/ and in PLAN.md files
  that describe an intention rather than the tree. A gate red on 85 unrelated
  findings on day one is a gate that gets switched off, which is the exact
  failure md_gate.require_non_vacuous exists to prevent.

  The figure was 141 in an earlier draft of this file and is now 85. That drop
  is not a scope change — it is the resolver getting accurate. The other 56 were
  citations that always resolved, reported as missing because the basename
  index was `.cs`-only and the prose resolver never tried `src/`+`apps/`. The
  one that matters for this argument is the direction: it went DOWN, so the case
  for keeping the fence opt-in did not weaken.

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

AND THE ONE THING A `file:line` FENCE CANNOT SEE AT ALL (#807)

  Every rule above asks the same question in a different costume: is this NUMBER
  inside that FILE? DOC-CITE-EOF, DOC-CITE-MISSING, DOC-CITED-TABLE-UNDECLARED
  all put a cited line next to a path on disk. So a number that is wrong about
  the DOCUMENT IT SITS IN is outside the perimeter of the entire script, and
  that is not a gap in one rule — it is the shape of all of them.

  DOC-COUNT-STALE asks the other question: does the number agree with the rows
  next to it. Measured, this is real and it is not one document's problem:

      docs/XML_DOC_AUDIT.md  states 2861, ships 2842 rows  — 19 short

  and the 19 decomposes exactly, because every deleted row had an XML doc:

      a917e308 (#732)  14 rows:  9 HIGH, 5 MED   the deleted Reducers layer
      c2eba3fd (#749)   5 rows:  4 HIGH, 1 MED   RecentItemsService moved

  The reason a rule about counts is worth more than the 19: the Summary in that
  file is INTERNALLY CONSISTENT. 808+543+670 = 2021, 303+222+315 = 840, and
  2021+840 = 2861 — every sum a reader can do checks out, and every one of them
  checks out against a number the file does not contain. The `Without Doc` cells
  are exact; only the documented columns drift. It is a table that passes the
  reader's own arithmetic and is still wrong, which is why a count guard has to
  compare against the rows rather than trust that the totals add up.

  It asks the same question DOC-CITED-TABLE-UNDECLARED asks — does the document
  SAY which kind of thing it is — and not "is the number true", because for a
  dated record 2861 can be a true statement about the sweep it recorded and a
  false one about the file it is in. That is what a запись is. The rule requires
  the difference to be declared on a line in the document, with a reason and the
  label of the figure it is about:

      <!-- check-doc-cites: allow-stale-count "Total members audited" — why -->

  Requiring the quoted label is not decoration: a waiver that does not say WHICH
  figure it covers is a mute button for every number the document will ever
  print, which is the hole #921 found in the allow-unwired list and closed.

  Measured before writing it: across all tracked markdown exactly ONE document
  has both a `| path | line |` table and a prose total. The rule is
  exception-free today without a list of known breakages to keep it quiet —
  which is the shape #847's owner rejected, avoided by measuring the shape
  first rather than after.

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

  And the fence could not always see the file at all. `CITATION` matches twelve
  extensions and `check_citations` fences line numbers inside `.csproj`, but the
  basename index was built from `git ls-files '*.cs' '*.axaml'`, so a bare
  `Harbor.App.Cli.csproj:145-224` was reported MISSING for a file in the tree —
  and the prose resolver never tried `src/`+`apps/`, which the table resolver
  did, so the same string was red in a paragraph and green in a table. Measured
  over the 511 prose citations in the 311 non-normative documents, widening the
  resolver removed 56 of 140 false findings and added none. See
  `index_sources` and `resolve_citation`.

  And DOC-TYPE-UNWIRED has a matching blind spot, which #905 closed one
  half of and left the other half standing. It used to read "no OTHER file
  names it" — a predicate that is unsatisfiable for two of the three shapes
  in this repo, so on docs/PATTERNS.md it reported 7 hits and 6 were false,
  all on ordinary shipped code. Names in comments and in string literals are
  no longer witnesses, so `"CompositeToolRegistry is read-only"` does not make
  that type look wired; a type referenced from a real (yet never-constructed)
  helper still is counted as wired, and that remains open. The rule is a floor
  on honesty, not a substitute for reading the diff.

WHAT THE FIX IS NOT — measured, not assumed (#905)

  Nesting is NOT the discriminator. 287 declarations in this tree are nested
  and 129 of those are `public`, 4 `internal` — all reachable from another
  file by construction. Exempting "nested" wholesale would have silenced the
  #905 false positives AND 133 legitimate declarations. Visibility decides.

  The top-level branch was NOT widened to "its own file mentions it".
  ModelEntryViewModel is named four times in SharedDataModels.cs and
  constructed by nobody; that is a live finding (#788, ADR-010 §4.5) and the
  widening would have deleted it. Only the confined branch, where no other
  file CAN reach the type, uses the own-file witness.

  The rule still has subjects. Two findings survive in the four normative
  documents — `CompositeToolRegistry` and `HarborEventKind` — and both are
  allow-listed with reasons, so the gate is green on two KNOWN, DISCLOSED
  facts rather than on nothing. Six of the thirteen allowances that #902 and
  #478 wrote became holes with no reader and were deleted; #594 set that
  standard.

  What it is good for: the two failure modes that actually happened — a
  deleted/renamed file, a number that drifted past the end of its file, and a
  type that only exists to be documented. See `--self-test`.

THE FIFTH SHAPE — MARKDOWN THAT ONLY LOOKS COMPILABLE (#853)

  `docs/TEST_PATTERNS.md` shipped `[NotInParallel("a", "b")]` (CS1729 — TUnit has
  no `params` overload) and `[SkipWhenNotLinux]` (CS0246 — it is `internal` to one
  test project), both in a document titled "Copy-Paste Ready". Neither is visible
  to any rule here: the first is a claim about constructor ARITY in a package, the
  second about a name's visibility, and neither is a `file:line` or a type this
  repository declares.

  Six candidate rules were written far enough to be counted (#853). Every one is
  unreachable from docs.yml or red on day one, so none was added. The two that
  look most attractive are worth the specific numbers, because both are wrong in
  a way that is only visible once measured:

    * TUnit.Core ships a 624 KB `TUnit.Core.xml` that stdlib Python parses fine, so
      a name-existence check looks free. It is not reachable — docs.yml runs six
      `python3 tools/…` lines and never restores, so `~/.nuget/packages` is empty
      there, and no `packages.lock.json` is tracked. And it is not complete: 21 of
      the 47 attribute types it declares carry NO `#ctor` row, including
      `NotInParallelAttribute` itself. A rule built on it would be silent on 45% of
      TUnit's attributes, and silent on the exact one whose arity broke.
    * Checking names against THIS repository instead needs no package, and found
      0 of 62 attribute spellings in the actionable "declared only internal"
      bucket — while 23 of 62 are irreducible false positives, including the real
      TUnit `RunOn` and `ExcludeOn`.

  Also measured, and also not built: requiring a citation on examples that quote
  code (131 of 1028 carry one), banning uncited examples (897 of them, across 105
  documents), fencing the citations non-normative documents already wrote, and
  resolving a source path named on a fence's first line (82 of 116 name files
  that do not exist, because the convention is a template).

  The "widen the prose fence" figure was recorded here as "818 of 2662" when
  #826 wrote it, and it was already wrong when it was written — the same disease
  this section is about, in the section that catalogs the disease. Re-measured on
  dev at 787c777d: 812 of 3353 fail (85 of 511 prose citations, 672 of 2842
  table rows). The conclusion is unchanged and is the reason the fence stays
  opt-in: two thirds of the failures are the XML_DOC_AUDIT rows a dated record
  is allowed to have, and of the 85 prose failures a large share are archived
  sprint notes under docs/.kilo-docs/. Widening would still be red on day one.

  Narrowing it by making the rule accurate instead: see `resolve_citation`.
  Those 85 were 140 before, and the 55 that went away were citations that were
  correct all along.

  Full reasoning and the three conditions that would re-open this:
  docs/adr/ADR-011-doc-example-compile-gate.md. Nothing here is a rule; this
  paragraph is a pointer so the question is not re-derived from scratch.

THE SIXTH SHAPE — A `file:line` IN A TEST'S PROSE (#947)

  `Status: normative` is a self-selector for MARKDOWN, so every rule above is
  structurally unable to see a test file: `file:line` citations live in the prose
  of `tests/**/*.cs` — 236 of them on a7a40335, 235 on bb33437a, the tree having
  moved in between with no test edited — and no rule in this repository had ever
  read one.

  They drift, and the shape of the drift is the reason a cheap version of this
  rule was measured and NOT shipped. Over that population the mechanical fence
  this file already knows how to write — resolves, not past EOF, not blank —
  reports 22 and stays silent on 214. Against the 66 citations that are provably
  wrong today (established by comparing the target file at the commit that wrote
  the citation against HEAD), it catches 4 and misses 62.

  The two figures are on different trees and both are stated with their commit,
  because this file is where `DOC-COUNT-STALE` was written and the discipline
  applies to it first: a measurement that does not say which tree it measured is
  a measurement that will be wrong silently. docs/CITATION_DRIFT_MEASUREMENT.md
  is the full write-up, with every table pinned the same way.

  So the rule is anchored on HISTORY. `git blame` the citing line, read the
  target at that commit and at HEAD, and ask whether line N still names what its
  author meant. That catches the class the cheap fence is blind to by
  construction, including `TokenTrackingRatchet.cs:83` citing `AgentLoop.cs:91`
  for `_tokenTracker` when line 91 is `_providers = providers;`.

  The cost is stated rather than buried: this makes a gate that has to be re-run
  at every substantive edit, because a line inserted above a cited one changes
  what the number means. That is a standing tax, and it is only worth paying
  because the alternative was measured at 4 of 66 rather than assumed.
  See `check_cite_drift`.

  ONE THING THIS RULE GOT WRONG ON ITS FIRST CI RUN, recorded because it is the
  failure mode the rest of this file is about

  It reported 87 findings in a worktree and 21 in CI, with no diagnostic.
  `actions/checkout` defaults to `fetch-depth: 1`; in a depth-1 clone `git blame`
  attributes every line to the shallow boundary commit, so the comparison becomes
  HEAD against HEAD and finds nothing. The mechanical classes never touch history
  and kept working, so the gate stayed GREEN on the one rule that catches the
  class while reporting a smaller, entirely plausible number.

  Hence `repo_is_shallow()`: the gate refuses on a truncated clone
  (TEST-CITE-NO-HISTORY) and says the anchored findings are absent, not zero.
  `docs.yml` sets `fetch-depth: 0`. The first fix is a bug someone corrects; the
  second is what stops the rule from lying while looking healthy.

USAGE

  tools/check-doc-cites.py                      # gate (what docs.yml runs)
  tools/check-doc-cites.py --self-test          # prove the gate still bites
  tools/check-doc-cites.py --verbose            # per-document counts
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from collections import defaultdict
from typing import NamedTuple

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

# The same extension list, as data, so the basename index can be built from it
# instead of from a hardcoded `git ls-files *.cs` that CITATION never agreed
# with (#807). One list, two consumers — the regex is the definition, this is
# what git is asked for. Deriving it from CITATION's own text would be cleverer
# and would also make a typo in the regex silently change what is scanned.
CITABLE_EXT = (
    ".cs", ".json", ".yml", ".yaml", ".csproj", ".slnx",
    ".props", ".targets", ".py", ".axaml", ".xaml", ".editorconfig",
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

# The per-document escape hatch for DOC-COUNT-STALE, and the second rule to
# use this shape. Same discipline as allow-unwired and for the same reason: the
# claim "this total is history, not a claim about these rows" is exactly the
# thing a reader needs to see, and a global list would hide it in a file nobody
# opens. A quoted label is required so the waiver says WHICH figure it is about.
ALLOW_STALE_COUNT = re.compile(
    r"<!--\s*check-doc-cites:\s*allow-stale-count\s+(?P<label>[^>]+?)"
    r"(?:\s+[-—:]\s*(?P<reason>.+?))?\s*-->"
)

# THE RECORD, NOT THE ALLOWANCE (#947)
#
# `allow-unwired` and `allow-stale-count` above excuse a CLAIM that is true of
# the tree (`LiveThing` really is unwired; 2861 really was the sweep's count).
# This one is different in kind and the difference is the whole design: a
# drifted citation is a claim that is FALSE. Writing it down does not make it
# true — it makes the falsity attributable, which is the most a gate can do
# about a known-wrong sentence.
#
# So the rule is a DISJUNCTION, exactly as #937 settled it:
#
#     either the two sides agree              -> no finding
#     or the disagreement is RECORDED, with a reason -> no finding, but the
#                                                     record must still be true
#
# and the second clause is what stops this being #847's rejected shape. #847's
# table of known breakages was a global list with no reason: the guard was green
# and the reason lived nowhere. Here a record is
#
#   * PER SITE, not per file and not global — it names the citing file and the
#     line, so it cannot become a blanket over a file's other citations;
#   * REASON-MANDATORY, with a quoted label, the same discipline as the two
#     waivers above;
#   * SELF-VERIFYING — a record whose site no longer drifts is a FINDING
#     (`TEST-CITE-RECORD-STALE`). This is the clause that matters most, and it
#     is what makes the form non-degenerate: the moment someone repairs the
#     citation, the record that was excusing it goes red until it is deleted.
#     A record that only ever silences can never be deleted, which means it can
#     never be wrong about the present.
#
# The site key is (cited target, cited LINE) — the fact being excused — scoped to
# the file the record sits in. NOT the citing line, and that is deliberate twice
# over. Placement: a record is inserted NEXT TO the citation it qualifies, so
# inserting it shifts the citing line, and a key that named the citing line
# would invalidate itself the moment it was written. Scope: two sentences citing
# the same `File.cs:91` are asserting the same fact about the same line, so one
# record covering both is honest rather than a blanket.
#
# The record is therefore per-FILE, which is the anti-#847 property that
# matters: it cannot excuse a citation in another file, and it cannot excuse a
# different line number in this one.
#
# `now="…"` is the FINGERPRINT and it is what closes the last hole in the form.
# Without it a record silences a SITE, so inserting a line above a recorded
# citation changes the divergence without changing the site, and the record goes
# on excusing a disagreement nobody has looked at since. Quoting what the cited
# line reads today — as `truncate()` renders it, the SAME string the gate prints
# in its finding, so the two can be diffed by eye — means the record describes
# ONE divergence: touch either side of the pair, move the target's line or
# repair the citation, and the fingerprint stops matching, the excuse lapses,
# and the gate goes red on the record itself.
#
# For a site that does not resolve there is no line to quote, so the
# fingerprint is the resolution verdict (`unresolved`, `ambiguous:2`). Same
# rule, and it behaves correctly in the interesting direction: the record goes
# stale the moment the file appears.
RECORD_DRIFT = re.compile(
    r"(?:<!--)?\s*check-doc-cites:\s*record-drift\s+"
    r"(?P<target>[A-Za-z0-9_][A-Za-z0-9_./\-]*\.[A-Za-z]+):"
    r"(?P<line>\d+)\s+"
    r"now=\"(?P<now>[^\"]*)\"\s*"
    r"(?:\[(?P<why>[^\]]+)\]\s*)?(?:-->)?"
)

SiteKey = tuple[str, int]  # (target, cited line), within the citing file


def drift_records(text: str) -> tuple[dict[SiteKey, tuple[int, str, str]], list[tuple[int, int]]]:
    """(SiteKey -> (where, `now` fingerprint, reason), spans to mask).

    A record with no quoted reason is NOT honoured and is reported by the caller
    as `TEST-CITE-RECORD-NOREASON`, for the reason the two waivers above are
    reason-mandatory: an unlabelled one is a mute button for every future
    finding on that line.

    The spans matter as much as the keys. A record NAMES a `File.cs:91`, so
    without masking it the record is itself a citation, the population grows by
    one per record (235 -> 322 the first time this ran), and the rule ends up
    checking whether its own bookkeeping is true about the tree. A gate that
    counts its own annotations is a gate whose floor means nothing.
    """
    out: dict[SiteKey, tuple[int, str, str]] = {}
    spans: list[tuple[int, int]] = []
    for m in RECORD_DRIFT.finditer(text):
        where = text.count("\n", 0, m.start()) + 1
        out[(m.group("target"), int(m.group("line")))] = (
            where,
            m.group("now"),
            (m.group("why") or "").strip(),
        )
        end = text.find("\n", m.end())
        spans.append((m.start(), n if (n := (len(text) if end < 0 else end)) else m.end()))
    return out, spans


def mask_spans(text: str, spans: list[tuple[int, int]]) -> str:
    """`text` with each span blanked to spaces, newlines preserved.

    Same contract as `strip_comments_and_literals`: offsets survive, so a line
    number computed on the result is the line number in the original.
    """
    if not spans:
        return text
    out = list(text)
    for start, end in spans:
        for i in range(start, min(end, len(out))):
            if out[i] != "\n":
                out[i] = " "
    return "".join(out)

# A stated total for a table the document itself contains — "Total members
# audited: 2861", "audited 2861" — in prose or a bullet, never in a table cell.
#
# Anchored to a line that cannot be a `| a | b |` row, so the Priority Breakdown
# table's own `| HIGH | 1111 | ...` cells are out of reach. Without that anchor
# the rule would compare a table's column total against the table it belongs to
# and report a correct table as wrong.
#
# Word-anchored rather than "any number on any line": a document that says
# "672 of 2842" has two figures on one line and neither is a total, and a rule
# that guessed between them is a rule that cries wolf on the first honest
# sentence a sweep writes.
COUNT_TOTAL = re.compile(
    r"(?im)^[ \t>*-]*[^\n|]{0,90}?"
    r"\b(?:total\s+(?:members|rows|files|types|items|tests|projects|cases)"
    r"|(?:members|rows|files|types|tests|projects)\s+(?:audited|total)"
    r"|audited)\b[^\n\d]{0,40}?(?P<n>\d[\d,]*)"
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
#
# The modifier list is captured as its own group so DOC-TYPE-UNWIRED can branch
# on the visibility modifier, which used to be matched and then thrown away.
# That is how the rule came to ask a `private` nested class for a SECOND FILE — a
# witness no legal C# program can supply (#905).
#
# The group is NON-CAPTURING and read by re-matching the matched text: making it
# capturing renumbers the groups, and `m.group(1)` is the type name everywhere in
# this file. A named group there is a silent, tree-wide break — it was measured
# doing exactly that (18 sample README findings, the gate green on dev only
# because it never asked).
#
# Nesting is deliberately NOT the discriminator. Measured on this tree: 287
# declarations are nested, and 129 of those are `public` and 4 `internal`, all
# reachable from another file by construction. Visibility decides, not nesting.
DECLARATION = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*(?:\r?\n[ \t]*)?)*"
    r"(?:(?:public|internal|protected|private|sealed|abstract|static|partial|"
    r"readonly|ref|new|unsafe|file)[ \t]+)*"
    r"(?:class|interface|enum|struct|record)[ \t]+"
    r"(?:(?:class|struct)[ \t]+)?"
    r"([A-Z][A-Za-z0-9_]*)",
    re.M,
)

# The visibility/static modifiers, read off the text DECLARATION already matched.
DECL_MODIFIERS = frozenset(
    {"public", "internal", "protected", "private", "static", "file"}
)


def decl_modifiers(matched: str) -> frozenset[str]:
    """The modifier words in a DECLARATION match, up to the type keyword."""
    head = re.split(r"\b(?:class|interface|enum|struct|record)\b", matched, maxsplit=1)[0]
    return frozenset(re.findall(r"[A-Za-z]+", head)) & DECL_MODIFIERS

# Comments and literals are blanked by ONE linear scanner rather than by regex.
#
# Two reasons, both measured on this tree:
#
#   A regex stripper cannot see that `//` inside a string literal is not a
#   comment. samples/plugins/Harbor.Plugin.WebSearch carries
#   `"Harbor/0.2 (https://harbor.sh)"`, and the old two-regex strip_comments ate
#   `//harbor.sh)"` as a line comment — leaving one unbalanced quote that made
#   every subsequent quote on the file open a literal, so a declared-and-shipped
#   `WebSearchTool` stopped being seen at all. That was latent: nothing needed
#   balanced text before this rule. It needs it now.
#
#   A regex alternation over verbatim/interpolated/plain literals backtracks
#   badly enough to blow the wall clock (>900s, no output). A character loop is
#   linear.
#
# The output is the SAME LENGTH as the input with the skipped spans blanked, so
# every offset in the original still addresses the same character — which is what
# lets the brace scoping in block_span work on this text directly.
def strip_comments_and_literals(text: str) -> str:
    out: list[str] = []
    i, n = 0, len(text)

    def blank(end: int) -> None:
        out.append("".join(ch if ch == "\n" else " " for ch in text[i:end]))
        return None

    while i < n:
        ch = text[i]
        two = text[i:i + 2]
        if two == "//":
            end = text.find("\n", i)
            end = n if end < 0 else end
            blank(end)
            i = end
        elif two == "/*":
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(end)
            i = end
        elif ch == "@" and text[i + 1:i + 2] == '"':
            # Verbatim string: `""` is the escape, and a backslash is literal.
            end = i + 2
            while end < n:
                if text[end:end + 2] == '""':
                    end += 2
                    continue
                if text[end] == '"':
                    end += 1
                    break
                end += 1
            blank(end)
            i = end
        elif text.startswith('"""', i):
            end = text.find('"""', i + 3)
            end = n if end < 0 else end + 3
            blank(end)
            i = end
        elif ch == '"':
            end = i + 1
            while end < n:
                if text[end] == "\\":
                    end += 2
                    continue
                if text[end] == '"':
                    end += 1
                    break
                end += 1
            blank(end)
            i = end
        elif ch == "'":
            # Char literal. A bare apostrophe that opens nothing (a `'` in code
            # that is not a literal cannot occur in valid C#) ends at the newline
            # rather than running away.
            end = i + 1
            while end < n and text[end] != "'" and text[end] != "\n":
                if text[end] == "\\":
                    end += 2
                    continue
                end += 1
            if end < n and text[end] == "'":
                end += 1
            blank(end)
            i = end
        else:
            out.append(ch)
            i += 1
    return "".join(out)


def block_span(text: str, start: int) -> tuple[int, int] | None:
    """Half-open span of the `{ … }` block that opens after `start`.

    `None` when the declaration has no body brace — a `record Foo(int X);` or a
    positional enum member. The caller falls back to the declaration LINE in that
    case, which is the weaker but still correct witness: a one-line declaration
    cannot contain a use of itself outside its own header.
    """
    open_brace = text.find("{", start)
    if open_brace < 0:
        return None
    depth = 0
    for pos in range(open_brace, len(text)):
        if text[pos] == "{":
            depth += 1
        elif text[pos] == "}":
            depth -= 1
            if depth == 0:
                return open_brace, pos
    return None


# ---------------------------------------------------------------------------
# THE SIXTH SHAPE — A `file:line` IN A TEST'S PROSE (#947)
# ---------------------------------------------------------------------------
#
# Every rule above reads MARKDOWN, and this one reads `tests/**/*.cs`. That is
# not a new kind of question: the fence already asks "is this number inside
# that file", and this asks the same thing about the population the fence
# cannot see. #922 scoped the prose fence to documents carrying
# `Status: normative`, which is a SELF-SELECTOR for markdown and, by
# construction, leaves every test file outside it. 245 `.cs:NNN` citations live
# in 68 test files, and no rule in this repo had ever read one.
#
# WHY THE NAIVE VERSION OF THIS RULE IS NOT ENOUGH — measured, not asserted
#
# The obvious rule is the mechanical one this file already knows how to write:
# the cited line must not be blank, must not be past EOF, the file must
# resolve. Over the 236 prose citations in tests/*.cs that reports 22 and stays
# silent on 214.
#
# And 22 is not a finding count. It is a FLOOR, and it is a floor that misses
# the class this issue is about. Measured against the 66 citations that are
# PROVABLY wrong today (see `check_cite_drift` below for how that is established
# without a human reading 235 claims):
#
#     mechanically decidable (blank / EOF / unresolved)   22
#     PROVABLY wrong today                               66
#     the mechanical rule CATCHES                         4
#     the mechanical rule MISSES                         62
#
# Four of sixty-six. The 62 are citations whose cited line is real, has code on
# it, and is the wrong code — `TokenTrackingRatchet.cs:83` cites
# `AgentLoop.cs:91` for `_tokenTracker`, and line 91 today is
# `_providers = providers;`. A "points at a line with code" fence is green on
# that, which is why it is not the rule.
#
# SO THE RULE IS ANCHORED ON HISTORY, NOT ON THE CURRENT LINE
#
# `git blame` on the citing line gives the commit that WROTE the citation. Read
# the target file at that commit and at HEAD, and compare what line N holds.
# If the author's N named X and today's N names Y, the citation is wrong — no
# semantics, no judgement, no table of known breakages. This is the only form
# of the rule that catches the class the issue calls most expensive.
#
# The anchor is per citing LINE, not per citing FILE. Anchoring on the file's
# last commit undercounts, and not slightly: `e46e0048` re-touched
# TokenTrackingRatchet.cs a day after `99b73bfe` wrote the citation, so a
# file-anchored baseline reads the already-stale state as the reference and
# reports the citation as correct forever.
#
# WHAT IT COSTS, STATED PLAINLY
#
# This rule makes a gate that must be re-run at every substantive edit. A guard
# built this way is not a one-time fix; it is a standing tax on whoever inserts
# a line above a cited one. That is the honest price and it is why the cheap
# mechanical fence was measured rather than shipped — but the price is only
# worth paying if the alternative is measured too, and it was: 4/64.

# Tests are the subject. `contrib/` is not scanned at all — it is unmaintained,
# and a citation inside it is not a claim this repo makes about itself.
TEST_CITATION_SCOPE = ("tests/",)


def comments_only(text: str) -> str:
    """`text` with everything OUTSIDE comments blanked, offsets preserved.

    The inverse of `strip_comments_and_literals`, which keeps code and drops
    prose. A `file:line` in a test is almost always in a comment, so this keeps
    the comments and drops the code — and, critically, drops STRING LITERALS
    too. A test that builds a synthetic compiler diagnostic as data
    (`PanelExtractorsTests.cs:358` writes `"src/a.cs:12 CS0246: type not
    found"`) is not making a claim about this repository, and a rule that
    counted it would be reporting a test's fixture back at the test.
    """
    out: list[str] = []
    i, n = 0, len(text)

    def keep(end: int) -> None:
        out.append(text[i:end])
        return None

    def blank(end: int) -> None:
        out.append("".join(ch if ch == "\n" else " " for ch in text[i:end]))
        return None

    while i < n:
        ch = text[i]
        two = text[i:i + 2]
        if two == "//":
            end = text.find("\n", i)
            end = n if end < 0 else end
            keep(end)
            i = end
        elif two == "/*":
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            keep(end)
            i = end
        elif ch == "@" and text[i + 1:i + 2] == '"':
            end = i + 2
            while end < n:
                if text[end:end + 2] == '""':
                    end += 2
                    continue
                if text[end] == '"':
                    end += 1
                    break
                end += 1
            blank(end)
            i = end
        elif text.startswith('"""', i):
            end = text.find('"""', i + 3)
            end = n if end < 0 else end + 3
            blank(end)
            i = end
        elif ch == '"':
            end = i + 1
            while end < n:
                if text[end] == "\\":
                    end += 2
                    continue
                if text[end] == '"':
                    end += 1
                    break
                end += 1
            blank(end)
            i = end
        elif ch == "'":
            end = i + 1
            while end < n and text[end] != "'" and text[end] != "\n":
                if text[end] == "\\":
                    end += 2
                    continue
                end += 1
            if end < n and text[end] == "'":
                end += 1
            blank(end)
            i = end
        else:
            blank(i + 1)
            i += 1
    return "".join(out)


def _blame_map(repo: str, rel: str, cache: dict[str, dict[int, str]]) -> dict[int, str]:
    """citing line number -> sha of the commit that wrote that line."""
    if rel not in cache:
        m: dict[int, str] = {}
        proc = subprocess.run(
            ["git", "blame", "--line-porcelain", "--", rel],
            cwd=repo, capture_output=True, text=True, env=md_gate.git_env(),
        )
        sha = lineno = None
        for raw in proc.stdout.splitlines():
            head = raw.split(" ")
            if len(head) >= 3 and len(head[0]) == 40 and head[1].isdigit() and head[2].isdigit():
                sha, lineno = head[0], int(head[2])
            elif raw.startswith("\t") and sha is not None and lineno is not None:
                m[lineno] = sha
        cache[rel] = m
    return cache[rel]


def _lines_at(repo: str, rev: str, rel: str, cache: dict[tuple[str, str], list[str]]) -> list[str] | None:
    """Stripped lines of `rel` at `rev`, or None when it did not exist there."""
    key = (rev, rel)
    if key not in cache:
        proc = subprocess.run(
            ["git", "show", f"{rev}:{rel}"], cwd=repo, capture_output=True, text=True,
            env=md_gate.git_env(),
        )
        # A path absent at that rev makes git exit non-zero with output on
        # stderr. That is the "the file was added later" case, not drift, and it
        # must not read as an empty file whose every line is blank.
        cache[key] = None if proc.returncode != 0 else [l.strip() for l in proc.stdout.splitlines()]
    return cache[key]


def repo_is_shallow(repo: str) -> bool:
    """True when the clone has truncated history.

    This is not a hypothetical. The first CI run of TEST-CITE-DRIFT did exactly
    this: `actions/checkout` defaults to `fetch-depth: 1`, `git blame` then
    attributes every line to the shallow boundary commit, and the rule's
    comparison becomes HEAD-versus-HEAD. Measured on that run: 21 findings
    instead of 87, with no diagnostic — the mechanical classes still worked, so
    the build stayed green while the only rule that catches the class went
    blind.

    A rule that cannot see is worse than no rule, because nothing turns red. So
    the gate refuses to run the anchored comparison on a truncated clone and
    says so, instead of reporting the smaller number as if it were the whole
    truth. `docs.yml` sets `fetch-depth: 0` for the same reason.
    """
    proc = subprocess.run(
        ["git", "rev-parse", "--is-shallow-repository"],
        cwd=repo, capture_output=True, text=True, env=md_gate.git_env(),
    )
    return proc.returncode == 0 and proc.stdout.strip() == "true"


def check_cite_drift(
    repo: str, by_base: dict[str, list[str]]
) -> tuple[int, dict[str, list[tuple[str, str, int]]]]:
    """Return (citations seen, {citing file: [(code, message, line), ...]}).

    A DISJUNCTION, per citation site (#947, shaped as #937 settled it):

        the two sides agree                              -> silent
        the two sides disagree AND the site is RECORDED   -> silent
        the two sides disagree and no record              -> TEST-CITE-DRIFT
        a record exists but its site no longer drifts      -> TEST-CITE-RECORD-STALE
        a record exists with no quoted reason              -> TEST-CITE-RECORD-NOREASON

    The second clause is what makes the form non-degenerate. A record is not a
    mute button: it names one citing line, it must say why in a quoted label,
    and the moment the citation is repaired the record goes RED until it is
    deleted. So a record can always be deleted and therefore can never be
    silently wrong about the present.

    Mechanical facts come first (unresolved / past EOF / blank), because they
    are free and they are what the rule would have been without the anchor.

    Keyed by the CITING file, not the target: a red run has to point at the
    line someone has to edit, and the whole defect is that a reader following
    a stale number lands somewhere that is not the prose.
    """
    seen = 0
    problems: dict[str, list[tuple[str, str, int]]] = defaultdict(list)
    # Reported once, against no citing file, because it is a statement about the
    # REPOSITORY and not about any sentence in it.
    if repo_is_shallow(repo):
        problems["(repository)"].append(
            (
                "TEST-CITE-NO-HISTORY",
                "the clone is shallow, so TEST-CITE-DRIFT cannot compare a "
                "citation against the commit that wrote it and will report "
                "nothing. Re-run with full history (actions/checkout: "
                "fetch-depth: 0). The mechanical TEST-CITE-* results below are "
                "still valid; the anchored ones are absent, not zero.",
                0,
            )
        )
    blame_cache: dict[str, dict[int, str]] = {}
    hist_cache: dict[tuple[str, str], list[str] | None] = {}
    head_cache: dict[str, list[str]] = {}

    for rel in md_gate.tracked_cs(repo):
        if not rel.startswith(TEST_CITATION_SCOPE):
            continue
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                body = fh.read()
        except OSError:
            continue
        prose = comments_only(body)
        if ":" not in prose:
            continue
        blames = _blame_map(repo, rel, blame_cache)
        records, record_spans = drift_records(prose)
        # The record's own `File.cs:91` is not a claim about the tree — it is
        # the bookkeeping that records one. Masked before CITATION runs, so the
        # population is the citations a human wrote.
        prose = mask_spans(prose, record_spans)
        # Every record this file carries, so the stale check at the end can tell
        # "excused a real finding" from "excusing nothing at all".
        used: set[tuple[str, int]] = set()

        for m in CITATION.finditer(prose):
            target, spec = m.group(1), m.group(2)
            doc_line = prose.count("\n", 0, m.start()) + 1
            seen += 1
            full, kind = resolve_citation(target, repo, by_base, rel)
            ranges = parse_ranges(spec)

            if full is None:
                code = "TEST-CITE-AMBIGUOUS" if kind == "ambiguous" else "TEST-CITE-MISSING"
                detail = (
                    f"{len(by_base.get(target, []))} tracked files share this name"
                    if kind == "ambiguous"
                    else "no such file in the tree"
                )
                # An unresolvable site has no line to quote, so the fingerprint
                # is the verdict itself. It goes stale the moment the file
                # appears, which is the direction that matters.
                verdict = f"ambiguous:{len(by_base.get(target, []))}" if kind == "ambiguous" else "unresolved"
                excused = False
                for start, _ in ranges:
                    rec = records.get((target, start))
                    if rec and rec[2] and rec[1] == verdict:
                        used.add((target, start))
                        excused = True
                if not excused:
                    problems[rel].append((code, f"{target}:{spec} — {detail}", doc_line))
                continue
            rel_target = os.path.relpath(full, repo)
            if rel_target not in head_cache:
                try:
                    with open(full, encoding="utf-8", errors="replace") as fh:
                        head_cache[rel_target] = [l.strip() for l in fh.read().splitlines()]
                except OSError:
                    head_cache[rel_target] = []
            head = head_cache[rel_target]
            for start, end in ranges:
                if start > len(head):
                    problems[rel].append(
                        ("TEST-CITE-EOF", f"{target}:{start} — file has {len(head)} lines", doc_line)
                    )
                    continue
                if head[start - 1] == "":
                    key = (target, start, doc_line)
                    rec = records.get((target, start))
                    if rec and rec[2] and rec[1] == "":
                        used.add((target, start))
                    else:
                        problems[rel].append(
                            ("TEST-CITE-BLANK", f"{target}:{start} — the line is blank", doc_line)
                        )
                    continue

            # The anchored comparison — the two sides. Only for a citation whose
            # author is known and whose target existed when they wrote it.
            sha = blames.get(doc_line)
            # `git blame` attributes an uncommitted line to the null oid, all
            # zeros. That is not an author, so there is no second side to read.
            if not sha or set(sha) == {"0"}:
                # One-sided: without an author there is no second side to read,
                # so the file side is never compared against anything. Reported
                # rather than skipped, because a silent `continue` here is a
                # hole shaped exactly like the one this rule closes.
                problems[rel].append(
                    ("TEST-CITE-UNANCHORED",
                     f"{target}:{spec} — no commit is attributed to this line, so "
                     f"there is nothing to compare it against", doc_line)
                )
                continue
            was = _lines_at(repo, sha, rel_target, hist_cache)
            if was is None:
                problems[rel].append(
                    ("TEST-CITE-UNANCHORED",
                     f"{target}:{spec} — the target did not exist when {sha[:8]} "
                     f"wrote this citation, so there is no prior side", doc_line)
                )
                continue
            for start, _end in ranges:
                if start > len(was):
                    problems[rel].append(
                        ("TEST-CITE-UNANCHORED",
                         f"{target}:{start} — past the end of the file as {sha[:8]} "
                         f"left it ({len(was)} lines); the citation named a line "
                         f"that did not exist when it was written", doc_line)
                    )
                    continue
                before = was[start - 1]
                after = head[start - 1]
                if before == after:
                    continue
                rec = records.get((target, start))
                # Compared against `truncate(after)`, not the raw line: the
                # record holds the same rendering the gate prints, so a reader
                # can diff the two by eye instead of trusting that they match.
                if rec and rec[2] and rec[1] == fingerprint(after):
                    used.add((target, start))
                    continue
                problems[rel].append(
                    (
                        "TEST-CITE-DRIFT",
                        f"{target}:{start} — {sha[:8]} wrote it over "
                        f"`{truncate(before)}`; that line now reads "
                        f"`{fingerprint(after)}`",
                        doc_line,
                    )
                )

        # A record that excused nothing. This is the clause that stops the form
        # from decaying into #847's table: a record whose site agrees again has
        # to be DELETED, and until it is, the gate is red on the record itself.
        for (rtarget, rline), (rwhere, rnow, rwhy) in sorted(
            records.items(), key=lambda kv: kv[1][0]
        ):
            if (rtarget, rline) in used:
                continue
            if not rwhy:
                problems[rel].append(
                    ("TEST-CITE-RECORD-NOREASON",
                     f"{rtarget}:{rline} — this record carries no quoted reason, so "
                     f"it cannot say which disagreement it is excusing", rwhere)
                )
                continue
            problems[rel].append(
                ("TEST-CITE-RECORD-STALE",
                 f"{rtarget}:{rline} — recorded with now=\"{rnow}\" [{rwhy}], but "
                 f"the citation and the file no longer disagree that way. Touching "
                 f"either side of the pair retires the record; delete it and, if "
                 f"the divergence is real, write a new one.", rwhere)
            )
    return seen, problems


def truncate(s: str, width: int = 58) -> str:
    """One-line, width-capped rendering of a line of code for a message."""
    s = " ".join(s.split())
    return s if len(s) <= width else s[: width - 1] + "…"


def fingerprint(s: str) -> str:
    """The record's `now=` value for a cited line holding `s`.

    `truncate`, then `"` folded to `'`. C# is full of double quotes — an
    interpolated string on the very line a citation names is ordinary — and the
    record quotes its fingerprint in double quotes, so an unfolded value would
    end the field at the first inner quote and silently parse as a DIFFERENT,
    shorter fingerprint. That is the worst possible failure for a field whose
    whole job is to be exact: it would mismatch forever and the finding could
    never be recorded.

    Defined once and used by BOTH the comparison and the reported message, so
    the string a reader copies into a record is the string the gate compares
    against, by construction rather than by agreement.
    """
    return truncate(s).replace('"', "'")


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


class Decl(NamedTuple):
    """One type declaration, with the facts DOC-TYPE-UNWIRED branches on.

    `confined` is a VISIBILITY property, not a nesting one, and the difference is
    the whole of #905: a `private` nested class is unreachable from another file,
    but a `public` nested class is reachable by construction, and this tree has
    129 of those and 4 `internal` ones. Deciding by nesting would have exempted
    the first shape and blinded the second.
    """

    rel: str
    start: int
    line_end: int
    is_static: bool
    confined: bool


def index_sources(repo: str) -> tuple[list[str], dict[str, list[str]], set[str]]:
    """Tracked citable files, basename -> candidates, and the production subset.

    `contrib/` is excluded from basename resolution: it is unmaintained, not
    compiled by CI, and a doc citing `AppStore.cs:5` must not be satisfied by a
    file that nothing builds.

    `.axaml` is indexed alongside `.cs` since #905: `x:Class` is a construction
    site, and a type Avalonia builds that way has no `.cs` reference to it
    anywhere. Globbing only `*.cs` here is what made the first XAML self-test
    fixture fail — the fixture was tracked by git and still invisible, because
    nothing ever asked git for it.

    The BASENAME index covers every extension CITATION accepts, not just
    `.cs`/`.axaml` (#807). CITATION matches `.csproj`, `.props`, `.targets`,
    `.slnx` and `.json` as first-class citation targets and `check_citations`
    even fences line numbers inside `.csproj` and `.slnx` — but a bare
    `Harbor.App.Cli.csproj:145-224` could never resolve, because the index that
    answered the question was built from `*.cs`. The rule reported
    DOC-CITE-MISSING for a file sitting in the tree:

        apps/Harbor.App.Cli/README.md:197  Harbor.App.Cli.csproj:145-224
        apps/Harbor.App.Cli/PLAN.md:14     Harbor.App.Cli.csproj:216-224

    Sixteen live claims across the tree were that shape. This is #905's lesson
    one level up: the rule both works and lies, and here it lied by being
    unable to see. `production` stays `.cs` under `src/`+`apps/` — that set
    feeds the type rule, which must not read a `.csproj` looking for a
    declaration.
    """
    # ONE `git ls-files`, no pathspec, filtered here. The pathspec form
    # (`git ls-files '*.cs' '*.props' …`) looks tidier and is wrong in a way
    # that fails silently: a pathspec of `.cs` is a LITERAL path named `.cs`, not
    # a glob, so the extension list has to be spelled `*<ext>` or the whole index
    # comes back holding one entry. It did, and the gate reported 112 citations
    # as missing in files that are in the tree. The unfiltered list is ~2.9k
    # paths, so filtering in Python costs nothing and cannot be misread.
    proc = subprocess.run(
        ["git", "ls-files"],
        cwd=repo,
        capture_output=True,
        text=True,
        check=True,
        env=md_gate.git_env(),
    )
    tracked = sorted(f for f in proc.stdout.split() if f)

    # The type rule reads C# and XAML only — never a .csproj looking for a
    # declaration.
    files = [f for f in tracked if f.endswith((".cs", ".axaml"))]

    by_base: dict[str, list[str]] = defaultdict(list)
    for f in tracked:
        if os.path.splitext(f)[1] in CITABLE_EXT:
            by_base[os.path.basename(f)].append(f)
    production = [f for f in files if f.startswith(("src/", "apps/"))]
    return files, dict(by_base), set(production)


# Where a project lives, for the project-relative form. A sweep or a README
# writes `Harbor.TestKit/Fakes.cs:13` or `Input/ITerminalModeController.cs:9`
# — the project or the folder, not the path from the root — and a rule that
# only tries the root form calls a live file missing.
#
# Ordered, not a set: the first hit wins and `src/` is tried before `tests/`,
# so the answer is deterministic and reviewable. `tools/` is here because the
# gates are cited by path from the repo root more often than not.
PROJECT_ROOTS = ("src/", "apps/", "tests/", "tools/")


def resolve_citation(
    target: str, repo: str, by_base: dict[str, list[str]], doc_rel: str | None = None
) -> tuple[str | None, str]:
    """Return (absolute path, kind). `kind` is 'path', 'basename' or 'miss'.

    A slash-bearing target is tried, in order:

      1. repo-relative, as written
      2. relative to the citing document's own directory — a README in
         `src/Harbor.Tui.CellForge.Engine/` writing `Input/MouseRouter.cs:25`
         means the file beside it, which is how a human reads it
      3. each of PROJECT_ROOTS, for the project-relative form

    This is the resolution `check_table_citations` already did for rows and
    `resolve_citation` did NOT do for prose, so the two shapes disagreed about
    the same string: a path in a table resolved, the identical path in a
    paragraph was DOC-CITE-MISSING. Both shapes now call this one function, so
    the table rule is exactly as capable as the prose rule instead of strictly
    more so.
    """
    if "/" in target:
        tries = [target]
        if doc_rel:
            tries.append(os.path.normpath(os.path.join(os.path.dirname(doc_rel), target)))
        tries.extend(root + target for root in PROJECT_ROOTS)
        for cand in tries:
            full = os.path.join(repo, cand)
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
    rel: str,
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
        full, kind = resolve_citation(target, repo, by_base, rel)
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
        if os.path.splitext(full)[1] not in CITABLE_EXT:
            # CITATION matched it, so by construction the extension is in
            # CITABLE_EXT; this is unreachable in practice and stays only as the
            # assertion that the fence and the matcher cannot drift apart again.
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
    declared_in: dict[str, list[Decl]],
    production_text: dict[str, str],
    xaml_text: dict[str, str],
    allowed: set[str],
) -> tuple[int, list[tuple[str, str, int]]]:
    """Return (name count, [(code, message, doc line), ...]).

    A backticked name that IS a type declared under src/ or apps/ must be
    REFERENCED by something that can reach it. Comments and string literals are
    stripped, so neither a `<see cref="…"/>` nor a `"… is read-only"` message
    can make a dead type look wired.

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
        unreached, why = unreachable(name, decls, production_text, xaml_text)
        if unreached:
            problems.append(
                (
                    "DOC-TYPE-UNWIRED",
                    f"`{name}` is declared in {', '.join(sorted({d.rel for d in decls}))} "
                    f"and {why}",
                    text.count("\n", 0, m.start()) + 1,
                )
            )
    return len(seen), problems


def unreachable(
    name: str,
    decls: list[Decl],
    production_text: dict[str, str],
    xaml_text: dict[str, str],
) -> tuple[bool, str]:
    """Return (is_unreachable, why). The three shapes are separate questions.

    Before #905 this was one predicate — "a DIFFERENT production file names it" —
    applied to every declaration. That predicate is unsatisfiable for two of the
    three shapes in this repo, and the unsatisfiability was invisible: enabling
    the rule on docs/PATTERNS.md produced 7 hits, 6 of them false, and every one
    of the six is a completely ordinary shipped declaration.

      STATIC    C# forbids `new` on a static class. "Nothing constructs it" is
                vacuous, so the rule must not ask: the members are reached
                through the type, not through an instance of it. 285 static
                declarations in this tree, `TuiModule` among them.

      CONFINED  `private` / `protected` / `file`. Reachable ONLY from inside its
                own file, so the second-file witness cannot exist in any legal
                C# program. The witness is the declaring file itself with the
                declaration's own block removed — which is the real question:
                does anything in that file use it? All 223 confined declarations
                with an own-file use answer yes; none is dead.

      TOP-LEVEL public/internal at namespace scope. Unchanged, and deliberately
                NOT widened to "its own file mentions it": a same-file mention
                as a property type or a constructor parameter is not
                construction. `ModelEntryViewModel` is named four times in
                SharedDataModels.cs and constructed by nobody — that is a live
                finding (#788, ADR-010 §4.5), and widening would have deleted it.

    XAML counts as a witness for the top-level shape: `x:Class="…:ChatView"` is a
    construction performed by the designer, not by C#, and 44 declarations are
    reachable only that way. Without it the rule reports every Avalonia view in
    the repo as dead.
    """
    if all(d.is_static for d in decls):
        return False, ""

    pattern = re.compile(rf"\b{re.escape(name)}\b")

    if all(d.confined for d in decls):
        for decl in decls:
            body = production_text[decl.rel]
            # The declaration line AND the body are both "the declaration": the
            # header carries the name too, so blanking only the block left the
            # name sitting in the text and every confined type read as wired.
            span = block_span(body, decl.start)
            high = span[1] if span else decl.line_end
            low = decl.start
            outside = body[:low] + " " * (high - low) + body[high:]
            if pattern.search(outside):
                return False, ""
        return True, (
            "nothing outside its own declaration references it. It is confined "
            "to that file, so its own file is the only place a use can be"
        )

    declaring = {d.rel for d in decls}
    for rel, body in production_text.items():
        if rel not in declaring and pattern.search(body):
            return False, ""
    for rel, body in xaml_text.items():
        if pattern.search(body):
            return False, ""
    return True, (
        "no other file under src/ or apps/, and no .axaml, references it — "
        "nothing constructs it"
    )


def check_stated_count(
    text: str, rows: int
) -> list[tuple[str, str, int]]:
    """Does the document's own headline total equal the rows it ships?

    The class #902 called out and no `file:line` fence can reach: every rule in
    this script asks whether a NUMBER is inside a FILE. None of them can ask
    whether a number agrees with the document's own contents, because the rows
    are the thing being counted. A fence proves `CompactionService.cs:209` is a
    line; it says nothing about whether "2861 members" is how many members the
    document lists.

    So the drift is invisible to all of it. Measured on this tree, the audit's
    Summary says 2861 and the document holds 2842 rows — 19 short, because two
    commits deleted documented rows and left the count:

        #732  a917e308  14 rows (9 HIGH, 5 MED) — the deleted Reducers layer
        #749  c2eba3fd   5 rows (4 HIGH, 1 MED) — RecentItemsService moved

    13 + 6 = 19, and every one is a row that HAD an XML doc, which is why the
    per-priority `With Doc` column is short by exactly 13 and 6 while every
    `Without Doc` cell is still exact. The arithmetic is a fingerprint, not a
    coincidence, and it is why a summary that "looks fine" is the failure mode.

    This is deliberately NOT a rule about whether the count is TRUE. A dated
    record's count may be a true statement about the sweep it recorded and a
    false statement about the file it is in — that is what a record IS, and
    #826 already settled that for this document. The rule asks one question:
    does the document admit the difference?

        <!-- check-doc-cites: allow-stale-count "Total members audited" — why -->

    Without that line the rule reports the gap, because an undeclared gap is the
    defect; a declared one is a sentence a reader can weigh. Same discipline as
    allow-unwired: per-document, reason-mandatory, no global list, and it shows
    up in the diff next to the sentence it qualifies.
    """
    if rows == 0:
        return []
    m = COUNT_TOTAL.search(text)
    if not m:
        return []
    claimed = int(m.group("n").replace(",", ""))
    if claimed == rows:
        return []
    if ALLOW_STALE_COUNT.search(text):
        return []
    line_no = text.count("\n", 0, m.start()) + 1
    return [
        (
            "DOC-COUNT-STALE",
            f"this document states {claimed} but ships {rows} "
            f"`| path | line |` row(s) — a gap of {claimed - rows}. A `file:line` "
            f"fence cannot see this: every other rule here asks whether a number "
            f"is inside a FILE, and this one asks whether a number agrees with "
            f"the rows next to it. If {claimed} is a true figure for the sweep "
            f"this document records and the rows were edited afterwards, say so "
            f"on a line in this document: "
            f'<!-- check-doc-cites: allow-stale-count "Total members audited" '
            f"— which count is historical, and what moved -->",
            line_no,
        )
    ]


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
    text: str, rel: str, repo: str, by_base: dict[str, list[str]], cache: dict[str, int]
) -> tuple[int, list[tuple[str, str, int]]]:
    """Fence the table rows of a NORMATIVE document.

    Only normative: for a dated record these numbers are the snapshot, and
    policing them would be the gate demanding a history be corrected. Path
    resolution is `resolve_citation`, shared with the prose rule: this function
    used to carry its own three-candidate loop, so the two shapes disagreed
    about the same string — a table row could resolve where the identical prose
    citation was DOC-CITE-MISSING. One resolver, so the table rule is exactly as
    capable as the prose rule and not strictly more so (#807).
    """
    found = 0
    problems: list[tuple[str, str, int]] = []
    for m in TABLE_CITATION.finditer(text):
        target, spec = m.group("target"), m.group("spec")
        line_no = text.count("\n", 0, m.start()) + 1
        full, _kind = resolve_citation(target, repo, by_base, rel)
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
        # Documents whose stated total was compared against their own rows
        # (#807). Counted separately from table_docs because the question is
        # asked of every document that has rows, declared or not.
        self.count_docs = 0
        self.api_names = 0
        self.api_docs = 0
        # `file:line` citations inside tests/**/*.cs prose (#947). A separate
        # population from `citations`, which counts markdown only, because the
        # two have nothing in common but the regex: different files, different
        # authors, and — for the drift rule — a different question.
        self.test_cites = 0
        self.test_cite_files = 0
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

    declared_in: dict[str, list[Decl]] = defaultdict(list)
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
                body = fh.read()
        except OSError:
            continue
        if rel.endswith(".axaml"):
            # A markup file declares no C# type; it is a WITNESS, read below.
            continue
        # Comments and literals both go: the type rule's witness must be a USE,
        # and `<see cref="…"/>` or a `"… is read-only"` message names a type
        # without constructing it. Offsets are preserved, so `Decl.start`
        # addresses the same character in `code` that the brace scan walks.
        code = strip_comments_and_literals(body)
        names = {m.group(1) for m in DECLARATION.finditer(code)}
        declared_anywhere |= names
        if rel in production:
            production_text[rel] = code
            for m in DECLARATION.finditer(code):
                mods = decl_modifiers(m.group(0))
                line_end = code.find("\n", m.start())
                declared_in[m.group(1)].append(
                    Decl(
                        rel=rel,
                        start=m.start(),
                        line_end=len(code) if line_end < 0 else line_end,
                        is_static="static" in mods,
                        confined=bool(mods & {"private", "protected", "file"}),
                    )
                )

    # XAML is a witness scope of its own: Avalonia constructs a view from
    # `x:Class="…:ChatView"`, which is C# that never appears in a .cs file.
    #
    # Read RAW, not through the C# scanner. Markup is not C#, and its quoted
    # attribute values are precisely the evidence: `x:Class="Demo.ShellView"` IS
    # the construction site, so blanking literals erased the witness and the
    # rule reported every Avalonia view in the repo as dead. (Measured: 44
    # declarations are reachable only through markup.)
    xaml_text: dict[str, str] = {}
    for rel in files:
        if not rel.endswith(".axaml") or not rel.startswith(("src/", "apps/")):
            continue
        try:
            with open(os.path.join(repo, rel), encoding="utf-8", errors="replace") as fh:
                xaml_text[rel] = fh.read()
        except OSError:
            continue

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
            result.count_docs += 1
            # ... and the count question rides on the same row count, on every
            # tracked document and not just the declared ones. A dated record's
            # rows are allowed to be stale; its HEADLINE TOTAL still has to be
            # either true of the file or declared historical, because that
            # sentence is the first thing a reader copies.
            shape_problems = shape_problems + check_stated_count(text, rows)

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
        cites, cite_problems = check_citations(text, rel, repo, by_base, cache)
        table_cites, table_cite_problems = check_table_citations(
            text, rel, repo, by_base, cache
        )
        allowance, allowance_problems = read_allowances(text)
        names, type_problems = check_types(
            text, declared_in, production_text, xaml_text, set(allowance)
        )
        result.citations += cites + table_cites
        result.type_names += names
        result.hits[rel] = (
            cite_problems + table_cite_problems + type_problems + allowance_problems
        )
        if verbose:
            print(f"  {rel}: {cites + table_cites} citations, {names} type names, {rows} table rows")

    # The sixth shape (#947). OUTSIDE the markdown loop and outside the
    # `normative` gate on purpose: `tests/**/*.cs` is not a document that could
    # carry a `Status: normative` banner, so putting this behind that self-
    # selector would scope it to nothing — the same scoping accident #922's
    # design causes for every test file, which is the whole of this issue.
    test_seen, test_problems = check_cite_drift(repo, by_base)
    result.test_cites = test_seen
    result.test_cite_files = len(
        [r for r in md_gate.tracked_cs(repo) if r.startswith(TEST_CITATION_SCOPE)]
    )
    for rel, probs in test_problems.items():
        result.hits.setdefault(rel, []).extend(probs)
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

    # ---- #905: THE THREE SHAPES, ONE PER CASE ---------------------------------
    #
    # The rule used to ask one question — "does a DIFFERENT production file name
    # this type?" — of every declaration. That question is unsatisfiable for two
    # of the three shapes in this repo, and on docs/PATTERNS.md it answered
    # itself six times out of seven, on shipped code. Each case below is the
    # shape and the shape's own witness, so a future edit cannot re-widen one of
    # them by accident.
    norm = "# N\n\n> Status: normative for the current implementation.\n\n"

    # 1. CONFINED and used — the #905 shape. A `private` nested class is
    #    unreachable from another file, so no second file can ever name it. Its
    #    witness is its own file, outside its own declaration block.
    code, out = run(
        {
            **live,
            "src/Demo/Host.cs": (
                "namespace Demo;\n"
                "public sealed class Host\n"
                "{\n"
                "    private Nested _inner = new Nested();\n"
                "    private sealed class Nested\n"
                "    {\n"
                "        public int Value => 1;\n"
                "    }\n"
                "}\n"
            ),
            "docs/NORM.md": norm + "The host holds a `Nested`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a CONFIRMED case: a `private` nested class constructed in its own file passes (#905)",
        code == 0,
        out.strip()[-400:],
    )

    # 2. CONFINED and NOT used — the same shape, genuinely dead. Without this the
    #    first case would also pass on a rule that had stopped asking.
    code, out = run(
        {
            **live,
            "src/Demo/Host.cs": (
                "namespace Demo;\n"
                "public sealed class Host\n"
                "{\n"
                "    private sealed class Orphan\n"
                "    {\n"
                "        public int Value => 1;\n"
                "    }\n"
                "}\n"
            ),
            "docs/NORM.md": norm + "The host holds an `Orphan`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a `private` nested class nothing uses still fails — the confinement exemption is not a mute button (#905)",
        code == 1 and "DOC-TYPE-UNWIRED" in out,
        out[-400:],
    )

    # 3. STATIC — C# forbids `new` on a static class, so "nothing constructs it"
    #    is vacuous. `TuiModule` is this shape, and the root calls its MEMBER.
    code, out = run(
        {
            **live,
            "src/Demo/Setup.cs": (
                "namespace Demo;\n"
                "public static class SetupModule\n"
                "{\n"
                "    public static void Install() { }\n"
                "}\n"
            ),
            "docs/NORM.md": norm + "Composition calls `SetupModule`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a STATIC class is not asked for a construction witness (#905)",
        code == 0,
        out.strip()[-400:],
    )

    # 4. A type named only in a STRING is not a witness. This is the blind spot
    #    the file header already admitted to, and it is what makes
    #    CompositeToolRegistry a finding rather than a false negative.
    code, out = run(
        {
            **live,
            "src/Demo/Orphan.cs": (
                "namespace Demo;\n"
                "public sealed class Orphan\n"
                "{\n"
                '    public string Note => "Orphan is read-only";\n'
                "}\n"
            ),
            "docs/NORM.md": norm + "The tool is `Orphan`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a type named only inside a string literal is still unwired — a message is not a use (#905)",
        code == 1 and "DOC-TYPE-UNWIRED" in out,
        out[-400:],
    )

    # 5. A type reached only from XAML. Avalonia constructs a view from
    #    `x:Class`, which is C# that never appears in a .cs file — 44
    #    declarations in this tree are reachable no other way.
    code, out = run(
        {
            **live,
            "src/Demo/ShellView.axaml.cs": "namespace Demo;\npublic sealed class ShellView { }\n",
            "src/Demo/ShellView.axaml": (
                '<Window xmlns="https://schemas.microsoft.com/winfx/avalonia"\n'
                '        x:Class="Demo.ShellView" />\n'
            ),
            "docs/NORM.md": norm + "The shell window is `ShellView`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a type constructed only by XAML passes — `x:Class` is a construction site (#905)",
        code == 0,
        out.strip()[-400:],
    )

    # 6. A `//` inside a string literal is not a comment. This one is here
    #    because the scanner that answers questions 1-5 was WRITTEN with a
    #    regex stripper that made exactly this mistake, and the result was a
    #    shipped sample type (`WebSearchTool`) that the rule could not see at
    #    all. The URL is the trigger.
    code, out = run(
        {
            **live,
            "src/Demo/Url.cs": (
                "namespace Demo;\n"
                "public sealed class UrlThing\n"
                "{\n"
                '    public string Agent => "Demo/1.0 (https://example.test/v1)";\n'
                "    public string Note => \"UrlThing is read-only\";\n"
                "}\n"
            ),
            "docs/NORM.md": norm + "The agent is `UrlThing`; see `src/Demo/Live.cs:4`.\n",
        }
    )
    st.expect(
        "a `//` inside a string literal does not start a comment and strand the quote (#905)",
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

    # ---- RESOLUTION: what the fence could not SEE (#807) ----------------------
    # CITATION matches `.csproj`/`.props`/`.targets`/`.py` and check_citations
    # fences `.csproj` line numbers — but the basename index was built from
    # `git ls-files '*.cs' '*.axaml'`, so a bare `Demo.csproj:3` resolved to
    # nothing and was reported MISSING for a file in the tree. Four shapes, one
    # per way the old resolver answered "no":

    # 1. a non-.cs tracked format, by bare basename.
    code, out = run(
        {
            **live,
            "src/Demo/Demo.csproj": "<Project>\n  <PropertyGroup>\n    <A>1</A>\n  </PropertyGroup>\n</Project>\n",
            "docs/NORM.md": clean_norm.replace("`src/Demo/Live.cs:4` and `Live.cs:4`",
                                                "`Demo.csproj:3`"),
        }
    )
    st.expect(
        "a bare `.csproj:3` resolves — the index is not `.cs`-only (#807)",
        code == 0, out.strip()[-400:],
    )

    # 2. the project-relative form a sweep writes: `Demo/Live.cs:4` for a file
    # at `src/Demo/Live.cs`. The TABLE rule always tried `src/`+`apps/`; the
    # PROSE rule did not, so the same string was red in a paragraph and green in
    # a table. That asymmetry is the defect, so the case is a PROSE citation.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm.replace("`src/Demo/Live.cs:4` and `Live.cs:4`",
                                                "`Demo/Live.cs:4`"),
        }
    )
    st.expect(
        "a project-relative prose citation `Demo/Live.cs:4` resolves to src/Demo/Live.cs",
        code == 0, out.strip()[-400:],
    )

    # 3. and the same string must be fenced, not merely resolved: project-relative
    # AND past EOF. Without this the previous case could pass by not resolving at
    # all while looking identical on stdout.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm.replace("`src/Demo/Live.cs:4` and `Live.cs:4`",
                                                "`Demo/Live.cs:999`"),
        }
    )
    st.expect(
        "a project-relative citation is FENCED, not just resolved (past EOF fails)",
        code == 1 and "DOC-CITE-EOF" in out, out[-400:],
    )

    # 4. relative to the citing document: a README inside the project writing
    # `Input/MouseRouter.cs:25`, which is how a human reads its own repo.
    # `Mouse` is named in backticks AND wired, because the `--min-types` floor
    # counts backticked names: a fixture with none exits 1 on the floor and would
    # prove nothing about resolution. (It did, on the first run.)
    code, out = run(
        {
            **live,
            "src/Demo/Input/Mouse.cs": "namespace Demo;\npublic sealed class Mouse { }\n",
            "src/Demo/Reader.cs": "namespace Demo;\npublic sealed class Reader { public Mouse M { get; } = new(); }\n",
            "src/Demo/README.md": "# R\n\n> Status: normative.\n\n"
            "The router is `Mouse`; see `Input/Mouse.cs:2`.\n",
        }
    )
    st.expect(
        "a citation relative to the citing document resolves (README beside the file)",
        code == 0, out.strip()[-400:],
    )

    # And the one shape that must stay a MISS after all that widening: a path
    # that exists nowhere. Widening the resolver is not a way to make every
    # citation resolve — if it were, the rule would stop meaning anything.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm.replace("`src/Demo/Live.cs:4` and `Live.cs:4`",
                                                "`Demo/Ghost/Live.cs:4`"),
        }
    )
    st.expect(
        "a project-relative citation to a file that exists NOWHERE still fails",
        code == 1 and "DOC-CITE-MISSING" in out, out[-400:],
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

    # ---- THE COUNT (#807) -----------------------------------------------------
    # A number that disagrees with the rows beside it. No `file:line` fence can
    # reach this: the other rules ask whether a number is inside a FILE, and
    # this asks whether it agrees with the document's own contents.
    #
    # Each "must pass" case carries `clean_norm` so the floors hold and the case
    # can only pass for the reason it names.
    audit_rows = (
        "| File | Line | Member | Has XML doc? | Priority |\n|---|---|---|---|---|\n"
        "| Demo/Live.cs | 4 | class LiveThing | YES | HIGH |\n"
        "| Demo/Live.cs | 5 | public int Value | YES | MED |\n"
    )
    stale_banner = "# A\n\n> **Status (2026-08-27):** a dated record.\n\n## Summary\n\n"

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner + "- **Total members audited:** 5\n\n"
            + audit_rows,
        }
    )
    st.expect(
        "a headline total that disagrees with the document's own rows fails (#807)",
        code == 1 and "DOC-COUNT-STALE" in out, out[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner + "- **Total members audited:** 2\n\n"
            + audit_rows,
        }
    )
    st.expect(
        "a total that MATCHES the rows passes — the rule is not a blanket complaint",
        code == 0, out.strip()[-400:],
    )

    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner + "- **Total members audited:** 5\n\n"
            + audit_rows
            + '<!-- check-doc-cites: allow-stale-count "Total members audited" '
            + "— 5 is the sweep figure; two documented rows were deleted after it -->\n",
        }
    )
    st.expect(
        "a declared historical count with a reason is allowed (a запись may be a "
        "true statement about the sweep and a false one about its own rows)",
        code == 0, out.strip()[-400:],
    )

    # The waiver must name WHICH figure it is about, or it is a mute button for
    # every future count in the document. Same reason-mandatory discipline as
    # allow-unwired, and it is the hole #921 found and closed.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner + "- **Total members audited:** 5\n\n"
            + audit_rows
            + "<!-- check-doc-cites: allow-stale-count -->\n",
        }
    )
    st.expect(
        "a waiver with no quoted label is NOT honoured — it must say which figure",
        code == 1 and "DOC-COUNT-STALE" in out, out[-400:],
    )

    # The row table's OWN cells must not be read as a stated total: a Priority
    # Breakdown whose `Total` column is 1111 is not a document claiming 1111
    # members, and an unanchored rule would call every correct summary wrong.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner
            + "| Priority | Total | With Doc | Without Doc |\n|---|---|---|---|\n"
            + "| HIGH | 1111 | 808 | 303 |\n| MED | 765 | 543 | 222 |\n\n"
            + audit_rows,
        }
    )
    st.expect(
        "a table's own Total COLUMN is not a stated total — no prose, no complaint",
        code == 0, out.strip()[-400:],
    )

    # And a document with rows but no headline figure at all is not asked.
    code, out = run(
        {
            **live,
            "docs/NORM.md": clean_norm,
            "docs/AUDIT.md": stale_banner + audit_rows,
        }
    )
    st.expect(
        "a table with no stated total is not asked a question it cannot answer",
        code == 0, out.strip()[-400:],
    )

    # -----------------------------------------------------------------------
    # #947 — the sixth shape: a `file:line` in a TEST's prose.
    #
    # These need history, which `make_fixture` does not create (it stops at
    # `git add`), so they are the first cases built with `commit_fixture`. Each
    # one writes the citation, commits, moves the target's lines, commits
    # again — the exact two-point shape the rule compares across.
    # -----------------------------------------------------------------------
    target_v1 = "namespace Demo;\npublic sealed class Target\n{\n    public int Keep = 1;\n}\n"
    # Same file, two lines inserted at the top. `_tokenTracker` in the repo's
    # real case; `Keep` here. Line 4 was `Keep`, now line 4 is `{`.
    target_v2 = "// inserted\n// inserted\nnamespace Demo;\npublic sealed class Target\n{\n    public int Keep = 1;\n}\n"
    citing = (
        "namespace Demo.Tests;\n"
        "public sealed class Cites\n{\n"
        "    // the field is assigned at Target.cs:4 and read nowhere\n"
        "    public void Check() { }\n"
        "}\n"
    )

    def run_history(v1: str, v2: str, citing_text: str, *args: str) -> tuple[int, str]:
        """Two commits, one citation; returns the gate's verdict."""
        root = md_gate.make_fixture(
            "check-doc-cites.py",
            {**live, "src/Demo/Target.cs": v1, "tests/Demo/Cites.cs": citing_text},
        )
        md_gate.commit_fixture(root, "fixture: baseline")
        # Second commit: the target grows ABOVE the cited line. Nothing in the
        # citing file changed, so its blame still points at the first commit —
        # which is the reference the rule needs.
        with open(os.path.join(root, "src/Demo/Target.cs"), "w", encoding="utf-8") as fh:
            fh.write(v2)
        subprocess.run(["git", "add", "-A"], cwd=root, check=True, env=md_gate.git_env())
        md_gate.commit_fixture(root, "fixture: target grows above the cited line")
        return md_gate.run_gate(root, "check-doc-cites.py", *args)

    code, out = run_history(target_v1, target_v2, citing)
    st.expect(
        "#947: a test citation whose line now names different code fails",
        code == 1 and "TEST-CITE-DRIFT" in out, out.strip()[-500:],
    )

    # -----------------------------------------------------------------------
    # THE DISJUNCTION, AND PROOF THAT IT DOES NOT DEGENERATE (#937's shape)
    #
    #     the two sides agree                            -> silent
    #     they disagree AND the site is RECORDED          -> silent
    #     they disagree and nothing is recorded           -> DRIFT
    #     a record exists but its site no longer drifts   -> RECORD-STALE
    #     a record exists with no quoted reason           -> RECORD-NOREASON
    #
    # The last two are what separate this from #847's rejected table. Each of
    # the four cases below is a ONE-SIDED change — touch one half of the pair
    # and leave the other — and each must be red. A form that only ever silences
    # would pass all four.
    # -----------------------------------------------------------------------
    RECORD = (
        '// check-doc-cites: record-drift Target.cs:4 now="'
        # Derived from the fixture, never hand-typed: a test that types its own
        # fingerprint is a test that can pass for the wrong reason, and this one
        # did exactly that on its first run.
        + fingerprint(target_v2.splitlines()[3].strip())
        + '" [#947: deferred to the owner] -->\n'
    )
    citing_with_record = (
        "namespace Demo.Tests;\n"
        "public sealed class Cites\n{\n"
        "    // the field is assigned at Target.cs:4 and read nowhere\n"
        + RECORD +
        "    public void Check() { }\n"
        "}\n"
    )

    def run_recorded(target_now: str, citing_text: str, *args: str) -> tuple[int, str]:
        root = md_gate.make_fixture(
            "check-doc-cites.py",
            {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
             "tests/Demo/Cites.cs": citing_text},
        )
        md_gate.commit_fixture(root, "fixture: baseline, with the record")
        if target_now != target_v1:
            with open(os.path.join(root, "src/Demo/Target.cs"), "w", encoding="utf-8") as fh:
                fh.write(target_now)
            subprocess.run(["git", "add", "-A"], cwd=root, check=True, env=md_gate.git_env())
            md_gate.commit_fixture(root, "fixture: target moves")
        return md_gate.run_gate(root, "check-doc-cites.py", *args)

    code, out = run_recorded(target_v2, citing_with_record)
    st.expect(
        "#947: a recorded divergence is GREEN — this is the whole point of the "
        "form, and it is what the previous shape could not be",
        code == 0 and "TEST-CITE-DRIFT" not in out, out.strip()[-500:],
    )

    # ONE-SIDED CHANGE #1: repair the citation, leave the record. The record is
    # now excusing nothing, so it must go red until it is deleted. Without this
    # a record is permanent, and a permanent record is a mute button.
    citing_repaired = citing_with_record.replace(
        "assigned at Target.cs:4", f"assigned at Target.cs:{len(target_v1.splitlines())}"
    )
    code, out = run_recorded(target_v1, citing_repaired)
    st.expect(
        "#947: ONE-SIDED CHANGE (citation repaired, record kept) is red — the "
        "record retires",
        code == 1 and "TEST-CITE-RECORD-STALE" in out, out.strip()[-500:],
    )

    # ONE-SIDED CHANGE #2: leave the citation, move the target AGAIN so the
    # line reads something else. The site is unchanged, so a record keyed on the
    # site alone would go on excusing a divergence nobody has looked at since.
    # The `now=` fingerprint is what catches this.
    target_v3 = "// moved\n// moved\n// moved\n" + target_v2
    code, out = run_recorded(target_v3, citing_with_record)
    st.expect(
        "#947: ONE-SIDED CHANGE (target moved again, record kept) is red — the "
        "fingerprint pins one divergence, not one line number",
        code == 1 and "TEST-CITE-DRIFT" in out, out.strip()[-500:],
    )

    # A record naming a DIFFERENT line than the prose cites is a record for a
    # different fact, and must not silence this one.
    wrong_line = citing_with_record.replace("Target.cs:4 now=", "Target.cs:5 now=")
    code, out = run_recorded(target_v2, wrong_line)
    st.expect(
        "#947: a record naming a different LINE does not silence this one",
        code == 1 and "TEST-CITE-DRIFT" in out, out.strip()[-500:],
    )

    # Reason-mandatory, like the two waivers above.
    no_reason = citing_with_record.replace('" [#947: deferred to the owner] -->', '" -->')
    code, out = run_recorded(target_v2, no_reason)
    st.expect(
        "#947: a record with no quoted reason is NOT honoured",
        code == 1 and "TEST-CITE-DRIFT" in out, out.strip()[-500:],
    )
    st.expect(
        "#947: ... and it says so, rather than passing quietly",
        code == 1 and "TEST-CITE-RECORD-NOREASON" in out, out.strip()[-500:],
    )

    # The record must not become a citation of its own. A record NAMES
    # `Target.cs:4`, so without masking the population grows by one per record
    # and the rule ends up checking its own bookkeeping.
    code, out = run_recorded(target_v2, citing_with_record)
    st.expect(
        "#947: a record does not count itself as a citation — otherwise the "
        "population and the floor both drift",
        "anchored 1 `file:line` citations" in out, out.strip()[-500:],
    )

    # The case the whole issue turns on. The cited line still EXISTS, still
    # holds code, still resolves — and is the wrong code. A fence that asked
    # only "is there code there" is green on exactly this fixture.
    code, out = run_history(target_v1, target_v2, citing)
    st.expect(
        "#947: the finding is not reachable by asking whether the line has code "
        "(it does — the rule reports DRIFT, not BLANK)",
        code == 1 and "TEST-CITE-BLANK" not in out and "TEST-CITE-DRIFT" in out,
        out.strip()[-500:],
    )

    # Unmoved target: the same citation against a file that never moved. This
    # is the case that makes the rule a gate rather than a tripwire — if the
    # comparison were "did anything change in the repo", this would fail too.
    root = md_gate.make_fixture(
        "check-doc-cites.py",
        {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
         "tests/Demo/Cites.cs": citing},
    )
    md_gate.commit_fixture(root, "fixture: baseline")
    os.makedirs(os.path.join(root, "docs"), exist_ok=True)
    with open(os.path.join(root, "docs/NOTE.md"), "w", encoding="utf-8") as fh:
        fh.write("# Note\n\nNot normative.\n")
    subprocess.run(["git", "add", "-A"], cwd=root, check=True, env=md_gate.git_env())
    md_gate.commit_fixture(root, "fixture: unrelated change")
    code, out = md_gate.run_gate(root, "check-doc-cites.py")
    st.expect(
        "#947: an unmoved citation passes — the rule compares, it does not "
        "complain that the repo changed",
        code == 0, out.strip()[-700:],
    )

    # A citation in a test's STRING DATA is not a claim about this repo. The
    # repo's own case is `PanelExtractorsTests.cs:358`, which builds
    # `"src/a.cs:12 CS0246: type not found"` as a fixture for a parser.
    data_citing = (
        "namespace Demo.Tests;\n"
        "public sealed class Synth\n{\n"
        "    // a synthetic diagnostic, quoted as data\n"
        "    public string Msg = \"Target.cs:4 CS0246: type not found\";\n"
        "}\n"
    )
    root = md_gate.make_fixture(
        "check-doc-cites.py",
        {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
         "tests/Demo/Synth.cs": data_citing},
    )
    md_gate.commit_fixture(root, "fixture: baseline")
    code, out = md_gate.run_gate(root, "check-doc-cites.py")
    st.expect(
        "#947: a `file:line` inside a test's string literal is data, not a claim",
        code == 0 and "TEST-CITE" not in out, out.strip()[-500:],
    )
    st.expect(
        "#947: ... and it is EXCLUDED, not merely un-reported: the count says "
        "zero, so the exclusion cannot be implemented by matching everything "
        "and staying quiet",
        "anchored 0 `file:line` citations" in out, out.strip()[-500:],
    )

    # ... and the counterpart: a real comment citation IS counted, so the case
    # above is not passing because the rule stopped reading test files.
    mixed = (
        "namespace Demo.Tests;\n"
        "public sealed class Mixed\n{\n"
        "    // see Target.cs:4\n"
        "    public string Msg = \"Target.cs:4 CS0246: type not found\";\n"
        "}\n"
    )
    root = md_gate.make_fixture(
        "check-doc-cites.py",
        {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
         "tests/Demo/Mixed.cs": mixed},
    )
    md_gate.commit_fixture(root, "fixture: baseline")
    code, out = md_gate.run_gate(root, "check-doc-cites.py", "--min-test-cites", "1")
    st.expect(
        "#947: the comment citation in the same file IS counted — one file, one "
        "comment cite + one string cite, and the count is 1",
        code == 0 and "anchored 1 `file:line` citations" in out, out.strip()[-500:],
    )

    # A citation past EOF and one on a blank line: the two mechanical classes.
    # Kept because they were the whole rule before the history anchor existed,
    # and a rule that is superseded rather than deleted still has to work.
    eof_citing = (
        "namespace Demo.Tests;\npublic sealed class Eof\n{\n"
        "    // see Target.cs:999\n    public void Check() { }\n}\n"
    )
    root = md_gate.make_fixture(
        "check-doc-cites.py",
        {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
         "tests/Demo/Eof.cs": eof_citing},
    )
    md_gate.commit_fixture(root, "fixture: baseline")
    code, out = md_gate.run_gate(root, "check-doc-cites.py")
    st.expect(
        "#947: a citation past the end of the file still fails",
        code == 1 and "TEST-CITE-EOF" in out, out.strip()[-500:],
    )

    # The floor: a fixture with no tests/ at all must not trip it, and a
    # narrowed scan must. Same opt-in discipline as --min-api/--min-counts.
    code, out = run(
        {**live, "docs/NORM.md": clean_norm, "src/Demo/Target.cs": target_v1,
         "tests/Demo/Cites.cs": citing},
        "--min-test-cites", "5",
    )
    st.expect(
        "#947: `--min-test-cites` fails when the tests/ citation count shrinks",
        code == 1 and "floor" in out, out[-400:],
    )

    # The failure this rule actually made on its first CI run, reproduced on
    # purpose. A shallow clone cannot answer "what did this line mean when the
    # citation was written", and the first version of the rule reported the
    # smaller number as if it were the whole truth: 21 findings instead of 87,
    # build green, anchored rule silently blind. The gate must now REFUSE rather
    # than under-report, which is what this case pins.
    shallow = subprocess.run(
        # `file://` is REQUIRED and not decorative: git ignores `--depth` on a
        # plain local-path clone (it hardlinks the object store instead), so
        # this would produce a FULL clone and the case would assert nothing.
        ["git", "clone", "--depth", "1", "--quiet",
         "file://" + os.path.abspath(root), root + "-shallow"],
        capture_output=True, text=True, env=md_gate.git_env(),
    )
    if shallow.returncode != 0:
        st.expect(
            "#947: a shallow clone of the fixture is refused rather than "
            "silently under-reported",
            False, shallow.stderr.strip()[-300:],
        )
    else:
        code, out = md_gate.run_gate(root + "-shallow", "check-doc-cites.py")
        st.expect(
            "#947: a shallow clone reports TEST-CITE-NO-HISTORY instead of "
            "quietly dropping the anchored rule",
            code == 1 and "TEST-CITE-NO-HISTORY" in out, out.strip()[-500:],
        )
        st.expect(
            "#947: ... and says the anchored findings are ABSENT, not zero — a "
            "smaller number and a different number are not the same claim",
            "absent, not zero" in out, out.strip()[-500:],
        )
        shutil.rmtree(root + "-shallow", ignore_errors=True)

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
        "--min-counts",
        type=int,
        default=0,
        help="fail unless at least this many documents had a stated total compared "
        "against their own `| path | line |` rows (#807; 0 = off)",
    )
    ap.add_argument(
        "--min-api",
        type=int,
        default=0,
        help="fail unless at least this many `## Public API` type names were examined "
        "in samples/plugins/*/README.md (0 = off)",
    )
    ap.add_argument(
        "--min-test-cites",
        type=int,
        default=0,
        help="fail unless at least this many `file:line` citations were examined "
        "in tests/**/*.cs prose (#947; 0 = off)",
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
            f"{result.table_docs} document(s), comparing a stated total against "
            f"those rows in {result.count_docs} of them (#807: the shape the prose "
            f"fence is blind to)"
        )
    print(
        f"and checked {result.api_names} `## Public API` type names in "
        f"{result.api_docs} sample README(s) (#794: names no tree declares)"
    )
    print(
        f"and anchored {result.test_cites} `file:line` citations in tests/**/*.cs "
        f"on the commit that wrote each one (#947: a number that still resolves "
        f"can still name the wrong line)"
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
    # Same opt-in-only discipline, and the same reason: this floor counts
    # DOCUMENTS WITH ROWS, so a fixture with no table would exit 1 on it and
    # prove nothing about DOC-COUNT-STALE. `--min-counts 1` is what says "the
    # count question is still being asked of something" — without it, a parser
    # that stopped matching COUNT_TOTAL would be indistinguishable from a
    # document that simply states no totals.
    if args.min_counts > 0:
        problems += md_gate.require_non_vacuous(
            "doc-cites/counts",
            result.count_docs,
            result.count_docs,
            "documents whose stated total was compared to their rows",
            1,
            args.min_counts,
        )
    # Same discipline, and the same reason it is opt-in: a fixture with no
    # tests/ directory would exit 1 on this floor and prove nothing about
    # TEST-CITE-DRIFT. `--min-test-cites 1` is what says the rule is still being
    # asked of something — without it, a `CITATION` regex that stopped matching
    # .cs files would be indistinguishable from a repo that cites nothing.
    if args.min_test_cites > 0:
        problems += md_gate.require_non_vacuous(
            "doc-cites/test-drift",
            result.test_cite_files,
            result.test_cites,
            "tests/**/*.cs file:line citations",
            1,
            args.min_test_cites,
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
