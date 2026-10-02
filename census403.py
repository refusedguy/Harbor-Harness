#!/usr/bin/env python3
"""#403 census: every way a run can end, what it reports today, and where the
limits fields are declared.

The issue's acceptance criteria rest on three claims that have to be auditable
rather than asserted:

  1. "Exactly one limits type is used by the agent loop."  -> DECLARATIONS
  2. "There is no wall-clock bound anywhere in the loop."   -> CLOCK SEAMS
  3. "A limit hit is distinguishable from a cancel and from a failure."
     -> TERMINATION SITES, with the token each one carries

Action kinds and the token column are the #401 census's, unchanged, so the two
tables are directly comparable: a wall-clock limit implemented the way
ToolTimeoutSeconds is implemented fires the run token, and every row below whose
token is `ct(run)` becomes reachable from a timeout as well as from a user
cancel. That is the reason the timeout is a new instance of the #401 class and
not a duplicate of it.
"""
import os
import re
import sys

ROOTS = ["src", "apps"]
SKIP = ("contrib/", "obj/", "bin/", "/tests/")

# ── 1. every declaration of a limits field ──────────────────────────────────
LIMIT_FIELDS = ("TimeoutSeconds", "MaxSteps")

decls = []
for root in ROOTS:
    for dirpath, dirnames, filenames in os.walk(root):
        if any(s in dirpath + "/" for s in SKIP):
            continue
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            lines = open(path, encoding="utf-8").read().splitlines()
            for i, ln in enumerate(lines, 1):
                s = ln.strip()
                # A doc comment is prose about the field, not a declaration of
                # it — CachingSystemPromptBuilder.cs:69 names MaxSteps in a
                # sentence and is not a limits type.
                if s.startswith("//"):
                    continue
                if "record" not in s and "class" not in s and "struct" not in s:
                    continue
                m = re.search(r"\b(?:record|class|struct)\s+(\w+)", s)
                if m is None:
                    continue
                # The primary-constructor parameter list, which may span lines.
                # Comments are dropped from the window for the same reason.
                window_lines = [lines[i - 1]]
                for follow in lines[i:i + 14]:
                    if follow.strip().startswith("//"):
                        break
                    window_lines.append(follow)
                    if ")" in follow:
                        break
                window = " ".join(window_lines)
                # Only the parenthesised list counts. A `private const int
                # TimeoutSeconds = 30;` (RipGrepTool.cs:16) is a subprocess
                # deadline for one tool, not a run limit, and it declares no
                # parameter list at all — so it falls out here rather than
                # needing a hand-maintained exclusion.
                paren = re.search(r"\((.*?)\)", window, re.S)
                if paren is None:
                    continue
                head = paren.group(1)
                for fld in LIMIT_FIELDS:
                    if re.search(rf"\b{fld}\b", head):
                        decls.append((m.group(1), fld, path, i))

print("=== 1. DECLARATIONS of a limits field (src/ + apps/) ===")
print(f"{'TYPE':<24}{'FIELD':<18}{'FILE':<52}LINE")
print("-" * 110)
for t, fld, path, i in sorted(decls):
    print(f"{t:<24}{fld:<18}{path:<52}{i}")
print(f"\nTOTAL declarations: {len(decls)}")

by_field = {}
for _, fld, _, _ in decls:
    by_field.setdefault(fld, set()).add(_)
print("\nBY FIELD (distinct declaring types):")
for fld in LIMIT_FIELDS:
    types = sorted({t for t, f2, _, _ in decls if f2 == fld})
    print(f"  {len(types)}  {fld:<18} {types}")
print()

# ── 2. clock seams: where a wall-clock limit could be enforced ──────────────
CLOCK_FILES = [
    "src/Harbor.Application/Agents/AgentLoop.cs",
    "src/Harbor.Application/Agents/TurnRunner.cs",
    "src/Harbor.Application/Agents/ToolDispatcher.cs",
    "src/Harbor.Application/Resilience/RetryPolicy.cs",
    "src/Harbor.Application/Agents/Pipeline/MaxStepsBehavior.cs",
]

print("=== 2. CLOCK SEAMS in the run path ===")
print(f"{'FILE':<50}{'TimeProvider':<14}{'CancelAfter':<13}CODE")
print("-" * 120)
for path in CLOCK_FILES:
    if not os.path.exists(path):
        print(f"MISSING {path}", file=sys.stderr)
        continue
    lines = open(path, encoding="utf-8").read().splitlines()
    found = False
    for i, ln in enumerate(lines, 1):
        if "TimeProvider" in ln or "CancelAfter" in ln:
            found = True
            print(f"{path:<50}{'yes' if 'TimeProvider' in ln else '-':<14}"
                  f"{'yes' if 'CancelAfter' in ln else '-':<13}{ln.strip()[:60]}")
    if not found:
        print(f"{path:<50}{'-':<14}{'-':<13}(no clock, no deadline)")
print()

# ── 3. every way a run ends, and what it reports ────────────────────────────
LOOP = "src/Harbor.Application/Agents/AgentLoop.cs"
TURN = "src/Harbor.Application/Agents/TurnRunner.cs"

print("=== 3. TERMINATION SITES and the token each carries ===")
sites = []
for path in (TURN, LOOP):
    lines = open(path, encoding="utf-8").read().splitlines()
    for i, ln in enumerate(lines, 1):
        s = ln.strip()
        if "EndRun: true" in s or "EndRun: false" in s or "AgentEndEvent(" in s \
                or "AgentErrorEvent(" in s or "IsCancellationRequested" in s:
            window = " ".join(lines[i - 1:i + 3])
            tok = "?"
            if "CancellationToken.None" in window:
                tok = "CancellationToken.None"
            elif re.search(r"\bct\)", window):
                tok = "ct(run)"
            if "AgentEndEvent(" in s and "Cancelled: true" in window:
                verdict = "Stopped (cancel)"
            elif "AgentEndEvent(" in s:
                verdict = "Succeeded (no limit known)"
            elif "AgentErrorEvent(" in s:
                verdict = "Failed"
            elif "EndRun: true" in s:
                verdict = "run ends — reason not carried"
            else:
                verdict = "loop continues"
            sites.append((path.split("/")[-1], i, verdict, tok, s[:64]))

print(f"{'FILE':<16}{'LINE':>5}  {'WHAT IT REPORTS':<28}{'TOKEN':<20}CODE")
print("-" * 130)
for f, i, v, t, s in sites:
    print(f"{f:<16}{i:>5}  {v:<28}{t:<20}{s}")
print(f"\nTOTAL termination sites: {len(sites)}")
print("\nThe 'run ends — reason not carried' rows are the finding: a run that ends")
print("for a reason the caller must be told about is indistinguishable from a run")
print("that finished its work, because EndRun is a bool.")
