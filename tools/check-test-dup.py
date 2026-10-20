#!/usr/bin/env python3
"""Duplicate-test-body detector and CI gate for the tests/ tree (#1778).

Stdlib only — no pip, no network, no dotnet. It lives in `tools/` next to
check-md-links.py / md-lint.py / check-doc-cites.py / check-doc-facts.py /
check-abstractions-contract.py / check-public-api.py, and it asks a question
none of them asks:

  check-md-links.py             does the link resolve?
  md-lint.py                    is the file shaped the way the repo says it is?
  check-doc-cites.py            does a `file:line` still point at a line?
  check-doc-facts.py            does a COUNT a document states still equal the
                                count of tracked files it is counting?
  check-abstractions-contract.py is every <PackageReference> load-bearing?
  check-public-api.py           is the public surface still the reviewed one?
  THIS ONE                     is a test body a copy of another test body?

WHY THIS FILE EXISTS (issue #1778, the mandatory tail of epic #1808)

  Epic #1808 removes ~21 000 lines of duplicated test code (fake/stub
  generators #1769, cross-file clone families #1771, data-driven tables
  #1770, shared interfaces #1772, TempDirScope/EnvScope #1773, one golden
  framework #1776, one render harness #1777). Every one of those removals is
  undone by the next copy-paste, and nothing in the repository notices: there
  is no gate that says "this new test is a copy of that one". Rule 6 of the
  epic makes this file the condition for calling the programme closed — the
  removals without this gate are a one-quarter reprieve.

  So this is a REGRESSION gate, not a cleanup tool. It has to be true on the
  tree as it stands today (a gate that is red on the current tree is a gate
  nobody lands) and it has to be false the moment a duplicate is added.

THE DETECTION METHOD

  1. Scan `tests/**/*.cs`, minus `bin/`, `obj/` and `tests/Harbor.Architecture.Tests/`
     (the arch-guard tree belongs to programme #1724 — #1778 explicitly does
     not duplicate it).
  2. Lex each file into tokens. Comments are dropped; string literals, char
     literals and numeric literals are kept as distinct token kinds, because
     a `}` inside a string is not a brace and a `//` inside a raw string is
     not a comment.
  3. Find method bodies by brace matching: an identifier followed by a
     parameter list and then `{` or `=>`, whose preceding token is a type or
     a modifier (so `Assert.That(...)` — a call — is not mistaken for a
     declaration). Nested declarations (a local function inside a method) are
     dropped: their text is already part of the enclosing body.
  4. Normalise the body into a token stream:
       comments            -> dropped
       string literals    -> S
       char literals      -> C
       numeric literals   -> N
       identifiers        -> I
       everything else    -> kept, whitespace collapsed
     That is the method #1778 specifies ("нормализация тела метода: строковые
     литералы → S, идентификаторы → I, затем md5"). It is the method that
     finds what the programme is removing: eight copies of one 11-line
     "the tool's Name is X, its Glyph is Y" test differ only in their
     identifiers and their literals, and a detector that keeps identifiers
     does not see them at all.

  5. A shape match alone is NOT enough, and the reason is worth stating
     because it is the difference between a gate and a rumour: two bodies can
     have the same token shape and nothing else in common. So a group is
     only a duplicate when its members also share at least
     `--min-similarity` of their IDENTIFIER VOCABULARY (Jaccard over the
     identifier tokens). A near-copy shares the vocabulary — `tool`, `Name`,
     `Glyph`, `Assert`, `That`, `IsEqualTo` — and a coincidence does not.
     Measured on this tree: over the 229 groups the floor admits, the worst
     pair shares 0.50 of its identifiers and the median shares 1.00, so the
     0.50 floor keeps every real duplicate and rejects the coincidences —
     and it is not a number that excludes anything the programme wants to
     remove.

  6. A group also has to be big enough to be worth a review: `--min-lines`
     code lines (default 6) per body, `--min-members` members (2).

THE THRESHOLDS, AND WHY THEY ARE WHERE THEY ARE

  `--min-lines` (default 6) is the body-size floor. Below it the tool stays
  quiet: a three-line `Assert.That(x).IsTrue();` helper copied into a second
  file is a legitimate small overlap (the issue names it), and a gate that
  flags it produces hundreds of findings nobody reads, which is how gates get
  switched off. The floor is also what keeps the report a work list rather
  than a wall. Measured on this tree: 6 lines finds 229 groups / 4 285
  removable lines, 10 lines finds 88 / 2 430, and the issue's own
  measurement — which used no floor at all — found 598 intra-file methods
  and 208 cross-file ones. The floor is one flag away from being tightened.

  `--min-similarity` (default 0.50) is the near-copy floor described above.
  It is what lets the gate act on the shape match instead of only reporting
  it.

  `--min-members` is 2 and is not configurable: one member is not a duplicate.

  The issue's own blocking rule ("≥ 3 copies in one file", cross-file groups
  reported only) is REPLACED here by the baseline, for the reason the issue
  itself gives — "лечится baseline'ом" — and the busy zones the issue says
  the gate must not block (Harbor.Ui.Framework.Tests #1549, the storage
  projects #1534/#1686, CellForge/pty) are handled by the exemption list
  below rather than by a rule about where a group lives. The net effect is
  the one the programme needs: NEW duplication fails wherever it appears,
  and the duplication that exists today is allowed to stay until the
  programme removes it.

RULES

  DUP-NEW            A duplicate group whose MEMBERS the baseline does not
                     know: a copy that was added, or a group that did not
                     exist. New duplication — the thing this gate exists to
                     stop. The comparison is on the group's MEMBERS (the
                     paths and names of the methods in it), never on the text
                     of their bodies: the text changes every time somebody
                     edits a duplicated test, and an edit is not new
                     duplication. Keying on the text made the first version
                     of this gate go red on #1814, which rewrote the PTY wait
                     helpers in eleven duplicated files without adding a
                     single copy.
  DUP-GROWTH         Total duplicated code lines are above the baseline's.
                     Catches growth the per-group comparison cannot see.
  DUP-BASELINE-MISSING
                     The baseline file is absent. A gate that passes on a
                     deleted baseline is decorative.
  DUP-FLOOR          Fewer files or fewer methods were scanned than the
                     `--min-*` floors. A matcher that quietly stops matching
                     must go red, not quiet — the #591 shape.
  DUP-EXEMPT-INVALID  An exemption without a reason or without an expiry.
  DUP-EXEMPT-EXPIRED An exemption past its expiry date. An allowance that
                     nobody retires is an allowance that never ends.
  DUP-BASELINE-STALE The tree has LESS duplication than the baseline records.
                     This is reported, not failed: the removal PRs of #1808
                     are in flight at the same time, and failing on shrinkage
                     would make every one of them conflict on the same
                     baseline file. The baseline is lowered by one deliberate
                     `--update` when the programme lands.

BASELINE MODE — WHY NOT "FAIL ON THE WHOLE TREE"

  Measured on this tree: hundreds of duplicated bodies exist today (the
  census `--report` prints the exact numbers, and they are the numbers the
  baseline records). A gate that fails on those is a gate that cannot be
  landed, and a gate that is switched off protects nothing. So the shipped
  mode is BASELINE MODE: the whole tree is scanned, the census is always
  printed, groups that the baseline knows are reported as legacy, and only
  NEW or GROWN duplication fails. When #1770/#1771/#1772/#1773/#1769/#1776/
  #1777 land, the same script with `--update` lowers the baseline, and the
  floor tightens with it. The ratchet is the point: the baseline only ever
  goes DOWN (see DUP-BASELINE-STALE).

  `--report` is the measurement mode: census and every group, exit 0, no
  baseline comparison. It is how the numbers in this file's docstring were
  taken, and it is how the next duplication ticket takes its work list from
  the JSON artifact (`--json-out`) instead of re-measuring by hand.

EXEMPTIONS

  An exemption is a list of path globs plus a REASON and an EXPIRY DATE, and
  it applies only when EVERY member of a group matches it. It exists for the
  zones other programmes own (`Harbor.Ui.Framework.Tests` #1549,
  `Harbor.Storage.*` #1534/#1686, CellForge/pty) which #1778 says the gate
  must not block while they are being migrated: the duplicate is reported,
  and the gate stays quiet until the owning ticket lands. An exemption with
  no reason, no expiry, or an expiry in the past FAILS the gate; an
  exemption that matches nothing is reported so that it does not outlive its
  reason. Exemptions live in the baseline file, so they are reviewed in the
  same diff as the numbers they excuse.

NON-VACUITY

  `--self-test` runs fixtures against known-good and known-broken trees:
  an injected duplicate MUST be reported and MUST fail the gate; two bodies
  that merely have the same shape MUST NOT be reported; a group already in
  the baseline MUST pass; a group whose count grew MUST fail; REWRITING the
  body of every copy of a known group MUST still pass (the edit is not new
  duplication); a tree below the floors MUST fail (the blindness case); a
  missing baseline MUST fail; a three-line assert helper MUST NOT be
  flagged (the false-positive control); a brace inside a string literal and
  a `//` inside a raw string MUST NOT move the scanner; and the gate's own
  source MUST NOT be inside the scanned scope. The CI `selftest` job runs it
  on every PR that touches the gate.

WHAT THIS DOES NOT DO, STATED RATHER THAN IMPLIED

  * It does not decide what a test SHOULD look like. It reports bodies that
    are the same code; collapsing them into a table or a base class is the
    owner's call, and #1770/#1771 are where that call gets made.
  * It does not detect near-duplicate FILES (the Jaccard families #1771
    targets) as files. It detects duplicate bodies, which is the part that
    grows back by copy-paste; a file family shows up here as one group per
    method it shares.
  * It does not read any document, and it does not check the architecture of
    the product — that is #1724's job and this file stays out of its zone.
  * It does not enforce that duplicated tests become data-driven. It only
    says the copy is there.
"""

import argparse
import bisect
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from itertools import combinations

# ---------------------------------------------------------------------------
# Scope and policy constants
# ---------------------------------------------------------------------------

# The scanned tree. `tests/Harbor.Architecture.Tests` is the arch-guard
# project, owned by programme #1724; #1778 says explicitly that this gate
# does not duplicate it, so the exclusion is a scope decision and not an
# omission.
SCOPE_DIR = "tests"
EXCLUDED_DIRS = ("bin", "obj")
EXCLUDED_PATHS = ("tests/Harbor.Architecture.Tests",)

# The committed baseline. Same directory as the gate, so a PR that edits
# either one lands in the same diff — the discipline check-public-api.py has.
BASELINE_NAME = "test-dup-baseline.json"

# A body must carry at least this many code lines to count as a duplicate.
# Below it the tool stays quiet: a three-line assert helper copied into a
# second file is a legitimate small overlap, and a gate that flags hundreds
# of those produces a report nobody reads.
MIN_BODY_LINES = 6

# A group needs at least this many members. One member is not a duplicate.
MIN_MEMBERS = 2

# Two bodies with the same shape are near-copies only if they also share
# this much of their identifier vocabulary. Measured: the worst pair in the
# tree sits at 0.54, the median at 1.00.
MIN_SIMILARITY = 0.50

# ---------------------------------------------------------------------------
# Lexer
# ---------------------------------------------------------------------------

_IDENT_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_NUMBER_RE = re.compile(
    r"0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.[\d_]*)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*"
)

# Identifiers that, when they precede a `(`, mean the `(` belongs to an
# EXPRESSION rather than to a declaration. `await Foo(` is a call,
# `new Foo(` is a construction, `return Foo(` is a return: none of them is a
# method header, and treating them as one produces bodies that start and end
# in the wrong place.
_NOT_A_DECLARATION = frozenset(
    """
    return await new throw yield nameof typeof sizeof default is as in out ref
    when case else do var let value this base true false null switch if while
    for foreach lock using fixed checked unchecked stackalloc delegate from
    group join into orderby select where operator
    """.split()
)

# Tokens that may precede a method name and still mean "a type", because the
# type is written with a generic or an array suffix: `Task<T> Foo()`,
# `int[] Foo()`, `int? Foo()`.
_TYPE_SUFFIX_PUNCT = frozenset(">]?")


def _scan_string(src, start, verbatim, raw):
    """End offset of the string literal whose opening quote is at `start`."""
    n = len(src)
    if raw:
        quotes = 3
        while start + quotes < n and src[start + quotes] == '"':
            quotes += 1
        i = start + quotes
        while i < n:
            if src.startswith('"' * quotes, i):
                return i + quotes
            i += 1
        return n
    i = start + 1
    if verbatim:
        while i < n:
            if src[i] == '"':
                if i + 1 < n and src[i + 1] == '"':
                    i += 2
                    continue
                return i + 1
            i += 1
        return n
    while i < n:
        ch = src[i]
        if ch == "\\":
            i += 2
            continue
        if ch == '"':
            return i + 1
        if ch == "\n":
            return i
        i += 1
    return n


def _scan_char(src, start):
    """End offset of the char literal at `start`, or -1 when it is not one."""
    n = len(src)
    i = start + 1
    if i < n and src[i] == "\\":
        return i + 2 if i + 1 < n else n
    if i < n and src[i] == "'":
        return i + 1
    return -1


def lex(src):
    """Tokenise C# source into (kind, start, end) tuples.

    Kinds: ident | number | string | char | punct. Comments are dropped
    entirely — they carry no code, and a `//` inside a raw string is not a
    comment, which is why the raw-string branch runs before the line-comment
    branch can see it.

    String literals are ONE token, so a `}` inside a string never closes a
    brace and a `"` inside a comment never opens one. That is the whole
    reason this is a lexer and not a regex: an arch scanner and this gate
    both die on a brace in a string, and the difference between them is that
    one of them can be rewritten.
    """
    tokens = []
    i = 0
    n = len(src)
    while i < n:
        ch = src[i]
        if ch.isspace():
            i += 1
            continue
        if ch == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if ch == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        # String-ish: `@"..."`, `$"..."`, `$@"..."`, `"""..."""`, `$"""..."""`.
        quote_at = -1
        verbatim = False
        raw = False
        if ch == '"':
            quote_at = i
            raw = src.startswith('"""', i)
        elif ch == "@" and i + 1 < n and src[i + 1] == '"':
            quote_at = i + 1
            verbatim = True
        elif ch == "$" and i + 1 < n and src[i + 1] == '"':
            quote_at = i + 1
            raw = src.startswith('"""', i + 1)
        elif (
            ch == "$"
            and i + 2 < n
            and src[i + 1] == "@"
            and src[i + 2] == '"'
        ):
            quote_at = i + 2
            verbatim = True
        if quote_at >= 0:
            end = _scan_string(src, quote_at, verbatim, raw)
            tokens.append(("string", i, end))
            i = end
            continue
        if ch == "'":
            end = _scan_char(src, i)
            if end > 0:
                tokens.append(("char", i, end))
                i = end
                continue
        match = _IDENT_RE.match(src, i)
        if match:
            tokens.append(("ident", i, match.end()))
            i = match.end()
            continue
        match = _NUMBER_RE.match(src, i)
        if match:
            tokens.append(("number", i, match.end()))
            i = match.end()
            continue
        tokens.append(("punct", i, i + 1))
        i += 1
    return tokens


# ---------------------------------------------------------------------------
# Method extraction
# ---------------------------------------------------------------------------


class Method:
    """One method body found in a file."""

    __slots__ = (
        "path",
        "name",
        "ordinal",
        "start_line",
        "end_line",
        "lines",
        "is_test",
        "shape",
        "exact",
        "idents",
    )

    def __init__(self, path, name, start_line, end_line, lines,
                 is_test, shape, exact, idents):
        self.path = path
        self.name = name
        self.ordinal = 0
        self.start_line = start_line
        self.end_line = end_line
        self.lines = lines
        self.is_test = is_test
        self.shape = shape
        self.exact = exact
        self.idents = idents

    def where(self):
        return f"{self.path}:{self.start_line}-{self.end_line}"


def _line_starts(src):
    """Offsets at which each line starts, for O(log n) line lookups."""
    starts = [0]
    index = src.find("\n")
    while index >= 0:
        starts.append(index + 1)
        index = src.find("\n", index + 1)
    return starts


def _line_of(starts, offset):
    """1-based line number of `offset`, from a precomputed line table."""
    return bisect.bisect_right(starts, offset)


def _match_paren(tokens, index, src):
    """Index of the `)` closing the `(` at `index`, or -1."""
    depth = 0
    for i in range(index, len(tokens)):
        kind, start, end = tokens[i]
        if kind != "punct":
            continue
        ch = src[start]
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _match_angle(tokens, index, src):
    """Index of the `>` closing the `<` at `index`, or -1."""
    depth = 0
    for i in range(index, len(tokens)):
        kind, start, end = tokens[i]
        if kind != "punct":
            continue
        ch = src[start]
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _match_brace(tokens, index, src):
    """Index of the `}` closing the `{` at `index`, or -1."""
    depth = 0
    for i in range(index, len(tokens)):
        kind, start, end = tokens[i]
        if kind != "punct":
            continue
        ch = src[start]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _is_declaration_context(tokens, index, src):
    """Whether the identifier at `index` can be a method NAME.

    A method name is preceded by its return type or by a modifier. A call is
    preceded by a dot, by an operator, or by a keyword that makes the
    parentheses an expression (`await Foo(`, `new Foo(`, `return Foo(`).
    """
    if index == 0:
        return False
    kind, start, end = tokens[index - 1]
    if kind == "ident":
        return src[start:end] not in _NOT_A_DECLARATION
    if kind == "punct":
        return src[start] in _TYPE_SUFFIX_PUNCT
    return False


def _attribute_names(tokens, index, src):
    """Names of the attributes written immediately before a declaration.

    The attribute list is found by walking back to the statement boundary
    (the `;`, `{` or `}` that ends the previous statement) and asking
    whether the declaration starts with an attribute list. Walking back from
    the method NAME instead would have to guess whether a `]` closes an
    array type (`int[] Foo()`) or an attribute (`[Test] void Foo()`), and
    guessing is how a test file ends up with zero attributed methods.

    Only the first identifier of each attribute is taken, which is the
    attribute's name: `[Test]`, `[TestCase(...)]`, `[Arguments(1)]`.
    """
    boundary = -1
    for i in range(index - 1, -1, -1):
        kind, start, end = tokens[i]
        if kind == "punct" and src[start] in ";{}":
            boundary = i
            break
    if boundary < 0 or boundary + 1 >= index:
        return []
    kind, start, end = tokens[boundary + 1]
    if kind != "punct" or src[start] != "[":
        return []
    depth = 0
    close = -1
    for i in range(boundary + 1, index):
        k, s, e = tokens[i]
        if k != "punct":
            continue
        if src[s] == "[":
            depth += 1
        elif src[s] == "]":
            depth -= 1
            if depth == 0:
                close = i
                break
    if close < 0:
        return []
    names = []
    for i in range(boundary + 2, close):
        k, s, e = tokens[i]
        if k == "ident":
            name = src[s:e]
            if name not in names:
                names.append(name)
    return names


def _normalise(tokens, src, first, last):
    """The shape stream (identifiers erased) and the exact stream.

    The shape stream is what #1778 specifies: literals collapse to a marker
    so that two bodies differing only in a string or a number are the SAME
    body, and identifiers collapse so that two bodies differing only in the
    names they use are the same body too.

    The exact stream keeps the identifiers: two bodies that agree token for
    token, modulo literals, are the same CODE and are reported as such. It
    is the stronger signal of the two and it is what the report calls "exact
    copies".
    """
    shape = []
    exact = []
    idents = set()
    for i in range(first, last + 1):
        kind, start, end = tokens[i]
        if kind == "string":
            token = "S"
        elif kind == "char":
            token = "C"
        elif kind == "number":
            token = "N"
        elif kind == "ident":
            idents.add(src[start:end])
            token = "I"
        else:
            token = src[start:end]
        shape.append(token)
        if kind == "ident":
            exact.append(src[start:end])
        else:
            exact.append(token)
    return " ".join(shape), " ".join(exact), frozenset(idents)


def extract_methods(path, src):
    """Every method body in `src`, outermost first."""
    tokens = lex(src)
    starts = _line_starts(src)
    found = []
    index = 0
    count = len(tokens)
    while index < count:
        kind, start, end = tokens[index]
        if kind != "ident":
            index += 1
            continue
        name = src[start:end]
        if name in _NOT_A_DECLARATION:
            index += 1
            continue
        if not _is_declaration_context(tokens, index, src):
            index += 1
            continue
        nxt = index + 1
        # A generic method: `void Foo<T>(...)`.
        if (
            nxt < count
            and tokens[nxt][0] == "punct"
            and src[tokens[nxt][1]] == "<"
        ):
            close = _match_angle(tokens, nxt, src)
            if close < 0:
                index += 1
                continue
            nxt = close + 1
        if nxt >= count or tokens[nxt][0] != "punct" or src[tokens[nxt][1]] != "(":
            index += 1
            continue
        close = _match_paren(tokens, nxt, src)
        if close < 0:
            index += 1
            continue
        tail = close + 1
        if tail >= count or tokens[tail][0] != "punct":
            index += 1
            continue
        tail_char = src[tokens[tail][1]]
        if tail_char == "{":
            body_open = tail
            body_close = _match_brace(tokens, body_open, src)
            if body_close < 0:
                index += 1
                continue
            body_first = body_open + 1
            body_last = body_close - 1
            end_offset = tokens[body_close][2]
        elif tail_char == "=" and tail + 1 < count and src[tokens[tail + 1][1]] == ">":
            # Expression-bodied member: `int Foo() => 42;`
            body_first = tail + 2
            body_last = body_first
            semi = None
            depth = 0
            for i in range(body_first, count):
                k, s, e = tokens[i]
                if k != "punct":
                    continue
                ch = src[s]
                if ch in "([{":
                    depth += 1
                elif ch in ")]}":
                    depth -= 1
                elif ch == ";" and depth == 0:
                    semi = i
                    break
            if semi is None:
                index += 1
                continue
            body_last = semi - 1
            end_offset = tokens[semi][2]
        else:
            index += 1
            continue

        if body_last < body_first:
            index = tail + 1
            continue

        attributes = _attribute_names(tokens, index, src)
        is_test = any(a in ("Test", "TestCase") for a in attributes)
        shape, exact, idents = _normalise(tokens, src, body_first, body_last)
        code_lines = len(
            {
                _line_of(starts, tokens[i][1])
                for i in range(body_first, body_last + 1)
            }
        )
        found.append(
            Method(
                path=path,
                name=name,
                start_line=_line_of(starts, tokens[body_first][1]),
                end_line=_line_of(starts, end_offset),
                lines=code_lines,
                is_test=is_test,
                shape=hashlib.sha1(shape.encode("utf-8")).hexdigest(),
                exact=hashlib.sha1(exact.encode("utf-8")).hexdigest(),
                idents=idents,
            )
        )
        index = tail + 1

    # Drop nested declarations: a local function's text is already part of the
    # enclosing body, and counting it twice would make one copy look like two.
    found.sort(key=lambda m: (m.start_line, -m.end_line))
    outermost = []
    for method in found:
        if outermost and method.start_line >= outermost[-1].start_line \
                and method.end_line <= outermost[-1].end_line:
            continue
        outermost.append(method)

    # A method's identity must be unique inside its file: one test class can
    # hold five methods called `CreateContext`, and a key that collides would
    # make a group look unchanged when one more copy is added — which is
    # exactly the hole this gate exists to close. The ordinal counts the
    # same-named methods of the file in source order, so it survives a body
    # edit and a parameter rename, and only moves when a same-named method
    # is inserted before it.
    per_name = {}
    for method in outermost:
        per_name.setdefault(method.name, []).append(method)
    for siblings in per_name.values():
        for index, method in enumerate(siblings):
            method.ordinal = index
    return outermost


# ---------------------------------------------------------------------------
# Scanning the tree
# ---------------------------------------------------------------------------


def _excluded(path):
    parts = path.split("/")
    if any(part in EXCLUDED_DIRS for part in parts):
        return True
    for excluded in EXCLUDED_PATHS:
        if path == excluded or path.startswith(excluded + "/"):
            return True
    return False


def scan_files(root):
    """Repo-relative paths of every scanned .cs file, sorted."""
    base = os.path.join(root, SCOPE_DIR)
    found = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDED_DIRS)
        for name in sorted(filenames):
            if not name.endswith(".cs"):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if _excluded(rel):
                continue
            found.append(rel)
    return sorted(found)


def scan_tree(root):
    """Every method body in the scanned tree, plus the census counters."""
    methods = []
    files = 0
    lines = 0
    for rel in scan_files(root):
        full = os.path.join(root, rel)
        try:
            with open(full, encoding="utf-8", errors="replace") as handle:
                src = handle.read()
        except OSError:
            continue
        files += 1
        lines += src.count("\n") + 1
        methods.extend(extract_methods(rel, src))
    return methods, {
        "files": files,
        "lines": lines,
        "methods": len(methods),
        "test_methods": sum(1 for m in methods if m.is_test),
    }


def _jaccard(left, right):
    union = len(left | right)
    if not union:
        return 1.0
    return len(left & right) / union


def build_groups(methods, min_lines=MIN_BODY_LINES, min_members=MIN_MEMBERS,
                 min_similarity=MIN_SIMILARITY):
    """Duplicate groups, keyed by the shape fingerprint of the body.

    A group is a set of methods with the same normalised shape. It counts as
    a duplicate only when its members also share enough of their identifier
    vocabulary: two bodies can have the same token shape and nothing else in
    common, and a coincidence is not a duplicate.

    The returned groups carry the exact-copy count as well: within a group,
    the members that are identical modulo literals are "exact copies" and
    the rest are near-copies. Both are duplication; the exact ones are the
    easier to see.

    Each group also carries its MEMBER KEY — the hash of the sorted
    `path::method` list. That, not the body fingerprint, is what the
    baseline is keyed on: see the DUP-NEW rule.
    """
    buckets = {}
    for method in methods:
        if method.lines < min_lines:
            continue
        buckets.setdefault(method.shape, []).append(method)
    groups = []
    coincidences = 0
    for shape, members in buckets.items():
        if len(members) < min_members:
            continue
        worst = 1.0
        for left, right in combinations(members, 2):
            worst = min(worst, _jaccard(left.idents, right.idents))
            if worst < min_similarity:
                break
        if worst < min_similarity:
            coincidences += 1
            continue
        exact_buckets = {}
        for member in members:
            exact_buckets.setdefault(member.exact, []).append(member)
        exact_members = [m for v in exact_buckets.values() if len(v) >= 2 for m in v]
        files = sorted({m.path for m in members})
        member_keys = sorted(
            f"{m.path}::{m.name}#{m.ordinal}" for m in members
        )
        groups.append(
            {
                "fingerprint": shape,
                "member_key": hashlib.sha1(
                    "\n".join(member_keys).encode("utf-8")
                ).hexdigest(),
                "member_keys": member_keys,
                "members": sorted(members, key=lambda m: (m.path, m.start_line)),
                "count": len(members),
                "exact_count": len(exact_members),
                "files": files,
                "file_count": len(files),
                "lines": members[0].lines,
                "duplicate_lines": (len(members) - 1) * members[0].lines,
                "intra_file": len(files) == 1,
                "is_test": all(m.is_test for m in members),
                "similarity": round(worst, 3),
            }
        )
    groups.sort(key=lambda g: (-g["duplicate_lines"], -g["count"], g["fingerprint"]))
    return groups, coincidences


def census(groups, scan, coincidences=0):
    """The headline numbers: groups, and the lines they make removable."""
    intra = [g for g in groups if g["intra_file"]]
    cross = [g for g in groups if not g["intra_file"]]
    return {
        "groups": len(groups),
        "intra_file_groups": len(intra),
        "cross_file_groups": len(cross),
        "exact_groups": sum(1 for g in groups if g["exact_count"] >= 2),
        "duplicate_lines": sum(g["duplicate_lines"] for g in groups),
        "group_lines": sum(g["count"] * g["lines"] for g in groups),
        "shape_coincidences": coincidences,
        "files": scan["files"],
        "lines": scan["lines"],
        "methods": scan["methods"],
        "test_methods": scan["test_methods"],
    }


# ---------------------------------------------------------------------------
# Baseline
# ---------------------------------------------------------------------------


def baseline_path(root):
    return os.path.join(root, "tools", BASELINE_NAME)


def load_baseline(root):
    """The committed baseline, or None when it is absent."""
    path = baseline_path(root)
    if not os.path.isfile(path):
        return None
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def write_baseline(root, groups, scan, min_lines, min_similarity):
    """Regenerate the baseline file from the current tree."""
    existing = load_baseline(root) or {}
    exemptions = existing.get("exemptions") or []
    rows = exempt_paths(exemptions)
    blocking = [g for g in groups if group_exempt(g, rows) < 0]
    payload = {
        "version": 1,
        "generator": "tools/check-test-dup.py",
        "policy": {
            "scope": "tests/**/*.cs minus bin/, obj/ and tests/Harbor.Architecture.Tests",
            "min_body_lines": min_lines,
            "min_members": MIN_MEMBERS,
            "min_similarity": min_similarity,
            "normalisation": (
                "comments dropped; string literals -> S; char literals -> C; "
                "numeric literals -> N; identifiers -> I; whitespace collapsed"
            ),
        },
        "census": {
            "files": scan["files"],
            "lines": scan["lines"],
            "methods": scan["methods"],
            "test_methods": scan["test_methods"],
        },
        "metrics": census(groups, scan),
        "blocking_duplicate_lines": sum(g["duplicate_lines"] for g in blocking),
        "groups": {
            g["member_key"]: {
                "members": g["member_keys"],
                "count": g["count"],
                "lines": g["lines"],
                "files": g["file_count"],
                "exact": g["exact_count"],
                "shape": g["fingerprint"],
            }
            for g in sorted(groups, key=lambda g: g["member_key"])
        },
        "exemptions": exemptions,
    }
    path = baseline_path(root)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2, sort_keys=False)
        handle.write("\n")
    return path


# ---------------------------------------------------------------------------
# Exemptions
# ---------------------------------------------------------------------------


def _glob_to_regex(pattern):
    out = ["^"]
    i = 0
    while i < len(pattern):
        ch = pattern[i]
        if ch == "*":
            if pattern.startswith("**", i):
                out.append(".*")
                i += 2
                continue
            out.append("[^/]*")
            i += 1
            continue
        if ch == "?":
            out.append("[^/]")
            i += 1
            continue
        out.append(re.escape(ch))
        i += 1
    out.append("$")
    return re.compile("".join(out))


def _match_glob(pattern, path):
    if pattern.endswith("/**"):
        prefix = pattern[:-3]
        return path == prefix or path.startswith(prefix + "/")
    if "**" in pattern or "*" in pattern or "?" in pattern:
        return bool(_glob_to_regex(pattern).match(path))
    return path == pattern


def exempt_paths(exemptions):
    """Flatten the exemption list into (patterns, reason, expires) rows."""
    rows = []
    for entry in exemptions or []:
        if not isinstance(entry, dict):
            continue
        patterns = entry.get("paths") or []
        if isinstance(patterns, str):
            patterns = [patterns]
        rows.append(
            (
                list(patterns),
                (entry.get("reason") or "").strip(),
                (entry.get("expires") or "").strip(),
            )
        )
    return rows


def group_exempt(group, rows):
    """The index of the exemption covering this group, or -1.

    An exemption covers a group only when EVERY member of the group is
    inside it: a duplicate that reaches outside the exempted zone is still a
    duplicate of code that is not being migrated.
    """
    for index, (patterns, reason, expires) in enumerate(rows):
        if not patterns:
            continue
        if all(
            any(_match_glob(pattern, member.path) for pattern in patterns)
            for member in group["members"]
        ):
            return index
    return -1


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------


def _today():
    return datetime.date.today().isoformat()


def run_gate(root, min_lines=MIN_BODY_LINES, min_members=MIN_MEMBERS,
             min_similarity=MIN_SIMILARITY, min_files=0, min_methods=0,
             changed_files=None):
    """Findings, groups and the census. The gate's whole decision."""
    methods, scan = scan_tree(root)
    groups, coincidences = build_groups(methods, min_lines, min_members,
                                        min_similarity)

    narrowed = changed_files is not None
    if narrowed:
        groups = [
            g
            for g in groups
            if any(member.path in changed_files for member in g["members"])
        ]

    measured = census(groups, scan, coincidences)
    findings = []
    warnings = []

    baseline = load_baseline(root)
    if baseline is None:
        findings.append(
            f"DUP-BASELINE-MISSING {BASELINE_NAME}: no baseline committed under "
            f"tools/ — the gate passes on nothing. Generate one with "
            f"`python3 tools/check-test-dup.py --update` and commit it."
        )
        return findings, warnings, groups, measured, []

    known = baseline.get("groups") or {}
    known_metrics = baseline.get("metrics") or {}

    rows = exempt_paths(baseline.get("exemptions"))
    for index, (patterns, reason, expires) in enumerate(rows):
        if not reason:
            findings.append(
                f"DUP-EXEMPT-INVALID exemption #{index} ({', '.join(patterns)}) has "
                f"no reason — an allowance with no reader is a hole"
            )
        if not expires:
            findings.append(
                f"DUP-EXEMPT-INVALID exemption #{index} ({', '.join(patterns)}) has "
                f"no expiry — an allowance that never ends is not an allowance"
            )

    # An exempt group is reported but not failed: the zones other programmes
    # own are being migrated by them, and #1778 says the gate must not block
    # them. It is still counted as "used" so that an exemption nobody needs
    # does not outlive its reason.
    used = set()
    blocking = []
    for group in groups:
        cover = group_exempt(group, rows)
        if cover >= 0:
            used.add(cover)
            continue
        blocking.append(group)

    # A group is matched to the baseline by its MEMBERS, not by the text of
    # its body. The text changes every time somebody edits a duplicated
    # test — and an edit that changes the text of ONE copy breaks the
    # duplication, while an edit that changes the text of ALL copies keeps it
    # and must not be reported as new duplication. Keying on the body made
    # the gate red on the first such edit; keying on the members makes it
    # red only when a COPY is added, which is what it is for.
    baseline_groups = []
    for entry in known.values():
        baseline_groups.append(
            (set(entry.get("members") or []), entry)
        )

    offenders = []
    for group in blocking:
        members = set(group["member_keys"])
        subset_of = None
        superset_of = None
        same_shape_of = None
        for base_members, entry in baseline_groups:
            if members <= base_members:
                subset_of = entry
                break
            if base_members < members:
                superset_of = entry
            if (
                same_shape_of is None
                and len(base_members) == len(members)
                and len({m.split("::")[0] for m in base_members})
                == group["file_count"]
            ):
                same_shape_of = entry
        if subset_of is not None:
            # Known: the same methods are still duplicated (the body may have
            # been rewritten on purpose — that is not new duplication).
            continue
        offenders.append(group)
        if superset_of is not None:
            findings.append(
                f"DUP-NEW {group['members'][0].where()} x {group['count']} copies "
                f"({group['lines']} code lines each) — the baseline recorded "
                f"{superset_of.get('count')} copies of these bodies; "
                f"{group['count'] - superset_of.get('count', 0)} more were added"
            )
        elif same_shape_of is not None:
            findings.append(
                f"DUP-NEW {group['members'][0].where()} x {group['count']} copies "
                f"({group['lines']} code lines each) — the baseline has a group "
                f"with the same {group['file_count']} file(s) and the same "
                f"count under other names; if the bodies were rewritten on "
                f"purpose, regenerate the baseline with `--update`"
            )
        else:
            findings.append(
                f"DUP-NEW {group['members'][0].where()} x {group['count']} copies "
                f"({group['lines']} code lines each) — no such group in "
                f"{BASELINE_NAME}; this body is new duplication"
            )

    # The aggregate comparisons only make sense over the WHOLE tree: a
    # narrowed run (`--new-since`) sees a fraction of the duplication, so
    # comparing its total against a whole-tree baseline would report a
    # shrinkage that did not happen.
    if not narrowed:
        blocking_lines = sum(g["duplicate_lines"] for g in blocking)
        baseline_lines = known_metrics.get("duplicate_lines")
        baseline_blocking = baseline.get("blocking_duplicate_lines")
        if isinstance(baseline_blocking, int) and blocking_lines > baseline_blocking:
            findings.append(
                f"DUP-GROWTH duplicate lines {blocking_lines} > baseline "
                f"{baseline_blocking} — the tree grew more duplication than the "
                f"baseline allows"
            )
        if isinstance(baseline_lines, int) and measured["duplicate_lines"] < baseline_lines:
            warnings.append(
                f"DUP-BASELINE-STALE duplicate lines {measured['duplicate_lines']} < "
                f"baseline {baseline_lines} — the tree got cleaner; lower the "
                f"baseline with `--update` when the programme lands"
            )

    if min_files and scan["files"] < min_files:
        findings.append(
            f"DUP-FLOOR files {scan['files']} < --min-files {min_files} — the "
            f"scanner sees less of the tree than it should"
        )
    if min_methods and scan["methods"] < min_methods:
        findings.append(
            f"DUP-FLOOR methods {scan['methods']} < --min-methods {min_methods} "
            f"— the scanner sees less of the tree than it should"
        )

    for index, (patterns, reason, expires) in enumerate(rows):
        if index in used:
            if expires and expires < _today():
                findings.append(
                    f"DUP-EXEMPT-EXPIRED exemption #{index} "
                    f"({', '.join(patterns)}) expired on {expires} — renew it in "
                    f"writing or remove it"
                )
        elif not narrowed:
            # A narrowed run sees a fraction of the tree, so "matches nothing"
            # says nothing about the exemption. Only a whole-tree run can
            # report an exemption that has outlived its reason.
            warnings.append(
                f"DUP-EXEMPT-UNUSED exemption #{index} ({', '.join(patterns)}) "
                f"matches no duplicate group — remove it rather than let it "
                f"outlive its reason"
            )

    return findings, warnings, groups, measured, offenders


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------


def describe_group(group):
    """One actionable block per duplicate group, plus its other members."""
    head = group["members"][0]
    lines = [f"  DUP  {head.where()} x {group['count']} copies, {group['lines']} "
             f"code lines each ({group['duplicate_lines']} removable lines)"]
    for member in group["members"][1:]:
        lines.append(f"       also {member.where()}  ({member.name})")
    lines.append(
        f"       shape sha1 {group['fingerprint'][:12]}, identifier similarity "
        f"{group['similarity']}, {group['exact_count']}/{group['count']} exact "
        f"copies"
    )
    return "\n".join(lines)


def print_report(groups, measured):
    """The census and the groups. `--report` prints all of them."""
    print(f"scanned      {measured['files']} .cs files under {SCOPE_DIR}/ "
          f"({measured['lines']} lines)")
    print(f"methods      {measured['methods']} bodies "
          f"({measured['test_methods']} attributed to [Test])")
    print(f"groups       {measured['groups']} duplicate groups "
          f"({measured['intra_file_groups']} intra-file, "
          f"{measured['cross_file_groups']} cross-file, "
          f"{measured['exact_groups']} with exact copies)")
    print(f"duplicate    {measured['duplicate_lines']} removable code lines "
          f"({measured['group_lines']} lines inside duplicated bodies)")
    print(f"coincidences {measured['shape_coincidences']} same-shape groups "
          f"rejected for sharing too few identifiers")
    if not groups:
        return
    for group in groups:
        print()
        print(describe_group(group))


def write_artifact(path, groups, measured):
    """The JSON artifact: the work list the next duplication ticket takes."""
    payload = {
        "generator": "tools/check-test-dup.py",
        "census": measured,
        "groups": [
            {
                "fingerprint": g["fingerprint"],
                "copies": g["count"],
                "exact_copies": g["exact_count"],
                "files": g["file_count"],
                "lines": g["lines"],
                "duplicate_lines": g["duplicate_lines"],
                "intra_file": g["intra_file"],
                "similarity": g["similarity"],
                "members": [
                    {
                        "path": m.path,
                        "name": m.name,
                        "start_line": m.start_line,
                        "end_line": m.end_line,
                        "lines": m.lines,
                        "is_test": m.is_test,
                    }
                    for m in g["members"]
                ],
            }
            for g in groups
        ],
    }
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)
        handle.write("\n")
    return path


# ---------------------------------------------------------------------------
# --new-since
# ---------------------------------------------------------------------------


def changed_files_since(root, ref):
    """Repo-relative paths changed between `ref` and the working tree.

    None means "cannot tell" — the caller then says so rather than
    pretending the branch touched nothing.
    """
    try:
        out = subprocess.run(
            ["git", "diff", "--name-only", f"{ref}...HEAD"],
            cwd=root,
            capture_output=True,
            text=True,
            check=False,
        )
    except (OSError, ValueError):
        return None
    if out.returncode != 0:
        return None
    return {
        line.strip().replace(os.sep, "/")
        for line in out.stdout.splitlines()
        if line.strip()
    }


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

BODY_A = """
    [Test]
    public async Task Alpha_Works()
    {
        var store = CreateStore();
        try
        {
            var result = await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/test/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
"""

# The same code with different literals: an EXACT copy modulo literals.
BODY_B = """
    [Test]
    public async Task Beta_Works()
    {
        var store = CreateStore();
        try
        {
            var result = await store.CreateAsync("/other/dir", "plan", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/other/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("plan");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
"""

# The same SHAPE with a renamed helper: a near copy. It is the case #1770
# turns into an `[Arguments]` table, so it has to be reported — and the
# shared identifier vocabulary is what tells it apart from a coincidence.
BODY_C = """
    [Test]
    public async Task Gamma_Works()
    {
        var tool = CreateTool();
        try
        {
            var result = await tool.CreateAsync("/third/dir", "code", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/third/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
        }
        finally
        {
            if (Directory.Exists(tool.GetRootDirectory())) Directory.Delete(tool.GetRootDirectory(), true);
        }
    }
"""

# The same SHAPE and almost nothing else in common: a coincidence, not a
# duplicate. Two bodies can be shaped alike and have nothing to do with each
# other, and a gate that calls that duplication is a gate that gets switched
# off. The identifier vocabulary is what separates the two cases.
BODY_D = """
    [Test]
    public void Delta_Works()
    {
        var counter = new Counter();
        counter.Add(1);
        counter.Add(2);
        counter.Add(3);
        var total = counter.Total();
        Assert.That(total).IsEqualTo(6);
    }
"""

BODY_E = """
    [Test]
    public void Epsilon_Works()
    {
        var ledger = new Ledger();
        ledger.Add(1);
        ledger.Add(2);
        ledger.Add(3);
        var sum = ledger.Sum();
        Assert.That(sum).IsEqualTo(6);
    }
"""

# Three lines: below the floor, and therefore not a duplicate worth a report.
TINY_A = """
    [Test]
    public void Tiny_One()
    {
        Assert.That(1).IsEqualTo(1);
    }
"""

TINY_B = """
    [Test]
    public void Tiny_Two()
    {
        Assert.That(2).IsEqualTo(2);
    }
"""

HEADER = """using System;
using System.IO;
using System.Threading.Tasks;
using TUnit.Assertions;

namespace Harbor.Demo.Tests;

public class {cls}
{{
    private static object CreateStore() => new object();

{body}}}
"""


def _write_tree(tmp, files):
    for name, text in files.items():
        path = os.path.join(tmp, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)


def _fixture(files):
    """A throwaway tree plus an EMPTY baseline that goes with it."""
    tmp = tempfile.mkdtemp(prefix="test-dup-gate-")
    _write_tree(tmp, files)
    payload = {
        "version": 1,
        "metrics": {"duplicate_lines": 0},
        "blocking_duplicate_lines": 0,
        "groups": {},
        "exemptions": [],
    }
    tools = os.path.join(tmp, "tools")
    os.makedirs(tools, exist_ok=True)
    with open(os.path.join(tools, BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    return tmp


def _write_fixture_baseline(tmp, groups, metrics=None):
    payload = {
        "version": 1,
        "metrics": metrics or {"duplicate_lines": 0},
        "blocking_duplicate_lines": metrics.get("duplicate_lines", 0)
        if metrics
        else 0,
        "groups": {
            g["member_key"]: {
                "members": g["member_keys"],
                "count": g["count"],
                "lines": g["lines"],
                "files": g["file_count"],
                "exact": g["exact_count"],
                "shape": g["fingerprint"],
            }
            for g in groups
        },
        "exemptions": [],
    }
    with open(os.path.join(tmp, "tools", BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)


def selftest():
    """The non-vacuity proof: this gate must FAIL on a planted duplicate."""
    failures = []
    ran = []

    def check(name, condition, detail=""):
        ran.append(name)
        if condition:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name} {detail}")
            failures.append(name)

    tree_a = HEADER.format(cls="AlphaTests", body=BODY_A)
    tree_b = HEADER.format(cls="BetaTests", body=BODY_B)
    tree_c = HEADER.format(cls="GammaTests", body=BODY_C)
    tree_d = HEADER.format(cls="DeltaTests", body=BODY_D)
    tree_e = HEADER.format(cls="EpsilonTests", body=BODY_E)

    # 1. THE POSITIVE CONTROL. Two files with the same body: the gate MUST
    #    find it and MUST fail. A gate that would pass on this tree is
    #    decorative, and that is the whole claim of this file.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a,
                    "tests/Demo.Tests/BetaTests.cs": tree_b})
    findings, _, groups, measured, _ = run_gate(tmp)
    check(
        "an injected duplicate is reported",
        any(f.startswith("DUP-NEW") for f in findings),
        str(findings),
    )
    check(
        "an injected duplicate is a group with two members",
        len(groups) == 1 and groups[0]["count"] == 2 and groups[0]["file_count"] == 2,
        str(groups),
    )
    check(
        "an injected duplicate names both files and the line range",
        groups
        and "AlphaTests.cs" in groups[0]["members"][0].path
        and groups[0]["members"][0].start_line > 0
        and groups[0]["members"][0].end_line > groups[0]["members"][0].start_line,
        str(groups),
    )
    check(
        "the report says which other file duplicates it",
        groups and "also tests/Demo.Tests/BetaTests.cs" in describe_group(groups[0]),
        describe_group(groups[0]) if groups else "",
    )
    check(
        "the injected duplicate is an EXACT copy modulo literals",
        groups and groups[0]["exact_count"] == 2,
        str(groups),
    )

    # 2. A near copy — same shape, different identifiers — is the case
    #    #1770 turns into a table. It must be reported too.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a,
                    "tests/Demo.Tests/GammaTests.cs": tree_c})
    findings, _, groups, _, _ = run_gate(tmp)
    check(
        "a near copy (same shape, renamed identifiers) is reported",
        len(groups) == 1 and groups[0]["count"] == 2 and groups[0]["exact_count"] == 0,
        str(groups),
    )

    # 3. THE NEGATIVE CONTROL. Two bodies that merely have the same shape
    #    and no vocabulary in common must NOT be reported: that is a
    #    coincidence, and a gate that calls coincidences duplication is a
    #    gate that gets switched off.
    tmp = _fixture({"tests/Demo.Tests/DeltaTests.cs": tree_d,
                    "tests/Demo.Tests/EpsilonTests.cs": tree_e})
    findings, _, groups, measured, _ = run_gate(tmp)
    check(
        "a same-shape body with unrelated identifiers is not a duplicate",
        not groups,
        str(groups),
    )
    check(
        "the rejected coincidence is counted, not hidden",
        measured["shape_coincidences"] == 1,
        str(measured),
    )

    # 4. The threshold floor: a three-line assert helper copied into another
    #    file is a legitimate small overlap and must stay quiet.
    tmp = _fixture(
        {
            "tests/Demo.Tests/TinyOne.cs": HEADER.format(cls="TinyOne", body=TINY_A),
            "tests/Demo.Tests/TinyTwo.cs": HEADER.format(cls="TinyTwo", body=TINY_B),
        }
    )
    findings, _, groups, _, _ = run_gate(tmp)
    check(
        "a three-line assert helper is not flagged",
        not findings and not groups,
        str(findings) + str(groups),
    )

    # 5. A group already in the baseline MUST pass: this is what makes the
    #    gate landable on today's tree.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a,
                    "tests/Demo.Tests/BetaTests.cs": tree_b})
    methods, _ = scan_tree(tmp)
    groups_now, _ = build_groups(methods)
    _write_fixture_baseline(
        tmp,
        groups_now,
        {"duplicate_lines": sum(g["duplicate_lines"] for g in groups_now)},
    )
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "a group the baseline already knows stays green",
        not findings,
        str(findings),
    )

    # 6. The same group with ONE MORE copy MUST fail, even though the
    #    baseline still lists it: "the baseline knows this body" is not
    #    "this body may have any number of copies".
    tree_e = HEADER.format(cls="EpsilonTests", body=BODY_A)
    _write_tree(tmp, {"tests/Demo.Tests/EpsilonTests.cs": tree_e})
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "one more copy of a known body fails",
        any("more were added" in f for f in findings),
        str(findings),
    )

    # 7. A missing baseline MUST fail. A gate that passes on a deleted
    #    baseline is decorative.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a})
    os.remove(os.path.join(tmp, "tools", BASELINE_NAME))
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "a missing baseline fails",
        any(f.startswith("DUP-BASELINE-MISSING") for f in findings),
        str(findings),
    )

    # 8. The blindness case: a tree below the floors MUST fail, so a matcher
    #    that quietly stops matching goes red instead of reporting a clean
    #    tree.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a})
    findings, _, _, _, _ = run_gate(tmp, min_files=100)
    check(
        "a scan below the file floor fails",
        any(f.startswith("DUP-FLOOR") for f in findings),
        str(findings),
    )
    findings, _, _, _, _ = run_gate(tmp, min_methods=100)
    check(
        "a scan below the method floor fails",
        any(f.startswith("DUP-FLOOR") for f in findings),
        str(findings),
    )

    # 9. A brace inside a string literal and a `//` inside a raw string must
    #    not move the scanner: the body must still be found, and the file
    #    must still parse into two methods rather than three.
    tricky = (
        "namespace Harbor.Demo.Tests;\n"
        "\n"
        "public class TrickyTests\n"
        "{\n"
        "    [Test]\n"
        "    public void Tricky()\n"
        "    {\n"
        "        var brace = \"}\";\n"
        "        var raw = \"\"\"\n"
        "        // not a comment }\n"
        "        \"\"\";\n"
        "        Assert.That(brace).IsNotNull();\n"
        "        Assert.That(raw).IsNotNull();\n"
        "    }\n"
        "\n"
        "    [Test]\n"
        "    public void Other()\n"
        "    {\n"
        "        Assert.That(1).IsEqualTo(1);\n"
        "    }\n"
        "}\n"
    )
    tmp = _fixture({"tests/Demo.Tests/TrickyTests.cs": tricky})
    methods, _ = scan_tree(tmp)
    names = sorted(m.name for m in methods)
    check(
        "a brace in a string and a // in a raw string do not move the scanner",
        names == ["Other", "Tricky"],
        str(names),
    )

    # 10. The gate's own source MUST NOT be inside the scanned scope: a rule
    #     that finds nothing because it is looking at its own text reports the
    #     zero as a result.
    here = os.path.realpath(__file__)
    repo = os.path.dirname(os.path.dirname(here))
    check(
        "the gate's own source is outside the scanned scope",
        not here.startswith(os.path.realpath(os.path.join(repo, SCOPE_DIR)) + os.sep),
        here,
    )

    # 11. An exemption without a reason or an expiry MUST fail, an expired
    #     one MUST fail, a valid one MUST cover its zone, and one that
    #     matches nothing MUST be reported: an allowance with no reader is a
    #     hole, and one that outlives its reason is worse.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a,
                    "tests/Demo.Tests/BetaTests.cs": tree_b})
    payload = {
        "version": 1,
        "metrics": {"duplicate_lines": 0},
        "blocking_duplicate_lines": 0,
        "groups": {},
        "exemptions": [{"paths": ["tests/Demo.Tests/**"]}],
    }
    with open(os.path.join(tmp, "tools", BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "an exemption without a reason or an expiry fails",
        sum(1 for f in findings if f.startswith("DUP-EXEMPT-INVALID")) == 2,
        str(findings),
    )
    payload["exemptions"] = [
        {
            "paths": ["tests/Demo.Tests/**"],
            "reason": "busy zone owned by another programme",
            "expires": "2999-01-01",
        }
    ]
    with open(os.path.join(tmp, "tools", BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "a valid exemption covers the group",
        not any(f.startswith("DUP-NEW") for f in findings),
        str(findings),
    )
    payload["exemptions"][0]["expires"] = "2000-01-01"
    with open(os.path.join(tmp, "tools", BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "an expired exemption fails",
        any(f.startswith("DUP-EXEMPT-EXPIRED") for f in findings),
        str(findings),
    )
    payload["exemptions"][0]["expires"] = "2999-01-01"
    payload["exemptions"][0]["paths"] = ["tests/Nowhere/**"]
    with open(os.path.join(tmp, "tools", BASELINE_NAME), "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    findings, warnings, _, _, _ = run_gate(tmp)
    check(
        "an exemption that matches nothing is reported",
        any(w.startswith("DUP-EXEMPT-UNUSED") for w in warnings),
        str(warnings),
    )

    # 12. A method call must not be mistaken for a declaration: `Assert.That`
    #     is a call, and a body that starts at the call's parenthesis would
    #     make every assertion in the tree look like a method.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a})
    methods, _ = scan_tree(tmp)
    names = sorted(m.name for m in methods)
    check(
        "a call is not a declaration",
        names == ["Alpha_Works", "CreateStore"] and "That" not in names,
        str(names),
    )

    # 13. The intra-file case: two copies of a body IN ONE FILE are also
    #     duplication, and the group must say so.
    twice = HEADER.format(cls="TwiceTests", body=BODY_A + BODY_A)
    tmp = _fixture({"tests/Demo.Tests/TwiceTests.cs": twice})
    findings, _, groups, _, _ = run_gate(tmp)
    check(
        "two copies in one file are a group too",
        len(groups) == 1 and groups[0]["count"] == 2 and groups[0]["intra_file"],
        str(groups),
    )

    # 14. REWRITING a duplicated body must NOT make the gate red. The gate
    #     is keyed on the MEMBERS of a duplicate group, not on the text of
    #     its body, because the text changes every time somebody edits a
    #     duplicated test — and an edit that rewrites every copy keeps the
    #     duplication while changing every fingerprint. The first version of
    #     this gate keyed on the body and went red on the very next PR that
    #     did exactly that (#1814 rewrote the PTY wait helpers). A gate that
    #     is red on a legitimate edit is a gate that gets switched off.
    tmp = _fixture({"tests/Demo.Tests/AlphaTests.cs": tree_a,
                    "tests/Demo.Tests/BetaTests.cs": tree_b})
    methods, _ = scan_tree(tmp)
    groups_now, _ = build_groups(methods)
    _write_fixture_baseline(
        tmp,
        groups_now,
        {"duplicate_lines": sum(g["duplicate_lines"] for g in groups_now)},
    )
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "a known group is green before the rewrite",
        not findings,
        str(findings),
    )
    rewritten_a = tree_a.replace('"/test/dir"', '"/rewritten/dir"')
    rewritten_b = tree_b.replace('"/other/dir"', '"/rewritten/dir"')
    _write_tree(tmp, {"tests/Demo.Tests/AlphaTests.cs": rewritten_a,
                      "tests/Demo.Tests/BetaTests.cs": rewritten_b})
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "rewriting the body of every copy stays green",
        not findings,
        str(findings),
    )

    # 15. Same-named methods in ONE file must not share a member key. A test
    #     class can hold five methods called `CreateContext`; if their keys
    #     collided, adding a sixth copy would not change the group and the
    #     gate would stay green on exactly the copy it exists to catch.
    overloads = HEADER.format(cls="OverloadTests", body=BODY_A + BODY_A.replace(
        "Alpha_Works", "Alpha_Works_Again"))
    tmp = _fixture({"tests/Demo.Tests/OverloadTests.cs": overloads})
    methods, _ = scan_tree(tmp)
    groups_now, _ = build_groups(methods)
    check(
        "same-named methods in one file get distinct keys",
        groups_now
        and len(groups_now[0]["member_keys"]) == len(groups_now[0]["members"]),
        str(groups_now),
    )
    _write_fixture_baseline(
        tmp,
        groups_now,
        {"duplicate_lines": sum(g["duplicate_lines"] for g in groups_now)},
    )
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "two same-named copies are green when the baseline knows them",
        not findings,
        str(findings),
    )
    _write_tree(tmp, {"tests/Demo.Tests/OverloadTests.cs": overloads + BODY_A.replace(
        "Alpha_Works", "Alpha_Works_Third")})
    findings, _, _, _, _ = run_gate(tmp)
    check(
        "a third copy of a same-named body fails",
        any(f.startswith("DUP-NEW") for f in findings),
        str(findings),
    )

    if failures:
        print(
            f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): "
            f"{', '.join(failures)}"
        )
        return 1
    print(f"\nself-test passed: {len(ran)} fixtures")
    return 0


# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--root",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        help="repository root (default: the checkout this script lives in)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the census and every duplicate group, exit 0",
    )
    parser.add_argument(
        "--update",
        action="store_true",
        help="regenerate the baseline from the current tree and exit 0",
    )
    parser.add_argument(
        "--json-out",
        default=None,
        help="write the group list as JSON (the next duplication ticket's "
             "work list)",
    )
    parser.add_argument(
        "--new-since",
        default=None,
        help="only consider groups that touch a file changed since REF "
             "(e.g. origin/dev)",
    )
    parser.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="fail unless at least this many .cs files were scanned (0 = off)",
    )
    parser.add_argument(
        "--min-methods",
        type=int,
        default=0,
        help="fail unless at least this many method bodies were scanned "
             "(0 = off)",
    )
    parser.add_argument(
        "--min-lines",
        type=int,
        default=MIN_BODY_LINES,
        help=f"minimum code lines in a duplicated body (default {MIN_BODY_LINES})",
    )
    parser.add_argument(
        "--min-similarity",
        type=float,
        default=MIN_SIMILARITY,
        help="minimum shared identifier vocabulary for two same-shape bodies "
             f"to count as near-copies (default {MIN_SIMILARITY})",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="run the gate against known-broken fixtures",
    )
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    if not os.path.isdir(os.path.join(root, SCOPE_DIR)):
        print(f"error: {SCOPE_DIR}/ not found under {root}", file=sys.stderr)
        return 2

    if args.update:
        methods, scan = scan_tree(root)
        groups, coincidences = build_groups(
            methods, args.min_lines, MIN_MEMBERS, args.min_similarity
        )
        path = write_baseline(root, groups, scan, args.min_lines,
                              args.min_similarity)
        measured = census(groups, scan, coincidences)
        print(
            f"wrote {len(groups)} group(s) / {measured['duplicate_lines']} "
            f"duplicate lines to {path}"
        )
        return 0

    changed = None
    if args.new_since:
        changed = changed_files_since(root, args.new_since)
        if changed is None:
            print(
                f"error: cannot resolve '{args.new_since}' with git — the "
                f"comparison needs a ref this checkout knows",
                file=sys.stderr,
            )
            return 2

    findings, warnings, groups, measured, offenders = run_gate(
        root,
        min_lines=args.min_lines,
        min_similarity=args.min_similarity,
        min_files=args.min_files,
        min_methods=args.min_methods,
        changed_files=changed,
    )

    print(
        f"scanned {measured['files']} .cs files, {measured['methods']} method "
        f"bodies ({measured['test_methods']} [Test]); "
        f"{measured['groups']} duplicate group(s), "
        f"{measured['duplicate_lines']} removable code lines"
    )

    if args.report:
        print_report(groups, measured)
        for warning in warnings:
            print(f"  warn  {warning}")
        if args.json_out:
            write_artifact(args.json_out, groups, measured)
            print(f"\nwrote {args.json_out}")
        return 0

    if offenders:
        print()
        for group in offenders:
            print(describe_group(group))
    for finding in findings:
        print(f"FAIL {finding}")
    for warning in warnings:
        print(f"warn  {warning}")

    if args.json_out:
        write_artifact(args.json_out, groups, measured)
        print(f"wrote {args.json_out}")

    if findings:
        print(f"\n{len(findings)} finding(s) — red")
        return 1
    print("OK: no duplication beyond the committed baseline")
    return 0


if __name__ == "__main__":
    sys.exit(main())
