#!/usr/bin/env python3
"""#401 census: every site that can still take an ACTION after the run token fires.

Action kinds: event publish, state write (append/drain), new turn,
new provider request, tool invocation, retry.
"""
import re, os, sys

ROOTS = ["src", "apps"]
SKIP = ("contrib/", "obj/", "bin/")

# Files on the run path: reachable from AgentLoop.RunCoreAsync.
CANDIDATES = [
    "src/Harbor.Application/Agents/AgentLoop.cs",
    "src/Harbor.Application/Agents/TurnRunner.cs",
    "src/Harbor.Application/Agents/ToolDispatcher.cs",
    "src/Harbor.Application/Agents/DefaultAgent.cs",
    "src/Harbor.Application/Agents/BackgroundDrain.cs",
    "src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs",
    "src/Harbor.Application/Agents/Pipeline/SteeringDrainBehavior.cs",
    "src/Harbor.Application/Resilience/RetryPolicy.cs",
    "src/Harbor.Application/Permissions/ApprovalCoordinator.cs",
]

rows = []
for path in CANDIDATES:
    if not os.path.exists(path):
        print(f"MISSING {path}", file=sys.stderr); continue
    lines = open(path, encoding="utf-8").read().splitlines()
    for i, ln in enumerate(lines, 1):
        s = ln.strip()
        kind = None
        if ".PublishAsync(" in s:
            kind = "event-publish"
        elif ".AppendMessageAsync(" in s:
            kind = "state-write(append)"
        elif ".DrainAsync(" in s:
            kind = "state-write(drain)"
        elif ".UpdateStatsAsync(" in s:
            kind = "state-write(stats)"
        elif "tool.ExecuteAsync(" in s or "ExecuteWithRetryAsync(" in s:
            kind = "tool-invocation"
        elif "StreamAsync(" in s or "operation(ct)" in s or "ConsumeTurnStreamAsync(" in s:
            kind = "provider-request"
        elif "Task.Delay(" in s:
            kind = "delay"
        if not kind:
            continue
        # which token does this call carry?
        tok = "?"
        joined = " ".join(lines[i-1:i+3])
        if "TerminalEventToken" in joined: tok = "TerminalEventToken"
        elif "CancellationToken.None" in joined: tok = "CancellationToken.None"
        elif re.search(r"\bct\)", joined): tok = "ct(run)"
        elif re.search(r"\beffectiveCt\)", joined): tok = "effectiveCt(run)"
        elif re.search(r"\battemptCt\)", joined): tok = "attemptCt(run)"
        rows.append((path.split("/")[-1], i, kind, tok, s[:78]))

print(f"{'FILE':<26}{'LINE':>5}  {'KIND':<22}{'TOKEN':<20}CODE")
print("-" * 130)
for f, i, k, t, s in rows:
    print(f"{f:<26}{i:>5}  {k:<22}{t:<20}{s}")

print(f"\nTOTAL action sites on the run path: {len(rows)}")
by_kind = {}
for _, _, k, t, _ in rows:
    by_kind[(k, t)] = by_kind.get((k, t), 0) + 1
print("\nBY (kind, token):")
for (k, t), c in sorted(by_kind.items()):
    print(f"  {c:>3}  {k:<22} {t}")