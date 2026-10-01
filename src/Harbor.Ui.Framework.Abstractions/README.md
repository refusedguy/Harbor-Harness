# Harbor.Ui.Framework.Abstractions

Contracts and abstractions for the Harbor UI Framework — configuration, diagnostics, navigation, and shell chrome. Renderer-agnostic; desktop GUIs and terminal hosts both reference this project.

## Layer

**Domain (framework contracts).** Innermost UI Framework project — and the matrix places it in **Domain**, not with its `Ui.Framework.*` siblings, which is what lets the port sit *below* its consumer instead of beside it (ADR-009). Depends on `Harbor.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` only.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Commands/` | `SlashCommandCatalog` + `SlashCommandDefinition` — the single slash-command registry every palette, autocomplete and help surface derives from (issue #462). |
| `Configuration/` | `ICommonConfigModelRefReader` — reads the one provider/model reference session bootstrap needs, out of the common config. The read-only half of a two-contract pair; the writable whole-config half is `ICommonConfigStore` in `Harbor.Desktop.Abstractions` (issue #453). |
| `Diagnostics/` | `IDiagnosticsPanel`, `InMemoryDiagnosticsPanel`, `DiagnosticEntry`, `DiagnosticsPanelLoggerProvider` (ILoggerProvider that forwards logs into the panel), `LogLevelTag` + `LogRow`/`LogRowFormat` — the one table that spells the 4-char level mnemonic and the named row format that producers format and readers parse by rules rather than by character offset (issue #563). |
| `Forking/` | `ISessionForker` + `SessionForked` — the narrow port over the core's `SessionForkService`, which lives in `Harbor.Application`, a layer this Presentation framework may not reference. Same bridge shape and same reason as `Configuration/` (issue #453, ADR-009); a composition root supplies the adapter. Before it existed the UI framework forked sessions with its own hand-written copy and the two drifted until a UI fork set no lineage, persisted no title and regenerated every copied message id (issue #670). |
| `Navigation/` | `IContentHost`, `IShellChrome`, `IWorkspaceCommands`, `OverlayIds` (palette, settings, diff, token usage, provider browser, model picker, sessions flyout, focus session). |
| `ToolCallState.cs` | `ToolCallState` + `ToolCallStateExtensions.IsTerminal()` — the single tool-call lifecycle vocabulary (issue #567). |

## Public API summary

- **`SlashCommandCatalog`**: `All` (every command), `Invocations` (`/help`, `/exit`, … — the advertised vocabulary), `Find(text)` (alias- and case-insensitive lookup), plus the palette group constants. `SlashCommandDispatcher` binds its handlers to these entries and throws at construction if a command has no handler, so a command can never be advertised without being runnable.
- **`ICommonConfigModelRefReader.ReadModelRefAsync()`**: returns `Maybe<ModelRef>` — the provider/model the common config names, or `None` when it names none that can be qualified (not yet configured, unreadable, or unusable). One value with one absence: it used to return `(string? ProviderId, string? ModelId)?`, which spelled four states the type could not tell apart, and issue #453 moved the qualification to the producer so `ModelRef.Qualify` is the only place "is this reference whole?" is answered.
- **`ISessionForker.ForkAsync(sessionId, upToMessageId, title, ct)`**: returns `Result<SessionForked>` — the new child session plus how many history messages were copied. `upToMessageId` is an INCLUSIVE cut point; an unknown id fails without creating anything. Failures come back with the store's own text, already naming the step that broke and how far the copy got; the calling layer adds the context it owns. It is an error-shaping port, not a second implementation — that is the whole point (#670).
- **`DiagnosticsPanelLoggerProvider` / `DiagnosticsPanelLogger` : `ILoggerProvider` / `ILogger` — bridges `Microsoft.Extensions.Logging` into the diagnostics panel.
- **`LogLevelTag.For(LogLevel)` / `TryParse(string?, out LogLevel)`**: the single 4-character level mnemonic — `TRAC`, `DBUG`, `INFO`, `WARN`, `ERRO`, `CRIT`, `NONE`. The switch names every member and carries **no wildcard arm**, so adding a `LogLevel` member is a compile error here (CS8509, escalated by `TreatWarningsAsErrors`) and a value outside the enum throws instead of rendering a token. Six sites used to spell this table and disagreed about the fallback — the panel sent an unknown level to a 4-question-mark sentinel, the two `FileLogger` copies sent it to `level.ToString().ToUpperInvariant()`, so one event read as the sentinel in the logs panel and as `VERBOSE` in the log file. `TryParse` is the inverse and is derived from `For` at type initialisation, so there is still exactly one hand-maintained list.
- **`LogRow`** (`Timestamp`, `Level`, `Category`, `Message`) + **`LogRowFormat`**: the named row format, its two documented layouts (`Panel` — `HH:mm:ss.fff TAG cat msg`, local time; `File` — `HH:mm:ss.fff [TAG] [thread] cat: msg`, UTC time), and `LogRowFormat.TryParse` which reads either back **by rule, not by offset**. A consumer that receives a pre-rendered row used to slice `row[13..17]`, an offset that only ever described the panel layout and returns `"[INF"` for a file row — so file rows lost their level colour and fell through to plain text. `TryParse` returns `false` for anything it does not recognise, which a fixed slice structurally cannot do. A rendered clock carries no date, so a parsed row's timestamp is anchored to today; only the time of day round-trips.
- **`ToolCallState`**: `Pending`, `Running`, `Success`, `Error`, `Cancelled`, `TimedOut`. The union of the three enums it replaced (`ViewModels.ToolCallStatus`, `Projection.ToolCallStatus`, `CellForge.ToolCallStatus`), which had three different member sets and no terminal-state rule. `IsTerminal()` classifies once instead of per call site. Every switch over the enum names all members explicitly and throws on the unnamed domain, and a guard test walks `Enum.GetValues<ToolCallState>()` — C# cannot express named-member-only exhaustiveness for an enum (CS8524 is unconditional), so the test is the gate that makes the next member a failure rather than a spinner (#567).
- **`IShellChrome` / `IWorkspaceCommands`**: navigation contracts for desktop shell integration.
- **`OverlayIds`**: string constants for builtin overlay identifiers.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | ILogger contracts |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, `KeyPress` |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/` and app-level tests.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Abstractions/Harbor.Ui.Framework.Abstractions.csproj
```

## Known limitations

- `OverlayIds` are string constants, not a strongly-typed enum — prone to typos at call sites.
- `InMemoryDiagnosticsPanel` capacity is fixed at construction; no auto-resize.
- The registry covers the CLI slash dispatcher's vocabulary. Renderer-local affordances (CellForge's `/vim` leader toggle, `/panels`) are deliberately not in it — they are not dispatchable everywhere, so advertising them globally would reintroduce the drift this registry removed.
