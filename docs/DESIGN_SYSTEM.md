# Harbor Design System (HDS v1) — theme guide

Harbor.DesignSystem is the standalone design-system package behind every Harbor
surface: terminal renderers (CellForge, ConsoleEx), the Avalonia desktop app,
WPF/MAUI/Blazor clients. One design system, every renderer.

- Package: `Harbor.DesignSystem` — zero dependencies, MIT, AOT-clean.
- API reference: [DESIGN_SYSTEM_API.md](DESIGN_SYSTEM_API.md) (generated from XML doc comments — do not edit).
- Theme JSON schema: [schemas/harbor-theme.schema.json](schemas/harbor-theme.schema.json).
- Example themes: [themes/harbor-dark.json](themes/harbor-dark.json), [themes/harbor-light.json](themes/harbor-light.json), [themes/harbor-warm.json](themes/harbor-warm.json).

> Compat note: the color/cell primitives (`RgbColor`, `PackedColor`, `CellStyle`,
> `StyleAttr`, `ChatPalette`) keep their historical `Harbor.Ui.Framework.*`
> namespaces for source compatibility with existing Harbor code; they physically
> live in the `Harbor.DesignSystem` assembly.

## Install and quick start

```sh
dotnet add package Harbor.DesignSystem
```

```csharp
using Harbor.DesignSystem;

// Read tokens for the active theme
RgbColor accent = TerminalColorPalette.Accent;
int panelPadding = DesignTokens.Space16;

// Swap the theme at runtime (atomic, raises TerminalColorPalette.ThemeChanged)
TerminalColorPalette.Apply(HarborTheme.HarborWarm);

// React to theme switches (re-project derived styles)
TerminalColorPalette.ThemeChanged += (_, _) => ReprojectStyles();
```

A runnable proof that a consumer needs nothing else lives in
`samples/designsystem-consumer/` — it references only `Harbor.DesignSystem`,
prints the active theme, and asserts the package assembly carries zero
`Harbor.*` assembly references.

## Write a theme

Themes are JSON files in the marketplace format. Every color slot is optional —
omitted slots merge over the currently active theme, so an override file can
tweak two accents without redefining the catalog:

```json
{
  "name": "sunset",
  "accent": "#ff8800",
  "background": "#10080a"
}
```

Rules:

- **Hex forms**: `#RRGGBB`, `#RGB`, with or without the leading `#`
  (`ThemeJson.TryParseHex`).
- **Slots**: `accent`, `success`, `warning`, `error`, `tool`, `system`, `user`,
  `background`, `panel`, `surface`, `surface2`, `border`, `muted`, `text` — see
  the [slot reference](DESIGN_SYSTEM_API.md#theme-slots-hds-v1) with dark-mode
  reference values.
- **Validation is fatal, lint is not.** Malformed JSON or an invalid hex value
  fails the load with a per-slot error (`invalid hex for 'accent': #nope`) —
  the previously applied theme stays active, nothing crashes. Unknown
  properties and WCAG contrast concerns produce non-fatal warnings
  (`ThemeParseResult.Warnings`).
- **Contrast lint**: body text below WCAG AA 4.5:1 against the background and
  muted text below 3:1 are flagged as warnings — a theme still loads, but the
  author gets told.
- **Editor support**: reference the JSON schema from your theme file
  (`"$schema": "https://harbor-sh.github.io/harbor/schemas/harbor-theme.schema.json"`).
- Keys are matched case-insensitively (PascalCase works); trailing commas are
  tolerated.

Parse/serialize directly through the codec:

```csharp
ThemeParseResult result = ThemeJson.Parse(json, TerminalColorPalette.Current);
if (result.IsSuccess)
    TerminalColorPalette.Apply(result.Theme);
else
    ShowError(result.Error);   // joined fatal errors — never an exception

string roundTrip = ThemeJson.Write(HarborTheme.HarborWarm);
```

## The theme marketplace directory

`ThemeStore` manages `~/.harbor/themes/` (override with `HARBOR_THEMES_DIR`):

- **Scan** lists built-ins first (`HarborTheme.BuiltIn`: dark, light, warm,
  cool), then user `*.json` files sorted by name. A broken file appears as an
  error entry with `Errors` filled — a bad theme never hides the others.
- **SeedBuiltIns** writes the built-ins as editable JSON files (skip-if-exists,
  idempotent) so users start from real examples.
- **Resolve(name)** prefers a user theme over a built-in with the same name —
  drop a `harbor-dark.json` into the directory to override the shipped dark
  theme everywhere the store resolves.

```csharp
var store = new ThemeStore();                 // ~/.harbor/themes
store.SeedBuiltIns();                          // editable starting points
foreach (var entry in store.Scan())
    Console.WriteLine($"{entry.Name} [{entry.Source}] {(entry.IsValid ? "" : string.Join("; ", entry.Errors))}");

if (store.Resolve("harbor-warm") is { } warm)
    TerminalColorPalette.Apply(warm);
```

## Live reload

`ThemeDirectoryWatcher` polls the themes directory (500 ms default) and applies
changed files through `TerminalColorPalette.Apply`; parse failures report via
the error callback and keep the last applied theme. Polling (not
`FileSystemWatcher`) keeps behaviour deterministic across terminals, network
mounts and CI; `Poll()` is public for deterministic tests.

```csharp
using var watcher = new ThemeDirectoryWatcher(
    onApplied: theme => ShowStatus($"theme: live-reload → {theme.Name}"),
    onError:   error => ShowStatus($"! theme: {error}"));
```

The CellForge interactive shell ships the same contract for a single file:
`HARBOR_THEME_FILE`, else `~/.harbor/theme.json` when present
(`JsonThemeLoader` + `ThemeFileWatcher`, which delegate to `ThemeJson`).

## Per-component overrides

Component scopes patch the active theme without cloning it. A
`PartialTheme` overrides individual slots (null inherits); a
`ThemeOverrideSet` maps scope names («sidebar», «composer», «status», …) to
patches. Consumers read the effective theme for their scope:

```csharp
// Dim borders inside the sidebar scope only
var patch = new PartialTheme(Border: new RgbColor(0x2A, 0x2F, 0x3B));
TerminalColorPalette.SetOverrides(new ThemeOverrideSet().With("sidebar", patch));

// In sidebar rendering code:
HarborTheme effective = TerminalColorPalette.EffectiveTheme("sidebar");
DrawBorder(effective.Border);

// Clear all overrides
TerminalColorPalette.SetOverrides(null);
```

`EffectiveTheme(scope)` returns the scope's patch merged over `Current` — with
no scope, no patch, or no override set it returns `Current` unchanged.
`TerminalColorPalette.ThemeChanged` fires after `Apply`/`SetOverrides` so
scoped consumers re-project.

## Best practices

1. **No hardcoded hex in widgets — ever.** Reference `ChatPalette` styles (or
   `TerminalColorPalette` reads for raw tokens). HDS §7.1 names ChatPalette the
   single source of truth for block colors inside the CellForge renderer.
2. **Meet WCAG AA.** Body text ≥ 4.5:1 against its background; the loader lints
   below 4.5:1 for `text` and 3:1 for `muted` — treat warnings as defects
   unless the trade-off is deliberate.
3. **Themes are data, not code.** Ship palette constants (`ColorPalette`) only
   as *source material* for building themes, never as runtime reads — runtime
   reads go through the active theme.
4. **Keep the package dependency-free.** No UI framework, no Harbor.* reference
   may be added to Harbor.DesignSystem (`tests/Harbor.Architecture.Tests`
   enforces an empty Allowed set, `StandalonePackageTests` pins zero Harbor
   assembly refs).
5. **AOT-safe serialization only.** Theme JSON goes through the
   source-generated `ThemeJsonContext`; no reflection-based serializers.
6. **Restore global state in tests.** `TerminalColorPalette` is process-global;
   palette-mutating test classes use `[NotInParallel("terminal-color-palette")]`
   and restore `HarborDark` in `[After(Test)]`.

## Maintaining the docs

- Token/slot/API reference: `dotnet run -p tools/DesignSystemDocGen -c Release`
  regenerates `docs/DESIGN_SYSTEM_API.md` and the example themes from XML doc
  comments + schema. Undocumented token fields fail the docgen build.
- Package dry-run: `dotnet pack src/Harbor.DesignSystem -c Release` — the
  produced nuspec must keep an empty dependency group and the MIT license
  expression.
