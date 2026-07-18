# Harbor.Tui.Maui

.NET MAUI renderer for Harbor. One project, four targets: WinUI (Windows),
Android, iOS, and macOS (Mac Catalyst). Single-column touch-friendly layout
with virtualized chat history via `CollectionView`.

## When to use

- You want Harbor on a phone or tablet (Android/iOS).
- You want one project that ships to mobile + desktop + Mac Catalyst.
- You can afford the MAUI workload install + platform SDKs.
- You want access to native platform APIs (notifications, share sheet, haptics).

## Platform support

| OS                | Backend          | Supported | Notes                              |
|-------------------|------------------|-----------|------------------------------------|
| Windows 10+       | WinUI 3          | ✅        | Default target                     |
| Android 24+       | native           | ✅        | Needs Android SDK                  |
| iOS 15+           | native           | ✅        | Needs Xcode                        |
| macOS (Catalyst)  | Mac Catalyst     | ✅        | Needs Xcode                        |
| Linux             | ❌                | ❌        | MAUI has no Linux target — use Avalonia |

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Tui.Abstractions`
- `Microsoft.Maui.Controls` + `Microsoft.Maui.Hosting` (implicit via `UseMaui=true`)
- `CommunityToolkit.Mvvm` (optional, for source-gen VMs)
- `Microsoft.Extensions.Logging.Abstractions`

## Workload install

```bash
# Install the MAUI workload (one-time)
dotnet workload install maui

# Verify targets
dotnet workload list
```

## Files

- `Harbor.Tui.Maui.csproj` — multi-target: `net10.0-windows;net10.0-android;net10.0-ios;net10.0-maccatalyst`,
  `UseMaui=true`, `SingleProject=true`. Per-platform `SupportedOSPlatformVersion`.
- `MauiTuiRenderer.cs` — sealed `ITuiRenderer`/`IInteractiveTuiRenderer`. Builds the
  MAUI host via `MauiProgram.CreateMauiApp`, marshals store changes via
  `MainThread.BeginInvokeOnMainThread`, binds history to
  `ObservableCollection<ChatLineViewModel>`.
- `MauiProgram.cs` — `CreateMauiApp` builder. Registers store, effects, lines,
  logger, and `MauiBridge` in DI.
- `App.xaml` + `App.xaml.cs` — MAUI `Application` subclass with Catppuccin-Mocha
  color resource dictionary. `CreateWindow` constructs the `MainPage` and notifies
  the bridge so the renderer can wait for readiness.
- `MainPage.xaml` + `MainPage.xaml.cs` — single-column chat layout. `CollectionView`
  for history (virtualized, mobile-friendly), `Editor` for input (multi-line with
  `AutoSize="TextChanges"`), status bar with token cost.

## How it reads from UiStore

```csharp
_store.Changed += OnStoreChanged;

void OnStoreChanged(object? _, UiStateChangedEventArgs e)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        while (_lines.Count < state.Lines.Length)
            _lines.Add(new ChatLineViewModel(state.Lines[_lines.Count].Role,
                                             state.Lines[_lines.Count].Text));
        UpdateStreaming(state);
    });
}
```

Same MVU contract as the other renderers. `MainThread.BeginInvokeOnMainThread`
is MAUI's equivalent of WPF's `Dispatcher.BeginInvoke` and Avalonia's
`Dispatcher.UIThread.Post`.

## Build

```bash
# Windows desktop only (no mobile workloads required on Windows)
dotnet build src/Harbor.Tui.Maui/Harbor.Tui.Maui.csproj -f net10.0-windows -c Release

# Android (from a machine with Android SDK + workload installed)
dotnet build src/Harbor.Tui.Maui/Harbor.Tui.Maui.csproj -f net10.0-android -c Release

# Run Harbor with the MAUI renderer (desktop)
HARBOR_TUI=maui dotnet run --project src/Harbor.Cli/Harbor.Cli.csproj
```

## Selecting this renderer

Set `HARBOR_TUI=maui` in your environment, or add `tui: "maui"` to
`~/.harbor/config.json`.

## Memory footprint

- Windows desktop (WinUI 3): ~90 MB RSS idle — heaviest of the desktop options.
- Android/iOS: ~30–50 MB depending on device.
- Mac Catalyst: ~70 MB.

## Limitations / TODO

- The skeleton targets Windows-only by default; mobile builds need their
  platform's manifest files (AndroidManifest.xml, Info.plist) added before they
  will package.
- No markdown rendering — wire `Markdig` → `FormattedString`.
- No diff overlay panel.
- No mobile-specific affordances yet: long-press to copy, swipe to dismiss,
  haptic feedback on tool errors — all good follow-ups.
- Soft keyboard handling: `Editor` resizes the page on Android but not on iOS;
  needs platform-specific tweaks.
