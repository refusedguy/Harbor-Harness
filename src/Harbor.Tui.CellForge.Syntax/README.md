# Harbor.Tui.CellForge.Syntax

Syntax-highlighting + terminal-theme vocabulary, extracted verbatim from
`Harbor.Tui.CellForge` ([#1617](https://github.com/refusedguy/Harbor-Harness/issues/1617))
the way `Harbor.Tui.CellForge.Engine` was split out in issue #33.

## What lives here

- **Fenced-code highlighting** (`Chat/Rendering/`, namespaces stay
  `Harbor.Tui.CellForge.Rendering`): `CodeSyntaxTokenizer` (the instantiable
  span tokenizer, issue #198) + `ILanguageSupport` / `LanguageSupportBase` /
  `BuiltinLanguageSupports` (six builtin language groups) +
  `LanguageSupportRegistry` + `CodeHighlightPalette` (injectable palette) +
  `CodeSpan` and the `CodeTokenizer` back-compat static façade.
- **Terminal theme plumbing** (`Chat/Widgets/`, namespace stays
  `Harbor.Tui.CellForge.Widgets`): `JsonThemeLoader` (pure JSON theme parser,
  disk reads stay on the `IThemeStore` port per #668) and `ThemeFileWatcher`
  (polling live-reload that applies through `TerminalColorPalette.Apply`,
  #479-A6).

## Invariants

- Namespaces are intentionally unchanged (`Harbor.Tui.CellForge.Rendering` /
  `.Widgets`) — consumers keep their usings; only `ProjectReference`s change.
- No Engine reference: the UIR alias (CellStyle from
  `Harbor.Ui.Framework.Rendering`) and the engine-owned verbatim copies of the
  same names stay disambiguated exactly as in the CellForge assembly, by the
  alias pins every tokenizer file already carries.
- Reads themes through the `IThemeStore` port (DesignSystem), never
  `System.IO.File` — both `ThemeStoreSeamRules` and
  `PresentationCapabilityRules` keep this file-legal.
- AOT/trim clean.

## Public API

Two clusters, one per directory. Namespaces stay `Harbor.Tui.CellForge.Rendering`
/ `.Widgets` — consumers keep their usings; only `ProjectReference`s change.

### `Rendering/` — fenced-code highlighting

`CodeSyntaxTokenizer` (`Rendering/CodeSyntaxTokenizer.cs`) is the instantiable
span tokenizer (#198): `public CodeSyntaxTokenizer(CodeHighlightPalette? palette
= null, LanguageSupportRegistry? registry = null)`; a `null` palette means the
live theme-aware `CodeHighlightPalette.Default` projection, an explicit one pins
styles. `Tokenize(string code, string? language)` / `TokenizeLine(...)` return
`List<CodeSpan>` (`readonly record struct CodeSpan(string Text, CellStyle
Style)`). `CodeSyntaxTokenizer.Default` backs the `CodeTokenizer` static
back-compat façade (`Tokenize` / `TokenizeLine` / `HighlightFenceBodies`).
`ILanguageSupport` (open extension: new language = new file, no edits to the
registry), `LanguageSupportBase`, `BuiltinLanguageSupports` (six builtin
language groups), `LanguageSupportRegistry.Default` / `.Resolve(language?)`, and
the injectable `CodeHighlightPalette` round it out.

### `Widgets/` — terminal theme plumbing

`JsonThemeLoader` is the pure parser:
`Default` (built-in harbor-terminal palette),
`Parse(string json)` / `Parse(string json, HarborTheme fallback)` /
`Parse(string json, HarborTheme fallback, IReadOnlyDictionary<string, string>?
variableOverrides)` — each returning `Result<HarborTheme>` with missing keys
merged over the fallback — and `internal TryParseHex(string, out RgbColor)`.
`ThemeFileWatcher` is the polling live-reload (#479-A6); it applies through
`TerminalColorPalette.Apply` and reads only via the `IThemeStore` port (#668).

## Wiring

**No activation of any kind.** There is no DI module, no `HARBOR_*` env var and
no `HARBOR_TUI` value here — this is a Presentation-sibling library compiled
into whatever references it. The CellForge root binds it
(`Harbor.Tui.CellForge` → `CodeTokenizer` for `AssistantMarkdownBlock`),
`Harbor.App.Cli`, and `Harbor.Tui.CellForge.Tests` reference it directly; the
reverse edge set is pinned by `tests/Harbor.Architecture.Tests`. Live theme
state stays in `Harbor.DesignSystem` (`TerminalColorPalette.Current`) — this
assembly holds none of it, so nothing here registers anything at startup.
Friend visibility: `InternalsVisibleTo` to `Harbor.Tui.CellForge.Tests` only
(`TryParseHex` tests).

## Usage

Parsing a theme document with merge + variable overrides (live-reload path):

```csharp
using CSharpFunctionalExtensions;
using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;

Result<HarborTheme> merged =
    JsonThemeLoader.Parse(themeJson, TerminalColorPalette.Current, overrides);
HarborTheme active =
    merged.IsSuccess ? merged.Value : TerminalColorPalette.Current;
```

Tokenizing a fenced body (free functions over `Tokenize`, as
`AssistantMarkdownBlock` drives `HighlightFenceBodies`):

```csharp
using Harbor.Tui.CellForge.Rendering;

var tokenizer = new CodeSyntaxTokenizer(
    palette: null,                       // live theme-aware projection
    registry: LanguageSupportRegistry.Default);
List<CodeSpan> spans = tokenizer.Tokenize("var x = 1;", "c#");
```

## Dependencies

| Reference | Why |
|-----------|-----|
| `Harbor.Extensions` | the `Ensure` guard helpers (`Harbor.Abstractions.Extensions` namespace kept as `RootNamespace`) |
| `Harbor.Ui.Framework.Rendering` | the span paint vocabulary — `UIR.CellStyle` and `MdLine`/`MdStyle` (the `#436` pin every tokenizer file carries) |
| `Harbor.DesignSystem` | the theme documents this assembly parses and applies: `HarborTheme`, `TerminalColorPalette`, `IThemeStore`, `ThemeJson`, `RgbColor` |
| `CSharpFunctionalExtensions` (NuGet, direct) | the `Result` railway in `JsonThemeLoader` / `ThemeFileWatcher` |

Referenced by `Harbor.Tui.CellForge`, `Harbor.App.Cli`, and
`tests/Harbor.Tui.CellForge.Tests`; no Engine reference — same-name vocabulary
(`CellStyle`, `RgbColor` ports) is disambiguated by the file-local alias pins,
as it was inside the CellForge assembly.

## Known limitations

- **Not a renderer.** No `ITuiRenderer`, no screen building, no render loop;
  span painting happens in `Harbor.Tui.CellForge` (`ChatScreen`, overlay
  stack), which consumes this assembly's spans.
- **Fenced-input contract.** `HighlightFenceBodies` walks an
  already-rendered `IReadOnlyList<MdLine>` list — it is not a markdown parser.
- **Language registry is closed at the type level.** A new language is a new
  `ILanguageSupport` implementation *in this assembly* (the #198 open shape is
  file-level, not plugin-level).
- **Theme file I/O is elsewhere.** `JsonThemeLoader` never touches disk (its old
  public `LoadFile` was retired by #668); loading and watching files is the
  `IThemeStore` / `ThemeFileWatcher` caller's job.
- `IsAotCompatible` is load-bearing: no reflection, no `JsonSerializer` without
  a source-generated context (theme JSON parses through `ThemeJson`'s own
  `ThemeJsonContext`).

## See also

- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the renderer this assembly was split out of
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — the sibling substratum split (issue #33 shape)
- [`../Harbor.DesignSystem/README.md`](../Harbor.DesignSystem/README.md) — the theme documents parsed here
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
