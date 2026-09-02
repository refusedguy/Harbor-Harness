# CodeGen Boilerplate Reduction

> Source generators that eliminate repetitive escape-code, renderer-adapter, and mood-dispatch boilerplate in the TUI/render pipeline.
>
> **Связанные документы:**
> - [docs/EXAMPLES.md §Source generators](./EXAMPLES.md#source-generators) — quick recipes.
> - [docs/DEVELOPMENT.md](./DEVELOPMENT.md) — how to add a new generator.
> - [AGENTS.md §Add a builtin tool](../AGENTS.md#add-a-builtin-tool) — registration pattern.
> - [src/Harbor.CodeGen/](../src/Harbor.CodeGen/) — generator implementations.

## Table of contents

1. [Overview](#1-overview)
2. [EscapeCodeGenerator](#2-escapecodegenerator)
3. [RendererAdapterGenerator](#3-rendereradaptergenerator)
4. [MoodFrameGenerator](#4-moodframegenerator)
5. [Adding a new generator](#5-adding-a-new-generator)
6. [Testing](#6-testing)
7. [Golden-frame regression](#7-golden-frame-regression)

---

## 1. Overview

Harbor uses three incremental source generators in `src/Harbor.CodeGen/`, wired as a Roslyn component via `<IsRoslynComponentPackage>true</IsRoslynComponentPackage>`:

| Generator | Attribute | Generates | Hot-spot |
|-----------|-----------|-----------|----------|
| `EscapeCodeGenerator` | `[TerminalEscape]` on enums | `*EscapeCodes` static classes with precomputed ECMA-48 sequences | `AnsiPlain/EscapeCodeStrategy.cs` |
| `RendererAdapterGenerator` | `[TuiRenderer(Backend = "...")]` on renderer classes | `*Adapter` static classes with backend metadata | 4 renderer backends |
| `MoodFrameGenerator` | `[MoodFrame("...")]` on enum members | `*FrameDispatch` static classes replacing manual mood switches | `AmbientMascot` frames |

All generators are **opt-in**: they only fire when the corresponding attribute is present. No public API changes; existing hand-written code continues to compile unchanged.

---

## 2. EscapeCodeGenerator

Replaces manual `\x1b[...m` string literals with generated static helpers.

### Attributes

```csharp
// src/Harbor.Abstractions.Contracts/TerminalEscapeAttribute.cs
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class TerminalEscapeAttribute : Attribute
{
    public string ClassName { get; set; } = "EscapeCodes";
    public string? Namespace { get; set; }
    public string ResetMember { get; set; } = "Reset";
}
```

### Annotated enums

```csharp
// src/Harbor.Terminal.Abstractions/StyleFlag.cs
[TerminalEscape]
public enum StyleFlag : byte { Reset = 0, Bold = 1, Dim = 2, Italic = 3, ... }

// src/Harbor.Terminal.Abstractions/Color8Bit.cs
[TerminalEscape]
public enum Color8Bit : byte { Black = 0, Red = 1, ... }

// src/Harbor.Terminal.Abstractions/CursorDirection.cs
[TerminalEscape]
public enum CursorDirection : byte { Up = 1, Down = 2, Forward = 3, ... }
```

### Generated output

For each annotated enum the generator emits a `.g.cs` file containing:

- **`StyleFlag`** → `StyleFlagEscapeCodes` with `const string` members (`Reset`, `Bold`, ...) and a `Combine(StyleFlag)` method.
- **`Color8Bit`** → `Color8BitEscapeCodes` with `Foreground(Color8Bit)` and `Background(Color8Bit)` methods.
- **`CursorDirection`** → `CursorDirectionEscapeCodes` with `Cursor(CursorDirection, int count = 1)`.

Generated files land next to the consuming assembly under `obj/Generated/Harbor.CodeGen/`.

### Usage

```csharp
using Harbor.Terminal.Abstractions;

// Before (manual)
writer.Write($"\x1b[1m{text}\x1b[0m");

// After (generated)
writer.Write($"{StyleFlagEscapeCodes.Bold}{text}{StyleFlagEscapeCodes.Reset}");
```

### Constraints

- Generated code is AOT-safe: no reflection, no `Activator.CreateInstance`.
- All strings are compile-time literals or interpolated from enum values — no runtime lookup tables.
- The generator does **not** touch truecolor (`38;2;R;G;B`) or DEC private-mode sequences; those remain in `AnsiEscapeStrategy`.

---

## 3. RendererAdapterGenerator

Generates per-backend adapter metadata for renderers annotated with `[TuiRenderer]`.

### Attributes

```csharp
// src/Harbor.Abstractions.Contracts/TuiRendererAttribute.cs
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TuiRendererAttribute : Attribute
{
    public string Backend { get; set; } = string.Empty;
    public string? ContextType { get; set; }
    public string? ArgsFormatter { get; set; }
}
```

### Annotated renderers

```csharp
[TuiRenderer(Backend = "ansi")]
public class AnsiTuiRenderer : AnsiPlainTuiRenderer { ... }

[TuiRenderer(Backend = "plain")]
public class PlainTuiRenderer : AnsiPlainTuiRenderer { ... }

[TuiRenderer(Backend = "cellforge")]
public class CellForgeTuiRenderer : BaseTuiRenderer { ... }

[TuiRenderer(Backend = "nickconsoleex")]
public class NickConsoleExTuiRenderer : BaseTuiRenderer { ... }
```

### Generated output

For each renderer the generator emits `<ClassName>Adapter` with:

- `BackendId` — stable string id.
- `CursorFrameBoundary` — whether the backend owns the terminal cursor across frames.
- `WriteFramePrologue(TextWriter)` / `WriteFrameEpilogue(TextWriter)` — cursor show/hide guards.
- `IsFrameBoundary(TextWriter)` — frame-boundary probe.

---

## 4. MoodFrameGenerator

Generates mood-to-frame dispatch tables replacing manual `switch` expressions.

### Attributes

```csharp
// src/Harbor.Abstractions.Contracts/MoodFrameAttribute.cs
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class MoodFrameAttribute : Attribute
{
    public string FrameBank { get; }
    public string? PanelEars { get; set; }
    public string? PanelPaws { get; set; }

    public MoodFrameAttribute(string frameBank) => FrameBank = frameBank;
}
```

### Annotated enum members

```csharp
// src/Harbor.Tui.CellForge/Widgets/AmbientMascot.cs
public enum MascotMood
{
    [MoodFrame("IdleFrames", PanelEars = "EarsUp", PanelPaws = "PawsLoaf")]
    Idle = 0,

    [MoodFrame("WorkingFrames", PanelEars = "EarsUp", PanelPaws = "PawsKnead")]
    Working = 1,
    ...
}
```

### Generated output

`MascotMoodFrameDispatch` with:

- `FramesOf(MascotMood mood)` — `string[]` frame bank.
- `PanelEarsOf(MascotMood mood)` — optional panel ear row.
- `PanelPawsOf(MascotMood mood)` — optional panel paw row.

---

## 5. Adding a new generator

1. Create a new `.cs` file in `src/Harbor.CodeGen/` implementing `IIncrementalGenerator`.
2. Define the trigger attribute in `src/Harbor.Abstractions.Contracts/`.
3. Register the generator in `src/Harbor.CodeGen/Harbor.CodeGen.csproj` — the project is already marked as `<IsRoslynComponentPackage>true</IsRoslynComponentPackage>` with `<RoslynComponent>Harbor.CodeGen</RoslynComponent>`.
4. Annotate target types in the consuming project.
5. Add unit tests in `tests/Harbor.Tui.Tests/` (or a dedicated generator test project).

### Example skeleton

```csharp
// src/Harbor.CodeGen/MyNewGenerator.cs
namespace Harbor.CodeGen;

[Generator]
public sealed class MyNewGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var symbols = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.SemanticModel.GetDeclaredSymbol(ctx.Node)!)
            .Where(static s => s.GetAttributes().Any(ad => ad.AttributeClass?.Name == nameof(MyAttribute)));

        context.RegisterSourceOutput(symbols, static (spc, symbol) =>
        {
            spc.AddSource($"MyGen_{symbol.Name}.g.cs", SourceText.From(Generate(symbol), Encoding.UTF8));
        });
    }

    private static string Generate(INamedTypeSymbol symbol) => /* ... */;
}
```

---

## 6. Testing

Generator tests live in `tests/Harbor.Tui.Tests/GeneratedEscapeCodeTests.cs`. They verify the generated output against pinned expected strings:

```csharp
[Test]
public async Task StyleFlagEscapeCodes_Bold_ReturnsCorrectSequence()
{
    await Assert.That(StyleFlagEscapeCodes.Bold).IsEqualTo("\x1b[1m");
}
```

Run generator tests:

```bash
dotnet test tests/Harbor.Tui.Tests -c Release --no-build
```

---

## 7. Golden-frame regression

Renderer output is pinned against committed golden frames in `tests/Harbor.Tui.RendererTests/GoldenFrames/`. Regenerate after intentional visual changes:

```bash
HARBOR_UPDATE_GOLDEN=1 dotnet test tests/Harbor.Tui.RendererTests -c Release --no-build
```

Golden files:

| Backend | Golden file |
|---------|-------------|
| ansi | `ansiplain-ansi.golden.txt` |
| plain | `ansiplain-plain.golden.txt` |
| cellforge | `cellforge.golden.txt` |
| nickconsoleex | `nickconsoleex.golden.txt` |

---

## Acceptance checklist

- [x] `EscapeCodeGenerator` emits `*EscapeCodes` for `StyleFlag`, `Color8Bit`, `CursorDirection`
- [x] `RendererAdapterGenerator` emits `*Adapter` for `AnsiTuiRenderer`, `PlainTuiRenderer`, `CellForgeTuiRenderer`, `NickConsoleExTuiRenderer`
- [x] `MoodFrameGenerator` emits `*FrameDispatch` for `MascotMood`
- [x] All generated code compiles with `dotnet build --warnaserror` (0 warnings)
- [x] Unit tests in `tests/Harbor.Tui.Tests/GeneratedEscapeCodeTests.cs` pass (13/13)
- [x] Golden-frame tests in `tests/Harbor.Tui.RendererTests/` pass (5/5)
- [x] No breaking changes to public API
- [x] All generators are opt-in via attributes
