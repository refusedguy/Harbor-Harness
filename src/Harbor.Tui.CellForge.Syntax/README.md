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
