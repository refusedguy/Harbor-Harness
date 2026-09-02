# Plan — Harbor.App.Avalonia

## Status: Active (mainline desktop shell)

The Avalonia app is the strategic cross-platform desktop GUI for Harbor (Windows, Linux, macOS — one codebase) and the mainline for UI work. Currently on **Avalonia 12.1.x** (`Directory.Packages.props:109`).

## Done

- [x] Avalonia 12 + AvaloniaEdit 12 + CommunityToolkit.Mvvm + Markdig integration
- [x] Microsoft.Extensions.Hosting bootstrap, split registration modules (`Hosting/`)
- [x] Sidebar + chat + code editor + status bar layout (1280×800)
- [x] Catppuccin-Mocha dark theme + Latte light (`ThemeSettingsViewModel`, `Ctrl+Shift+T`)
- [x] Streaming chat with role colors and markdown via Markdig AST → Avalonia control projection (`ChatMarkdown.cs`)
- [x] Session manager (list / load / save; per-message persistence through `ISessionStore`)
- [x] Onboarding window using provider preset catalog: connection health check (`IProviderHealthCheck`) + live model lists (PROD-UI-0)
- [x] Provider config VMs: `ProviderConfigViewModel`, `ProviderModelPickerViewModel`
- [x] Command palette (`Ctrl+P`) fuzzy search
- [x] Diff view for file-changing tool calls
- [x] Token-usage chart
- [x] Board view (`Views/Board/`) + focus-session view
- [x] Plugin panel host view (right dock)
- [x] Toast notifications stack
- [x] Desktop event routing into the shared UI framework reducers (`Hosting/UiEventRouter.cs`)
- [x] Tool registry built via `Harbor.Hosting` `ToolsCatalog.CreateToolRegistry` with `HarborToolSetKind.Standard10` (`AppHost.cs:100`)
- [x] API keys resolved through `AuthStore` (`Services/CommonConfigAuthResolver.cs`)

## TODO

- [ ] Mobile / touch layout (currently desktop-only)
- [ ] System tray icon + background runs
- [ ] Native OS notifications on completion (per-OS)
- [ ] Auto-update (via Velopack or similar)
- [ ] Multi-window (detachable panels)
- [ ] Theme picker beyond Mocha/Latte presets (custom palettes)
- [ ] Mac notarization + Windows code signing
- [ ] Linux AppImage / Flatpak packaging
- [ ] Wire plugin panel host to the full panel lifecycle

## Known issues

- AOT/trimming incompatible by nature of Avalonia — desktop build requires JIT.
- Headless Avalonia tests on Avalonia 12 have pre-existing "Stack empty" failures in
  `SetInheritanceParent` (see root AGENTS.md quick state).

## Task U4 — Input + chat binding fix (archived narrative)

User reported "не работает ввод". Root cause and fixes:

1. **`Views/ChatView.axaml`** — TextBox binding hardened: explicit `Mode=TwoWay` +
   `UpdateSourceTrigger=PropertyChanged`; `x:Name="InputBox"`;
   `VerticalScrollBarVisibility="Auto"` under the `MaxHeight=160` cap;
   handler renamed to `OnInputKeyDown`.
2. **`Views/ChatView.axaml.cs`** — sealed class; plain Enter marked handled before
   CanExecute check (no stray newline); send gated on `SendCommand.CanExecute`.
3. **`ViewModels/ChatViewModel.cs`** — `[RelayCommand(CanExecute = nameof(CanSend))]`;
   `OnInputTextChanged` calls `SendCommand.NotifyCanExecuteChanged()` live.

## Next priorities

1. **P0**: Auto-update (Velopack integration)
2. **P1**: Mac notarization + Windows code signing
3. **P1**: System tray + background runs
4. **P2**: Linux AppImage / Flatpak
5. **P2**: Multi-window detachable panels

## See also

- [README.md](README.md) — full app README
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md)
- [../../docs/DESKTOP_APP_PLAN.md](../../docs/DESKTOP_APP_PLAN.md)
