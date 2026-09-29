# Harbor Design System (HDS)

## Resource type reference table

Every HDS resource key has a fixed XAML type. Supplying the wrong type in a
`<Setter Value="...">` or `<StaticResource>` causes runtime `InvalidCastException`
under compiled XAML (and silent failures without it).

| Key pattern | XAML type | Example |
| --- | --- | --- |
| `Space*`, `Width*`, `Height*`, `FontSize*`, `IconSize*` | `<x:Double>` | `<x:Double x:Key="Space4">16</x:Double>` |
| `Radius*` | `<CornerRadius>` | `<CornerRadius x:Key="RadiusMd">6</CornerRadius>` |
| `Shadow*` | `<BoxShadows>` (plural) | `<BoxShadows x:Key="ShadowSm">0 2 8 0 #20000000</BoxShadows>` |
| `Duration*`, `Motion*`, `Ease*` | `<sys:TimeSpan>` | `<sys:TimeSpan x:Key="MotionFast">0:0:0.150</sys:TimeSpan>` |
| `Color*` | `<Color>` | `<Color x:Key="MochaBase">#1E1E2E</Color>` |
| `*Brush` | `SolidColorBrush` / `LinearGradientBrush` | `<SolidColorBrush x:Key="BgAppBrush" Color="{StaticResource MochaBase}"/>` |
| `Font*` | `<FontFamily>` | `<FontFamily x:Key="FontUi">Inter</FontFamily>` |
| `Border*Width` | `<Thickness>` | `<Thickness x:Key="BorderWidthHairline">0,0,0,1</Thickness>` |
| `Ic*` (IconGeometry) | `<StreamGeometry>` | `<StreamGeometry x:Key="IcAdd">M12,4L12,20M4,12L20,12</StreamGeometry>` |

### Rules

- **NEVER** use `string` in a `Setter Property="BoxShadow"` — only `BoxShadows` objects.
- Strings work **only** in inline attributes (`BoxShadow="0 2 8 0 #20000000"`) where
  Avalonia knows the target type. In a `ResourceDictionary` the target type is unknown,
  so the value is stored as plain text and crashes at runtime when a `Setter` tries
  to assign it to `BoxShadows`.
- Multi-layer shadows use comma syntax inside a single `BoxShadows` value:
  `<BoxShadows x:Key="ShadowFocusRing">0 8 32 -4 #30000000, 0 0 0 3 #335E6AD2</BoxShadows>`.

## Cascade order (App.axaml)

```
Application.Resources.MergedDictionaries:
  [0] Themes/Hds/BaseTokens.axaml       ← metrics + motion + typography
  [1] Themes/Hds/<ActiveTheme>.axaml    ← ONLY ONE — swapped in-place by ThemeService
  [2] Themes/Hds/Icons.axaml            ← icon geometry

Application.Styles:
  FluentTheme → Themes/AppStyles.axaml → Views/Components/HdsStyles/*.axaml
               → Themes/Hds/Typography.axaml
```

`ThemeService.ApplyHds` replaces slot `[1]` **in-place** (`merged[i] = new ResourceInclude(...)`)
so `DynamicResource` bindings keep working without a full resource refresh.

## C# token classes — FORBIDDEN

Any class named `*Tokens.cs`, `*Theme.cs`, `*Palette.cs` in the UI layer is
**architecturally forbidden**. The source of truth is the XAML `ResourceDictionary`.

Reason: dual ownership between C# and XAML causes sync drift, memory leaks on
theme switch (C# brush is not subscribed to theme-change), and AOT breaks
(XamlX IL vs reflection `GetValue` from `ResourceProvider`).

Enforced by `ThemeTokenDuplicationGuardTests`: a hex value declared anywhere under
`Themes/` must not be typed again in `apps/Harbor.App.Avalonia/**/*.cs`, and a
stricter ratchet test rejects *any* colour literal in that C# tree. `HdsThemeCatalog`
stays on the right side of this rule — it is an index that **reads** the dictionaries
via `ResourceInclude`, not a second declaration of their values.

## Palette index — `HdsThemeCatalog`

`apps/Harbor.App.Avalonia/Themes/HdsThemeCatalog.cs` lists the palette names and
resolves each one's preview (`AppBackgroundBrush` / `AccentBrush` / `TextBrush` plus
its variant) out of the dictionary itself — `HdsThemeCatalog.ReadAll()` for the list,
`HdsThemeCatalog.Find(name)` for one. Settings binds the result through
`ThemeSettingsViewModel.AvailableThemes`; it holds no colour of its own.

Each palette declares which `ThemeVariant` it is designed for:

```xml
<x:String x:Key="HdsThemeVariant">Dark</x:String>
```

so the light/dark answer belongs to the palette rather than to a hand-written array
in the view-model. `HdsThemeCatalogParityTests` pins the name list to the folder
(a palette file is a dictionary merging `BaseTokens.axaml` — the same derivation
`ThemeParityTests` uses) and asserts every palette declares a real `ThemeVariant`.

`HdsThemeCatalog.PaletteNames` is a list rather than a directory scan because a
compiled Avalonia app embeds no XAML source and `avares://` cannot be enumerated —
which is exactly why the parity test has to exist.

## AutomationId naming convention

Every interactive element uses `Zone_Element` naming:

```
Rail_BoardButton
Rail_SearchButton
Rail_DiffButton
Rail_ThemeButton
Rail_SettingsButton
Tab_Chat
Tab_Code
```

## Legacy alias map

Legacy keys kept for backward compatibility with views that have not yet migrated
to semantic HDS names:

| Legacy key | Semantic key |
| --- | --- |
| `TextBrush` | `TextPrimaryBrush` |
| `TextMutedBrush` | `TextSubtleBrush` |
| `AccentBrush` | `AccentPrimaryBrush` |
| `AccentPressedBrush` | `AccentMutedBrush` |
| `BorderStrongBrush` | `BorderStrongBrush` (same) |
| `HoverBackgroundBrush` | `BgHoverBrush` |
| `SelectedBackgroundBrush` | `BgSelectedBrush` |
| `AppBackgroundBrush` | `BgAppBrush` |
| `CardBackgroundBrush` | `BgPanelBrush` |
| `SidebarBackgroundBrush` | `BgRailBrush` |
| `PanelBackgroundBrush` | `BgPanelBrush` |
| `StatusBarBackgroundBrush` | `BgPanelElevatedBrush` |
| `MochaBase` | `MochaBase` (same) |
| `MochaMantle` | `MochaMantle` (same) |
| `MochaCrust` | `MochaCrust` (same) |
| `InputCornerRadius` | `RadiusMd` |
| `CardCornerRadius` | `RadiusLg` |
| `ButtonCornerRadius` | `RadiusMd` |
| `FontMono` | `FontMono` (same) |
| `CodeFont` | `FontMono` (same) |
