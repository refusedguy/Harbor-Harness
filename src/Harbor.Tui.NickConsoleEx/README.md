# Harbor.Tui.NickConsoleEx

An **additive** TUI backend that hosts the Harbor chat window on the
[nickprotop/ConsoleEx](https://github.com/nickprotop/ConsoleEx) window system
(SharpConsoleUI). It is an `ITuiRenderer` adapter: Harbor agent state is
mirrored into a SharpConsoleUI window with a `MarkupControl` log surface, and
each event appends markup lines and drives one differential render cycle
through their `ForceRender` blitter.

> **This backend complements `Harbor.Tui.CellForge`; it does not replace it.**
> CellForge remains the canonical fullscreen cell-diff backend. NickConsoleEx
> exists to prove the `ITuiRenderer` seam is genuinely backend-agnostic, and to
> offer a windowed alternative on terminals where fullscreen cell-diff is a poor
> fit. Adding work here does not deprioritise CellForge.

## What it is

A **Presentation-layer** `ITuiRenderer` adapter over a vendored third-party
window system. It is a backend like any other: same `ITuiRenderer` contract,
same `AgentEvent` stream, different painting surface. It renders nothing itself
— `MarkupControl` and `ForceRender` do.

## Public API

One renderer and one render context, both in
[`NickConsoleExTuiRenderer.cs`](./NickConsoleExTuiRenderer.cs):

- `NickConsoleExTuiRenderer : BaseTuiRenderer` — carries
  `[TuiRenderer(Backend = "nickconsoleex")]`. Constructor at `:45`; the
  event-driven path is `RenderAsync(AgentEvent, CancellationToken)` (`:73`),
  which pattern-matches agent events and appends markup. I/O overrides:
  `ReadLineAsync` (`:200`), `WriteAsync` (`:208`), `WriteLineAsync` (`:214`),
  `ClearAsync` (`:220`), `Dispose` (`:232`).
- `NickConsoleExRenderContext : ITuiRenderContext` (`:398`) — the adapter that
  maps Harbor render calls onto SharpConsoleUI controls.

`Context` is a `NickConsoleExRenderContext` instance (`:61`).

## Wiring

Selected with `HARBOR_TUI=nickconsoleex`, or `ui.renderer: "nickconsoleex"` in
config.

Unlike every other backend, this one is **conditionally compiled**. It needs the
vendored submodule `external/ConsoleEx/SharpConsoleUI/SharpConsoleUI.csproj`, and
[`src/Harbor.Hosting/Harbor.Hosting.csproj:45`](../Harbor.Hosting/Harbor.Hosting.csproj)
defines `HARBOR_WITH_NICK_CONSOLE_EX` only when that file exists:

```xml
<DefineConstants Condition="Exists('..\..\external\ConsoleEx\SharpConsoleUI\SharpConsoleUI.csproj')">
  $(DefineConstants);HARBOR_WITH_NICK_CONSOLE_EX
</DefineConstants>
```

The registry entry (`NickConsoleExTuiRendererFactory`) and the project reference
are both inside `#if HARBOR_WITH_NICK_CONSOLE_EX`. Consequences:

- **Submodule not checked out** → the backend is not compiled in, and
  `HARBOR_TUI=nickconsoleex` falls through the normal fallback rule to `ansi`
  (or `plain`), with a warning naming the requested id.
- **Submodule checked out** → the backend is available.

`Harbor.Providers`/`Storage` are not involved, but note the Spectre retarget:
`external/Directory.Build.targets` pins the vendored SharpConsoleUI's
`Spectre.Console` edge to the Harbor root pin `0.54.0` for RazorConsole
compatibility.

## Usage

```csharp
using Harbor.Tui.NickConsoleEx;
using Microsoft.Extensions.Logging;

var renderer = new NickConsoleExTuiRenderer(
    loggerFactory.CreateLogger<NickConsoleExTuiRenderer>());

await renderer.InitializeAsync(ct);
await renderer.RenderAsync(agentEvent, ct);
```

## Dependencies

| Reference | Why |
|-----------|-----|
| `external/ConsoleEx/SharpConsoleUI` | the vendored window system |
| `Harbor.Abstractions` | `AgentEvent` |
| `Harbor.Terminal.Abstractions` | `BaseTuiRenderer`, `ITuiRenderContext`, views |
| `Harbor.CodeGen` | source generator (analyzer reference) |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger` |

Referenced by `src/Harbor.Hosting` (conditionally, see above) and the
`Harbor.Tui.RendererTests` / `Harbor.Tui.PerfTests` projects.

## Known limitations

- **Requires a git submodule.** Without `external/ConsoleEx` the project does
  not build at all, and the backend cannot be selected. This is the single
  biggest operational caveat.
- **Additive by rule, not feature-parity.** It does not implement CellForge's
  cell-diff renderer, frame scheduler, or overlay stack. Do not treat it as a
  CellForge replacement.
- **Line-accumulating, not cell-addressed.** State is mirrored as appended
  markup lines (`_lines`, capped by `_maxLines`); there is no in-place cell
  update of the kind `FrameDiff` provides.
- **Driver-dependent.** On a redirected stdout (pipes, CI) it uses
  `HeadlessConsoleDriver`; on a real terminal, `NetConsoleDriver` (both
  `SharpConsoleUI.Drivers`). Terminals it cannot drive fall back to the
  headless driver, so it is not a fullscreen experience everywhere.
- `ReadLineAsync` is line-oriented, not full raw-mode key handling.

## See also

- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the canonical backend this complements
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — input layer and cell-diff engine
- [`../Harbor.Tui.AnsiPlain/README.md`](../Harbor.Tui.AnsiPlain/README.md) — `ansi`/`plain` backends
- [`../Harbor.Terminal.Abstractions/README.md`](../Harbor.Terminal.Abstractions/README.md) — the `ITuiRenderer` seam being implemented
