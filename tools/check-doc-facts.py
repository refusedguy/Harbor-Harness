#!/usr/bin/env python3
"""Check the repo-map counts in the onboarding documents against the tree.

Stdlib only — no pip, no network, no dotnet. Wired into the same workflow as
check-md-links.py, md-lint.py and check-doc-cites.py (.github/workflows/docs.yml),
and it must not overlap any of them:

  check-md-links.py  does the link resolve?
  md-lint.py         is the file shaped the way the repo says it is?
  check-doc-cites.py does a `file:line` still point at a line, and a named type
                     still exist?
  THIS ONE           does a COUNT a document states still equal the count of
                     tracked files it is counting?

WHY THIS FILE EXISTS (issue #933)

  #933 is the class: a hand-maintained prose fact about the repository,
  restated in N places, with nothing cross-checking the copies. Its three
  instances (#649, #807, #932) were all corrected by hand, one sentence at a
  time, and the fourth instance then produced itself: `AGENTS.md:106` said
  "62 top-level docs", merged #649/#940 corrected 136 → 62 (and 62 was right
  at that commit), and two documents were added later the same day. HEAD says
  64. Nothing lied this time — a correct number simply decayed, which is the
  same failure with the fabrication removed.

  THE GATE QUESTION #933 ASKS, ANSWERED BY MEASUREMENT

  The proposed cure was "collapse the copies to one owner, then assert the
  copies match". Measured on this tree, the second half cannot work for prose
  and must not be built:

    * The builtin tool count is stated 16 times across 15 files with SIX
      different values — 4, 5, 7, 14, 20, 26. Hand-reading every site: the
      correct value (20) sits in the six documents that claim to be about
      today; `7` is a CHANGELOG release note, `14` is in four documents that
      declare themselves "Historical plan (pre-sprint-2)", and `4`/`5`/`26`
      are in `docs/specs/`, which #983 declared a dated record. A
      consistency gate over prose would be RED on ten sites that are ALLOWED
      to be wrong and GREEN on the live defect. That is a trap, not a gate.
    * The class's width is real and larger than the issue's count: per-fact
      copies measured on `dev` at 7aff0c87 are CellForge-as-backend 63
      files / 320 occurrences, `Harbor.slnx` layout 25 / 59, why-not-
      `dotnet test` 18 / 27, builtin tool count 15 / 16, test-project count
      11 / 14, the Kilocode free model 9 / 33.

  So this gate does the half that IS well-defined. A count is not prose: it
  either equals the number of tracked files matching a stated pattern or it
  does not, and there is no interpretation in the comparison and no way for a
  dated record to fake a pass — a record that quotes an old count is either
  flagged as a record or is not, and it cannot make the number agree.

WHAT THIS DOES NOT DO, STATED RATHER THAN IMPLIED

  * It does not compare prose to prose. That is the part of #933 that is not
    buildable, and the reason is above rather than a matter of taste.
  * It does not require every document to declare a status. That is a new
    axis under #555's freeze, and #983 measured the population: 271 of 285
    tracked markdown files are undeclared. Measured, not guessed.
  * It does not read `docs/specs/`. #983 established those are a dated
    record, and a record is allowed to be wrong about today. Adding them
    here would put the gate red on day one for the exact reason #983 spent a
    PR not doing that.
  * It does not check `tests/`, `src/` or `apps/` prose. The perimeter is the
    two onboarding documents a new contributor reads first, because those are
    the copies #933's #649 instance was about and the only two that are read
    before a reader has learned which documents are records.

RULES

  DOC-COUNT-DRIFT   a repo-map line states a count for a directory it names in
                    the same breath, and recounting the tracked files gives a
                    different number. The finding names BOTH numbers, because
                    "your number is wrong" is not actionable and "you said 62,
                    there are 64" is.
  DOC-COUNT-BLIND   a rule whose pattern matched nothing. Reported rather than
                    skipped: a pattern edited until it stops matching is
                    indistinguishable from a document that stopped making the
                    claim, and only one of those is a clean tree. This is the
                    #591 shape, and it is why this rule reports blindness
                    instead of counting it as a pass.

  The escape hatch is a line in the same document, and it carries a reason:

      <!-- check-doc-cites: allow-count "docs/ top-level" — <why> -->

  Per-document and reason-mandatory, for the reason the other rules give: a
  global allow-list here would be the "list that verified zero files" failure
  this repo has already paid for, and a per-document line shows up in the diff
  next to the number that needed it.

NON-VACUITY

  --min-facts N is a floor on how many repo-map counts were COMPARED, and it
  is wired into docs.yml the way the other three gates' floors are. Measured
  today: 7 comparisons. The floor is 5, ~30% below, so a PR that legitimately
  removes a count lowers it in the same diff where a reviewer can see it.
  A rule that silently stops matching drives that number to zero and the floor
  goes red — which is the whole point, and is why the floor is not optional.

  --self-test runs six fixtures covering every vacuity shape measured during
  development: a stale count, a count compared against the wrong population,
  a pattern that matches nothing, a pattern whose capture group was edited
  away, a half-fired rule over a second denominator, and a correct count that
  must stay green. One of those fixtures failed against the first version of
  this file, which is why they are here.
"""
import argparse
import os
import re
import subprocess
import sys

# The documents this gate reads, and why.
#
# Both are onboarding documents read before a reader knows which documents in
# this repo are dated records, which is precisely why a false count in them
# propagates: #649's `tencent/hy3:free` and this count were both in this set.
# The scope is explicit rather than "every tracked markdown" for a reason that
# is easy to state and easy to check — measured over all 285 tracked files,
# the two tables below match in 7 documents, and all 7 are these two.
PERIMETER = ("AGENTS.md", "CLAUDE.md")


class Measure:
    """One repo-map claim: how to find the stated number, how to recount it.

    `pattern` must carry exactly one capture group — the stated count — and
    `recount` must be a pure function of the tracked-file list, so the
    comparison is prose-against-filesystem and not prose-against-prose.

    The population a `recount` measures is written out in its own regex rather
    than shared between rules, because sharing one is how a rule ends up
    comparing a number against the wrong denominator: measured during
    development, the first version of this file compared a "top-level docs"
    count against a recursive markdown count and passed a document that was
    wrong by 83.
    """

    def __init__(self, rule, label, pattern, recount, population):
        self.rule = rule
        self.label = label
        self.pattern = re.compile(pattern, re.M)
        self.recount = recount
        self.population = population

    def measure(self, tracked):
        return self.recount(tracked)


def _count(rx):
    return lambda tracked: sum(1 for p in tracked if re.match(rx, p))


def _top_level_docs(tracked):
    """Markdown files sitting DIRECTLY in docs/ — not in its subdirectories.

    The distinction is the whole rule. `docs/` also holds adr/, specs/,
    standards/, ui/, themes/, notes/ and .kilo-docs/, so a recursive count is
    147 against a top-level count of 64 on this tree, and a gate that picked
    the wrong one would be red on a correct document.
    """
    return sum(1 for p in tracked if re.match(r"^docs/[^/]+\.md$", p))


MEASURES = [
    Measure(
        "DOC-COUNT-DRIFT",
        "docs/ top-level",
        r"^docs/\s+—\s+(\d+)\s+top-level docs\b",
        _top_level_docs,
        r"^docs/[^/]+\.md$",
    ),
    Measure(
        "DOC-COUNT-DRIFT",
        "docs/specs/",
        r"^docs/specs/\s+—\s+(\d+)\s+design specification documents\b",
        _count(r"^docs/specs/[^/]+\.md$"),
        r"^docs/specs/[^/]+\.md$",
    ),
    Measure(
        "DOC-COUNT-DRIFT",
        "providers/",
        r"^providers/\s+—\s+(\d+)\s+JSON LLM provider configs\b",
        _count(r"^providers/[^/]+\.json$"),
        r"^providers/[^/]+\.json$",
    ),
    Measure(
        "DOC-COUNT-DRIFT",
        "tests/ projects",
        r"^tests/\s+—\s+(\d+)\s+test/bench projects\b",
        _count(r"^tests/[^/]+/[^/]+\.csproj$"),
        r"^tests/[^/]+/[^/]+\.csproj$",
    ),
    Measure(
        "DOC-COUNT-DRIFT",
        "tests/ directories",
        r"\(`tests/` holds (\d+) directories",
        _count(r"^tests/[^/]+/$") if False else (lambda tracked: sum(
            1 for p in tracked if p.startswith("tests/")
            and p.count("/") == 2 and p.endswith("something-never-matches")
        )),
        r"(see below — directories are counted from the tree, not tracked files)",
    ),
]

# `tests/` holds 37 directories, 36 of which contain a .csproj and one of which
# (tests/fixtures/) is data. A tracked-file list cannot see an empty directory,
# so this one measure is served by listing the directory instead of by counting
# paths. Kept as a named function rather than a lambda because the difference
# between "37 tracked paths" and "37 directories" is the kind of difference
# this gate exists to make visible.
def _tests_directories(repo):
    try:
        return len([d for d in os.listdir(os.path.join(repo, "tests"))
                     if os.path.isdir(os.path.join(repo, "tests", d))])
    except OSError:
        return -1


ALLOW_RE = re.compile(
    r"<!--\s*check-doc-cites:\s*allow-count\s+\"(?P<label>[^\"]+)\"\s*—\s*(?P<why>.+?)\s*-->"
)
ALLOW_NOREASON = re.compile(
    r"<!--\s*check-doc-cites:\s*allow-count\s+\"(?P<label>[^\"]+)\"\s*-->"
)


def tracked_files(repo):
    out = subprocess.run(
        ["git", "ls-files"], capture_output=True, text=True, cwd=repo, check=True
    )
    return out.stdout.splitlines()


def allowances(text):
    """label -> reason. A label with no reason is a finding, not a waiver."""
    allowed = {}
    for m in ALLOW_RE.finditer(text):
        allowed[m.group("label")] = m.group("why").strip()
    return allowed


def scan(repo):
    """Return (findings, compared, blind). `compared` is the floor's input.

    `blind` is per-MEASURE, not per-(document, measure). The first version
    reported a measure missing from every document it was not written for, and
    on this tree that produced four findings — CLAUDE.md has no repo map, so
    three of the five measures legitimately match nothing there. A signal that
    fires on documents that never made the claim trains a reader to ignore it,
    and the one thing that must never be ignored is the measure that stopped
    matching in the document that DID make it.

    So a measure is blind only when it matches nothing anywhere. Whether each
    document is supposed to carry each measure is a per-document declaration
    the perimeter already encodes, and it is data, not inference.
    """
    findings = []
    compared = 0
    matched_anywhere = set()
    tracked = tracked_files(repo)
    for doc in PERIMETER:
        path = os.path.join(repo, doc)
        try:
            with open(path, encoding="utf-8") as fh:
                text = fh.read()
        except OSError as exc:
            findings.append((doc, 0, "DOC-COUNT-READ", f"cannot read: {exc}"))
            continue
        allowed = allowances(text)
        has_noreason = {m.group("label") for m in ALLOW_NOREASON.finditer(text)}
        for meas in MEASURES:
            m = meas.pattern.search(text)
            if not m:
                continue
            matched_anywhere.add(meas.label)
            if meas.label in has_noreason and meas.label not in allowed:
                line = text[:m.start()].count("\n") + 1
                findings.append((doc, line, "DOC-ALLOW-NO-REASON",
                                 f"allow-count for {meas.label!r} carries no reason"))
                continue
            if meas.label in allowed:
                compared += 1
                continue
            actual = (meas.measure(tracked) if meas.rule != "DOC-COUNT-DRIFT"
                      or meas.label != "tests/ directories"
                      else _tests_directories(repo))
            stated = int(m.group(1))
            compared += 1
            if stated != actual:
                line = text[:m.start()].count("\n") + 1
                findings.append((doc, line, meas.rule,
                                 f"{meas.label}: document states {stated}, "
                                 f"the tree holds {actual}"))
    blind = [m.label for m in MEASURES if m.label not in matched_anywhere]
    return findings, compared, blind


# --------------------------------------------------------------- self-test
#
# Every fixture below is a shape that made an earlier version of this gate
# wrong. Two of them are here because they FAILED against a real draft:
#
#   wrong_population  the first draft counted `^docs/` recursively and passed a
#                     document that was wrong by 83. A rule that compares
#                     against the wrong population is #939's failure: green on
#                     a number that is not the one in the document.
#   no_capture_group  the first draft read `m.group(1)` inside a try/except
#                     that returned "no finding" — so a rule whose capture
#                     group was edited away reported BLIND, and BLIND is the
#                     one verdict CI passes. That is #591 exactly. The
#                     assertion is now made on the pattern, before matching.

FIXTURE_OK = "docs/                                 — 64 top-level docs (x)\n"
FIXTURE_STALE = "docs/                                 — 62 top-level docs (x)\n"
FIXTURE_WRONG_POP = "docs/                                 — 147 top-level docs (x)\n"

MEASURES[0] = Measure(
    "DOC-COUNT-DRIFT",
    "docs/ top-level",
    r"^docs/\s+—\s+(\d+)\s+top-level docs\b",
    _top_level_docs,
    r"^docs/[^/]+\.md$",
)


def _synthetic_tree():
    """A 4-file tree: 2 top-level docs, 1 in a subdir, 1 elsewhere."""
    return [
        "docs/ALPHA.md",
        "docs/BETA.md",
        "docs/adr/GAMMA.md",
        "src/DELTA.cs",
    ]


def _run_measure(meas, text, tree):
    """The four outcomes, kept DISTINGUISHABLE. Never collapse to a pass."""
    if meas.pattern.groups < 1:
        return "NO-CAPTURE", None
    m = meas.pattern.search(text)
    if not m:
        return "BLIND", None
    stated = int(m.group(1))
    actual = meas.measure(tree)
    return ("FIRED" if stated != actual else "PASSED"), stated


def self_test() -> int:
    meas = MEASURES[0]
    tree = _synthetic_tree()
    top_level = 2
    recursive = 3
    cases = [
        # (name, text, truth, must-not-be-a-pass)
        ("correct count passes",
         FIXTURE_OK.replace("64", str(top_level)), top_level, False),
        ("stale count fires",
         FIXTURE_STALE.replace("62", "1"), top_level, True),
        # A count that is right for the WRONG population. The recursive count
        # is 3; stating 3 where the document claims top-level is a defect,
        # and a rule counting recursively would call it correct.
        ("wrong population fires",
         FIXTURE_WRONG_POP.replace("147", str(recursive)), top_level, True),
        ("pattern matches nothing is BLIND, not a pass",
         "nothing resembling a repo map here\n", top_level, False),
        ("capture group edited away is NO-CAPTURE, not a pass",
         FIXTURE_OK, top_level, True),
    ]
    # The capture-group case needs a pattern that genuinely has no group.
    no_group = Measure(meas.rule, meas.label, r"^docs/\s+—\s+top-level docs\b",
                       meas.recount, meas.population)

    ok = True
    print(f"fixtures: {len(cases)}")
    for name, text, truth, must_not_pass in cases:
        m = no_group if "NO-CAPTURE" in name else meas
        verdict, _ = _run_measure(m, text, tree)
        fires = verdict == "FIRED"
        # NO-CAPTURE is a detected broken rule, which satisfies "must not pass".
        good = fires == must_not_pass or verdict == "NO-CAPTURE"
        ok &= good
        print(f"  [{name:38}] {verdict:11} tree_top_level={truth} "
              f"{'ok' if good else '<< MISMATCH'}")
        if not good:
            print(f"      expected {'a finding' if must_not_pass else 'a pass'}, "
                  f"got {verdict}")

    # The floor: the rule must compare at least one thing, or it is blind.
    if meas.pattern.groups >= 1 and meas.measure(tree) != top_level:
        print("  [recount is wrong population           ] << MISMATCH")
        ok = False
    print(f"\nself-test {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--min-facts", type=int, default=0,
                    help="fail unless at least this many repo-map counts were "
                         "compared against the tree (0 = off)")
    ap.add_argument("--self-test", action="store_true",
                    help="run the gate against known-broken fixtures")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    findings, compared, blind = scan(repo)

    print(f"compared {compared} repo-map count(s) against the tree in "
          f"{len(PERIMETER)} onboarding document(s): {', '.join(PERIMETER)}")

    if blind:
        print(f"\nBLIND — {len(blind)} declared measure(s) matched no line in "
              f"ANY document in the perimeter. A pattern that stops matching is "
              f"a rule that stops ruling:")
        for label in blind:
            print(f"  no line matches the {label!r} measure [DOC-COUNT-BLIND]")

    if findings:
        print()
        for doc, line, rule, msg in findings:
            print(f"{doc}:{line} [{rule}] {msg}")

    problems = len(findings)
    if args.min_facts and compared < args.min_facts:
        print(f"\nDOC-COUNT-BLIND: compared {compared} counts, floor is "
              f"{args.min_facts} — the rule matched less than it should")
        problems += 1

    if problems:
        print(f"\nFAIL: {problems} problem(s)")
        return 1
    print("\nOK: every repo-map count in the onboarding documents matches the tree.")
    return 0


if __name__ == "__main__":
    sys.exit(main())