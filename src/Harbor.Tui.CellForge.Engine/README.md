# Harbor.Tui.CellForge.Engine

The terminal I/O engine behind the canonical CellForge backend: raw-mode
ownership, byte-level escape-sequence parsing, terminal capability probing, and
the cell-diff write path. BCL-only and AOT-compatible (`<IsAotCompatible>` —
the analyzers hold it at zero IL2xxx warnings).

This is the **engine half** of CellForge. It knows nothing about chat, agents
or events. The renderer that drives it lives in
[`../Harbor.Tui.CellForge`](../Harbor.Tui.CellForge/README.md); this project is
the reusable substrate. Namespaces stay `Harbor.Tui.CellForge.*` (issue #33
split) even though the assembly is `Harbor.Tui.CellForge.Engine`.

## What it is

A **Presentation-layer terminal I/O engine**, BCL-only and AOT-compatible. It
owns the four concerns between a byte stream and a painted frame — raw-mode
ownership, escape-sequence parsing, capability probing, and the cell-diff write
path — and knows nothing about chat, agents or events.

## Public API

Four areas, one per directory.

### `Input/` — raw mode and byte decoding

`ITerminalModeController` (`Input/ITerminalModeController.cs:9`) is the
raw-mode ownership seam: `Enter()`, `Restore()` (idempotent), `IsRaw`. Three
implementations, picked by platform:

| Implementation | Platform |
|----------------|----------|
| `UnixTermiosModeController` | Linux/macOS via `tcgetattr`/`tcsetattr` |
| `WindowsVtModeController` | Windows VT processing |
| `NullModeController` | anything else — no-op, never throws |

`TerminalInputSource` (`Input/TerminalInputSource.cs:16`) is the reader: it
takes a `Stream` plus an optional `TerminalInputSourceOptions` (`:39`) and
pushes decoded `InputEvent`s into a single-reader channel. It drives kitty
keyboard protocol, SGR mouse reporting, and bracketed paste. Supporting types:
`InputEvent`, `MouseEvent`, `PasteEvent`, `ResizeSignal`, `CapabilityEvent`,
`PasteSanitizer`, and the `FocusRouter`/`IFocusTarget` focus stack.

`MouseRouter` (`Input/MouseRouter.cs:25`) translates wheel ticks into
`UiMsg.KeyInput` for the State layer, so wheel input reaches the same message
bus as the keyboard.

### `Parsing/` — the state machine

`EscapeSequenceParser` (`Parsing/EscapeSequenceParser.cs:19`) is an incremental
byte state machine over a stdin stream: it recognises CSI/SS3 sequences, SGR
mouse reports, kitty keyboard codes and bracketed-paste markers, and drives
`ParserState`/`ParserOptions` plus `Utf8IncrementalDecoder` so multi-byte
sequences split across read boundaries still decode correctly.

### `Capabilities/` — terminal probing

`TerminalCapabilities` (`Capabilities/TerminalCapabilities.cs:4`) is the
resolved capability record; `CapabilityProber`, `TerminalQueries`,
`InlineImageProbe` and `NotifyProbe` detect kitty graphics, OSC 776/1337 inline
images and OSC 777/99 notifications at startup.

### `Rendering/` — the write path

`ITerminalBackend` (`Rendering/ITerminalBackend.cs:9`) is the single write seam:
one assembled frame leaves the process through one `WriteAsync` call (with a
`Write(ReadOnlySpan<byte>)` variant for the synchronous adapter path).
`StdoutBackend` is the production implementation; tests capture frames in
memory.

`AnsiWriter` builds those frames. `BufferSwapChain`/`BufferPair` own the
double-buffer pool (`Rent`/`Publish`/`TryTake`/`Return`), and
`FrameDiff`/`FrameDiffMode`/`FrameDiffEnumerator` compute the changed cells
between frames — `FrameDiff` is a `readonly ref struct` built inside the render
loop and enumerated without allocating. `DirtyRect` narrows the diff region to
hint rectangles. Also here: the engine-owned vocabulary ports (`Cell`,
`Rect`, `ScreenBuffer`, `CellStyle`, `UnicodeWidth`, the `KeyEvent` family,
`UiKeyDto`, `RgbColor`, `TerminalBackgroundProbe`, plus the
`EngineStageCounters` instrument), `FlexLayout`/`Graphics` (layout),
`DiffEngine`, the `PostFxPipeline` / `SpringFx` post-effect chain,
`InlineImageEncoder`, and the OSC helpers `Osc52Clipboard`,
`Osc777Notify`, `Osc99Notify`, `Osc1337Image`. The chat-owned rendering —
`OverlayStack`, `LayoutTree`, `PromptBuffer` / `PromptHistory` /
`PromptRenderer` / `PromptViewport`, `ComposerController` / `VimComposerMode`,
the markdown tokenizers (`CodeTokenizer`, `CodeSyntaxTokenizer`,
`CodeHighlightPalette`, `LanguageSupportRegistry`), `InlineSession` /
`InlineImageLayer`, `MarkdownEditOps` and `GlowEffect` — moved to
`Harbor.Tui.CellForge` under `Chat/Rendering` (#436).

## Wiring

The engine has **no `HARBOR_TUI` value and no DI module of its own** — it is
instantiated directly by `Harbor.Tui.CellForge`, which is itself selected by
`HARBOR_TUI=cellforge` (legacy alias `consoleex`). Registration of that backend
lives in
[`TuiBackendRegistry.cs`](../Harbor.Hosting/Modules/TuiBackendRegistry.cs) as
`CellForgeTuiRendererFactory`.

`Harbor.Hosting` carries an unconditional `ProjectReference` to this project,
so it is always present in a full build. The assembly exposes
`InternalsVisibleTo` to `Harbor.Tui.CellForge` and `Harbor.Tui.CellForge.Tests`
— the renderer is a friend, not a public consumer.

## Usage

Driving the input pipeline directly, as the renderer does:

```csharp
using Harbor.Tui.CellForge.Input;

ITerminalModeController mode = OperatingSystem.IsWindows()
    ? new WindowsVtModeController()
    : new UnixTermiosModeController();

mode.Enter();
try
{
    using var input = new TerminalInputSource(Console.OpenStandardInput());
    await foreach (InputEvent evt in input.ReadAsync(ct))
    {
        // evt is a KeyEvent, MouseEvent, PasteEvent, ResizeSignal or CapabilityEvent
    }
}
finally
{
    mode.Restore();   // idempotent — safe even if Enter() threw
}
```

Writing one frame. `AnsiWriter` is a cursor-style writer over a backend: you
open a frame, emit cells, and `EndFrameAsync` flushes the single atomic write.

```csharp
using System.Text;
using Harbor.Tui.CellForge.Rendering;

ITerminalBackend backend = new StdoutBackend();
var writer = new AnsiWriter(backend);

writer.BeginFrame();
writer.MoveTo(0, 0);
writer.SetStyle(default);
writer.PutRune(new Rune('>'));
writer.ResetStyle();
await writer.EndFrameAsync(ct);
```

`BufferSwapChain`/`BufferPair` own the double-buffer pool
(`Rent`/`Publish`/`TryTake`/`Return`) used to build those frames.
`FrameDiff`/`FrameDiffMode`/`FrameDiffEnumerator` compute the changed cells
between frames — `FrameDiff` is a `readonly ref struct` built inside the render
loop and enumerated without allocating. `DirtyRect` narrows the diff region to
hint rectangles. Also here: the engine-owned vocabulary ports (`Cell`,
`Rect`, `ScreenBuffer`, `CellStyle`, `UnicodeWidth`, the `KeyEvent` family,
`UiKeyDto`, `RgbColor`, `TerminalBackgroundProbe`, plus the
`EngineStageCounters` instrument), `FlexLayout`/`Graphics` (layout),
`DiffEngine`, the `PostFxPipeline` / `SpringFx` post-effect chain,
`InlineImageEncoder`, and the OSC helpers `Osc52Clipboard`,
`Osc777Notify`, `Osc99Notify`, `Osc1337Image`. The chat-owned rendering —
`OverlayStack`, `LayoutTree`, `PromptBuffer` / `PromptHistory` /
`PromptRenderer` / `PromptViewport`, `ComposerController` / `VimComposerMode`,
the markdown tokenizers (`CodeTokenizer`, `CodeSyntaxTokenizer`,
`CodeHighlightPalette`, `LanguageSupportRegistry`), `InlineSession` /
`InlineImageLayer`, `MarkdownEditOps` and `GlowEffect` — moved to
`Harbor.Tui.CellForge` under `Chat/Rendering` (#436).

## Dependencies

| Reference | Why |
|-----------|-----|
| _(none — standalone leaf since #436)_ | The cell/input/probe vocabulary lives here now as engine-owned ports (verbatim copies under `Harbor.Tui.CellForge.*`); the chat-owned rendering moved to `Harbor.Tui.CellForge/Chat/Rendering`, which references both sides |

`Harbor.Abstractions` and `Harbor.Ui.Framework.State` were removed in #435.
The first measured as a zero real dependency — zero imports and zero bound
types, the #980 shape — and the second because the two files that spoke it now
name the BCL-only `UiKeyDto` (#162) and `EnterDecision` vocabularies instead.
`MouseRouter` returns a direction rather than `AppMsg.KeyInput`, and
`ComposerController` asks `EnterPolicy` rather than `EnterKeyPolicy`; the host
converts through `KeyEventAdapter`, which stays the single
Rendering→State crossing point. `MouseRouter.WheelToKey` + a
`TimelineWheelTarget` callback is now half a round trip that a host completes.

`Harbor.Ui.Framework.Rendering` and `Harbor.DesignSystem` were removed in #436:
the remaining cell/input/probe/layout vocabulary (SplitDir/Size included) was ported into the engine
verbatim (same shape, engine-owned namespaces), and the chat-owned rendering
(prompt/composer/markdown/overlay/layout/image-layer, 17 files) moved to
`Harbor.Tui.CellForge/Chat/Rendering`. The #1009 caveat is cleared with them:
`Contracts` reached the engine only transitively (`Rendering→Contracts`), so
with both edges gone no Harbor assembly is reachable at all.

Referenced by `Harbor.Tui.CellForge` (its only production consumer),
`src/Harbor.Hosting`, `apps/Harbor.App.Cli`, and the CellForge test/benchmark
projects. The edge set is pinned by `tests/Harbor.Architecture.Tests`; CF-A-001
requires these to be direct references rather than transitive ones.

## Known limitations

- **Not a renderer.** There is no `ITuiRenderer` and no `HARBOR_TUI` value here;
  using it means building the input/parse/diff loop yourself.
- **Synchronous `Write` is optional on backends.** `ITerminalBackend.Write`
  throws `NotSupportedException` by default; a backend must override it to back
  the synchronous `CellForgeRenderContext` adapter.
- **Input handling is raw-mode only.** `NullModeController` makes the engine
  inert on platforms with no mode support rather than failing loudly.
- `IsAotCompatible` is load-bearing: adding reflection or a
  `JsonSerializer` call without a source-generated context breaks the build.

## See also

- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the renderer that drives this engine
- [`../Harbor.Ui.Framework.Rendering/README.md`](../Harbor.Ui.Framework.Rendering/README.md) — cells, screen buffer, cell-diff protocol
- [`../Harbor.Tui.AnsiPlain/README.md`](../Harbor.Tui.AnsiPlain/README.md) — the non-cell renderer pair (`ansi`/`plain`)
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
