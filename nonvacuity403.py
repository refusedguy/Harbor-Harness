#!/usr/bin/env python3
"""Non-vacuity proof for #403 — the step-limit stop reason.

THE CLAIM UNDER TEST
--------------------
"A run that hit its step budget must not reconstruct as Succeeded."

The dangerous failure mode for a guard here is VACUITY, and for THIS issue it
has a specific shape. The whole pre-existing test suite (RunOutcomeTests.cs, 3
tests) passes on the unfixed tree, because the unfixed tree answers Succeeded
to *everything* that is not cancelled and not errored. A guard of the form
"reconstruct X and assert it is not Succeeded" therefore cannot tell a correct
tree from a weakened one: the catch-all satisfies it for the wrong reason.

So the synthetic below does not assert a verdict. It asserts the thing that is
actually missing on `origin/dev`: that the OLD decision chain is *incapable* of
telling a step-capped run from a clean run, because both arrive at the catch-all
with the same three inputs. Then it shows the NEW chain separating them, and
finally deletes the new branch again to prove the separation is what the branch
buys — a positive control, in the #591 sense (a matcher weakened into returning
a plausible verdict on a tree it never examined).

The chain below is transcribed from src/Harbor.Abstractions.Contracts/Models/
RunOutcome.cs:155-167 (pre-fix) and the same lines with one added arm
(post-fix). `HISTORY` is the message list a real capped run leaves behind: the
last assistant message carries StopReason.ToolUse, because the run was cut off
immediately after a turn that DID emit tool calls and had them executed.

Deterministic: no sleeps, no threads, no clock.
"""
import sys

# ── the vocabulary (RunStopReason, RunOutcome.cs) ──────────────────────────
SUCCEEDED = "Succeeded"
FAILED = "Failed"
STOPPED = "Stopped"
LIMIT_EXCEEDED = "LimitExceeded"  # the new member


# ── the run, as the store holds it ──────────────────────────────────────────
class AssistantMessage:
    def __init__(self, stop_reason):
        self.StopReason = stop_reason


# A step-capped run and a clean run leave STRUCTURALLY IDENTICAL history:
# the capped run's last assistant message is a tool-use turn, exactly like the
# last turn of a run that then happened to finish. Nothing in the transcript
# distinguishes them — which is the entire point.
CAPPED_HISTORY = [AssistantMessage("ToolUse")]
CLEAN_HISTORY = [AssistantMessage("ToolUse")]


# ── the decision chain, transcribed ─────────────────────────────────────────
def decide_old(cancelled, error_message, last_assistant, limit):
    """RunOutcome.cs:155-167 as it reads on origin/dev."""
    if cancelled:
        return STOPPED
    if error_message is not None:
        return FAILED
    if last_assistant is not None and last_assistant.StopReason == "Aborted":
        return STOPPED
    if last_assistant is not None and last_assistant.StopReason == "Error":
        return FAILED
    return SUCCEEDED  # ← the catch-all #993 is about


def decide_new(cancelled, error_message, last_assistant, limit):
    """The same chain with ONE added arm, after errorMessage."""
    if cancelled:
        return STOPPED
    if error_message is not None:
        return FAILED
    if limit is not None:
        return LIMIT_EXCEEDED
    if last_assistant is not None and last_assistant.StopReason == "Aborted":
        return STOPPED
    if last_assistant is not None and last_assistant.StopReason == "Error":
        return FAILED
    return SUCCEEDED


# ── the two runs ────────────────────────────────────────────────────────────
def run(history, limit, decide):
    return decide(
        cancelled=False,
        error_message=None,
        last_assistant=history[-1] if history else None,
        limit=limit,
    )


capped_old = run(CAPPED_HISTORY, "MaxSteps", decide_old)
clean_old = run(CLEAN_HISTORY, None, decide_old)
capped_new = run(CAPPED_HISTORY, "MaxSteps", decide_new)
clean_new = run(CLEAN_HISTORY, None, decide_new)

print("run                     limit      old chain   new chain")
print("-" * 62)
print(f"hit the step budget     MaxSteps   {capped_old:<11} {capped_new}")
print(f"finished normally       (none)     {clean_old:<11} {clean_new}")
print()

failures = []


def check(name, condition, detail):
    status = "ok  " if condition else "FAIL"
    print(f"[{status}] {name}")
    if not condition:
        failures.append(name)
        print(f"        {detail}")


# 1. THE PRE-EXISTING TREE. This is what is red today, and it is red as a
#    CAPACITY claim, not a verdict claim: the old chain gives the two runs the
#    same answer, so no amount of asserting on `Succeeded` could have caught it.
check(
    "origin/dev cannot tell a capped run from a clean run",
    capped_old == clean_old,
    f"expected both to collapse to {SUCCEEDED}, got {capped_old} / {clean_old}",
)
check(
    "origin/dev reports a step-capped run as Succeeded",
    capped_old == SUCCEEDED,
    f"expected {SUCCEEDED}, got {capped_old}",
)

# 2. THE FIXED TREE.
check(
    "a capped run reconstructs as LimitExceeded",
    capped_new == LIMIT_EXCEEDED,
    f"expected {LIMIT_EXCEEDED}, got {capped_new}",
)
check(
    "a capped run is NOT Succeeded, NOT Failed, NOT Stopped",
    capped_new not in (SUCCEEDED, FAILED, STOPPED),
    f"{capped_new} is one of the three it must not collapse into",
)
check(
    "a clean run still reconstructs as Succeeded (the arm did not swallow it)",
    clean_new == SUCCEEDED,
    f"expected {SUCCEEDED}, got {clean_new}",
)
check(
    "the two runs are now distinguishable at all",
    capped_new != clean_new,
    "the added arm separated nothing — the guard is vacuous",
)

# 3. THE THREE THINGS MUST NOT MIX (issue #403, fourth requirement). A limit is
#    not a cancel and not a failure, and the arm ordering is what guarantees it.
check(
    "a cancelled run is Stopped, not LimitExceeded",
    decide_new(True, None, CAPPED_HISTORY[-1], "MaxSteps") == STOPPED,
    f"got {decide_new(True, None, CAPPED_HISTORY[-1], 'MaxSteps')}",
)
check(
    "an errored run is Failed, not LimitExceeded",
    decide_new(False, "boom", CAPPED_HISTORY[-1], "MaxSteps") == FAILED,
    f"got {decide_new(False, 'boom', CAPPED_HISTORY[-1], 'MaxSteps')}",
)

# 4. POSITIVE CONTROL. Delete the added arm and the separation must vanish —
#    this is what proves the assertions above are carried by the branch and not
#    by the shape of the inputs. A guard that survives its own removal proves
#    nothing, which is the #591 shape.
weakened = decide_old(cancelled=False, error_message=None,
                      last_assistant=CAPPED_HISTORY[-1], limit="MaxSteps")
check(
    "POSITIVE CONTROL: removing the arm restores the collapse",
    weakened == SUCCEEDED and weakened == clean_old,
    f"the weakened chain gave {weakened}; a control that cannot go red is not a control",
)

print()
if failures:
    print(f"{len(failures)} check(s) failed: {', '.join(failures)}")
    sys.exit(1)
print("all checks passed")
sys.exit(0)
