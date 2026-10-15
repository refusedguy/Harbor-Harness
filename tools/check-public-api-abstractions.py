#!/usr/bin/env python3
"""Public-API snapshot gate for the frozen interface facade (#428, second slice).

Stdlib only — no pip, no network, no dotnet. Wired into
.github/workflows/ci.yml as the `public-api` job, and it must not overlap
the sibling gate:

  tools/check-public-api.py       snapshots Harbor.Abstractions.Contracts
                                  (data surface: models/events/permissions),
                                  wired into docs.yml.
  THIS ONE                        snapshots Harbor.Abstractions (interface
                                  facade: ITool, ILlmClient, ISessionStore,
                                  ITuiRenderer, IPlugin, ...). Any added,
                                  removed or re-signed member fails.

WHY THIS FILE EXISTS (issue #428)

  #428 freezes the public API of Harbor.Abstractions (+ Contracts) for v1.0:
  third parties build against these assemblies via NuGet and via the plugin
  path, and until these gates nothing enforced the freeze — a `sealed`
  slipped onto an extension point or a new abstract member on an interface
  broke every out-of-tree consumer with no compile-time warning in our CI.
  This is the second slice: a snapshot of the REMAINING assembly plus the
  approval gate, with no API change. The baseline is generated from the tree
  as it is, not as it should be — seal-the-gaps work (accidental `public`,
  missing `sealed`) stays open for a later slice and must not ride along
  here.

WHY THE SCOPE IS ABSTRACTIONS ONLY

  The frozen interface surface is Harbor.Abstractions (64 files: Tools,
  Providers, Sessions, Agents, Events, Tui, Plugins namespaces — the
  contracts every extension author compiles against). Contracts is covered
  by the sibling gate (tools/check-public-api.py, docs.yml); this file
  covers the facade so the two slices compose to the full #428 freeze
  without either PR touching the other's baseline or trigger block (#847:
  an allowance with no reader is a hole; this paragraph is the reader).

WHAT COUNTS AS A MEMBER

  A line-based scan, deliberately small: every `public`/`protected` type and
  member whose whole enclosing type chain is `public`/`protected` (a `public`
  member of an `internal` type is not public API and is ignored), implicit
  interface members (no access modifier inside an interface body), public
  delegate declarations, and enum values. Record primary-constructor
  parameters are NOT expanded — the record's own declaration line carries
  them, so adding one still changes the snapshot.

  Entries are `Qualified.Type :: normalized declaration` with NO file path
  and NO line number: moving a type between files or re-wrapping a signature
  must not fail the gate (multi-line signatures are joined and whitespace is
  collapsed first). What fails it is only what a consumer can observe: an
  added member (PUBLICAPI-ADDED), a removed one (PUBLICAPI-REMOVED), or a
  re-signed one (one of each). `const` initializers and enum values ARE kept —
  both bake into callers — so changing either fails the gate.

RULES

  PUBLICAPI-ADDED          A surface member with no baseline row. Additive-only
                           changes still fail: the reviewer regenerates the
                           baseline in the same diff (see --update), where the
                           addition is visible.
  PUBLICAPI-REMOVED        A baseline row with no surface member. Removals and
                           re-signs are breaking: they need a major version
                           bump and a CHANGELOG.md entry, decided outside this
                           gate — this rule only makes them impossible to land
                           silently.
  PUBLICAPI-BASELINE-MISSING  The baseline file is absent. A gate that passes
                           on a deleted baseline is decorative.
  PUBLICAPI-FLOOR          Fewer members scanned than --min-members. A matcher
                           that silently stops matching must go red, not quiet.

ESCAPE HATCH (--update)

  python3 tools/check-public-api-abstractions.py --update

  Regenerates the baseline from the current tree. Run it locally, review the
  `git diff` of the baseline file, and commit it in the SAME PR as the API
  change — a PR that changes the surface without touching the baseline fails,
  and a PR that touches the baseline without changing the surface is where
  the reviewer looks.
"""

import argparse
import glob
import os
import re
import sys

SCOPE = "src/Harbor.Abstractions"
BASELINE_NAME = "PublicAPI.Baseline.txt"

TYPE_RE = re.compile(
    r"^\s*(public|protected|internal|private)\s+"
    r"(?:(?:sealed|abstract|static|partial|readonly|ref|unsafe)\s+)*"
    r"(class|record(?:\s+(?:struct|class))?|struct|interface|enum)\s+(\w+)"
)
DELEGATE_RE = re.compile(
    r"^\s*(public|protected)\s+(?:.*\s)?delegate\s+\S+\s+(\w+)\s*\(.*"
)
MEMBER_RE = re.compile(r"^\s*(public|protected)\s+")
NS_FILE_RE = re.compile(r"^\s*namespace\s+([\w.]+)\s*;")
NS_BLOCK_RE = re.compile(r"^\s*namespace\s+([\w.]+)\s*\{")
ENUM_VALUE = re.compile(r"^\s*(\w+)\s*(?:=\s*([^,;]+))?,?\s*$")


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


def _paren_balance(text):
    """Open parens minus close parens, ignoring string-literal contents."""
    code = _strip_line_noise(text)
    return code.count("(") - code.count(")")


def _join_decl(lines, i):
    """Join a possibly multi-line declaration starting at lines[i].

    Balance is measured on noise-stripped text so a paren inside a string
    literal (e.g. `= "task("`) cannot swallow the rest of the file, and
    comment-only continuation lines are skipped so a `///` block between a
    signature's lines cannot pollute the entry.
    """
    decl = lines[i].strip()
    j = i
    n = len(lines)
    while _paren_balance(decl) > 0 and j + 1 < n:
        nxt = lines[j + 1].strip()
        j += 1
        if nxt.startswith("//"):
            continue
        decl += " " + nxt
    return decl, j


def _normalize_decl(decl):
    """Collapse a (possibly multi-line, joined) declaration to one stable line.

    Attributes are dropped, whitespace is collapsed, the body/initializer is
    cut at the first `{`, `;` or `=>` outside parentheses — EXCEPT a `const`
    or enum-style `= value` tail, which is public API (it bakes into callers)
    and is kept.
    """
    decl = re.sub(r"\[[^\]]*\]", "", decl)
    decl = re.sub(r"\(\s+", "(", decl)
    decl = re.sub(r"\s+\)", ")", decl)
    decl = re.sub(r"\s*,\s*", ", ", decl)
    decl = re.sub(r"\s+", " ", decl).strip()
    if re.match(r"^(public|protected)\s+(const\s|.*\sevent\s)", decl):
        return decl.rstrip(";").strip()
    depth = 0
    cut = None
    for i, ch in enumerate(decl):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        elif depth == 0 and ch in "{;":
            cut = i
            break
        elif depth == 0 and decl.startswith("=>", i):
            cut = i
            break
    if cut is not None:
        decl = decl[:cut].strip()
    return decl


def scan_file(lines):
    """Yield snapshot entries for one file. No paths, no line numbers."""
    ns = ""
    for raw in lines:
        m = NS_FILE_RE.match(raw) or NS_BLOCK_RE.match(raw)
        if m:
            ns = m.group(1)
            break
    depth = 0
    stack = []  # [name, kind, access, body_depth]
    pending = None  # type decl whose `{` sits on a later line
    entries = []

    def qn(extra=None):
        parts = [ns] + [s[0] for s in stack] + ([extra] if extra else [])
        return ".".join(p for p in parts if p)

    def chain_public():
        return all(s[2] in ("public", "protected") for s in stack)

    def push_frame(name, kind, access, opens, closes):
        nonlocal depth
        depth += opens - closes
        stack.append([name, kind, access, depth])

    def pop_to_depth():
        while stack and depth < stack[-1][3]:
            stack.pop()

    i, n = 0, len(lines)
    while i < n:
        raw = lines[i]
        code = _strip_line_noise(raw).strip()
        if pending is not None:
            if "{" in code:
                push_frame(pending[0], pending[1], pending[2],
                           code.count("{"), code.count("}"))
                pending = None
                i += 1
                continue
            if ";" in code:
                pending = None
                i += 1
                continue
            if code and not code.startswith(
                    ("[", "/", "#", ":", "where ", "where(")):
                pending = None  # unexpected shape: drop, depth still updates
            else:
                i += 1
                continue
        tm = TYPE_RE.match(raw)
        if tm and re.search(r"\b(class|record|struct|interface|enum)\b", raw):
            access, kind, name = tm.group(1), tm.group(2), tm.group(3)
            decl, j = _join_decl(lines, i)
            if access in ("public", "protected") and chain_public():
                entries.append(f"{qn(name)} :: {_normalize_decl(decl)}")
            jcode = " ".join(_strip_line_noise(lines[k])
                             for k in range(i, j + 1))
            if "{" in jcode:
                push_frame(name, kind, access,
                           jcode.count("{"), jcode.count("}"))
            elif ";" not in jcode:
                pending = [name, kind, access]
            i = j + 1
            continue
        dm = DELEGATE_RE.match(raw)
        if dm and chain_public():
            decl, j = _join_decl(lines, i)
            entries.append(f"{qn()} :: {_normalize_decl(decl)}")
            dd = sum(_strip_line_noise(lines[k]).count("{")
                     - _strip_line_noise(lines[k]).count("}") for k in range(i, j + 1))
            depth += dd
            pop_to_depth()
            i = j + 1
            continue
        if chain_public():
            mm = MEMBER_RE.match(raw)
            if mm and "namespace" not in raw:
                decl, j = _join_decl(lines, i)
                entries.append(f"{qn()} :: {_normalize_decl(decl)}")
                dd = sum(_strip_line_noise(lines[k]).count("{")
                         - _strip_line_noise(lines[k]).count("}") for k in range(i, j + 1))
                depth += dd
                pop_to_depth()
                i = j + 1
                continue
            if stack and stack[-1][1] == "interface":
                if (code and not code.startswith(
                        ("[", "/", "#", "{", "}", "using ", "namespace "))
                        and re.search(r"\(|\bget\b|\bset\b", code)):
                    decl, j = _join_decl(lines, i)
                    entries.append(f"{qn()} :: {_normalize_decl(decl)}")
                    dd = sum(_strip_line_noise(lines[k]).count("{")
                             - _strip_line_noise(lines[k]).count("}") for k in range(i, j + 1))
                    depth += dd
                    pop_to_depth()
                    i = j + 1
                    continue
            if stack and stack[-1][1] == "enum":
                em = ENUM_VALUE.match(raw)
                if (em and code and not code.startswith(
                        ("[", "/", "#", "{", "}", "using "))):
                    value = em.group(2)
                    entries.append(f"{qn()} :: {em.group(1)}"
                                   + (f" = {value.strip()}" if value else ""))
        depth += code.count("{") - code.count("}")
        pop_to_depth()
        if depth < 0:
            depth = 0
        i += 1
    return entries


def scan_scope(root):
    """Sorted snapshot entries for the whole SCOPE under root."""
    entries = []
    for path in sorted(glob.glob(os.path.join(root, SCOPE, "**", "*.cs"),
                                 recursive=True)):
        with open(path, encoding="utf-8") as f:
            entries.extend(scan_file(f.read().splitlines()))
    return sorted(entries)


def baseline_path(root):
    return os.path.join(root, SCOPE, BASELINE_NAME)


def load_baseline(root):
    """Baseline rows, skipping blanks and `#` comments. None when missing."""
    path = baseline_path(root)
    if not os.path.isfile(path):
        return None
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                rows.append(line)
    return sorted(rows)


def diff_entries(baseline, current):
    base, cur = set(baseline), set(current)
    return sorted(cur - base), sorted(base - cur)


def run_gate(root, min_members=0):
    findings = []
    baseline = load_baseline(root)
    if baseline is None:
        return ([f"PUBLICAPI-BASELINE-MISSING {BASELINE_NAME}: no baseline "
                 f"committed under {SCOPE} — the gate passes on nothing"],
                0, None, None)
    current = scan_scope(root)
    added, removed = diff_entries(baseline, current)
    for entry in added:
        findings.append(
            f"PUBLICAPI-ADDED {entry} — new surface member with no reviewed "
            f"baseline row; run --update, review the diff, commit it here")
    for entry in removed:
        findings.append(
            f"PUBLICAPI-REMOVED {entry} — baseline row gone from the surface; "
            f"removals and re-signs are breaking (major bump + CHANGELOG), "
            f"never land them silently")
    if len(current) < min_members:
        findings.append(
            f"PUBLICAPI-FLOOR members {len(current)} < --min-members "
            f"{min_members} — the matcher sees less of the tree than it "
            f"should")
    return findings, len(current), added, removed


# --------------------------------------------------------------- self-test
#
# Every fixture is a shape this gate must not get wrong. The load-bearing
# non-vacuity proofs are the added/removed-member pair: a tree with a member
# the baseline does not name MUST fail naming it, and a baseline row the tree
# no longer has MUST fail naming it. If either stops failing, the gate is
# decorative.

TREE_BASE = {
    "ns.cs": (
        "namespace Demo;\n"
        "/// <summary>Box.</summary>\n"
        "public sealed class Box\n"
        "{\n"
        "    /// <summary>Id.</summary>\n"
        "    public string Id { get; }\n"
        "    /// <summary>Make.</summary>\n"
        "    public static Box Create(string id)\n"
        "    {\n"
        "        return new Box();\n"
        "    }\n"
        "}\n"
    ),
    "kind.cs": (
        "namespace Demo;\n"
        "/// <summary>Kinds.</summary>\n"
        "public enum Kind\n"
        "{\n"
        "    First,\n"
        "    Second = 2,\n"
        "}\n"
    ),
    "marks.cs": (
        "namespace Demo;\n"
        "/// <summary>Markers.</summary>\n"
        "public static class Marks\n"
        "{\n"
        "    /// <summary>Prefix.</summary>\n"
        # A paren inside the string literal must not read as an open
        # signature: the draft that counted raw parens swallowed the rest
        # of the file into this entry.
        "    public const string Prefix = \"task(\";\n"
        "\n"
        "    /// <summary>Check.</summary>\n"
        "    public static bool IsMarked(string v)\n"
        "    {\n"
        "        return v.StartsWith(Prefix);\n"
        "    }\n"
        "}\n"
    ),
    "helper.cs": (
        "namespace Demo;\n"
        "internal static class Helper\n"
        "{\n"
        "    public static bool Check(string v)\n"
        "    {\n"
        "        return v.Length > 0;\n"
        "    }\n"
        "}\n"
    ),
}


def _write_tree(tmp, files):
    scope = os.path.join(tmp, SCOPE)
    os.makedirs(scope, exist_ok=True)
    for name, text in files.items():
        with open(os.path.join(scope, name), "w", encoding="utf-8") as f:
            f.write(text)
    return tmp


def _write_baseline(tmp, rows):
    with open(os.path.join(tmp, SCOPE, BASELINE_NAME), "w",
              encoding="utf-8") as f:
        f.write("# fixture baseline\n")
        for row in rows:
            f.write(row + "\n")


def self_test():
    import tempfile
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"  [{name:46}] {'ok' if cond else '<< MISMATCH'} {detail}")

    with tempfile.TemporaryDirectory() as tmp:
        _write_tree(tmp, TREE_BASE)
        current = scan_scope(tmp)
        check("baseline measures the tree", len(current) == 9,
              f"{len(current)} entries")
        check("string-literal paren swallows nothing",
              any("Prefix = \"task(\"" in e for e in current)
              and any("IsMarked(string v)" in e for e in current)
              and not [e for e in current if e.count("::") > 1],
              str([e for e in current if "Mark" in e or "Prefix" in e]))
        check("internal type contributes nothing",
              not [e for e in current if "Helper" in e])
        check("const/enum tails kept",
              any("Second = 2" in e for e in current),
              str([e for e in current if "Second" in e]))

        _write_baseline(tmp, current)
        findings, total, _, _ = run_gate(tmp)
        check("matching baseline is clean", not findings and total == 9,
              f"findings={findings}")

        # Added member fails, naming it.
        grown = dict(TREE_BASE)
        grown["ns.cs"] = TREE_BASE["ns.cs"].replace(
            "    /// <summary>Make.</summary>\n",
            "    /// <summary>Make.</summary>\n"
            "    public int Extra { get; }\n")
        _write_tree(tmp, grown)
        findings, _, added, _ = run_gate(tmp)
        check("added member fails, naming it",
              len(added) == 1 and "Extra" in added[0]
              and any("PUBLICAPI-ADDED" in f for f in findings),
              f"added={added}")

        # Removed member fails, naming it.
        _write_tree(tmp, TREE_BASE)
        short = [r for r in current if "Second = 2" not in r]
        _write_baseline(tmp, short)
        findings2, _, added2, removed2 = run_gate(tmp)
        check("tree ahead of baseline reads as added",
              len(added2) == 1 and "Second = 2" in added2[0],
              f"added={added2}")
        _write_baseline(tmp, current + ["Demo.Ghost :: public int Gone { get; }"])
        _, _, _, removed3 = run_gate(tmp)
        check("baseline row gone from tree fails, naming it",
              len(removed3) == 1 and "Ghost" in removed3[0],
              f"removed={removed3}")

        # Re-signed member reads as one add + one remove.
        _write_baseline(tmp, current)
        resigned = dict(TREE_BASE)
        resigned["ns.cs"] = TREE_BASE["ns.cs"].replace(
            "public static Box Create(string id)",
            "public static Box Create(string id, bool force)")
        _write_tree(tmp, resigned)
        _, _, added4, removed4 = run_gate(tmp)
        check("re-signed member is add + remove",
              len(added4) == 1 and len(removed4) == 1
              and "force" in added4[0],
              f"added={added4} removed={removed4}")

        # Formatting-only change passes: signature re-wrapped across lines.
        _write_baseline(tmp, current)
        wrapped = dict(TREE_BASE)
        wrapped["ns.cs"] = TREE_BASE["ns.cs"].replace(
            "    public static Box Create(string id)\n",
            "    public static Box Create(\n        string id)\n")
        _write_tree(tmp, wrapped)
        findings5, _, _, _ = run_gate(tmp)
        check("re-wrapped signature still matches", not findings5,
              f"findings={findings5}")

        # Missing baseline is its own finding, never a pass.
        os.remove(os.path.join(tmp, SCOPE, BASELINE_NAME))
        findings6, _, _, _ = run_gate(tmp)
        check("missing baseline fails loudly",
              len(findings6) == 1 and "BASELINE-MISSING" in findings6[0],
              f"findings={findings6}")

    # Floors: a matcher that sees nothing must fail the floor, not pass.
    with tempfile.TemporaryDirectory() as tmp:
        _write_tree(tmp, TREE_BASE)
        _write_baseline(tmp, scan_scope(tmp))
        findings7, _, _, _ = run_gate(tmp, min_members=10 ** 9)
        check("absurd floor fails (matcher blindness is red)",
              any("PUBLICAPI-FLOOR" in f for f in findings7))

    print(f"\nself-test {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--min-members", type=int, default=0,
                    help="fail unless at least this many surface members "
                         "were scanned (0 = off)")
    ap.add_argument("--update", action="store_true",
                    help="regenerate the baseline from the current tree and "
                         "exit 0 (review the diff before committing)")
    ap.add_argument("--self-test", action="store_true",
                    help="run the gate against known-broken fixtures")
    ap.add_argument("--report", action="store_true",
                    help="print the scanned surface and exit 0")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if args.update:
        current = scan_scope(repo)
        with open(baseline_path(repo), "w", encoding="utf-8") as f:
            f.write(
                f"# Public-API baseline for {SCOPE} (#428, second slice).\n"
                f"#\n"
                f"# One sorted entry per effectively-public type/member: "
                f"`Qualified.Type :: normalized declaration`.\n"
                f"# No file paths, no line numbers — moves and re-wraps "
                f"must not fail the gate.\n"
                f"# Regenerate with `python3 tools/check-public-api-abstractions.py "
                f"--update`, review the diff, commit it in the same PR.\n")
            for entry in current:
                f.write(entry + "\n")
        print(f"wrote {len(current)} entries to "
              f"{os.path.join(SCOPE, BASELINE_NAME)}")
        return 0

    if args.report:
        for entry in scan_scope(repo):
            print(entry)
        return 0

    findings, total, added, removed = run_gate(
        repo, args.min_members)
    n_add = len(added) if added is not None else 0
    n_rem = len(removed) if removed is not None else 0
    print(f"compared {total} surface members against "
          f"{os.path.join(SCOPE, BASELINE_NAME)} "
          f"(+{n_add} added, -{n_rem} removed)")
    for text in findings:
        print(f"FAIL {text}")
    if findings:
        print(f"\n{len(findings)} finding(s) — red")
        return 1
    print("OK: surface matches the reviewed baseline")
    return 0


if __name__ == "__main__":
    sys.exit(main())
