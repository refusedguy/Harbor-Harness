#!/usr/bin/env python3
"""XML-doc coverage gate for the frozen contract assembly (#431, slice D3).

Stdlib only — no pip, no network, no dotnet. Wired into
.github/workflows/docs.yml alongside check-md-links.py, md-lint.py,
check-doc-cites.py, check-doc-facts.py and check-abstractions-contract.py,
and it must not overlap any of them:

  check-md-links.py               does the link resolve?
  md-lint.py                      is the file shaped the way the repo says it is?
  check-doc-cites.py              does a `file:line` still point at a line?
  check-doc-facts.py              does a COUNT a document states still equal the
                                  count of tracked files it is counting?
  check-abstractions-contract.py  is every <PackageReference> load-bearing?
  THIS ONE                        is every public member of the frozen contract
                                  surface XML-documented (or explicitly
                                  suppressed with a reason)?

WHY THIS FILE EXISTS (issue #431 item 5, epic #1068 slice D3)

  #428 freezes the public API of Harbor.Abstractions.Contracts, and D2
  (docfx.json metadata lane) turns its XML docs into the published API
  reference. `Directory.Build.props` sets GenerateDocumentationFile globally
  but keeps CS1591 (missing XML comment) in NoWarn + WarningsNotAsErrors, so
  the compiler never asks the question — an undocumented public member builds
  silently and ships silently into the reference. This gate asks it instead,
  in the one workflow that runs without the SDK.

WHY THE SCOPE IS CONTRACTS ONLY

  The frozen data surface is Harbor.Abstractions.Contracts (31 files, pure
  models/events/permissions — the layer every other assembly points at).
  Harbor.Abstractions is a thin interface facade whose members resolve through
  the same docfx metadata lane but whose shape churns with provider/tool
  work; gating it here would pin product churn behind a docs job. Stated so a
  narrower scope reads as a decision, not an omission (#847: an allowance with
  no reader is a hole; this paragraph is the reader).

WHAT COUNTS AS A MEMBER

  A line-based scan, deliberately small: a declaration starting with `public`
  or `protected` (types, methods, ctors, properties, fields, consts,
  operators), enum values inside an enum body, and members without an access
  modifier inside an interface body (implicitly public C#). Record
  primary-constructor parameters are NOT members — the record's own doc covers
  them. A member is documented when the nearest preceding non-blank,
  non-attribute line starts with `///` (`<summary>` or `<inheritdoc/>` both
  count; cref resolution is NOT checked — that is CS1574's job, still NoWarn).

RULES

  XMLDOC-UNDOCUMENTED      A public member with no XML doc and no entry in
                           SUPPRESSED below. Any single one fails the gate —
                           this is the enforced minimum: zero unsuppressed
                           gaps, so the first undocumented member goes red.
  XMLDOC-STALE-SUPPRESSION Every SUPPRESSED key must match exactly one
                           currently-undocumented member. A member that gained
                           docs (or vanished) with its suppression still
                           listed fails the gate — the allowlist may only
                           describe the tree as it is, never a tree that was.

SUPPRESSIONS ARE EXPLICIT, NOT A THRESHOLD

  A percentage floor alone cannot do the job: at ~97% coverage one more
  undocumented member still clears any sane floor, which is green on the
  defect. So the 15 members below are listed by name with a reason each, and
  the gate is zero-tolerance on top: anything not listed fails. A PR that
  documents one of them drops its entry in the same diff, where a reviewer
  can see it; a PR that adds a member adds docs or a new entry with a reason.

THE FLOORS ARE THE POINT (#509 shape, same as every other gate here)

  --min-members / --min-documented / --min-coverage make a matcher that
  silently stopped matching fail instead of passing quietly. Measured on
  dev@702647a9: 486 members, 471 documented (96.9%). The count floors are set
  AT those values, not below (the check-abstractions-contract.py precedent):
  a floor with slack cannot tell "the tree got smaller" from "the matcher
  stopped matching". The percentage floor is 0.96, just under measured —
  float-boundary flakiness is not a signal. Lower the counts only in the same
  diff that legitimately shrinks the surface, where a reviewer can see it.

USAGE

  ./tools/check-xml-doc-coverage.py --min-members 486 --min-documented 471 \\
      --min-coverage 0.96
  ./tools/check-xml-doc-coverage.py --self-test   # prove it still fails
  ./tools/check-xml-doc-coverage.py --report      # member table for review
"""

import argparse
import glob
import os
import re
import sys

SCOPE = ["src/Harbor.Abstractions.Contracts"]

# file-relative-path :: member-name -> why no XML doc (yet).
# Each key must match exactly one currently-undocumented member (enforced:
# XMLDOC-STALE-SUPPRESSION). Member names, not lines: line numbers in a
# snapshot are stale before the PR lands (docs/XML_DOC_AUDIT.md, "Known
# drift"). To document one of these, add the doc and delete its entry here
# in the same diff.
SUPPRESSED = {
    # The base event record carries the union doc; the timestamp is
    # DateTimeOffset.UtcNow defaulting infrastructure, self-describing.
    "src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::Timestamp":
        "base-record plumbing, type-level union doc applies",
    # Zero-payload envelope records (session id + inherited timestamp only);
    # sibling events with payload all carry docs.
    "src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::AgentStartEvent":
        "payload-free envelope, union doc on AgentEvent applies",
    "src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::TextStartEvent":
        "payload-free envelope, union doc on LlmEvent applies",
    # Legacy chat-role enum + one-line struct kept for compat; values mirror
    # the documented ContentPart/role model and carry no independent semantics.
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ChatRole":
        "legacy enum, values mirror the documented role model",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::User":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Assistant":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Thinking":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Tool":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ToolResult":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::System":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Error":
        "legacy enum value, see ChatRole",
    "src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ChatLine":
        "legacy one-line struct, positional roles mirror ChatRole",
    # Mechanical System.Text.Json converter; the StopReason parsing it serves
    # (StopReasonTable.TryParseSpan) is documented.
    "src/Harbor.Abstractions.Contracts/Models/Session.cs::StopReasonJsonConverter":
        "mechanical JsonConverter, documented via StopReasonTable",
    # Default-arg ctor next to a documented class doc; the capacity semantic
    # is stated on DefaultCapacity.
    "src/Harbor.Abstractions.Contracts/Permissions/PatternRegexCache.cs::BoundedPatternRegexCache":
        "default-arg ctor, semantic stated on DefaultCapacity",
    # The /// block directly above documents the backing field's sharing
    # story; the property itself is a get/set passthrough for tests.
    "src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs::RegexCacheProvider":
        "test seam passthrough, field doc above applies",
}

TYPE_KIND = r"(class|record(?:\s+(?:struct|class))?|struct|interface|enum)"
TYPE_DECL = re.compile(
    r"^\s*(?:public|protected|internal|private)\s+"
    r"(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*"
    + TYPE_KIND + r"\s+(\w+)"
)
IMPLICIT_TYPE_DECL = re.compile(
    r"^\s*(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*"
    + TYPE_KIND + r"\s+(\w+)"
)
ENUM_VALUE = re.compile(r"^\s*(\w+)\s*(?:=\s*[^,;]+)?,?\s*(?://.*)?$")
ACCESS_MOD = ("public ", "protected ", "private ", "internal ")


def _strip_line_noise(line):
    """Remove string literals so braces inside them cannot move the depth."""
    out = []
    in_str = False
    i = 0
    while i < len(line):
        c = line[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if c == '"':
            in_str = True
            i += 1
            continue
        out.append(c)
        i += 1
    code = "".join(out)
    code = re.sub(r"//.*$", "", code)
    return code


def _member_name_from_decl(stripped):
    """Simple name of a public/protected declaration line (first line only).

    Initializers and bodies are cut at the first `;`, `{` or `=` outside
    parentheses, so `= new(...)` / `=> ...` tails cannot donate a name
    (that shape once yielded "new" for a property).
    """
    depth = 0
    cut = None
    for i, ch in enumerate(stripped):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        elif depth == 0 and ch in ";{=":
            cut = i
            break
    head = stripped[:cut] if cut is not None else stripped
    if "operator" in head:
        after = head.split("operator", 1)[1].strip()
        return "operator " + after.split()[0] if after else "operator"
    m = re.search(r"(\w+)\s*(?:<[^()]*>)?\s*\(", head)
    if m and m.group(1) not in (
        "public", "protected", "private", "internal", "static", "sealed",
        "abstract", "partial", "readonly", "override", "virtual", "async",
        "new", "extern", "unsafe",
    ):
        return m.group(1)
    # The identifier before `(` was a modifier/keyword (e.g. a `new(...)`
    # tail that survived the cut): fall through to the last word.
    tokens = re.findall(r"\w+", head)
    return tokens[-1] if tokens else head.strip()


def iter_members(lines, relpath):
    """Yield (line_no, name, display) for every gated member in one file."""
    members = []
    depth = 0
    stack = []  # [kind, ...] of enclosing named types
    pending_kind = None
    for idx, raw in enumerate(lines):
        lineno = idx + 1
        stripped = raw.strip()
        code = _strip_line_noise(raw).strip()

        m = TYPE_DECL.match(raw) or IMPLICIT_TYPE_DECL.match(code)
        if m and re.search(
            r"\b(class|record|struct|interface|enum)\b", raw
        ):
            kind, name = m.group(1), m.group(2)
            if raw.strip().startswith(("public ", "protected ")):
                members.append((lineno, name, stripped))
            pending_kind = kind
        elif code.startswith(ACCESS_MOD[:2]):
            # A public/protected declaration line. `namespace`/`using` never
            # carry an access modifier, so no exclusion needed beyond those.
            members.append((lineno, _member_name_from_decl(stripped), stripped))
        elif stack and stack[-1] == "interface":
            # Implicitly-public interface member (no access modifier). Only
            # shapes with a parameter list or an accessor block qualify: bare
            # `type name = value);` continuation lines of a multi-line
            # signature end in `;` but declare nothing (that shape once
            # yielded members named "1" and "default").
            if (
                stripped
                and not stripped.startswith(
                    ("[", "/", "#", "{", "}", "using ", "namespace ")
                )
                and re.search(r"\(|\bget\b|\bset\b", code)
            ):
                members.append(
                    (lineno, _member_name_from_decl(stripped), stripped)
                )
        elif stack and stack[-1] == "enum":
            em = ENUM_VALUE.match(raw)
            if em and not stripped.startswith(("[", "/", "#", "{", "}", "using ")):
                members.append((lineno, em.group(1), stripped))

        opens = code.count("{")
        closes = code.count("}")
        if pending_kind is not None:
            if opens > 0:
                stack.append(pending_kind)
                pending_kind = None
            elif ";" in code:
                pending_kind = None
        depth += opens - closes
        while len(stack) > depth:
            stack.pop()
        # A `record X(...);` without a body never pushes, so depth is the
        # only tracker — but a file-scope type leaves depth at 0 and the
        # stack must not leak into the next type.
        if depth < 0:
            depth = 0
        while stack and depth == 0:
            # File-scope types close at depth 0 only when the next type or
            # EOF arrives; keep them — enum/interface context ends at the
            # matching close brace, and file-scope members are all prefixed
            # `public`/`protected` anyway, so a stale top entry changes
            # nothing. Pop defensively on blank-separated top-level decls.
            break

    return members


def _has_doc(lines, idx):
    """True when /// docs precede lines[idx] (skipping blanks/attributes)."""
    j = idx - 1
    while j >= 0 and (
        lines[j].strip() == "" or lines[j].strip().startswith("[")
    ):
        j -= 1
    return j >= 0 and lines[j].strip().startswith("///")


def check_tree(repo):
    """Scan SCOPE. Returns (members, undocumented, stale)."""
    members = []  # (relpath, line, name, display, documented)
    for scope in SCOPE:
        for path in sorted(glob.glob(os.path.join(repo, scope, "**", "*.cs"),
                                     recursive=True)):
            relpath = os.path.relpath(path, repo).replace(os.sep, "/")
            with open(path, encoding="utf-8") as f:
                lines = f.read().splitlines()
            for lineno, name, display in iter_members(lines, relpath):
                members.append(
                    (relpath, lineno, name, display,
                     _has_doc(lines, lineno - 1)))
    undocumented = [m for m in members if not m[4]]
    # Each suppression must match exactly one undocumented member.
    hits = {}
    for m in undocumented:
        hits.setdefault(m[0] + "::" + m[2], []).append(m)
    stale = []
    for key in SUPPRESSED:
        n = len(hits.get(key, []))
        if n != 1:
            stale.append((key, n))
    return members, undocumented, stale


def run_gate(repo, min_members=0, min_documented=0, min_coverage=0.0):
    members, undocumented, stale = check_tree(repo)
    total = len(members)
    documented = total - len(undocumented)
    coverage = (documented / total) if total else 0.0
    findings = []
    for relpath, lineno, name, display, _ in undocumented:
        if relpath + "::" + name not in SUPPRESSED:
            findings.append(
                (relpath, lineno,
                 f"XMLDOC-UNDOCUMENTED {name}: no /// doc and no "
                 f"SUPPRESSED entry — add a doc or a suppression with reason"))
    for key, n in stale:
        findings.append(
            ("tools/check-xml-doc-coverage.py", 0,
             f"XMLDOC-STALE-SUPPRESSION {key}: matches {n} undocumented "
             f"members, want exactly 1 — drop it (docs landed) or fix the key"))
    if total < min_members:
        findings.append(
            ("tools/check-xml-doc-coverage.py", 0,
             f"XMLDOC-FLOOR members {total} < --min-members {min_members} — "
             f"the matcher sees less of the tree than it should"))
    if documented < min_documented:
        findings.append(
            ("tools/check-xml-doc-coverage.py", 0,
             f"XMLDOC-FLOOR documented {documented} < --min-documented "
             f"{min_documented}"))
    if coverage < min_coverage:
        findings.append(
            ("tools/check-xml-doc-coverage.py", 0,
             f"XMLDOC-FLOOR coverage {coverage:.3f} < --min-coverage "
             f"{min_coverage}"))
    suppressed = len(undocumented) - sum(
        1 for m in undocumented if m[0] + "::" + m[2] not in SUPPRESSED)
    return findings, total, documented, coverage, suppressed


# --------------------------------------------------------------- self-test
#
# Every fixture is a shape this gate must not get wrong. The second one is
# the load-bearing non-vacuity proof the slice requires: a tree with a
# single undocumented public member MUST fail, naming the member. If that
# fixture ever stops failing, the gate is decorative.

FIX_OK = (
    "namespace Demo;\n"
    "/// <summary>Base.</summary>\n"
    "public abstract record Event\n"
    "{\n"
    "    /// <summary>Stamp.</summary>\n"
    "    public int Stamp { get; }\n"
    "}\n"
    "\n"
    "/// <summary>A box.</summary>\n"
    "/// <param name=\"Id\">Id.</param>\n"
    "public sealed record Box(string Id) : Event;\n"
)

FIX_ONE_GAP = (
    "namespace Demo;\n"
    "/// <summary>Base.</summary>\n"
    "public abstract record Event\n"
    "{\n"
    "    public int Stamp { get; }\n"
    "}\n"
)

FIX_INHERITDOC = (
    "namespace Demo;\n"
    "/// <summary>Cache.</summary>\n"
    "public interface ICache\n"
    "{\n"
    "    /// <inheritdoc />\n"
    "    int Count { get; }\n"
    "}\n"
)

FIX_ENUM = (
    "namespace Demo;\n"
    "/// <summary>Kinds.</summary>\n"
    "public enum Kind\n"
    "{\n"
    "    /// <summary>First.</summary>\n"
    "    First,\n"
    "    Second,\n"
    "}\n"
)

FIX_CTOR = (
    "namespace Demo;\n"
    "/// <summary>Cache.</summary>\n"
    "public sealed class Cache\n"
    "{\n"
    "    public Cache(int capacity)\n"
    "    {\n"
    "    }\n"
    "}\n"
)


def _scan_text(text, relpath="x/A.cs"):
    lines = text.splitlines()
    out = []
    for lineno, name, display in iter_members(lines, relpath):
        out.append((relpath, lineno, name, display, _has_doc(lines, lineno - 1)))
    return out


def self_test():
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"  [{name:44}] {'ok' if cond else '<< MISMATCH'} {detail}")

    m = _scan_text(FIX_OK)
    check("documented tree is clean", all(x[4] for x in m), f"{len(m)} members")

    m = _scan_text(FIX_ONE_GAP)
    gaps = [x for x in m if not x[4]]
    check("single undocumented member fails, naming it",
          len(gaps) == 1 and gaps[0][2] == "Stamp",
          f"gaps={[x[2] for x in gaps]}")

    m = _scan_text(FIX_INHERITDOC)
    check("inheritdoc counts as documented", all(x[4] for x in m),
          f"{len(m)} members")
    check("implicit interface member is gated", len(m) == 2,
          f"names={[x[2] for x in m]}")

    m = _scan_text(FIX_ENUM)
    gaps = [x for x in m if not x[4]]
    check("undocumented enum value fails", len(gaps) == 1 and gaps[0][2] == "Second",
          f"gaps={[x[2] for x in gaps]}")

    m = _scan_text(FIX_CTOR)
    gaps = [x for x in m if not x[4]]
    check("undocumented ctor fails", len(gaps) == 1 and "Cache" in gaps[0][2],
          f"gaps={[x[2] for x in gaps]}")

    # Suppression consumes exactly its member; a stale one is a finding.
    real = [("x/A.cs", 3, "Stamp", "public int Stamp { get; }", False)]
    key = "x/A.cs::Stamp"
    saved = dict(SUPPRESSED)
    try:
        globals()["SUPPRESSED"] = {key: "test reason"}
        hits = [x for x in real if x[0] + "::" + x[2] in SUPPRESSED]
        check("suppression consumes its member", len(hits) == 1)
        fixed = [("x/A.cs", 3, "Stamp", "public int Stamp { get; }", True)]
        n = len([x for x in fixed if not x[4] and
                 x[0] + "::" + x[2] == key])
        check("documented member orphans its suppression", n == 0)
    finally:
        globals()["SUPPRESSED"] = saved

    # Floors: a matcher that sees nothing must fail the floor, not pass.
    findings, total, documented, coverage, _ = run_gate(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        min_members=10 ** 9)
    check("absurd floor fails (matcher blindness is red)",
          any("XMLDOC-FLOOR" in f[2] for f in findings))

    print(f"\nself-test {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--min-members", type=int, default=0,
                    help="fail unless at least this many public members were "
                         "scanned (0 = off)")
    ap.add_argument("--min-documented", type=int, default=0,
                    help="fail unless at least this many members carry XML "
                         "docs (0 = off)")
    ap.add_argument("--min-coverage", type=float, default=0.0,
                    help="fail unless documented/total is at least this (0 = off)")
    ap.add_argument("--self-test", action="store_true",
                    help="run the gate against known-broken fixtures")
    ap.add_argument("--report", action="store_true",
                    help="print the per-file member table and exit 0")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if args.report:
        members, undocumented, _ = check_tree(repo)
        gaps = {(m[0], m[2]) for m in undocumented}
        by_file = {}
        for m in members:
            by_file.setdefault(m[0], []).append(m)
        for relpath in sorted(by_file):
            for _, lineno, name, _, documented in sorted(by_file[relpath]):
                mark = "doc " if documented else (
                    "supp" if (relpath, name) in gaps and
                    relpath + "::" + name in SUPPRESSED else "GAP ")
                print(f"{mark} {relpath}:{lineno}: {name}")
        total = len(members)
        documented_n = sum(1 for m in members if m[4])
        suppressed_n = sum(1 for m in undocumented
                           if m[0] + "::" + m[2] in SUPPRESSED)
        print(f"\n{total} members, {documented_n} documented "
              f"({documented_n / total:.1%}), {suppressed_n} suppressed")
        return 0

    findings, total, documented, coverage, suppressed = run_gate(
        repo, args.min_members, args.min_documented, args.min_coverage)
    print(f"compared {total} public members ({documented} documented, "
          f"{suppressed} suppressed, coverage {coverage:.1%}) against the "
          f"XML-doc gate in src/Harbor.Abstractions.Contracts")
    for relpath, lineno, text in findings:
        print(f"FAIL {relpath}:{lineno}: {text}")
    if findings:
        print(f"\n{len(findings)} finding(s) — red")
        return 1
    print("OK: no unsuppressed undocumented members, no stale suppressions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
