# i18n string inventory (issue #434, slice 1: inventory + catalogue home)

Scope: **inventory of hardcoded user-visible strings + where the shared
catalogue lives**. Shipping locales (`en` + second), missing-key fallback
tests, the extraction itself, and the `InvariantCulture` fixes are separate
implementation slices. Measured on `origin/dev` (`ff4c5ba6`).

Decision on the catalogue home: [ADR-015](adr/ADR-015-i18n-string-catalogue-home.md).

## Method

Counts are candidate **occurrences**, not final keys — extraction will dedupe
(e.g. `"Cancel"` x10, icon keys like `"ThemeIcon"` x3) and drop non-UI hits
(chunk magic `"IHDR"`, env-var names, log templates). Reproduce:

```bash
# XAML attributes (Avalonia)
grep -rhoP 'Text="[^"]+"' apps/Harbor.App.Avalonia --include="*.axaml" | wc -l
grep -rhoP 'Content="[^"]+"' apps/Harbor.App.Avalonia --include="*.axaml" | wc -l
# C# candidates outside doc comments (any surface)
grep -rnP '"[A-Z][^"]{4,}"' <dir> --include="*.cs" | grep -vP ":\d+:\s*///" | wc -l
# Culture audit
grep -rn "InvariantCulture" src/ apps/ --include="*.cs" | wc -l
```

## Numbers

| Surface | What was counted | Occurrences |
|---|---|---:|
| Avalonia XAML (64 `.axaml` files) | `Text="…"` | 233 |
| Avalonia XAML | `Content="…"` | 65 |
| Avalonia XAML | `Header="…"` | 5 |
| Avalonia XAML | `Watermark` / `PlaceholderText` | 19 |
| Avalonia XAML | `ToolTip.Tip="…"` | 20 |
| Avalonia C# (code-behind + ViewModels) | string-literal candidate lines | 263 |
| CellForge TUI (`src/Harbor.Tui.CellForge`) | non-comment candidate lines | 134 |
| `Harbor.Terminal.Abstractions` views/VMs | non-comment candidate lines | 12 |
| `Harbor.Tui.AnsiPlain` | non-comment candidate lines | 0 (22 hits, all `///` doc comments) |
| `Harbor.Tui.NickConsoleEx` + `Harbor.Tui.Notifications` | non-comment candidate lines | 12 |
| `Harbor.Ui.Framework.*` (mappers, rows, status text, widgets) | non-comment candidate lines | 160 |
| CLI (`apps/Harbor.App.Cli`) | non-comment candidate lines | 449 |
| Existing `.resx` (`ErrorMessages` + `LogMessages`, 5 entries each) | log/diagnostic text, **not UI** | 10 |

Total hardcoded UI candidates pre-dedupe: **~1 370 occurrences**
(342 XAML attributes + ~1 030 C# candidate lines). Pending-string count for
the extraction slice: same ~1 370 occurrences; final key count will be lower
after dedupe.

## High-traffic groups (extraction order)

| Group | Files |
|---|---|
| Onboarding wizard | `apps/Harbor.App.Avalonia/Views/OnboardingWindow.axaml` + `ViewModels/OnboardingViewModel.cs`, CellForge `Onboarding/` + `SetupChecklistOverlay.cs` (`"Setup guide"`, `"Esc to close · /setup to reopen"`, `"Welcome to Harbor"`, `"Pick a model — {providerId}"`) |
| Settings view | `Views/SettingsView.axaml` + `ViewModels/SettingsViewModel.cs` + `ViewModels/ThemeSettingsViewModel.cs` (`"Toggle Theme"`, `"Switch between dark and light"`) |
| Command palette | `Views/CommandPaletteView.axaml` + `ViewModels/CommandPaletteViewModel.cs`, CellForge `CommandPaletteView` (`"Commands"`, `"Type a number or id…"`) |
| Status bar | `Views/Shell/StatusBarView.axaml`, `Harbor.Ui.Framework.State/State/StatusBarText.cs`, `Harbor.Terminal.Abstractions/Views/StatusBarView.cs` (`"Status Bar"`, token `K`/`M` cells) |
| Tool-call card verbs | `Views/Controls/ToolCallCardView.axaml`, CellForge `ToolCallBlock.cs`, `Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallViewModel.cs`, `Converters/StatusMappers.cs` |
| Error / toast messages | `Views/ToastNotificationsView.axaml`, `ViewModels/ChatViewModel.cs` (`_toasts.Show(…)`), `ViewModels/CodeEditorViewModel.cs` (`"Save failed…"`, `"File not found…"`) |
| CLI slash-command help | `apps/Harbor.App.Cli/Repl/SlashCommandDispatcher.cs`, `Repl/ReplCommandHost.cs`, `Commands/HelpVerbs.cs` (`"Usage:"`, `"Switch Session"`, `"New Session"`, `"No sessions."`) |

## `InvariantCulture` audit (user-facing subset)

57 `InvariantCulture` hits repo-wide (`src/` + `apps/`). User-facing
formatting sites that the implementation slice must switch to the current
culture (dates, numbers, token counts, durations):

- `src/Harbor.Ui.Framework.State/State/StatusBarText.cs:46-47` — token `K`/`M`
- `src/Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs:133-134` — durations `ms`/`s`
- `src/Harbor.Ui.Framework.Projection/Projection/PanelRows.cs:642-645` — counts `K`/`M`
- `src/Harbor.Tui.CellForge/Chat/Widgets/SideBarView.cs:637-639` — token `k`/`M`
- `src/Harbor.Tui.CellForge/Chat/Widgets/SessionTabStripPanel.cs:551` — `"+" + hidden`
- `src/Harbor.Tui.CellForge/Chat/Widgets/RetryCountdown.cs:81` — seconds + `"s"`

The remainder are wire formats (JSON/SSE payloads, ids, paths, log templates)
that must stay invariant — each gets a one-line justification comment in the
implementation slice, not a culture switch.

## Notes

- Blazor lives in `contrib/` (unmaintained, not in `Harbor.slnx`, not compiled
  by CI). The `IStringLocalizer`-over-shared-catalogue mechanism is decided in
  ADR-015 for when it returns; no Blazor strings were inventoried here.
- Existing precedent: `CoreResources.GetLog/GetError` already falls back to the
  key (`?? name`) — the UI catalogue keeps that shape with `en` as the
  fallback locale.
- Locale selection (`HARBOR_LOCALE` config/env) is decided in ADR-015;
  implementing it is out of scope here.
