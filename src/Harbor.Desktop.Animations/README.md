# Harbor.Desktop.Animations

Cross-platform animation helpers (easing functions, durations, transition
record types). No UI framework deps; each platform app uses these constants
and delegates to configure its own animation primitives (Avalonia
`Transitions`, WPF `Storyboard`, MAUI `Animation`, Blazor CSS transitions).

## What's shared

- **`EasingFunctions`**: `Linear`, `EaseIn`, `EaseOut`, `EaseInOut`,
  `CubicInOut`, `QuarticOut`, `QuinticInOut`, `Spring` — plus a
  `Resolve(name)` factory that maps design-system easing names
  (e.g. `"cubicInOut"`) to delegates.
- **`AnimationDurations`**: `Instant`, `Fast` (150ms), `Normal` (300ms),
  `Slow` (500ms), `Slower` (800ms), plus convenience aliases (`Fade`,
  `Slide`, `Scale`, `Toast`, `Palette`).
- **`Transitions`**:
    - `FadeTransition` — opacity 0 ↔ 1
    - `SlideTransition` — translate from (offsetX, offsetY) to (0, 0)
    - `ScaleTransition` — scale from `FromScale` to 1.0
    - `ColorTransition` — animate between two `RgbColor` values (used by the
      theme switcher for a smooth color fade)

## Dependency rules

✅ **Allowed**: `Harbor.Desktop.Abstractions` (for `RgbColor`).

❌ **Forbidden**: any UI framework, `System.Drawing`.

These rules are enforced by `tests/Harbor.Architecture.Tests`.

## Usage example

```csharp
// In apps/Harbor.App.Avalonia/Views/ToastNotificationsView.axaml.cs
using Harbor.Desktop.Animations;

var fade = new FadeTransition(AnimationDurations.Fade);
var easing = EasingFunctions.Resolve(fade.EasingName);
// Platform: configure Avalonia Transitions using fade.Duration and easing.
```

```csharp
// Theme-switch color fade
var from = ColorPalette.MochaBase;
var to = ColorPalette.LatteBase;
var fade = new ColorTransition(AnimationDurations.Normal, from, to);
for (double t = 0; t <= 1; t += 0.05)
{
    var color = fade.Interpolate(t);
    // Apply color to app resources.
}
```
