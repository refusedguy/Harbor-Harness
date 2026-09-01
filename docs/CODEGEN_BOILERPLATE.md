# Harbor.CodeGen — Boilerplate-Reduction Generators

Internal Roslyn source generators that eliminate hand-written boilerplate in
the TUI/render pipeline. All three are **opt-in via attributes**, produce
**zero-allocation** output (spans, static arrays, stackalloc formatters), and
are **AOT-safe** (no runtime reflection). Generators match attributes by
fully-qualified metadata name — consuming projects link the shared-source
attribute file, no assembly reference is required.

| Generator | Attribute | Emits | Sprint task |
|---|---|---|---|
| `EscapeCodeGenerator` | `[TerminalEscape]` on an enum | Static `EscapeCodes` class: precomputed `ReadOnlySpan<char>` ECMA-48 tables + stackalloc CSI/SGR formatters | Task 1 |
| `RendererAdapterGenerator` | `[TuiRenderer("backend-id")]` on a renderer class | Partial-class companion: `BackendId` constant, `CursorFrameBoundary` flag, frame prologue/epilogue writers, per-namespace `TuiRendererFrameBoundaries` lookup | Task 2 |
| `MoodFrameGenerator` | `[MoodFrame(...)]` on an enum | Static `{EnumName}Frames` dispatch: `FramesOf` / `FrameIndex` / `Frame` / `FrameCount` | Task 3 |

The generator project is `src/Harbor.CodeGen/` (a build tool, outside
`Harbor.slnx`), targeting `netstandard2.0` per RS1041 — Roslyn components
load into the compiler process. Each generator lives in its own `.cs` file
(`EscapeCodeGenerator.cs`, `RendererAdapterGenerator.cs`,
`MoodFrameGenerator.cs`).

---

## Wiring a consuming project

```xml
<ItemGroup>
  <!-- Build-time only: the analyzer runs in the compiler process. -->
  <ProjectReference Include="..\Harbor.CodeGen\Harbor.CodeGen.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false"/>
</ItemGroup>

<ItemGroup>
  <!-- Shared-source attribute surface (matched by FQ metadata name). -->
  <Compile Include="..\Harbor.CodeGen\Attributes\HarborCodeGenAttributes.cs"
           Link="CodeGen\HarborCodeGenAttributes.cs"/>
</ItemGroup>
```

In code, import the attribute namespace once:

```csharp
using Harbor.CodeGen;
```

---

## Task 1 — `[TerminalEscape]` and the generated `EscapeCodes`

Annotate the escape-code vocabulary enums (canonical names unlock the
matching generated API section):

```csharp
[TerminalEscape]
public enum Color8Bit { Default = 0, Red = 31, Green = 32, /* 30–37 / 90–97 */ }

[TerminalEscape]
public enum CursorDirection { Up, Down, Forward, Backward }

// Member values are distinct bits; the emitted SGR codes (1/2/3/4/7/9) are
// fixed by the generated FormatStyle table, keyed by member name.
[TerminalEscape]
public enum StyleFlag { None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Reverse = 16, Strike = 32 }
```

The generated `EscapeCodes` static class (same namespace as the enums)
provides:

- **Fixed sequences** as `ReadOnlySpan<char>` properties — `Reset`,
  `HideCursor`, `ShowCursor`, `ClearLine`, `ClearScreen`,
  `EnterAlternateScreen`, `ExitAlternateScreen`, `ForegroundDefault`,
  `BackgroundDefault`.
- **Palette spans** — `Foreground(Color8Bit)` / `Background(Color8Bit)`
  switch spans (value `0` → default fg/bg).
- **Zero-alloc formatters** (write into caller `Span<char>`, return written
  length, `-1` on undersized buffer): `FormatForeground(r,g,b,dst)`,
  `FormatBackground(r,g,b,dst)`, `FormatMove(dir,count,dst)`,
  `FormatPosition(row,col,dst)`, `FormatStyle(StyleFlag,dst)`.
- **Allocating convenience wrappers** for non-hot paths: `ForegroundRgb`,
  `BackgroundRgb`, `Move`, `Position`, `Style`.

`Harbor.Tui.AnsiPlain` consumes this for every styled/cursor write
(`AnsiEscapeStrategy`, `AnsiPlainRenderContext.WriteStyled` /
`SetCursorPosition`), replacing hand-written `"\x1b[...m"` literals.
Hand-written literals that remain in `EscapeCodeStrategy` are intentional
public-API composition constants (`ResetSeq`, palette strings) kept for
binary compatibility; the emission paths use the generated tables.

## Task 2 — `[TuiRenderer]` and the backend registry

Annotate each renderer backend class (the class must be declared `partial`):

```csharp
[TuiRenderer("ansi", CursorFrameBoundary = true)]   // owns the cursor across a frame
public partial class AnsiTuiRenderer : AnsiPlainTuiRenderer { ... }

[TuiRenderer("plain")]                              // line/markup backend
public partial class PlainTuiRenderer : AnsiPlainTuiRenderer { ... }
```

The generator emits a partial companion with `public const string BackendId`,
`public const bool CursorFrameBoundary`, and specialized frame-boundary
writers (`WriteFramePrologue` / `WriteFrameEpilogue(TextWriter)` — hide/show
the cursor only when the backend owns it across frames, literal-false
backends get no-op bodies so no unreachable code exists). Per namespace it
additionally emits `TuiRendererFrameBoundaries.HasCursorFrameBoundary(id)` —
a generated switch mapping backend ids to their boundary flag, unknown ids →
`false`. Covered backends: `ansi`, `plain` (Harbor.Tui.AnsiPlain),
`cellforge` (Harbor.Tui.CellForge), `nickconsoleex` (Harbor.Tui.NickConsoleEx).

## Task 3 — `[MoodFrame]` and the mood dispatch table

Annotate the mood enum; frame banks resolve by naming convention
(`{Mood}Frames` fields on `BankContainer`):

```csharp
[MoodFrame(
    MascotMood.Idle, MascotMood.Working, MascotMood.Awaiting, MascotMood.Sleeping,
    MascotMood.Thinking, MascotMood.ToolCall, MascotMood.Error, MascotMood.Success,
    BankContainer = "AmbientMascot",
    SleepPeriodTicks = AmbientMascot.SleepPeriod)]   // "Sleeping" advances 1 frame / 8 ticks
public enum MascotMood : byte { ... }
```

Generated `MascotMoodFrames` (same namespace as the enum):

- `FramesOf(mood)` → the static `string[]` bank **by reference** (zero copy).
- `FrameIndex(monotonicTick, mood)` — deterministic index, negative ticks
  wrap, the `Sleeping` member advances once per `SleepPeriodTicks` ticks.
- `Frame(monotonicTick, mood)` and `FrameCount(mood)` conveniences.

`AmbientMascot.FramesOf` / `FrameIndex` / `Frame` now delegate to the
generated table; the hand-written `switch` is gone.

---

## Testing

Each generator is pinned by tests in `tests/Harbor.Tui.RendererTests/`:

- `AnsiPlain/EscapeCodeTests.cs` — escape-code tables golden frame
  (`GoldenFrames/escapecodes-tables.golden.txt`) + direct formatter asserts.
- `RendererAdapterGoldenFrameTests.cs` — backend registry coverage.
- `MascotMoodFrameTests.cs` — 25-tick × 8-mood frame matrix golden
  (`GoldenFrames/mascot-mood-frames.golden.txt`) + Sleeping/negative-tick
  parity asserts.

Golden frames follow the shared convention: regenerate intentionally with
`HARBOR_UPDATE_GOLDEN=1 dotnet test tests/Harbor.Tui.RendererTests` and
review the golden diff.

## Guardrails

- Generated files are marked `<auto-generated/>`; do not edit them.
- Runtime dispatch is allocation-free and trim-safe (no reflection).
- Public API changes: none — generators add types/members alongside
  existing surfaces; renderer classes only gain the `partial` modifier.
- Adding a new vocabulary/backend/mood is an attribute edit, not a
  hand-maintained table edit — the compiler regenerates the dispatch.
