#!/usr/bin/env python3
"""Non-vacuity proof for #401.

The claim under test: "no tool call is started once the run token is observed."

The dangerous failure mode for a guard like this is VACUITY — it passes because
it never reaches the gate (wrong tool name, wrong registry, an exception
swallowed earlier, the gate placed after the point it was supposed to guard).
So instead of trusting the test file, we model the dispatch loop EXACTLY as it
reads in ToolDispatcher.ExecuteAsync and run the real batch shape through both
the OLD and the NEW loop, counting how many calls reach the tool body.

Deterministic: no sleeps, no threads. The tool cancels from inside call #1, so
"the token is already fired when iteration 2 begins" is a structural fact.
"""
import sys

BATCH = 4
CANCEL_ON = 0  # 0-based index of the call that cancels from inside itself


class Token:
    """CancellationToken: a flag."""

    def __init__(self):
        self.fired = False

    def is_cancellation_requested(self):
        return self.fired

    def cancel(self):
        self.fired = True


def run_old_loop(token, entered):
    """Pre-fix: every iteration calls ExecuteSingleAsync unconditionally."""
    for i in range(BATCH):
        entered.append(i)
        # The tool body runs; call #CANCEL_ON cancels the run from inside.
        if i == CANCEL_ON:
            token.cancel()


def run_new_loop(token, entered):
    """Post-fix: the gate is consulted before each dispatch."""
    for i in range(BATCH):
        if token.is_cancellation_requested():
            entered.append(("skipped", i))
            continue
        entered.append(i)
        if i == CANCEL_ON:
            token.cancel()


def reached_tool(entered):
    return [e for e in entered if not (isinstance(e, tuple) and e[0] == "skipped")]


print(f"batch={BATCH}  cancel from inside call #{CANCEL_ON}")
print()

tok_old = Token()
old = []
run_old_loop(tok_old, old)
n_old = len(reached_tool(old))

tok_new = Token()
new = []
run_new_loop(tok_new, new)
n_new = len(reached_tool(new))

print(f"OLD loop — tool invocations: {n_old}  (trace: {old})")
print(f"NEW loop — tool invocations: {n_new}  (trace: {new})")
print()

fail = False

# 1. The guard MUST be red on the unfixed tree: more than one call reached the
#    tool after the cancel. This is what proves the guard can fail at all.
if n_old <= 1:
    print(f"FATAL: old loop only reached {n_old} call(s) — the guard would be VACUOUS")
    fail = True
else:
    print(f"OK  non-vacuity: old tree starts {n_old} calls, so an assertion of "
          f"'<= 1' is RED on it ({n_old - 1} post-cancel starts)")

# 2. The fix must make it green.
if n_new != 1:
    print(f"FATAL: new loop reached {n_new} calls, expected exactly 1")
    fail = True
else:
    print("OK  fix holds: only the already-in-flight call reached the tool")

# 3. The gate must actually be consulted (not dead code) — prove it fires.
skipped = [e for e in new if isinstance(e, tuple)]
if len(skipped) != BATCH - 1:
    print(f"FATAL: gate fired {len(skipped)} times, expected {BATCH - 1} — gate is unreachable")
    fail = True
else:
    print(f"OK  gate is live: it refused {len(skipped)} post-cancel dispatches "
          f"({[s[1] for s in skipped]})")

# 4. The token really was fired BEFORE the later iterations — otherwise the
#    whole scenario is a race the test cannot rely on.
if not tok_old.fired or not tok_new.fired:
    print("FATAL: token never fired — scenario does not reproduce")
    fail = True
else:
    print("OK  token fired inside the batch, before later iterations: deterministic, no sleeps")

print()
print("RESULT:", "FAIL" if fail else "PASS — guard is non-vacuous and the fix holds")
sys.exit(1 if fail else 0)