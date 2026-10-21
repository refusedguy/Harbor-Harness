# Tool error codes and per-tool budgets

> Status: normative for the current implementation. Every code named here is
> part of the contract: the spelling does not change, and a policy may branch
> on it. The message beside a code is free text and may be reworded at any
> time — that separation is the whole point of the feature.

A failed tool call used to be one boolean plus a line of text. Everything that
wanted to tell a timeout from a permission refusal from "no such tool" had to
parse that text, and the three places that did were each a failure point the
moment somebody reworded a message. Issue #1760 replaces the prose puzzle
with a **structured error**: a stable code next to the message.

## The shape

Two fields, side by side, on the tool result:

| Field | Type | What it is |
|---|---|---|
| `Output` | `string` | The message. The model reads it. |
| `ErrorInfo` | `ToolErrorInfo?` | The fact. A policy reads it. |

`ToolErrorInfo` carries three primitives:

- `Name` — the class-like label (`ToolTimeoutError`).
- `Code` — the stable machine code (`TOOL_TIMEOUT`).
- `Reason` — an optional detail (the deadline that fired, the exception
  message, the policy reason). Nothing branches on it.

`ErrorInfo` is a **separate typed field**, not a slot in `ToolResult.Metadata`:
`Metadata` is `object?`-typed, is excluded from MemoryPack, and is dropped by
both storage backends (#51). A code parked there would not survive a restart.
This record holds primitives only, so it serializes everywhere the result
itself does — JSONL, SQLite and MemoryPack alike.

## The invariant

**A failed tool result's text starts with its code.**

```
TOOL_TIMEOUT: Tool 'webfetch' timed out after 30s.
TOOL_PERMISSION_DENIED: Permission denied
TOOL_NOT_STARTED: tool 'read' was never started — the turn ended before it ran. The call had no effect and is safe to retry.
```

The text the model reads and the text the transcript stores are the same
string, so the model gets a code and not a trace, and a reader of a session
file later sees the same thing. `ToolResult.Failed` puts the code in front
once, so no producer has to remember to.

The structured field says the same thing in a form a program can compare:

```csharp
if (entry.ErrorInfo?.Code == ToolErrorCodes.Timeout) { /* ... */ }
```

## The vocabulary

Fourteen codes. `ToolErrorCodes.IsKnown` answers "does THIS build recognise
the code" — it is not a validity check, see "Unknown codes" below.

| Code | `Name` | What it means | Who produces it |
|---|---|---|---|
| `TOOL_ERROR` | `ToolError` | The tool reported a failure and did not classify it. The generic code. | The dispatcher, for a tool error with no code of its own |
| `TOOL_TIMEOUT` | `ToolTimeoutError` | The call's own deadline fired. | The dispatcher |
| `TOOL_ABORTED_BEFORE_DISPATCH` | `AbortError` | The call was never sent to the tool. It had no effect, so a retry is safe. | The dispatcher |
| `TOOL_NOT_STARTED` | `ToolNotStartedError` | The call never began (the turn closed first). It had no effect, so a retry is safe. | The turn runner |
| `TOOL_OUTCOME_UNKNOWN` | `ToolOutcomeUnknownError` | The call may have run and its outcome was never captured. A blind retry can apply it twice. | The transcript repair (#1751) |
| `TOOL_NOT_FOUND` | `ToolNotFoundError` | No such tool is registered, or the name is not a valid tool name. | The dispatcher |
| `TOOL_VALIDATION_FAILED` | `ToolValidationError` | The arguments failed the tool's own validation. The tool never ran. | The dispatcher |
| `TOOL_PERMISSION_DENIED` | `PermissionDeniedError` | The permission policy refused the call. A decision, not a malfunction. | The dispatcher |
| `TOOL_PERMISSION_FAILED` | `PermissionFailedError` | The permission subsystem itself failed, so nothing was decided. | The dispatcher |
| `TOOL_HOOK_DENIED` | `ToolHookDeniedError` | A user `PreToolUse` hook refused the call. | The dispatcher |
| `TOOL_CANCELLED` | `ToolCancelledError` | The run was cancelled while the call was in flight. | The dispatcher |
| `TOOL_ABANDONED` | `ToolAbandonedError` | The call outlived the accepted stop and was abandoned at the dispatch boundary. | The dispatcher |
| `TOOL_EXECUTION_FAILED` | `ToolExecutionError` | The tool threw. | The dispatcher |
| `TOOL_MALFORMED_ARGUMENTS` | `MalformedToolCallError` | The model emitted arguments that are not valid JSON. The tool never ran. | The turn runner |

`TOOL_NOT_STARTED` and `TOOL_OUTCOME_UNKNOWN` are the two codes #1751 already
owns; `ToolErrorCodes` names them rather than spelling them twice.

## Returning a coded error from a tool

A tool that knows why it failed says so, and the code travels with the result
into the transcript and into the model's context:

```csharp
public Task<ToolResult> ExecuteAsync(
    JsonElement args,
    ToolContext context,
    CancellationToken cancellationToken = default)
{
    if (!File.Exists(path))
    {
        return Task.FromResult(ToolResult.Failed(
            $"no such file: {path}",
            ToolErrorCodes.Create(ToolErrorCodes.NotFound, path)));
    }

    // ...
}
```

`ToolResult.Failed(detail, error)` takes the RAW detail and puts the code in
front of it. A tool that returns the plain `ToolResult.Error(text)` gets the
generic `TOOL_ERROR` code, and the dispatcher says that much and no more: it
does not guess WHY the tool failed.

The generic code is the honest label for a failure nobody classified. A
dispatcher that guessed "timeout" because the message said "timed out" would be
guessing — which is what the text-parsing callers did, and what the code
replaces.

## Unknown codes

A plugin may return a code this build has never heard of. That is not a
defect:

- the code is carried **verbatim** — it is not rewritten into a known one and
  not dropped;
- it reaches the model, and the text still leads with it;
- it survives storage;
- `ToolErrorCodes.IsKnown(code)` is `false`, which means "this build has no
  name and no policy for it", not "this code is invalid".

The name of an unknown code is `ToolError`; the shape of the record never
depends on the vocabulary.

## Per-tool budgets

A tool call used to have exactly ONE deadline, and it lived on the agent
(`AgentDefinition.ToolTimeoutSeconds`). Every tool in a turn shared that
number: a slow `webfetch` was cut off by the same budget a directory listing
never needed.

A tool may now declare its own budget:

```csharp
public int? TimeoutMs => null;   // null = "no opinion"
```

The precedence, decided in `ToolTimeouts.Resolve`:

1. the tool's own `TimeoutMs`, when it declares one;
2. otherwise the agent's common default (`ToolTimeoutSeconds`);
3. otherwise no deadline at all (the legacy unbounded behaviour).

A tool's own number wins because the agent's number is a default and the
tool's is a fact about the tool ("a network fetch takes seconds, a directory
listing takes milliseconds"). A default that overrode the specific value would
make the specific value pointless.

A non-positive budget is refused at registration (`ToolRegistry.Register`): a
zero budget would cancel the call before it started, and a negative one is a
typo that would otherwise read as "no deadline". The failure would otherwise
surface at RUN time as a mysterious instant cancellation, far from the
registration that caused it.

The budget covers the whole call — the permission gate included, exactly as
the agent-level deadline always has.

**No builtin tool declares a budget today.** The agent's common default
applies to all of them, which is the behaviour before #1760. A builtin should
adopt one when its own domain names a bound; a number invented here would be
a guess dressed as a fact.

## Reading an older session file

A session file written before the structured code existed carries no
`errorInfo` member at all. That is not a defect: the member is absent, and
the entry reads back with no code. An absent member is the honest reading of a
record that never carried one.

The one place where the old shape still matters is the **abandoned** mark. An
abandoned call used to be recognised by a marker substring in the message
text (`ToolCallLink.AbandonedOutputMarker`), and that is how a file written
before #1760 still reads as abandoned. A result that carries a code is
classified by the code; a result without one falls back to the marker.
`ToolCallLink.IsAbandoned` is the one place that decides.

## Where it lives

| What | Where |
|---|---|
| The record | `src/Harbor.Abstractions.Contracts/Models/ToolErrorInfo.cs` |
| The vocabulary | `src/Harbor.Abstractions.Contracts/Models/ToolErrorCodes.cs` |
| The result fields | `src/Harbor.Abstractions.Contracts/Models/Messages.cs` |
| The dispatcher's codes | `src/Harbor.AgentRuntime/Agents/ToolDispatcher.cs`, `src/Harbor.AgentRuntime/Agents/ToolRetryPolicy.cs` |
| The budget | `src/Harbor.Abstractions/Tools/ITool.cs`, `src/Harbor.Abstractions/Tools/ToolTimeouts.cs` |
| The storage round-trip | `src/Harbor.Storage.Jsonl/JsonlMessageCodec.cs`, `src/Harbor.Storage.Jsonl/JsonlLineParser.cs` |
