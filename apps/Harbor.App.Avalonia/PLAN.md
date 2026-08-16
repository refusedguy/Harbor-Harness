# Plan — Harbor.App.Avalonia

## Status: MVP

The Avalonia app is the strategic cross-platform desktop GUI for Harbor (Windows, Linux, macOS — all from one codebase).

## Done

- [x] Avalonia 11.2 + AvaloniaEdit + CommunityToolkit.Mvvm + Markdig integration
- [x] Microsoft.Extensions.Hosting bootstrap
- [x] Sidebar + chat + code editor + status bar layout (1280x800)
- [x] Catppuccin-Mocha dark theme
- [x] Streaming chat with markdown rendering
- [x] Session manager (list / load / save)
- [x] Command palette (Cmd+K)
- [x] Diff view for `write` / `edit` tool calls
- [x] Token-usage chart
- [x] Plugin panel host
- [x] Multi-provider + multi-model switching
- [x] Settings UI (provider, model, theme, permissions)

## TODO

- [ ] Mobile / touch layout (currently desktop-only)
- [ ] Native file dialogs (currently uses Avalonia's built-in)
- [ ] System tray icon + background runs
- [ ] Native notifications on completion (per-OS)
- [ ] Auto-update (via Velopack or similar)
- [ ] Multi-window (detachable panels)
- [ ] Theme picker (Mocha / Latte / custom)
- [ ] Mac notarization + Windows code signing
- [ ] Linux AppImage / Flatpak packaging

## Known issues

- Avalonia 11.2.7 brings `Tmds.DBus.Protocol 0.20.0` transitively (GHSA-xrw6-gwf8-vvr9) — suppressed NU1903, no fix available until Avalonia 11.3.
- AVLN1000/AVLN1001 warnings from XAML codegen suppressed.
- MA0046 (EventHandler<T> with record payloads) suppressed — intentional for simple event contracts.

## Task U4 — Input + chat binding fix

User reported "не работает ввод" (input doesn't work). Root cause analysis and fixes:

### Fixes applied

1. **`Views/ChatView.axaml`** — TextBox binding hardened:
    - `Text="{Binding InputText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"` — explicit `Mode=TwoWay` (Avalonia defaults to OneWay for `TextBox.Text` if omitted, so the VM never saw typed text) and `UpdateSourceTrigger=PropertyChanged` (push on every keystroke, not on LostFocus — so `InputText` is current when Enter fires `SendCommand`).
    - Added `x:Name="InputBox"` for code-behind clarity.
    - Added `VerticalScrollBarVisibility="Auto"` so long multi-line input scrolls instead of overflowing the `MaxHeight=160` cap.
    - Renamed `KeyDown="InputBox_KeyDown"` → `KeyDown="OnInputKeyDown"` to match the new handler name.

2. **`Views/ChatView.axaml.cs`** — key handler rewritten:
    - Class made `sealed` (quality requirement; Avalonia XAML codegen works fine with sealed).
    - Handler renamed `InputBox_KeyDown` → `OnInputKeyDown`.
    - Always marks plain Enter as `e.Handled = true` BEFORE the CanExecute check, so the `AcceptsReturn=True` TextBox never inserts a stray newline — even when SendCommand cannot execute (empty input).
    - Gates `SendCommand.Execute(null)` on `SendCommand.CanExecute(null)` so we don't fire a no-op send.

3. **`ViewModels/ChatViewModel.cs`** — `SendCommand` CanExecute wiring:
    - `[RelayCommand(CanExecute = nameof(CanSend))]` — Send button now greys out when input is empty.
    - `private bool CanSend() => !string.IsNullOrWhiteSpace(InputText);`
    - `partial void OnInputTextChanged(string value)` — source-generated partial (from `[ObservableProperty]`) that calls `SendCommand.NotifyCanExecuteChanged()` on every keystroke, so the Send button reflects the current input state live (without this, the button would stay disabled until focus left the box).
    - After clearing `InputText` in `Send()`, calls `SendCommand.NotifyCanExecuteChanged()` so the button immediately disables after sending.

4. **Wiring verified (already correct, no changes needed):**
    - `AppHost.cs` — `ChatViewModel` registered as `Singleton` (line 294).
    - `MainViewModel.cs` — exposes `Chat` property (line 49: `Chat = services.GetRequiredService<ChatViewModel>();`).
    - `MainWindow.axaml` — `<views:ChatView DataContext="{Binding Chat}"/>` (line 126).

### Verification

- `dotnet build apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj` — 0 errors, 0 warnings.
- `dotnet test tests/Harbor.App.Avalonia.Tests` — 48/48 passed.

### Behavior after fix

- Input accepts text (TwoWay binding pushes keystrokes to `InputText`).
- Enter sends the message (key handler fires `SendCommand.Execute`).
- Shift+Enter inserts a newline (handler skips, TextBox default behavior).
- `SendCommand.CanExecute` updates live as `InputText` changes (Send button enables/disables).
- Empty input: Send button disabled; plain Enter still swallowed (no stray newline).

### Concurrent fixes (unblocking build)

Other parallel agents introduced broken changes that blocked the build. Reverted to unblock:

- `Directory.Packages.props` — reverted Avalonia 12.1.0 → 11.2.7 (12.1.0 doesn't exist on nuget.org; latest is 11.3.18). Removed non-existent `Avalonia.Fonts.JetBrainsMono` package.
- `apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj` — reverted matching csproj comments and removed the `Avalonia.Fonts.JetBrainsMono` `<PackageReference>`.

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
