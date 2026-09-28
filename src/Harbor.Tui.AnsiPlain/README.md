# Harbor.Tui.AnsiPlain

The unified streaming TUI renderer for Harbor: one renderer, two escape-code
strategies. Serves real ANSI terminals (`HARBOR_TUI=ansi`) and plain-text sinks
— pipes, CI logs, files, accessibility readers (`HARBOR_TUI=plain`) — from a
single render pipeline, with no duplicated render logic. AOT-compatible.

This project **merges the former `Harbor.Tui.Ansi` and `Harbor.Tui.Plain`
backends** (renderer-unification sprint, Phase 4). The two old project names no
longer exist; `plain` and `ansi` are now two `ITuiRendererFactory` strategies
over one shared base.

## What it is

A **Presentation-layer** `ITuiRenderer` implementation pair. One render
pipeline, two escape-code strategies: ANSI SGR sequences for real terminals,
empty strings for plain-text sinks. It is an append-only stream, not a cell
grid — for fullscreen cell-addressed rendering see
[`Harbor.Tui.CellForge`](../Harbor.Tui.CellForge/README.md).

## Public API

### Renderers

Both derive from `BaseTuiRenderer` (`Harbor.Terminal.Abstractions`) and are
attributed with the backend id the registry resolves:

| Type | Backend id | Escape strategy |
|------|-----------|-----------------|
| `AnsiTuiRenderer` | `ansi` | `AnsiEscapeStrategy.Instance` |
| `PlainTuiRenderer` | `plain` | `NullEscapeStrategy.Instance` |

`AnsiTuiRenderer` has two constructors — one writing to `Console.Out`
(`src/Harbor.Tui.AnsiPlain/AnsiTuiRenderer.cs:29`), one to a caller-supplied
`TextWriter` for golden-frame tests (`:38`). `PlainTuiRenderer` takes an
optional `TextWriter` and an optional `UiStore`
(`src/Harbor.Tui.AnsiPlain/PlainTuiRenderer.cs:18`).

### Escape-code strategy

`IEscapeCodeStrategy` (`src/Harbor.Tui.AnsiPlain/EscapeCodes/EscapeCodeStrategy.cs:22`)
is the seam that makes one pipeline serve both sinks. Every styled or
cursor-affecting write goes through it:

```csharp
public interface IEscapeCodeStrategy
{
    bool SupportsColor { get; }
    string Reset { get; }
    string Foreground(TuiColor color);
    string Background(TuiColor color);
    string Style(TuiStyle style);
    string HideCursor { get; }
    string ShowCursor { get; }
    string ClearLine { get; }
    string ClearScreen { get; }
    string EnterAlternateScreen { get; }
    string ExitAlternateScreen { get; }
    string CursorPosition(int row, int col);
}
```

Two implementations, both stateless singletons:

- `AnsiEscapeStrategy.Instance` — ECMA-48 SGR/CSI sequences, 24-bit truecolor
  (`\x1b[38;2;R;G;Bm`).
- `NullEscapeStrategy.Instance` — every member returns `string.Empty`, which
  collapses all styling to raw text. This is what makes `plain` output
  diff-friendly in CI.

`AnsiEscapeStrategy` reads its reset constant from the **source-generated**
`StyleFlagEscapeCodes` in `Harbor.Terminal.Abstractions` (a `Harbor.CodeGen`
analyzer output, not hand-written), so the enum and the escape table cannot
drift. Truecolor and DEC private-mode sequences stay hand-written by design —
they are not enum-based.

### Supporting types

- `Color8Bit`, `StyleFlag`, `CursorDirection` — the enums the SGR layer maps
  `TuiColor`/`TuiStyle` onto.
- `TerminalQrRenderer` — static QR-code-to-block-glyph helper for terminals.

## Wiring

Selected by the `HARBOR_TUI` environment variable (or `tui:` in
`~/.harbor/config.json`). Both backends are registered unconditionally in
[`TuiBackendRegistry.cs`](../Harbor.Hosting/Modules/TuiBackendRegistry.cs) via
`PlainTuiRendererFactory` and `AnsiTuiRendererFactory` — no feature flag, no
`#if`.

| `HARBOR_TUI` | Renderer |
|--------------|----------|
| `plain` | `Harbor.Tui.AnsiPlain.PlainTuiRenderer` |
| `ansi` | `Harbor.Tui.AnsiPlain.AnsiTuiRenderer` |

- `plain` is the default fallback when the Spectre backends are **not** compiled
  in (`TuiBackendRegistry.FallbackBackendId`).
- `ansi` is the default fallback when they **are** compiled in. An unrecognised
  `HARBOR_TUI` value therefore renders ANSI in a full CLI build rather than
  erroring; `TuiModule` logs a warning naming the requested id.

Both factories resolve the **DI-shared `UiStore`** singleton (issue #77), so
chat writes survive a renderer swap at runtime. The `plain` path is what the
E2E smoke test captures — see the expected output in
[`AGENTS.md`](../../AGENTS.md#e2e-testing).

## Usage

Constructing the ANSI renderer directly, as the registry does:

```csharp
using Harbor.Tui.AnsiPlain;
using Microsoft.Extensions.Logging;

var renderer = new AnsiTuiRenderer(
    loggerFactory.CreateLogger<AnsiTuiRenderer>(),
    store: sp.GetRequiredService<UiStore>());
```

And the plain renderer, with an explicit sink for golden-frame tests:

```csharp
using Harbor.Tui.AnsiPlain;

var renderer = new PlainTuiRenderer(writer: testWriter, store: store);
await renderer.InitializeAsync(ct);
await renderer.RenderAsync(evt, ct);   // emits `[agent_start] …` markers
```

## Dependencies

| Reference | Why |
|-----------|-----|
| `Harbor.Abstractions` | `AgentEvent` |
| `Harbor.Terminal.Abstractions` | `ITuiRenderer`, `BaseTuiRenderer`, `TuiColor`/`TuiStyle` |
| `Harbor.Ui.Framework.State` | `UiStore` |
| `Harbor.CodeGen` | source generator (analyzer reference) |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger` |

Referenced by `Harbor.Hosting` (the backend registry), `apps/Harbor.App.Cli`,
`contrib/tui/Harbor.Tui.Sixel`, and the TUI test/benchmark projects. The
reference set is pinned by `tests/Harbor.Architecture.Tests`.

## Known limitations

- **No cursor addressing on the plain path.** `NullEscapeStrategy` returns
  empty for `CursorPosition` and the alternate-screen sequences, so the plain
  renderer is a linear append-only log by design. It is not a TUI.
- **Sequential stream, not a cell grid.** Unlike
  [`Harbor.Tui.CellForge`](../Harbor.Tui.CellForge/README.md) there is no
  screen buffer and no frame diffing; every render is an append. Choose
  `cellforge` for fullscreen in-place updates.
- `AnsiTuiRenderer` writes to `Console.Out` via the single-argument
  constructor and therefore does **not** own that writer; pass an explicit
  `TextWriter` in tests rather than relying on redirection.

## See also

- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the canonical fullscreen cell-diff backend
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — terminal input layer and cell primitives
- [`../Harbor.Ui.Framework.Rendering/README.md`](../Harbor.Ui.Framework.Rendering/README.md) — `UiStore`'s cell/screen layer
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
