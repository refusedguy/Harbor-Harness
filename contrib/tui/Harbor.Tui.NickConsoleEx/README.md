# Harbor.Tui.NickConsoleEx

> **CONTRIBUTED — NOT BUILT BY CI, NOT PART OF THE PRODUCT.**
>
> This backend lives in `contrib/`. It is not in `Harbor.slnx`, not in
> `contrib/Contrib.slnx`, and no project under `src/` or `apps/` references
> it. The Harbor product does not contain this renderer, and
> `HARBOR_TUI=nickconsoleex` is no longer a value the shipped CLI knows.

An **additive** TUI backend that hosts the Harbor chat window on the
[nickprotop/ConsoleEx](https://github.com/nickprotop/ConsoleEx) window system
(SharpConsoleUI). It is an `ITuiRenderer` adapter: Harbor agent state is
mirrored into a SharpConsoleUI window with a `MarkupControl` log surface, and
each event appends markup lines and drives one differential render cycle
through their `ForceRender` blitter.

> **This backend complements `Harbor.Tui.CellForge`; it does not replace it.**
> CellForge remains the canonical fullscreen cell-diff backend.

## How to build it

The vendored window system is a git submodule, and CI does not check it out.
To build this project locally:

```bash
# 1. fetch the vendored tree (the submodule lives under contrib/external/ now)
git submodule update --init contrib/external/ConsoleEx

# 2. build this project alone
dotnet build contrib/tui/Harbor.Tui.NickConsoleEx/Harbor.Tui.NickConsoleEx.csproj -c Release
```

There is no solution file entry for it on purpose. If you want it in a
solution, add it to a local-only solution — do not add it to `Harbor.slnx`
(that file is what CI builds) and do not add it to `contrib/Contrib.slnx`
(that file is what the report-only contrib dry run walks, and the vendored
submodule is not checked out there either).

### What the move cost the product

The vendored tree used to sit under `external/` at the repository root and was
referenced by `src/Harbor.Hosting` behind a `HARBOR_WITH_NICK_CONSOLE_EX`
define, so a Release build of the product compiled SharpConsoleUI — in
Debug — on every run. Moving both the vendored tree and this adapter into
`contrib/` takes that compile window out of the product build.

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

## Wiring (for whoever maintains this fork)

The product no longer composes this backend: there is no
`HARBOR_WITH_NICK_CONSOLE_EX` define, no `NickConsoleExTuiRendererFactory`
entry in `src/Harbor.Hosting/Modules/TuiBackendRegistry.cs`, and no
`BackendKind.NickConsoleEx` value. If you want the backend back in a fork,
you have to re-add all three:

1. A `ProjectReference` to this project from your composition root, plus a
   define constant of your own.
2. An `ITuiRendererFactory` implementation returning
   `NickConsoleExTuiRenderer`, registered in `TuiBackendRegistry.Build()`.
3. A `BackendKind` value, if your fork's consumers need one.

The `Spectre.Console` retarget still applies: `contrib/external/Directory.Build.targets`
pins the vendored SharpConsoleUI's `Spectre.Console` edge to the Harbor root
pin `0.54.0` for RazorConsole compatibility.

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
| `contrib/external/ConsoleEx/SharpConsoleUI` | the vendored window system |
| `src/Harbor.Abstractions` | `AgentEvent` |
| `src/Harbor.Terminal.Abstractions` | `BaseTuiRenderer`, `ITuiRenderContext`, views |
| `src/Harbor.CodeGen` | source generator (analyzer reference) |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger` |

## Known limitations

- **Requires a git submodule.** Without `contrib/external/ConsoleEx` the
  project does not build at all, and the backend cannot be selected. This is
  the single biggest operational caveat.
- **Not maintained by the Harbor project.** There is no test suite for it in
  the repository any more, and no CI job builds it. Treat it as a starting
  point, not a supported backend.
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

- [`src/Harbor.Tui.CellForge/README.md`](../../../src/Harbor.Tui.CellForge/README.md) — the canonical backend this complements
- [`src/Harbor.Tui.CellForge.Engine/README.md`](../../../src/Harbor.Tui.CellForge.Engine/README.md) — input layer and cell-diff engine
- [`src/Harbor.Tui.AnsiPlain/README.md`](../../../src/Harbor.Tui.AnsiPlain/README.md) — `ansi`/`plain` backends
- [`src/Harbor.Terminal.Abstractions/README.md`](../../../src/Harbor.Terminal.Abstractions/README.md) — the `ITuiRenderer` seam being implemented
