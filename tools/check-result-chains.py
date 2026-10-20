#!/usr/bin/env python3
"""check-result-chains.py — the lever for issue #1558.

WHY PYTHON: stdlib only, and it must run where there is no .NET SDK and no
built solution — a gate that needs the SDK cannot run on the PR that fixes
the build.

Issue #1558 asks for the ``if (x.IsFailure) return …; var v = x.Value;`` shape
to turn into CSharpFunctionalExtensions ``.Bind()`` / ``.Map()`` chains. This
script finds those places so a reviewer can re-run the same census instead of
trusting a number typed into a comment.

Modes
-----
    --check              census only. Prints totals, per-file counts and the
                         per-site classification. With ``--baseline FILE`` it
                         is a gate: exit 1 when the number of chain
                         candidates is above the recorded one. Without a
                         baseline it is a report and exits 0.
    --apply              rewrites the one shape that is mechanically safe (see
                         REWRITE below) and leaves every other site untouched.
                         Prints what it changed and what it refused to touch.
    --write-baseline FILE
                         writes the current census to FILE so that
                         ``--check --baseline FILE`` gates on *new* candidates
                         only.
    --shapes             prints every chain candidate with the statement that
                         follows its guard — the review aid. This is what a
                         reviewer reads before deciding.
    --self-test          plants the shapes in a throwaway tree and proves the
                         scanner finds them, the classifier tells them apart,
                         and the rewrite produces the expected source. The
                         non-vacuity proof: a gate that stopped firing is
                         indistinguishable from a tree nobody changed.

REWRITE (the only mechanical rewrite this script performs)
----------------------------------------------------------
A method body of the exact shape

    if (X.IsFailure)
        return X.ConvertFailure<U>();        // or Result.Failure<U>(X.Error)
    var v = X.Value;
    return EXPR;                              // EXPR yields Result<U>

becomes

    return X.Bind(v => EXPR);

and the variant that reads ``X.Value`` inline:

    if (X.IsFailure)
        return X.ConvertFailure<U>();
    return EXPR;                              // EXPR reads X.Value

becomes

    return X.Bind(value => EXPR);

Both are semantics-identical. ``Bind`` on a failure yields
``Result.Failure<U>(x.Error)`` — the same error instance, in the same result
type, that the guard would have re-wrapped — and on a success it evaluates
EXPR with the same value. ``Bind`` does not catch exceptions, so an exception
raised inside EXPR propagates exactly as it did before.

The rewrite is applied only when every precondition is provable from the
text; the script refuses (and reports) everything else. The decision to
translate a site the script *can* rewrite is still a human one: run --apply on
a scratch branch, read the diff, keep what reads better.

Deliberately not covered: guards whose body is not a plain return (logging,
side effects), guards that build a *different* error message, guards whose
unwrapped value is used by more than the single following statement, and
methods whose body is a ladder of three or more links that also carries a
try/catch or a compensating side effect. Those need eyes, not a regex — see
docs/ROP-API-INVENTORY.md §3.1 for the house rule that decides which of them
is a chain and which stays a ladder.

What the categories mean
-------------------------
    chain-same       the guard re-types the failure the guarded result already
                     carries (ConvertFailure / Result.Failure(X.Error)) and
                     the guarded value is unwrapped further down — a candidate
                     for a chain.
    chain-mapped     the guard builds a different error message and the
                     guarded value is unwrapped further down — a candidate for
                     a chain plus MapError.
    terminal-same    the same re-type, but nothing is unwrapped after it: the
                     guard IS the statement. Nothing to chain.
    terminal-mapped  the same, with a rewritten message.
    side-effect      the guard body is not a plain return (it logs, or it has
                     a side effect). The explicit form is the honest one.
    non-result       the guard does not return a Result at all (it returns a
                     domain record, an HTTP response, a bool …). This is not
                     a Result ladder and the census does not count it.
    rewriteable      the mechanical rewrite above applies here.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
from dataclasses import dataclass

# --------------------------------------------------------------------------
# lexical helpers
# --------------------------------------------------------------------------

_IDENT = r"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*"


def blank_code(text: str) -> str:
    """Return `text` with comments and string/char literals replaced by spaces.

    Line structure and length are preserved, so offsets in the blanked text
    still point at the original source. This is what lets the script ignore an
    ``IsFailure`` that only appears inside a comment or a string literal.
    """
    out = list(text)
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if ch == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                out[i] = " "
                i += 1
        elif ch == "/" and nxt == "*":
            out[i] = out[i + 1] = " "
            i += 2
            while i < n and not (text[i] == "*" and i + 1 < n and text[i + 1] == "/"):
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            if i < n:
                out[i] = " "
                if i + 1 < n:
                    out[i + 1] = " "
                i += 2
        elif ch == "@" and nxt == '"':
            i += 1
            if i < n and text[i] == '"':
                out[i] = " "
                i += 1
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out[i] = out[i + 1] = " "
                        i += 2
                        continue
                    out[i] = " "
                    i += 1
                    break
                if text[i] != "\n":
                    out[i] = " "
                i += 1
        elif ch in "\"'":
            quote = ch
            i += 1
            while i < n:
                if text[i] == "\\":
                    out[i] = " "
                    if i + 1 < n and text[i + 1] != "\n":
                        out[i + 1] = " "
                    i += 2
                    continue
                if text[i] == quote:
                    out[i] = " "
                    i += 1
                    break
                if text[i] != "\n":
                    out[i] = " "
                i += 1
        else:
            i += 1
    return "".join(out)


def match_pair(text: str, open_idx: int, opener: str, closer: str) -> int:
    """Index of the closer matching the opener at open_idx, or -1."""
    depth = 0
    for i in range(open_idx, len(text)):
        c = text[i]
        if c == opener:
            depth += 1
        elif c == closer:
            depth -= 1
            if depth == 0:
                return i
    return -1


# --------------------------------------------------------------------------
# site model
# --------------------------------------------------------------------------

K_CHAIN_SAME = "chain-same"            # guard re-types the same failure, unwrap follows
K_CHAIN_MAPPED = "chain-mapped"        # guard rewrites the message, unwrap follows
K_TERMINAL_SAME = "terminal-same"      # same failure, nothing unwrapped -> already minimal
K_TERMINAL_MAPPED = "terminal-mapped"  # different message, nothing unwrapped
K_SIDE_EFFECT = "side-effect"          # guard body is not a plain return
K_NON_RESULT = "non-result"            # the guard does not return a Result at all
K_REWRITEABLE = "rewriteable"          # guard + unwrap + single return -> Bind

CHAIN_KINDS = (K_CHAIN_SAME, K_CHAIN_MAPPED, K_REWRITEABLE)


@dataclass
class Site:
    path: str
    line: int                     # 1-based line of the `if (...)`
    guard_var: str                # the X in `X.IsFailure`
    kind: str
    detail: str = ""
    rewrite: str | None = None    # replacement text, when --apply can do it
    start: int = 0                # offset of the region to replace
    end: int = 0
    guard_start: int = 0          # offset of the `if` keyword
    next_statement: str = ""      # the statement after the guard, for --shapes


def classify(body: str, guard_var: str) -> tuple[str, str]:
    """Classify the body of an ``if (X.IsFailure)`` guard.

    ``same`` means the guard re-types the failure the guarded result already
    carries (``ConvertFailure`` / ``Result.Failure(X.Error)``); ``mapped``
    means the guard builds a different message. The distinction decides
    whether a translation can use ``Bind`` alone or needs ``MapError`` too.
    """
    b = " ".join(body.split())
    if not b.startswith("return"):
        return K_SIDE_EFFECT, "guard body does not return"
    expr = b[len("return"):].strip().rstrip(";").strip()
    if re.fullmatch(rf"{re.escape(guard_var)}\.ConvertFailure\(\)", expr):
        return K_CHAIN_SAME, "ConvertFailure() re-type"
    m = re.fullmatch(rf"{re.escape(guard_var)}\.ConvertFailure<(.+)>\(\)", expr)
    if m:
        return K_CHAIN_SAME, f"ConvertFailure<{m.group(1)}>() re-type"
    m = re.fullmatch(rf"Result\.Failure(<[^>]+>)?\(\s*{re.escape(guard_var)}\.Error\s*\)", expr)
    if m:
        return K_CHAIN_SAME, "Result.Failure(X.Error) re-type"
    if re.fullmatch(r"Result\.Failure(<[^>]+>)?\(.*\)", expr):
        return K_CHAIN_MAPPED, "guard returns a different error message"
    if re.match(r"Result\.(Success|Failure)\b", expr):
        return K_CHAIN_MAPPED, "guard returns a Result the chain cannot rebuild"
    return K_NON_RESULT, "the guard does not return a Result — this is not a Result ladder"


# --------------------------------------------------------------------------
# scanning
# --------------------------------------------------------------------------

IF_GUARD_RE = re.compile(r"\bif\s*\(\s*(?P<x>" + _IDENT + r")\s*\.\s*IsFailure\s*\)")
ASSIGN_VALUE_RE = re.compile(
    r"^(?:var|Result<[^;]*?>|readonly\s+Result<[^;]*?>|[A-Za-z_][A-Za-z0-9_]*)\s+"
    r"(?P<v>" + _IDENT + r")\s*=\s*(?P<x>" + _IDENT + r")\.Value\s*;$"
)


def _skip_ws(code: str, i: int) -> int:
    while i < len(code) and code[i] in " \t\r\n":
        i += 1
    return i


def _read_statement(code: str, i: int) -> tuple[str, int]:
    """Read one statement starting at i (code is blanked). Returns (text, end)."""
    start = i
    depth = 0
    while i < len(code):
        c = code[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                break
            depth -= 1
        elif c == ";" and depth == 0:
            return code[start:i + 1], i + 1
        i += 1
    return code[start:i], i


def unwraps_after(code: str, body_end: int, guard_var: str) -> bool:
    """True when the guarded variable is unwrapped with `.Value` further down.

    The scan stops at the end of the enclosing block, so a `.Value` read in a
    *sibling* method does not count.
    """
    pat = re.compile(r"\b" + re.escape(guard_var) + r"\.Value\b")
    rel = 0
    i = body_end
    while i < len(code):
        c = code[i]
        if c == "{":
            rel += 1
        elif c == "}":
            if rel == 0:
                return False
            rel -= 1
        elif rel == 0 and pat.match(code, i):
            return True
        i += 1
    return False


def scan_file(path: str, rel: str) -> list[Site]:
    with open(path, encoding="utf-8") as fh:
        raw = fh.read()
    code = blank_code(raw)
    sites: list[Site] = []

    for m in IF_GUARD_RE.finditer(code):
        guard_var = m.group("x")
        line_no = code.count("\n", 0, m.start()) + 1
        after = _skip_ws(code, m.end())

        if after < len(code) and code[after] == "{":
            close = match_pair(code, after, "{", "}")
            if close < 0:
                continue
            inner = code[after + 1:close]
            # a brace body that holds exactly one statement is still a
            # "plain return" guard
            stmts = [s for s in inner.split(";") if s.strip()]
            if len(stmts) == 1 and stmts[0].strip().startswith("return"):
                body = stmts[0].strip() + ";"
                body_end = close + 1
            else:
                kind, detail = classify(inner, guard_var)
                sites.append(Site(rel, line_no, guard_var, kind, detail))
                continue
        else:
            body, body_end = _read_statement(code, after)
            if not body.strip().startswith("return"):
                kind, detail = classify(body, guard_var)
                sites.append(Site(rel, line_no, guard_var, kind, detail))
                continue

        kind, detail = classify(body, guard_var)
        site = Site(rel, line_no, guard_var, kind, detail)
        site.guard_start = m.start()
        sites.append(site)
        _maybe_rewrite(site, code, guard_var, body_end)
        if site.kind in (K_CHAIN_SAME, K_CHAIN_MAPPED) and not unwraps_after(code, body_end, guard_var):
            site.kind = K_TERMINAL_SAME if site.kind == K_CHAIN_SAME else K_TERMINAL_MAPPED
            site.detail = "terminal guard: the guarded value is never unwrapped"
        _record_next(site, code, body_end)
    return sites


def _record_next(site: Site, code: str, body_end: int) -> None:
    """Remember the statement that follows the guard, for --shapes."""
    j = _skip_ws(code, body_end)
    stmt, _ = _read_statement(code, j)
    site.next_statement = " ".join(stmt.split())[:160]


def _maybe_rewrite(site: Site, code: str, guard_var: str, body_end: int) -> None:
    """Try the mechanical Bind rewrite on a chain-same guard.

    Two shapes are recognised, both ending in a single ``return EXPR;`` that
    is the last statement of the enclosing block:

        if (X.IsFailure)          var v = X.Value;   return EXPR;
            return X.ConvertFailure<U>();

        if (X.IsFailure)          return EXPR;      // EXPR reads X.Value
            return X.ConvertFailure<U>();

    Both become ``return X.Bind(v => EXPR);``. Anything else is refused.
    """
    if site.kind != K_CHAIN_SAME:
        return
    i = _skip_ws(code, body_end)

    stmt, stmt_end = _read_statement(code, i)
    m = ASSIGN_VALUE_RE.match(stmt.strip())
    if m and m.group("x") == guard_var:
        local = m.group("v")
        j = _skip_ws(code, stmt_end)
        ret_stmt, ret_end = _read_statement(code, j)
        rs = ret_stmt.strip()
        if not (rs.startswith("return ") and rs.endswith(";")):
            return
        expr_src = rs[len("return"):].strip()[:-1].strip()
    else:
        # the return reads X.Value directly
        rs = stmt.strip()
        if not (rs.startswith("return ") and rs.endswith(";")):
            return
        expr_src = rs[len("return"):].strip()[:-1].strip()
        local = "value"
        if not re.search(r"\b" + re.escape(guard_var) + r"\.Value\b", expr_src):
            return
        expr_src = re.sub(r"\b" + re.escape(guard_var) + r"\.Value\b", local, expr_src)
        ret_end = stmt_end

    if not expr_src:
        return
    # the return must be the last statement before the method's closing brace
    k = _skip_ws(code, ret_end)
    if k >= len(code) or code[k] != "}":
        return
    # the local must be used by this return and by nothing else in the block
    if not re.search(r"\b" + re.escape(local) + r"\b", expr_src):
        return
    tail = code[ret_end:k]
    if re.search(r"\b" + re.escape(local) + r"\b", tail):
        return

    site.kind = K_REWRITEABLE
    site.detail = f"guard + unwrap + single return -> Bind({local} => ...)"
    site.start = site.guard_start
    site.end = ret_end
    site.rewrite = f"return {guard_var}.Bind({local} => {expr_src});"


# --------------------------------------------------------------------------
# apply
# --------------------------------------------------------------------------


def apply_sites(sites: list[Site], root: str) -> tuple[int, list[str]]:
    """Perform the mechanical rewrite for every rewriteable site."""
    by_file: dict[str, list[Site]] = {}
    for s in sites:
        if s.kind == K_REWRITEABLE:
            by_file.setdefault(s.path, []).append(s)

    changed = 0
    notes: list[str] = []
    for rel, file_sites in by_file.items():
        path = os.path.join(root, rel)
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        # apply bottom-up so earlier offsets stay valid
        for s in sorted(file_sites, key=lambda s: s.start, reverse=True):
            line_start = text.rfind("\n", 0, s.start) + 1
            indent = re.match(r"[ \t]*", text[line_start:]).group(0)
            replacement = indent + s.rewrite + "\n"
            text = text[:line_start] + replacement + text[s.end:]
            changed += 1
            notes.append(f"{rel}:{text.count(chr(10), 0, s.start) + 1} -> Bind")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)
    return changed, notes


# --------------------------------------------------------------------------
# census
# --------------------------------------------------------------------------

DEFAULT_ROOTS = ["src", "apps"]


def collect(roots: list[str], base: str) -> list[Site]:
    sites: list[Site] = []
    for root in roots:
        for dirpath, _dirnames, filenames in os.walk(os.path.join(base, root)):
            for name in sorted(filenames):
                if not name.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, name)
                rel = os.path.relpath(path, base)
                sites.extend(scan_file(path, rel))
    return sites


def census(sites: list[Site]) -> tuple[dict[str, int], dict[str, int], list[Site]]:
    by_kind: dict[str, int] = {}
    per_file: dict[str, int] = {}
    for s in sites:
        by_kind[s.kind] = by_kind.get(s.kind, 0) + 1
        per_file[s.path] = per_file.get(s.path, 0) + 1
    survivors = [s for s in sites if s.kind in CHAIN_KINDS]
    return by_kind, per_file, survivors


# --------------------------------------------------------------------------
# self-test
# --------------------------------------------------------------------------

SELF_TEST_SOURCE = """\
using CSharpFunctionalExtensions;

namespace Probe;

internal static class Probe
{
    // A comment mentioning x.IsFailure must not be counted.
    internal static Result<int> Rewriteable(Result<int> x)
    {
        if (x.IsFailure)
            return x.ConvertFailure<int>();
        var v = x.Value;
        return Result.Success(v + 1);
    }

    internal static Result<int> Terminal(Result<int> x)
    {
        if (x.IsFailure)
            return x.ConvertFailure<int>();
        return Result.Success(0);
    }

    internal static Result<int> Mapped(Result<int> x)
    {
        if (x.IsFailure)
            return Result.Failure<int>($"boom: {x.Error}");
        var v = x.Value;
        return Result.Success(v);
    }

    internal static int SideEffect(Result<int> x)
    {
        if (x.IsFailure)
        {
            Log(x.Error);
            return 0;
        }
        return x.Value;
    }

    internal static object NotAResult(Result<int> x)
    {
        if (x.IsFailure)
            return new Thing(x.Error);
        return x.Value;
    }

    private static void Log(string message) { }
    private sealed class Thing { public Thing(string message) { } }
}
"""


def self_test() -> int:
    """Plant the shapes in a throwaway tree and prove the scanner sees them."""
    failures: list[str] = []

    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(tmp, "src", "Probe")
        os.makedirs(src)
        with open(os.path.join(src, "Probe.cs"), "w", encoding="utf-8") as fh:
            fh.write(SELF_TEST_SOURCE)

        sites = collect(["src"], tmp)
        by_kind: dict[str, int] = {}
        for s in sites:
            by_kind[s.kind] = by_kind.get(s.kind, 0) + 1

        expected = {
            K_REWRITEABLE: 1,   # Rewriteable
            K_TERMINAL_SAME: 1,  # Terminal
            K_CHAIN_MAPPED: 1,  # Mapped
            K_SIDE_EFFECT: 1,   # SideEffect
            K_NON_RESULT: 1,    # NotAResult
        }
        for kind, want in expected.items():
            got = by_kind.get(kind, 0)
            if got != want:
                failures.append(f"{kind}: expected {want}, got {got}")

        # the comment line must not produce a site
        if len(sites) != sum(expected.values()):
            failures.append(f"expected {sum(expected.values())} sites, got {len(sites)}")

        # the rewrite must produce the expected source
        changed, _ = apply_sites(sites, tmp)
        if changed != 1:
            failures.append(f"expected 1 rewrite, got {changed}")
        with open(os.path.join(src, "Probe.cs"), encoding="utf-8") as fh:
            rewritten = fh.read()
        rewritten_method = rewritten.split("Rewriteable(Result<int> x)")[1].split("}")[0]
        if "return x.Bind(v => Result.Success(v + 1));" not in rewritten_method:
            failures.append("the rewrite did not produce the expected Bind call")
        if "x.ConvertFailure<int>()" in rewritten_method:
            failures.append("the rewrite left the old guard behind")
        # the other methods must be untouched
        for name in ("Terminal", "Mapped", "SideEffect", "NotAResult"):
            body = rewritten.split(f"{name}(Result<int> x)")[1].split("private static")[0]
            if "if (x.IsFailure)" not in body:
                failures.append(f"the rewrite touched the {name} method")

        # after the rewrite the census must report no chain candidate there
        after = collect(["src"], tmp)
        after_kinds: dict[str, int] = {}
        for s in after:
            after_kinds[s.kind] = after_kinds.get(s.kind, 0) + 1
        if after_kinds.get(K_REWRITEABLE, 0) != 0:
            failures.append("the rewrite left a rewriteable site behind")

    if failures:
        for f in failures:
            print(f"FAIL: {f}", file=sys.stderr)
        return 1
    print("self-test OK: the scanner finds every planted shape, tells them apart, "
          "and the rewrite produces the expected source.")
    return 0


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    ap.add_argument("--roots", nargs="*", default=DEFAULT_ROOTS)
    ap.add_argument("--check", action="store_true", help="census only; exit 1 above the baseline")
    ap.add_argument("--apply", action="store_true", help="apply the mechanical rewrite")
    ap.add_argument("--shapes", action="store_true", help="print every candidate with its context")
    ap.add_argument("--self-test", action="store_true", help="prove the scanner is not vacuous")
    ap.add_argument("--baseline", help="baseline file: exit 1 only above the recorded count")
    ap.add_argument("--write-baseline", help="write the current census to this file")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--per-file", action="store_true", help="print the per-file table")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()

    base = os.path.abspath(args.repo)
    sites = collect(args.roots, base)
    by_kind, per_file, survivors = census(sites)

    if args.write_baseline:
        with open(args.write_baseline, "w", encoding="utf-8") as fh:
            json.dump(
                {
                    "total_guards": len(sites),
                    "by_kind": by_kind,
                    "survivors": len(survivors),
                    "per_file": per_file,
                },
                fh,
                indent=2,
                sort_keys=True,
            )
            fh.write("\n")
        print(f"baseline written to {args.write_baseline}")
        return 0

    if args.apply:
        changed, notes = apply_sites(sites, base)
        for n in notes:
            print(f"rewrote {n}")
        print(f"rewrote {changed} site(s); {len(sites) - changed} site(s) left for a human")
        return 0

    if args.shapes:
        for s in sites:
            if s.kind not in CHAIN_KINDS:
                continue
            print(f"{s.path}:{s.line}  [{s.kind}] {s.guard_var}")
            print(f"    guard : {s.detail}")
            print(f"    next  : {s.next_statement}")
        print(f"\n{len(survivors)} chain candidate(s)")
        return 0

    if args.json:
        json.dump(
            {
                "total_guards": len(sites),
                "by_kind": by_kind,
                "survivors": len(survivors),
                "per_file": per_file,
                "sites": [
                    {"path": s.path, "line": s.line, "var": s.guard_var, "kind": s.kind, "detail": s.detail}
                    for s in sites
                ],
            },
            sys.stdout,
            indent=2,
        )
        sys.stdout.write("\n")
    else:
        print(f"guard sites (`if (x.IsFailure)`) in {', '.join(args.roots)}: {len(sites)}")
        for kind in sorted(by_kind):
            print(f"  {kind:<16} {by_kind[kind]}")
        print(f"  chain candidates (a chain could replace the guard): {len(survivors)}")
        if args.per_file:
            print()
            print(f"{'file':<70} sites")
            for path, count in sorted(per_file.items(), key=lambda kv: (-kv[1], kv[0])):
                print(f"{path:<70} {count}")

    if args.check:
        if not args.baseline:
            print("report mode: no baseline given, so nothing is compared. "
                  "Pass --baseline FILE for the gate (exit 1 above the recorded count).")
            return 0
        allowed = 0
        try:
            with open(args.baseline, encoding="utf-8") as fh:
                allowed = int(json.load(fh).get("survivors", 0))
        except (OSError, ValueError, KeyError) as exc:
            print(f"baseline unreadable: {exc}", file=sys.stderr)
            return 2
        if len(survivors) > allowed:
            print(
                f"FAIL: {len(survivors)} chain candidate(s) (baseline {allowed}). "
                "Chain them, or record in the baseline why not.",
                file=sys.stderr,
            )
            return 1
        print(f"OK: {len(survivors)} chain candidate(s), baseline {allowed}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
