#!/usr/bin/env python3
"""codemod-test-events.py — rewrite hand-built test events and messages into factory calls.

WHAT IT DOES
============
Issue #1821: the test tree built its events by hand —

    await bus.PublishAsync(new MessageUpdateEvent(
        new TextDeltaEvent("id", "Answer **line**\\n"),
        AssistantMessage.Empty("s1", "m")));

— 604 constructions across 83 files, repeating the same handful of literals in
every one of them. `tests/Harbor.TestKit.Support/TestEvents.cs` is the one place
that builds them now, and this script is what moved the call sites over.

Issue #1816: the same tree built its MESSAGES by hand —

    var user = new UserMessage(
        "m1",
        session.Id,
        DateTimeOffset.UtcNow,
        "hi",
        "code",
        "test-model");

— 196 constructions across 72 files, repeating the same handful of literals
again. `tests/Harbor.TestKit.Support/TestMessages.cs` is the one place that
builds them now, and this script is what moved those call sites over too
(195 of the 196 — the one that carries a comment inside the construction is
left alone, and the report says so).

    python3 tools/codemod-test-events.py --check     # report only, writes nothing
    python3 tools/codemod-test-events.py --apply     # rewrite the tree

`--check` and `--apply` are the same code path; the only difference is whether
the result is written back. Both print the same report, so a reviewer can run
`--check` on a clean tree and see "0 sites", and on the branch and see the
numbers the PR body quotes.

THE ONE RULE THE SCRIPT ENFORCES
=================================
A rewritten call must MEAN what the hand-written one meant. Not "nearly", not
"the same shape" — the same values, in the same order, with the same defaults.
So the script never guesses:

  * Every factory parameter has a DEFAULT, and the script omits an argument
    only when the source value IS that default (compared after normalising the
    whitespace that sits outside string literals). A value the script does not
    recognise is passed through EXPLICITLY, and the call still gets shorter
    because the type name and the `new` are gone.
  * A form the script does not understand is LEFT ALONE and reported with the
    reason. The list of those forms is in the report, so "the codemod did not
    take this one" is a fact a reviewer can check, not a gap.
  * `null` is never passed to a parameter whose default substitutes a value.
    Those sites are left alone: `new MessageStartEvent(null!)` means "a null
    message", and a builder that substitutes a default would silently change
    it.

THE MESSAGES ARE WRITTEN DIFFERENTLY, AND WHY
==============================================
A message record's adjacent parameters are often the SAME type — `agent` and
`model` are both `string`, `id` and `sessionId` are both `string` — so a
positional call can swap them and still COMPILE. The script therefore writes
the payload of a message positionally (it is the argument every call site
passes and the one the reader looks for) and every other argument as a NAMED
argument, which makes the swap inexpressible. The event records do not have
that shape, so the event calls keep the positional form they had.

WHAT IT TOUCHES
===============
  * `tests/**/*.cs`, except `tests/Harbor.Architecture.Tests/` — the guard tree
    cites literal SOURCE SNIPPETS as strings, and rewriting a snippet inside a
    string would change what the rule looks for.
  * String literals are masked before the search, so a `new AgentStartEvent(`
    inside a string is never a match.
  * The csproj of a project whose files were rewritten gets a
    `<ProjectReference>` to `Harbor.TestKit.Support` if it does not have one
    already. That part references only `Harbor.Abstractions` and
    `Harbor.Abstractions.Contracts`, so the edge adds no product assembly to a
    consumer that publishes events or builds a message — it already has both.

Stdlib only: no pip, no network, no dotnet. Same shape as the other tools in
this directory.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

# ---------------------------------------------------------------------------
# The scope
# ---------------------------------------------------------------------------

SCOPE_DIR = "tests"
EXCLUDED_DIRS = (
    "tests/Harbor.Architecture.Tests",
    # The kit's own sources DEFINE the builders. Rewriting them would be
    # self-referential — the codemod would turn the factory's body into calls
    # to the factory.
    "tests/Harbor.TestKit",
    "tests/Harbor.TestKit.Support",
    "tests/Harbor.TestKit.Tui",
)

# The event types the script knows how to build. Anything else is left alone.
DOMAIN_EVENTS = [
    "AgentStartEvent",
    "AgentEndEvent",
    "AgentErrorEvent",
    "TurnStartEvent",
    "TurnEndEvent",
    "MessageStartEvent",
    "MessageEndEvent",
    "MessageUpdateEvent",
    "ToolExecutionStartEvent",
    "ToolExecutionUpdateEvent",
    "ToolExecutionEndEvent",
    "SessionStatsEvent",
    "SessionChangedEvent",
    "CompactionStartedEvent",
    "CompactionCompletedEvent",
    "CompactionFailedEvent",
    "PluginBlockedEvent",
]

LLM_EVENTS = [
    "TextStartEvent",
    "TextDeltaEvent",
    "TextEndEvent",
    "ThinkingStartEvent",
    "ThinkingDeltaEvent",
    "ThinkingEndEvent",
    "ToolCallStartEvent",
    "ToolCallDeltaEvent",
    "StepStartEvent",
    "StepFinishEvent",
    "FinishEvent",
    "ErrorEvent",
]

KNOWN_EVENTS = DOMAIN_EVENTS + LLM_EVENTS

# The MESSAGE types the script knows how to build (#1816). Anything else is
# left alone.
MESSAGE_TYPES = [
    "UserMessage",
    "AssistantMessage",
    "ToolResultMessage",
]

KNOWN_TYPES = KNOWN_EVENTS + MESSAGE_TYPES

# ---------------------------------------------------------------------------
# The defaults, and the text that stands for each of them
# ---------------------------------------------------------------------------

DEFAULT_SESSION_ID = '"s1"'
DEFAULT_TOOL_CALL_ID = '"tc1"'
DEFAULT_LLM_TOOL_CALL_ID = '"call-1"'
DEFAULT_TOOL_NAME = '"counter"'
DEFAULT_EMPTY_ASSISTANT = 'AssistantMessage.Empty("s1", "m")'
DEFAULT_METADATA = "SessionMetadata.Empty"
DEFAULT_EMPTY_ARGS = '"{}"'

# The MESSAGE defaults (#1816). Each one is the value the builder substitutes,
# so a source argument equal to it can be left out. The counts that chose
# them are in `tests/Harbor.TestKit.Support/TestMessages.cs`.
DEFAULT_MESSAGE_ID = 'Guid.NewGuid().ToString("N")'
DEFAULT_MESSAGE_SESSION = '"session-1"'
DEFAULT_MESSAGE_CREATED_AT = "DateTimeOffset.UtcNow"
DEFAULT_MESSAGE_AGENT = '"code"'
DEFAULT_MESSAGE_MODEL = '"test-model"'
DEFAULT_MESSAGE_STOP_REASON = "StopReason.Stop"
DEFAULT_MESSAGE_USAGE = "new Usage(0, 0)"
DEFAULT_MESSAGE_PARENT = "null"
DEFAULT_MESSAGE_ATTACHMENTS = "null"
DEFAULT_MESSAGE_IS_SUMMARY = "false"
DEFAULT_MESSAGE_SUMMARY_KEPT = "null"

EMPTY_LIST = "[]"


def is_empty_list(text: str) -> bool:
    """Whether the argument is an empty list of some element type."""
    stripped = text.strip()
    if stripped == EMPTY_LIST:
        return True
    return stripped.startswith("Array.Empty<") and stripped.endswith(">()")


def is_null(text: str) -> bool:
    stripped = text.strip()
    return stripped in ("null", "null!")


# ---------------------------------------------------------------------------
# Lexing helpers
# ---------------------------------------------------------------------------


def _string_end(text: str, start: int) -> int:
    """The index just past the string literal that starts at `start`."""
    n = len(text)
    if text.startswith('"""', start):
        end = text.find('"""', start + 3)
        return n if end == -1 else end + 3
    verbatim = start > 0 and text[start - 1] == "@"
    i = start + 1
    while i < n:
        if not verbatim and text[i] == "\\":
            i += 2
            continue
        if text[i] == '"':
            if verbatim and i + 1 < n and text[i + 1] == '"':
                i += 2
                continue
            return i + 1
        i += 1
    return n


def _char_end(text: str, start: int) -> int:
    """The index just past the character literal that starts at `start`."""
    n = len(text)
    i = start + 1
    while i < n:
        if text[i] == "\\":
            i += 2
            continue
        if text[i] == "'":
            return i + 1
        i += 1
    return n


def mask_code(text: str) -> str:
    """Blank out string literals AND comments, preserving offsets.

    A `new AgentStartEvent(` inside a string literal is TEXT, not code — the
    architecture suite keeps source snippets in strings on purpose, and this
    file's own header quotes a call site. A `new AgentStartEvent(` inside a
    comment is a sentence, not code. Masking both keeps the search from ever
    seeing one, and keeping the length keeps every offset in the masked text
    valid in the original.

    The scan checks for a COMMENT before it checks for a quote, because a
    quote inside a comment is not the start of a string: an apostrophe in
    "don't" would otherwise open a character literal and blank out half the
    file.
    """
    out = list(text)

    def blank(start: int, end: int) -> None:
        for k in range(start, end):
            if out[k] != "\n":
                out[k] = " "

    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            end = text.find("\n", i)
            end = n if end == -1 else end
            blank(i, end)
            i = end
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            end = n if end == -1 else end + 2
            blank(i, end)
            i = end
            continue
        if c == '"':
            end = _string_end(text, i)
            blank(i + 1, max(i + 1, end - 1))
            i = end
            continue
        if c == "'":
            end = _char_end(text, i)
            blank(i + 1, max(i + 1, end - 1))
            i = end
            continue
        i += 1
    return "".join(out)


def has_comment(text: str) -> bool:
    """Whether the text carries a comment outside its string literals.

    The check cannot be done on the masked text: masking blanks the comment
    out, so the marker disappears with it. And it cannot be a plain substring
    search either: `/* body */` inside a string literal is DATA, not a
    comment. So the text is walked the way the lexer walks it.
    """
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] in "/*":
            return True
        if c == '"':
            i = _string_end(text, i)
            continue
        if c == "'":
            i = _char_end(text, i)
            continue
        i += 1
    return False


def collapse_whitespace(text: str) -> str | None:
    """Fold an argument that spans several lines onto one line.

    Returns `None` when the fold is not safe — an argument that carries a
    comment, where a newline ends the comment and folding it would comment
    out the rest of the argument.
    """
    if "\n" not in text:
        return text
    if has_comment(text):
        return None

    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            end = _string_end(text, i)
            out.append(text[i:end])
            i = end
            continue
        if c.isspace():
            j = i
            while j < n and text[j].isspace():
                j += 1
            following = text[j : j + 1]
            if out and out[-1] not in "([{" and following not in ")]}":
                out.append(" ")
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


def normalize_code(text: str) -> str:
    """Whitespace outside string literals removed.

    Two spellings of the same expression differ only in spacing, and C# does
    not read spacing. Inside a string literal the spacing is DATA, so it is
    kept — which is what makes `AssistantMessage.Empty("s1", "m")` and
    `AssistantMessage.Empty("s1","m")` compare equal, and `"a b"` and `"ab"`
    compare different.
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            end = _string_end(text, i)
            out.append(text[i:end])
            i = end
            continue
        if c.isspace():
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def same_value(left: str, right: str) -> bool:
    """Whether two argument texts stand for the same value."""
    return normalize_code(left) == normalize_code(right)


@dataclass
class Argument:
    """One argument of a constructor call: positional, or `Name: value`."""

    name: str | None
    value: str
    named: bool = False


def split_arguments(text: str) -> list[Argument]:
    """Split an argument list on its top-level commas."""
    args: list[Argument] = []
    depth = 0
    current: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            end = _string_end(text, i)
            current.append(text[i:end])
            i = end
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            args.append(Argument(None, "".join(current).strip()))
            current = []
            i += 1
            continue
        current.append(c)
        i += 1

    tail = "".join(current).strip()
    if tail:
        args.append(Argument(None, tail))

    for arg in args:
        match = re.match(r"^([A-Za-z_]\w*)\s*:\s*(.*)$", arg.value, re.DOTALL)
        if match:
            arg.name = match.group(1)
            arg.value = match.group(2).strip()
            arg.named = True
    return args


def _matching_paren(text: str, open_paren: int) -> int:
    """The index of the ')' that closes the '(' at `open_paren`, or -1."""
    depth = 1
    i = open_paren + 1
    n = len(text)
    while i < n and depth > 0:
        c = text[i]
        if c == '"':
            i = _string_end(text, i)
            continue
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def find_calls(text: str) -> list[tuple[str, int, int, list[Argument]]]:
    """Every `new <Event>(...)` and `new <Message>(...)` in the text, with its arguments."""
    masked = mask_code(text)
    calls = []
    pattern = re.compile(r"\bnew\s+(" + "|".join(KNOWN_TYPES) + r")\s*\(")
    for match in pattern.finditer(masked):
        typename = match.group(1)
        open_paren = match.end() - 1
        close = _matching_paren(masked, open_paren)
        if close == -1:
            continue
        args = split_arguments(text[open_paren + 1 : close])
        calls.append((typename, match.start(), close + 1, args))
    return calls


def parse_new_expression(text: str) -> tuple[str, list[Argument]] | None:
    """Parse `new <Type>(...)` as an argument value."""
    stripped = text.strip()
    match = re.match(r"^new\s+([A-Za-z_]\w*)\s*\(", stripped)
    if not match:
        return None
    open_paren = match.end() - 1
    close = _matching_paren(stripped, open_paren)
    if close == -1:
        return None
    return match.group(1), split_arguments(stripped[open_paren + 1 : close])


# ---------------------------------------------------------------------------
# The rules
# ---------------------------------------------------------------------------


@dataclass
class Param:
    """One factory parameter: its name, the value to pass, and the default."""

    name: str
    value: str
    default: str
    empty_list_is_default: bool = False
    substitutes_null: bool = False


@dataclass
class Plan:
    """What to write in place of a construction."""

    method: str
    params: list[Param]


def build_params(
    args: list[Argument], spec: list[tuple[str, str, bool, bool]]
) -> list[Param] | None:
    """Map a constructor's arguments onto a builder's parameters.

    `spec` is (name, default text, "an empty list counts as the default",
    "the builder substitutes a non-null value for a null").

    The mapping follows C#'s own rules for a call that mixes positional and
    named arguments:

      * a positional argument takes the next parameter that no earlier
        argument filled;
      * a named argument takes the parameter of that name, and a named
        argument that comes after a positional one must name a parameter at
        or after the position the positional arguments reached — which is
        what lets `new AgentEndEvent(messages, Limit: X)` skip the optional
        `Cancelled` in between;
      * a positional argument that comes after a named one is only legal when
        the named argument sat in its own position, and it then takes the
        parameter right after it.
    """
    params = [
        Param(name, default, default, empty_ok, substitutes)
        for name, default, empty_ok, substitutes in spec
    ]
    all_named = all(arg.named for arg in args)
    next_positional = 0

    for arg in args:
        if arg.named:
            index = next(
                (i for i, param in enumerate(params) if param.name.lower() == arg.name.lower()),
                None,
            )
            if index is None:
                return None
            if not all_named and index < next_positional:
                return None
            params[index].value = arg.value
            next_positional = max(next_positional, index + 1)
        else:
            if next_positional >= len(params):
                return None
            params[next_positional].value = arg.value
            next_positional += 1

    return params


# (method, spec) per event type. The spec's default text is what the BUILDER
# substitutes, so a source argument equal to it can be left out.
SPECS: dict[str, tuple[str, list[tuple[str, str, bool, bool]]]] = {
    "AgentStartEvent": (
        "AgentStart",
        [
            ("sessionId", DEFAULT_SESSION_ID, False, False),
            ("messages", EMPTY_LIST, True, True),
            ("model", "null", False, False),
            ("kind", "SessionKind.User", False, False),
        ],
    ),
    "AgentEndEvent": (
        "AgentEnd",
        [
            ("messages", EMPTY_LIST, True, True),
            ("cancelled", "false", False, False),
            ("limit", "null", False, False),
        ],
    ),
    "AgentErrorEvent": (
        "AgentError",
        [("message", '"boom"', False, False), ("exception", "null", False, False)],
    ),
    "TurnStartEvent": (
        "TurnStart",
        [("turnIndex", "1", False, False), ("sessionId", "null", False, False)],
    ),
    "TurnEndEvent": (
        "TurnEnd",
        [
            ("message", DEFAULT_EMPTY_ASSISTANT, False, True),
            ("toolResults", EMPTY_LIST, True, True),
            ("sessionId", "null", False, False),
        ],
    ),
    "MessageStartEvent": ("MessageStart", [("message", DEFAULT_EMPTY_ASSISTANT, False, True)]),
    "MessageEndEvent": ("MessageEnd", [("message", DEFAULT_EMPTY_ASSISTANT, False, True)]),
    "ToolExecutionStartEvent": (
        "ToolExecutionStart",
        [
            ("toolCallId", DEFAULT_TOOL_CALL_ID, False, False),
            ("toolName", '"read"', False, False),
            ("argsJson", DEFAULT_EMPTY_ARGS, False, True),
            ("glyph", "null", False, False),
        ],
    ),
    "ToolExecutionUpdateEvent": (
        "ToolExecutionUpdate",
        [
            ("toolCallId", DEFAULT_TOOL_CALL_ID, False, False),
            ("partialResult", '""', False, True),
            ("retryAttempt", "null", False, False),
            ("retryMaxAttempts", "null", False, False),
            ("retryBackoffSeconds", "null", False, False),
        ],
    ),
    "ToolExecutionEndEvent": (
        "ToolExecutionEnd",
        [
            ("toolCallId", DEFAULT_TOOL_CALL_ID, False, False),
            ("result", "null", False, True),
            ("isError", "false", False, False),
        ],
    ),
    "SessionStatsEvent": (
        "SessionStats",
        [("sessionId", DEFAULT_SESSION_ID, False, False), ("metadata", DEFAULT_METADATA, False, True)],
    ),
    "SessionChangedEvent": ("SessionChanged", [("sessionId", DEFAULT_SESSION_ID, False, False)]),
    "CompactionStartedEvent": ("CompactionStarted", [("sessionId", DEFAULT_SESSION_ID, False, False)]),
    "CompactionCompletedEvent": (
        "CompactionCompleted",
        [
            ("sessionId", DEFAULT_SESSION_ID, False, False),
            ("summary", '"summary"', False, False),
            ("prunedMessageCount", "0", False, False),
            ("tokensSaved", "0", False, False),
            ("duration", "TimeSpan.FromSeconds(1)", False, False),
        ],
    ),
    "CompactionFailedEvent": (
        "CompactionFailed",
        [("sessionId", DEFAULT_SESSION_ID, False, False), ("error", '"boom"', False, False)],
    ),
    "PluginBlockedEvent": (
        "PluginBlocked",
        [
            ("pluginName", '"plugin"', False, False),
            ("reason", '"timeout"', False, False),
            ("detail", '"detail"', False, False),
        ],
    ),
    "TextStartEvent": ("TextStart", [("id", '"t"', False, False)]),
    "TextDeltaEvent": ("TextDelta", [("id", '"t"', False, False), ("delta", '""', False, False)]),
    "TextEndEvent": ("TextEnd", [("id", '"t"', False, False), ("finalText", '""', False, False)]),
    "ThinkingStartEvent": ("ThinkingStart", [("id", '"h1"', False, False)]),
    "ThinkingDeltaEvent": (
        "ThinkingDelta",
        [("id", '"h1"', False, False), ("delta", '""', False, False)],
    ),
    "ThinkingEndEvent": (
        "ThinkingEnd",
        [("id", '"h1"', False, False), ("finalText", '""', False, False)],
    ),
    "ToolCallStartEvent": (
        "ToolCallStart",
        [("id", DEFAULT_LLM_TOOL_CALL_ID, False, False), ("toolName", DEFAULT_TOOL_NAME, False, False)],
    ),
    "ToolCallDeltaEvent": (
        "ToolCallDelta",
        [("id", DEFAULT_LLM_TOOL_CALL_ID, False, False), ("argsDelta", DEFAULT_EMPTY_ARGS, False, False)],
    ),
    "StepStartEvent": ("StepStart", [("index", "0", False, False)]),
    "StepFinishEvent": (
        "StepFinish",
        [
            ("index", "0", False, False),
            ("finishReason", '"stop"', False, False),
            ("usage", "null", False, False),
            ("providerModelId", "null", False, False),
        ],
    ),
    "FinishEvent": ("LlmFinish", []),
    "ErrorEvent": (
        "LlmError",
        [
            ("message", '"boom"', False, False),
            ("exception", "null", False, False),
            ("kind", "ProviderErrorKind.Unknown", False, False),
            ("statusCode", "null", False, False),
        ],
    ),
}

# The provider-level events that also have a "wrapped in an update" builder.
UPDATE_FACTORIES = {
    "TextDeltaEvent": "TextDeltaUpdate",
    "ThinkingDeltaEvent": "ThinkingDeltaUpdate",
    "ToolCallStartEvent": "ToolCallStartUpdate",
    "ToolCallDeltaEvent": "ToolCallDeltaUpdate",
    "StepFinishEvent": "StepFinishUpdate",
}

# The `StepFinish` builder inside an update has no `providerModelId` — a step
# finish that names a provider model is left alone rather than half-migrated.
UPDATE_DROPPED_PARAMS = {"StepFinishEvent": ("providerModelId",)}

# The arguments the codemod has to RECONSTRUCT rather than copy.
JSON_ARGS_PATTERN = re.compile(
    r"^(?:System\.Text\.Json\.)?JsonDocument\.Parse\((.*)\)\.RootElement\.Clone\(\)$", re.DOTALL
)

# ---------------------------------------------------------------------------
# The messages (#1816)
# ---------------------------------------------------------------------------

# The parameters of the MESSAGE records, in the records' own order. The
# codemod needs them to read a construction that names its arguments
# (`new UserMessage(Id: "m1", ...)`) and to know which parameter each
# positional argument fills.
RECORD_PARAMS: dict[str, list[str]] = {
    "UserMessage": [
        "Id",
        "SessionId",
        "CreatedAt",
        "Content",
        "Agent",
        "Model",
        "ParentId",
        "Attachments",
    ],
    "AssistantMessage": [
        "Id",
        "SessionId",
        "CreatedAt",
        "Parts",
        "StopReason",
        "Usage",
        "Model",
        "ParentId",
        "IsSummary",
        "SummaryFirstKeptId",
    ],
    "ToolResultMessage": [
        "Id",
        "SessionId",
        "CreatedAt",
        "Results",
        "ParentId",
    ],
}

# The record parameters the record itself gives a default. A source that did
# not name one of them means the record's default, which is what the builder
# substitutes too.
RECORD_DEFAULTS: dict[str, str] = {
    "ParentId": "null",
    "Attachments": "null",
    "IsSummary": "false",
    "SummaryFirstKeptId": "null",
}


@dataclass
class MessageParam:
    """One parameter of a message builder: its name, its value and its default."""

    name: str
    value: str
    default: str
    substitutes_null: bool = False


@dataclass
class MessagePlan:
    """What to write in place of a message construction."""

    method: str
    payload: str
    params: list[MessageParam]


# (method, the record parameter that carries the payload, the parameters) per
# message type. Each parameter is (factory name, record name, default text,
# "the builder substitutes a value for a null").
MESSAGE_SPECS: dict[str, tuple[str, str, list[tuple[str, str, str, bool]]]] = {
    "UserMessage": (
        "User",
        "Content",
        [
            ("id", "Id", DEFAULT_MESSAGE_ID, True),
            ("sessionId", "SessionId", DEFAULT_MESSAGE_SESSION, True),
            ("createdAt", "CreatedAt", DEFAULT_MESSAGE_CREATED_AT, True),
            ("agent", "Agent", DEFAULT_MESSAGE_AGENT, True),
            ("model", "Model", DEFAULT_MESSAGE_MODEL, True),
            ("parentId", "ParentId", DEFAULT_MESSAGE_PARENT, False),
            ("attachments", "Attachments", DEFAULT_MESSAGE_ATTACHMENTS, False),
        ],
    ),
    "AssistantMessage": (
        "Assistant",
        "Parts",
        [
            ("id", "Id", DEFAULT_MESSAGE_ID, True),
            ("sessionId", "SessionId", DEFAULT_MESSAGE_SESSION, True),
            ("createdAt", "CreatedAt", DEFAULT_MESSAGE_CREATED_AT, True),
            ("stopReason", "StopReason", DEFAULT_MESSAGE_STOP_REASON, True),
            ("usage", "Usage", DEFAULT_MESSAGE_USAGE, True),
            ("model", "Model", DEFAULT_MESSAGE_MODEL, True),
            ("parentId", "ParentId", DEFAULT_MESSAGE_PARENT, False),
            ("isSummary", "IsSummary", DEFAULT_MESSAGE_IS_SUMMARY, True),
            ("summaryFirstKeptId", "SummaryFirstKeptId", DEFAULT_MESSAGE_SUMMARY_KEPT, False),
        ],
    ),
    "ToolResultMessage": (
        "ToolResult",
        "Results",
        [
            ("id", "Id", DEFAULT_MESSAGE_ID, True),
            ("sessionId", "SessionId", DEFAULT_MESSAGE_SESSION, True),
            ("createdAt", "CreatedAt", DEFAULT_MESSAGE_CREATED_AT, True),
            ("parentId", "ParentId", DEFAULT_MESSAGE_PARENT, False),
        ],
    ),
}

# The assistant's payload is a list of content parts. When the list is exactly
# one text part, the builder's TEXT form says the same thing in fewer
# characters, and the codemod uses it.
TEXT_PART_PATTERN = re.compile(r"^new\s+TextPart\s*\((.*)\)$", re.DOTALL)
ARRAY_INIT_PATTERN = re.compile(r"^new\s*(?:\w+\s*)?\[\s*\]\s*\{(.*)\}$", re.DOTALL)


def single_text_part(parts: str) -> str | None:
    """The text of a parts expression that is exactly one text part.

    Returns `None` when the expression is not one text part — then the
    caller keeps the parts list as it is.
    """
    text = parts.strip()
    if text.startswith("[") and text.endswith("]"):
        inner = text[1:-1]
    else:
        match = ARRAY_INIT_PATTERN.match(text)
        if match is None:
            return None
        inner = match.group(1)

    elements = [arg for arg in split_arguments(inner) if arg.value.strip()]
    if len(elements) != 1:
        return None
    match = TEXT_PART_PATTERN.match(elements[0].value.strip())
    if match is None:
        return None
    literal = match.group(1).strip()
    if not (literal.startswith('"') or literal.startswith('$"')):
        return None
    return literal


def resolve_message_args(typename: str, args: list[Argument]) -> dict[str, str] | None:
    """The values a construction passes, keyed by the record's parameter name.

    Returns `None` when the arguments do not fit the record — a name the
    record does not have, or one argument too many.
    """
    names = RECORD_PARAMS[typename]
    values: dict[str, str] = {}
    next_positional = 0
    for arg in args:
        if arg.named:
            index = next(
                (i for i, name in enumerate(names) if name.lower() == arg.name.lower()),
                None,
            )
            if index is None:
                return None
            values[names[index]] = arg.value
            next_positional = max(next_positional, index + 1)
        else:
            if next_positional >= len(names):
                return None
            values[names[next_positional]] = arg.value
            next_positional += 1
    return values


def plan_for_message(typename: str, args: list[Argument]) -> tuple[MessagePlan | None, str]:
    """The plan for one message construction, and the reason there is none."""
    spec = MESSAGE_SPECS.get(typename)
    if spec is None:
        return None, f"{typename}: no builder for this type"
    method, payload_name, param_spec = spec

    # A comment inside a construction is left alone. The comma-split puts a
    # trailing comment at the HEAD of the next argument, and the message
    # factory moves the payload to the front — so the comment would travel
    # to a place where it reads as if it documented something else. Leaving
    # the construction alone keeps the comment where its author put it.
    for argument in args:
        if has_comment(argument.value):
            return None, f"{typename}: a comment inside the construction"

    values = resolve_message_args(typename, args)
    if values is None:
        return None, f"{typename}: the argument shape is not one the builder can reproduce"

    payload = values.get(payload_name)
    if payload is None:
        return None, f"{typename}: the payload argument is missing"

    if typename == "AssistantMessage":
        text = single_text_part(payload)
        if text is not None:
            method = "Assistant"
            payload = text
        else:
            method = "AssistantParts"

    params: list[MessageParam] = []
    for factory_name, record_name, default, substitutes in param_spec:
        value = values.get(record_name)
        if value is None:
            # The source did not name the parameter, so the RECORD's default
            # applies. It is the same default the builder substitutes, so the
            # argument is left out.
            if record_name not in RECORD_DEFAULTS:
                return None, f"{typename}: the {record_name} argument is missing"
            continue
        if is_null(value) and substitutes:
            return None, f"{typename}: the {record_name} argument is null and the builder substitutes a value for it"
        params.append(MessageParam(factory_name, value, default, substitutes))

    return MessagePlan(method, payload, params), ""


def plan_for(typename: str, args: list[Argument]) -> tuple[Plan | None, str]:
    """The plan for one construction, and the reason there is none."""
    entry = SPECS.get(typename)
    if entry is None:
        return None, f"{typename}: no builder for this type"
    method, spec = entry

    if typename == "ToolExecutionStartEvent":
        # The third argument is a JsonElement the caller already owns. The
        # builder takes the JSON as a string and clones it inside, so the
        # argument has to be a literal the script can read.
        if len(args) < 3:
            return None, f"{typename}: no arguments element"
        match = JSON_ARGS_PATTERN.match(args[2].value.strip())
        if not match:
            return None, f"{typename}: the arguments element is not a readable JSON literal"
        literal = match.group(1).strip()
        if not literal.startswith('"'):
            return None, f"{typename}: the arguments element is not a JSON string literal"
        args = list(args)
        args[2] = Argument(args[2].name, literal, args[2].named)

    params = build_params(args, spec)
    if params is None:
        return None, f"{typename}: the argument shape is not one the builder can reproduce"
    if not nulls_are_representable(params):
        return None, f"{typename}: an argument is null and the builder substitutes a default for it"
    return Plan(method, params), ""


def nulls_are_representable(params: list[Param]) -> bool:
    """Whether an explicit `null` survives the builder's default.

    A builder that substitutes a value for a `null` argument would CHANGE the
    event: `new MessageStartEvent(null!)` means "a null message", and
    `TestEvents.MessageStart(null!)` would mean "the default message". The
    parameters whose builder substitutes a value for a null are marked in the
    spec, and a site that passes one of them a null is left alone.
    """
    for param in params:
        if is_null(param.value) and param.substitutes_null:
            return False
    return True


def plan_for_update(typename: str, args: list[Argument]) -> tuple[Plan | None, str]:
    """The plan for a `MessageUpdateEvent` wrapping a provider-level event."""
    if len(args) != 2:
        return None, "MessageUpdateEvent: not exactly two arguments"
    inner = parse_new_expression(args[0].value)
    if inner is None:
        return None, "MessageUpdateEvent: the first argument is not a construction"
    inner_type, inner_args = inner
    entry = SPECS.get(inner_type)
    if entry is None or inner_type not in UPDATE_FACTORIES:
        return None, f"MessageUpdateEvent: {inner_type} has no wrapped builder"
    _, spec = entry
    if is_null(args[1].value):
        return None, "MessageUpdateEvent: the partial message is null"

    params = build_params(inner_args, spec)
    if params is None:
        return None, f"MessageUpdateEvent: the {inner_type} arguments are not reproducible"
    if not nulls_are_representable(params):
        return None, f"MessageUpdateEvent: an argument of the {inner_type} is null"
    for dropped in UPDATE_DROPPED_PARAMS.get(inner_type, ()):
        for param in params:
            if param.name == dropped and not is_default(param):
                return None, f"MessageUpdateEvent: {inner_type} names a {dropped}"
        params = [param for param in params if param.name != dropped]

    params.append(Param("partial", args[1].value, DEFAULT_EMPTY_ASSISTANT, False, True))
    return Plan(UPDATE_FACTORIES[inner_type], params), ""


def plan_for_message_update(args: list[Argument]) -> tuple[Plan | None, str]:
    """The fallback for a `MessageUpdateEvent` whose payload is not a construction."""
    if len(args) != 2:
        return None, "MessageUpdateEvent: not exactly two arguments"
    if is_null(args[1].value):
        return None, "MessageUpdateEvent: the partial message is null"

    payload = args[0].value.strip()
    inner = parse_new_expression(payload)
    if inner is not None:
        inner_type, inner_args = inner
        entry = SPECS.get(inner_type)
        if entry is None:
            return None, f"MessageUpdateEvent: {inner_type} has no builder"
        method, spec = entry
        inner_params = build_params(inner_args, spec)
        if inner_params is None:
            return None, f"MessageUpdateEvent: the {inner_type} arguments are not reproducible"
        if not nulls_are_representable(inner_params):
            return None, f"MessageUpdateEvent: an argument of the {inner_type} is null"
        payload = emit(method, inner_params)

    params = [
        Param("llmEvent", payload, ""),
        Param("partial", args[1].value, DEFAULT_EMPTY_ASSISTANT, False, True),
    ]
    return Plan("MessageUpdate", params), ""


# ---------------------------------------------------------------------------
# Emitting the call
# ---------------------------------------------------------------------------


def is_default(param: Param) -> bool:
    """Whether the argument can be left out because it equals the default."""
    if param.empty_list_is_default and is_empty_list(param.value):
        return True
    if not param.default:
        return False
    return same_value(param.value, param.default)


def emit(method: str, params: list[Param]) -> str:
    """Write the call, omitting every argument that equals its default."""
    kept = [(index, param) for index, param in enumerate(params) if not is_default(param)]
    rendered: list[str] = []
    next_positional = 0
    for index, param in kept:
        value = param.value
        if "\n" in value:
            folded = collapse_whitespace(value)
            if folded is not None:
                value = folded
        if index == next_positional:
            rendered.append(value)
            next_positional += 1
        else:
            rendered.append(f"{param.name}: {value}")
    return f"TestEvents.{method}({', '.join(rendered)})"


def fold(value: str) -> str:
    """The argument on one line, when folding it does not lose a comment."""
    if "\n" not in value:
        return value
    folded = collapse_whitespace(value)
    return value if folded is None else folded


def emit_message(plan: MessagePlan) -> str:
    """Write the message call.

    The payload is positional — it is the argument every call site passes and
    the one the reader looks for. Every other argument is NAMED, because a
    message record's adjacent parameters are often the same type: `agent` and
    `model` are both `string`, `id` and `sessionId` are both `string`, and a
    positional call can swap them and still compile.
    """
    rendered = [fold(plan.payload)]
    for param in plan.params:
        if param.default and same_value(param.value, param.default):
            continue
        rendered.append(f"{param.name}: {fold(param.value)}")
    return f"TestMessages.{plan.method}({', '.join(rendered)})"


# ---------------------------------------------------------------------------
# The per-file rewrite
# ---------------------------------------------------------------------------


@dataclass
class FileResult:
    path: str
    sites: int = 0
    lines_before: int = 0
    lines_after: int = 0
    chars_before: int = 0
    chars_after: int = 0


def rewrite_text(text: str) -> tuple[str, int, list[tuple[int, str, str]], list[str]]:
    """Rewrite every construction in one file.

    Returns the new text, the number of sites rewritten, the sites that
    were left alone — each with the offset and the reason — and the type of
    every site that was rewritten.
    """
    calls = find_calls(text)
    skipped: list[tuple[int, str, str]] = []
    replacements: list[tuple[int, int, str]] = []
    rewritten: list[str] = []
    consumed_until = -1

    for typename, start, end, args in calls:
        if start < consumed_until:
            continue  # inside a construction that was already rewritten
        if typename in MESSAGE_TYPES:
            message_plan, reason = plan_for_message(typename, args)
            if message_plan is None:
                skipped.append((start, typename, reason))
                continue
            call = emit_message(message_plan)
            if call == text[start:end]:
                continue
            replacements.append((start, end, call))
            rewritten.append(typename)
            consumed_until = end
            continue
        plan: Plan | None
        if typename == "MessageUpdateEvent":
            plan, reason = plan_for_update(typename, args)
            if plan is None:
                generic, generic_reason = plan_for_message_update(args)
                if generic is not None:
                    plan, reason = generic, ""
                else:
                    reason = f"{reason}; and {generic_reason}"
        else:
            plan, reason = plan_for(typename, args)
        if plan is None:
            skipped.append((start, typename, reason))
            continue
        call = emit(plan.method, plan.params)
        if call == text[start:end]:
            continue
        replacements.append((start, end, call))
        rewritten.append(typename)
        consumed_until = end

    if not replacements:
        return text, 0, skipped, rewritten

    out = text
    for start, end, call in reversed(replacements):
        out = out[:start] + call + out[end:]
    return out, len(replacements), skipped, rewritten


# ---------------------------------------------------------------------------
# The `using` directive
# ---------------------------------------------------------------------------

NAMESPACE_PATTERN = re.compile(r"^namespace\s+([\w.]+)\s*;", re.MULTILINE)
USING_PATTERN = re.compile(
    r"^([ \t]*)using\s+(?:static\s+)?([\w.]+)\s*(?:=\s*[\w.]+)?\s*;", re.MULTILINE
)


def needs_using(text: str) -> bool:
    """Whether the file has to import `Harbor.TestKit` to see the builders."""
    if re.search(r"^[ \t]*using\s+Harbor\.TestKit\s*;", text, re.MULTILINE):
        return False
    match = NAMESPACE_PATTERN.search(text)
    if match:
        namespace = match.group(1)
        if namespace == "Harbor.TestKit" or namespace.startswith("Harbor.TestKit."):
            return False
    return True


def insert_using(text: str) -> str:
    """Add `using Harbor.TestKit;` where the file keeps its other imports."""
    namespace = NAMESPACE_PATTERN.search(text)
    boundary = namespace.start() if namespace else len(text)
    usings = list(USING_PATTERN.finditer(text))
    inside = [m for m in usings if m.start() > boundary]
    outside = [m for m in usings if m.start() < boundary]

    # An alias (`using Foo = Bar.Baz;`) is a using DIRECTIVE, but a namespace
    # import reads better before the aliases than after them.
    def plain(entries):
        return [m for m in entries if "=" not in m.group(0)]

    def first_alias(entries):
        for m in entries:
            if "=" in m.group(0):
                return m
        return None

    for scope in (inside, outside):
        plain_scope = plain(scope)
        if plain_scope:
            last = plain_scope[-1]
            indent = last.group(1)
            return text[: last.end()] + f"\n{indent}using Harbor.TestKit;" + text[last.end() :]
        alias = first_alias(scope)
        if alias is not None:
            indent = alias.group(1)
            return text[: alias.start()] + f"{indent}using Harbor.TestKit;\n" + text[alias.start() :]

    if namespace:
        return text[: namespace.start()] + "using Harbor.TestKit;\n\n" + text[namespace.start() :]

    lines = text.split("\n")
    index = 0
    while index < len(lines):
        stripped = lines[index].strip()
        if stripped and not stripped.startswith("//"):
            break
        index += 1
    head = "\n".join(lines[:index])
    tail = "\n".join(lines[index:])
    return f"{head}\nusing Harbor.TestKit;\n{tail}"


# ---------------------------------------------------------------------------
# The csproj edge
# ---------------------------------------------------------------------------

SUPPORT_REFERENCE_COMMENT = (
    "<!-- #1821: the shared event builders (TestEvents). The edge is\n"
    "         closure-neutral — that part references only Harbor.Abstractions\n"
    "         and Harbor.Abstractions.Contracts, and this project already has\n"
    "         both because it already publishes events. -->"
)


def project_has_testkit_reference(csprj: str) -> bool:
    return "Harbor.TestKit.Support.csproj" in csprj or "Harbor.TestKit.csproj" in csprj


def add_support_reference(csprj: str) -> str:
    """Add the support reference to the ItemGroup that holds the references."""
    groups = list(re.finditer(r"<ItemGroup>(.*?)</ItemGroup>", csprj, re.DOTALL))
    chosen = None
    for group in groups:
        if "ProjectReference" in group.group(1):
            chosen = group
            break
    if chosen is None:
        chosen = groups[0] if groups else None
    if chosen is None:
        return csprj

    # Match the separator the file already spells its references with.
    separator = "\\" if 'Include="..\\' in csprj else "/"
    reference = (
        f'<ProjectReference Include="..{separator}Harbor.TestKit.Support'
        f'{separator}Harbor.TestKit.Support.csproj"/>'
    )

    body = chosen.group(1)
    if "ProjectReference" in body:
        new_body = body.replace(
            "<ProjectReference",
            SUPPORT_REFERENCE_COMMENT + "\n    " + reference + "\n    <ProjectReference",
            1,
        )
    else:
        new_body = (
            "\n    " + SUPPORT_REFERENCE_COMMENT + "\n    " + reference + "\n  " + body
        )
    return csprj[: chosen.start(1)] + new_body + csprj[chosen.end(1) :]
    return csprj[: chosen.start(1)] + new_body + csprj[chosen.end(1) :]


# ---------------------------------------------------------------------------
# The driver
# ---------------------------------------------------------------------------


def iter_source_files(root: Path) -> list[Path]:
    files = []
    for path in sorted((root / SCOPE_DIR).rglob("*.cs")):
        relative = path.relative_to(root).as_posix()
        if "/obj/" in relative or "/bin/" in relative:
            continue
        if any(relative.startswith(excluded + "/") for excluded in EXCLUDED_DIRS):
            continue
        files.append(path)
    return files


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Rewrite hand-built test events and messages into TestEvents and "
            "TestMessages calls (#1821, #1816)."
        )
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="report only; write nothing")
    mode.add_argument("--apply", action="store_true", help="rewrite the tree")
    parser.add_argument("--root", default=None, help="the repository root (default: inferred)")
    parser.add_argument("--report", default=None, help="write the report to this file as JSON")
    args = parser.parse_args()

    if not args.check and not args.apply:
        parser.error("one of --check or --apply is required")

    root = Path(args.root) if args.root else Path(__file__).resolve().parents[1]
    root = root.resolve()

    results: list[FileResult] = []
    skipped_by_reason: Counter = Counter()
    skipped_sites: list[dict] = []
    projects_needing_reference: set[str] = set()
    total_sites = 0
    event_sites = 0
    message_sites = 0
    lines_before = lines_after = 0
    chars_before = chars_after = 0

    for path in iter_source_files(root):
        text = path.read_text(encoding="utf-8")
        new_text, sites, skipped, rewritten = rewrite_text(text)
        relative = path.relative_to(root).as_posix()
        for offset, typename, reason in skipped:
            family = "messages" if typename in MESSAGE_TYPES else "events"
            labelled = f"{family}: {reason}"
            skipped_by_reason[labelled] += 1
            skipped_sites.append(
                {
                    "file": relative,
                    "line": text.count("\n", 0, offset) + 1,
                    "type": typename,
                    "family": family,
                    "reason": reason,
                }
            )
        if sites == 0:
            continue
        if needs_using(text):
            new_text = insert_using(new_text)
        results.append(
            FileResult(
                path=relative,
                sites=sites,
                lines_before=text.count("\n") + 1,
                lines_after=new_text.count("\n") + 1,
                chars_before=len(text),
                chars_after=len(new_text),
            )
        )
        total_sites += sites
        for typename in rewritten:
            if typename in MESSAGE_TYPES:
                message_sites += 1
            else:
                event_sites += 1
        lines_before += text.count("\n") + 1
        lines_after += new_text.count("\n") + 1
        chars_before += len(text)
        chars_after += len(new_text)
        projects_needing_reference.add(path.parent.relative_to(root).as_posix())
        if args.apply:
            path.write_text(new_text, encoding="utf-8")

    # The csproj edges.
    reference_projects: list[str] = []
    for project in sorted(projects_needing_reference):
        name = Path(project).name
        csproj = root / project / f"{name}.csproj"
        if not csproj.exists():
            continue
        text = csproj.read_text(encoding="utf-8")
        if project_has_testkit_reference(text):
            continue
        reference_projects.append(project)
        if args.apply:
            csproj.write_text(add_support_reference(text), encoding="utf-8")

    report = {
        "mode": "apply" if args.apply else "check",
        "files_changed": len(results),
        "sites_rewritten": total_sites,
        "event_sites_rewritten": event_sites,
        "message_sites_rewritten": message_sites,
        "lines_before": lines_before,
        "lines_after": lines_after,
        "chars_before": chars_before,
        "chars_after": chars_after,
        "projects_gaining_a_reference": reference_projects,
        "skipped_by_reason": dict(skipped_by_reason.most_common()),
        "skipped_sites": skipped_sites,
        "files": [
            {
                "path": r.path,
                "sites": r.sites,
                "lines_before": r.lines_before,
                "lines_after": r.lines_after,
                "chars_before": r.chars_before,
                "chars_after": r.chars_after,
            }
            for r in results
        ],
    }

    if args.report:
        Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")

    print(f"mode:            {report['mode']}")
    print(f"files changed:   {report['files_changed']}")
    print(f"sites rewritten: {report['sites_rewritten']}")
    print(f"  events:        {event_sites}")
    print(f"  messages:      {message_sites}")
    print(
        f"lines:           {report['lines_before']} -> {report['lines_after']} "
        f"({report['lines_after'] - report['lines_before']:+d})"
    )
    print(
        f"characters:      {report['chars_before']} -> {report['chars_after']} "
        f"({report['chars_after'] - report['chars_before']:+d})"
    )
    print(f"left alone:      {sum(skipped_by_reason.values())}")
    if reference_projects:
        print(f"projects gaining a Harbor.TestKit.Support reference: {len(reference_projects)}")
        for project in reference_projects:
            print(f"  {project}")
    if skipped_by_reason:
        print("left alone (form -> count):")
        for reason, count in skipped_by_reason.most_common():
            print(f"  {count:5d}  {reason}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
