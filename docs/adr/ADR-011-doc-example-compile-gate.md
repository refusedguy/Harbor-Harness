# ADR-011: a gate for documentation examples that only LOOK compilable — measured, not built

## Status
Accepted as a record. **No gate was added.** Six candidate rules were measured on
`dev` = `538d9f1c` and every one of them is either unreachable from the docs gate or
red on the day it is written. This ADR exists so that the question is not re-derived
from scratch, and so that the re-open conditions in §6 are checkable when they occur.

## Date
2026-09-30 (issue #853; the defect it follows is #849, fixed by PR #855)

## Context

`docs/TEST_PATTERNS.md` is titled **"Copy-Paste Ready"** and shipped two untrue rows
in its attribute table. Both were caught only because a different PR copied the text
and CI failed on the copy:

- `[NotInParallel("a", "b")]` — **CS1729**. TUnit 1.61.0's `NotInParallelAttribute`
  declares `()`, `(string)` and `(string[])`; there is no `params` overload. This is a
  question about constructor **arity**.
- `[SkipWhenNotLinux]` — **CS0246**. It is `internal sealed class
  SkipWhenNotLinuxAttribute` in `tests/Harbor.Tools.Builtin.Tests`, presented as a
  framework attribute. This is a question about **name ownership and visibility**.

25% of that table was a lie a reader would copy. The question this ADR answers is
narrow and concrete: **can the stdlib-only docs gate catch either class?** The docs
gate is `tools/check-doc-cites.py`, its two siblings, and `.github/workflows/docs.yml`
— a ~30s job whose own header says it is "NOT a dotnet job: nothing here needs the
SDK, the solution, or a submodule". No pip, no network, no dotnet, by construction.

## Decision

Build nothing. Each candidate below was implemented far enough to be counted, not
argued about.

### §1. Candidate A — check the NAME against TUnit's shipped XML doc file

The idea: `TUnit.Core.xml` is plain text, and `xml.etree.ElementTree` is stdlib, so a
name-existence check needs neither a compiler nor a signature reader.

**The file exists and parses.** TUnit.Core 1.61.0 ships
`lib/{net8.0,net9.0,net10.0,netstandard2.0}/TUnit.Core.xml` — 623 872 bytes, 1509
members (291 `T:`, 673 `M:`, 476 `P:`, 69 `F:`), parsed without error by stdlib
Python. Existence is therefore a question this medium *can* answer.

**It cannot answer the question #849 actually asked.** The file carries 72 `#ctor`
rows across 59 types. Of the 47 `TUnit.Core.*Attribute` types it declares, **26 carry
at least one constructor row and 21 carry none at all** — including
`NotInParallelAttribute` (0 rows) and `MethodDataSourceAttribute` (0 rows). TUnit
documents inherited members once, on the base, and `<inheritdoc/>` writes no row on
the derived attribute. So a rule built on this file would have been **silent on 45%
of TUnit's attributes, and silent on the exact type whose arity broke** — a rule that
cannot fail on the class it was made for. Existence and arity are not the same
medium; CS1729 lives in PE/CLI metadata.

**It is also unreachable where the gate runs.** `docs.yml` contains exactly six
`run:` lines, all of them `python3 tools/…`. There is no restore, so
`~/.nuget/packages` is empty in that job, and the repo tracks no `packages.lock.json`
to read a member list from. The file would have to be vendored — 624 KB committed to
be re-vendored on every TUnit bump, which is the hand-maintained table this repo has
already paid for. And presence is not even stable across versions: **TUnit.Core 0.50.0
ships no XML file at all.**

### §2. Candidate A′ — check the NAME against this repository

Drop the package and ask whether the tree declares it. No restore, no runner.

Across 279 tracked markdown files there are **62 distinct `[PascalCase]` attribute
spellings**. Their buckets:

| bucket | count | note |
|---|---:|---|
| public in this tree | 5 | the only clean answers |
| real attributes the tree itself spells, declared outside it | 34 | BCL and family |
| declared nowhere, spelled by no `.cs` file either | 23 | includes the genuine TUnit `RunOn` and `ExcludeOn` |
| **declared only `internal`** — the `#SkipWhenNotLinux` shape | **0** | the only actionable bucket, and it is empty |

The rule would have had **zero work to do** on this tree, and it could not have caught
the second defect from #849 anyway: `SkipWhenNotLinuxAttribute` *does* exist in the
tree. The defect was its visibility and its owning project, not its existence.
Meanwhile 23 of 62 names are irreducible false positives — among them two real TUnit
attributes that `docs/TEST_PATTERNS.md` names correctly today. There is also no stable
spelling to match on: the doc writes `[SkipWhenNotLinux]` while the declaration is
`SkipWhenNotLinuxAttribute`, because C# usage drops the suffix.

### §3. Candidate B — require an example that quotes code to cite it

A cheap rule in the shape the repo already uses: if an example is taken from code, it
must carry a resolvable `path/File.cs:line`, which `check-doc-cites.py` already knows
how to check.

The corpus cannot support it. Of **1028 copy-paste-shaped fenced blocks** (849 tagged
`csharp`, 179 untagged), **131 carry a `file:line` citation** in the fence or within
four lines of it — **12.7%**. The other **897 do not**. A "must cite" rule is red on
87% of the corpus. There is also no convention to build on: across all tracked markdown
the corpus contains **zero** `not-compilable` markers, **zero** illustration markers
and **zero** `source=` comments. Only 7 fences sit next to a verbatim-style phrase at
all, and reading all seven shows **none is an actual verbatim claim** — the phrases are
ordinary prose ("as written in", "exactly as"). A rule keyed on that phrase would have
had no class to bite on and no fixture that was not a false positive.

### §4. Candidate C — ban any example that neither cites a source nor declares itself illustrative

This is the rule the corpus most directly refutes. It would outlaw **897 fenced blocks
across 105 documents**, and those blocks are not fiction:

| what they actually are | blocks |
|---|---:|
| contain an ellipsis (elided middle) | 232 |
| untagged fences holding shell transcripts and ASCII art | 175 |
| elide the body with `// …` | 103 |
| shell transcripts | 45 |
| use an obvious placeholder identifier | 11 |

`docs/COMPONENT_CATALOG.md` alone contributes real `record struct` and `enum`
declarations with no citation. Banning them bans the documentation.

### §5. Candidates D and E — the two that need no new claim from any document

Both were measured because they are the only shapes that cannot be red for asking
something new of a document.

- **D: fence every citation that already exists, in every document.** No opt-in, no
  banner, nothing for a reader to do. It fails: **2662 citations** are already written
  in the 276 non-normative documents and **818 of them fail** (758 `DOC-CITE-MISSING`,
  60 `DOC-CITE-EOF`) across 26 documents — 672 of them inside the single dated
  snapshot `docs/XML_DOC_AUDIT.md`. A gate red on 818 pre-existing findings on day one
  is the failure this repo's own gate headers keep warning about.
- **E: a source path named on a fence's first line must resolve.**
  `docs/CODEGEN_BOILERPLATE.md` teaches exactly this shape. **116 fences** use it;
  **34 resolve and 82 name files that do not exist** — `MyTool.cs`,
  `IChatSessionService.cs`, `AmbientMascot.cs`, `TimeTool.cs`. The convention is a
  template convention, not a provenance convention.

## Consequences

- **No code changed and no rule was added.** This is a deliberate refusal to add a
  check that cannot fail, which is worse than no check: a green gate that never fires
  reads identically to a gate that is working.
- **The measurement is the deliverable.** The six numbers that decide this — 21 of 47,
  0 of 62, 12.7%, 897, 818, 82 of 116 — are reproducible from the tree and from a
  NuGet cache, and are restated in the `#853` PR body.
- **`docs/TEST_PATTERNS.md` still sits outside every fence.** It declares neither
  `Status: normative` nor a dated banner, so the densest copy-paste document in the repo
  (32 copy-paste-shaped blocks, 2 of them citing) is not scanned by
  `check-doc-cites.py` at all. Three documents declare the normative banner today:
  `docs/EVENT_TOPOLOGY.md`, `docs/EVENT_BUS_SINKS.md` and
  [ADR-010](ADR-010-token-notation-one-cell.md). Marking `TEST_PATTERNS.md` normative
  is a one-line edit that would pull its citations and every backticked type name into
  existing fences; it is a judgement call about that document's claims, and it is left
  to the owner rather than taken here.
- **Nothing here conflicts with the merged work.** The `#826` rule
  (`DOC-CITED-TABLE-UNDECLARED`) and its banner-anchored marker are untouched: this ADR
  adds no rule and writes no `| path | line |` row. [ADR-010](ADR-010-token-notation-one-cell.md),
  added by `#811`, is normative and cites code lines such as `StatusBarText.cs:42-48`;
  those citations are matched by the *prose* citation rule, not the table rule, because
  its first cell is the writer number and its second cell holds the whole `File.cs:42-48`
  span. `check-doc-cites.py` reports 199 citations, 82 type names, 2842 declared table
  rows and 18 sample API names across the three normative documents, and exits 0.

### §6. What would re-open this

Stated so the decision can be revisited on evidence rather than re-argued:

1. `docs.yml` gains a `dotnet restore` step. That makes `TUnit.Core.xml` reachable and
   Candidate A's *existence* half buildable — though arity would still need a metadata
   reader, so CS1729 stays out of reach.
2. A TUnit release starts emitting `#ctor` rows for all 47 attribute types. Candidate A
   becomes a complete arity check with no runner, and the 21-attribute gap closes.
3. The docs corpus grows a provenance convention that is actually used — today it has
   zero markers of any kind. Candidate B becomes meaningful the day a few dozen
   examples cite a source, and not before.

## Alternatives considered

- **Compile every fence in a runner.** Rejected before measuring: it needs a restore
  per block, is the slow gate `docs.yml` exists to avoid, and would be red on day one
  on fragments with elided usings and bodies.
- **A hand-written TUnit signature table.** Rejected: it is the "allow-list that
  verified zero files" pattern named in `check-doc-cites.py`'s own docstring. Nothing
  can validate it against reality, and the first thing it gets wrong is the next
  TUnit bump.
- **A `<!-- not-compilable: reason -->` marker convention.** Not adopted on its own. A
  rule that checks only that a marker is present cannot fail on the defect it was made
  for, and the corpus would need 897 markers to satisfy it. It remains worth having
  later as the cheap half of a provenance rule, if §6(3) ever happens.

## References

- Issue #853, issue #849, PR #855.
- `tools/check-doc-cites.py` — the gate this ADR declines to extend, and the
  "allow-list that verified zero files" warning in its docstring.
- `docs/TEST_PATTERNS.md` — the document the defect shipped in.
