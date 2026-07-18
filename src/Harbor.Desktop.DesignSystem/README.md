# Harbor.Desktop.DesignSystem

Theme tokens as pure C# constants — no XAML, no UI-framework deps. Each
platform app converts the `(byte r, byte g, byte b)` tuples / hex strings to
its own color type at startup (Avalonia `Color`, WPF `Color`, MAUI `Color`,
Blazor CSS).

## What's shared

- **`ThemeTokens`**: full 60-color Catppuccin Mocha + Latte palette as
  `RgbColor` constants.
- **`TypographyTokens`**: font sizes, weights, line heights, letter spacing.
- **`SpacingTokens`**: 2/4/8/12/16/24/32/48/64/96 px scale + corner radius +
  border widths + z-index layers.
- **`AnimationTokens`**: animation durations (Fast=150ms, Normal=300ms,
  Slow=500ms) and easing-curve name constants. The actual easing functions
  live in `Harbor.Desktop.Animations`.
- **`Themes/DarkTheme.cs`** + **`Themes/LightTheme.cs`**: flat
  `Dictionary<string, string>` of hex strings for each theme.
- **`Themes/ThemeManager.cs`**: holds the current theme, persists to
  `~/.harbor/theme.json` (format `{"theme":"dark"}`), exposes the active
  token dictionary.

## Dependency rules

✅ **Allowed**: `Harbor.Desktop.Abstractions` (for `RgbColor` + `ThemeKind`),
`Microsoft.Extensions.Logging.Abstractions`.

❌ **Forbidden**: any UI framework, `System.Drawing`, XAML.

These rules are enforced by `tests/Harbor.Architecture.Tests`.

## Usage example

```csharp
// In apps/Harbor.App.Avalonia/Services/ThemeService.cs
using Harbor.Desktop.Abstractions.Services;
using Harbor.Desktop.DesignSystem.Themes;

public sealed class ThemeService : IThemeService
{
    private readonly ThemeManager _manager;
    private readonly Application _app;

    public ThemeService(ThemeManager manager, Application app)
    {
        _manager = manager;
        _app = app;
        _manager.ThemeChanged += (_, kind) => ApplyToApp(kind);
    }

    public ThemeKind Current => _manager.Current;
    public event EventHandler<ThemeKind>? ThemeChanged;

    public void ApplyDark()  => _manager.ApplyDark();
    public void ApplyLight() => _manager.ApplyLight();
    public void ApplySystem() => _manager.ApplySystem(/* OS check */);
    public void Toggle()     => _manager.Toggle();

    private void ApplyToApp(ThemeKind kind)
    {
        // Swap Avalonia resource dictionaries using _manager.CurrentTokens.
    }
}
```

```json
// ~/.harbor/theme.json
{ "theme": "dark" }
```
