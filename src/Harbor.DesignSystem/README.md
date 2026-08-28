# Harbor.DesignSystem

Standalone design-system package for [Harbor](https://github.com/harbor-sh/harbor) — the HDS v1
token catalog that powers the Harbor terminal renderers, desktop apps, and web surfaces. One
design system, every renderer: ConsoleEx / CellForge TUI, Avalonia, WPF, MAUI, Blazor.

**Zero dependencies.** References no Harbor assembly, no UI framework, no NuGet package — BCL only,
NativeAOT/trim clean.

## Install

```sh
dotnet add package Harbor.DesignSystem
```

## What's inside

| Surface | Type | Purpose |
|---|---|---|
| Design tokens | `DesignTokens`, `Typography` | spacing / radius / font-size / weight scale |
| Color primitive | `Harbor.Ui.Framework.Projection.RgbColor` | canonical 24-bit sRGB value |
| Theme catalog | `HarborTheme` | immutable 15-slot theme record + built-ins (dark, light, warm, cool) |
| Live theme | `TerminalColorPalette` | active-theme accessor; `Apply`/`ThemeChanged` swaps atomically |
| Per-component overrides | `ThemeOverrideSet`, `PartialTheme` | scope-scoped patches over the active theme |
| Cell styles | `ChatPalette`, `CellStyle`, `PackedColor`, `StyleAttr` | terminal projection of the catalog |
| Palette constants | `ColorPalette` | Catppuccin Mocha/Latte constants |
| Accessibility | `Accessibility` | WCAG luminance/contrast math |
| Auto-theme | `TerminalBackgroundProbe` | OSC 11 background detection |

> Note: `RgbColor`, `CellStyle`, `PackedColor`, `StyleAttr` keep their historical
> `Harbor.Ui.Framework.*` namespaces so existing Harbor code compiles unchanged;
> they physically live in this assembly.

## Quick start

```csharp
using Harbor.DesignSystem;

// Read tokens for the active theme
RgbColor accent = TerminalColorPalette.Accent;

// Swap theme at runtime
TerminalColorPalette.Apply(HarborTheme.HarborWarm);

// React to theme changes
TerminalColorPalette.ThemeChanged += (_, _) => ReprojectStyles();
```

See `docs/DESIGN_SYSTEM.md` (theme guide, marketplace format, per-component overrides) and
`docs/DESIGN_SYSTEM_API.md` (generated API reference).

## License

MIT — see [LICENSE](../../LICENSE).
