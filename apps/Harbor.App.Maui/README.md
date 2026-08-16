# Harbor.App.Maui

.NET MAUI desktop app skeleton for Harbor. Targets Windows (WinUI) and macOS (Mac Catalyst). **This is a skeleton project** — the MAUI shell boots, shows a basic chat view, but does not yet have the full feature set of the CLI or Avalonia apps.

> Requires the `maui-windows` (Windows) and/or `maui-maccatalyst` (macOS) workloads. Linux is **not** supported by MAUI — see `Harbor.App.Avalonia` for a cross-platform desktop GUI that works on Linux.

## Layer

Composition Root — depends on `Harbor.Abstractions`, `Harbor.Core`, `Harbor.Storage.Memory`, `Harbor.Tui.Abstractions`. Same DI responsibilities as `Harbor.App.Cli`, but boots a MAUI lifetime.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Core` (AgentLoop, registries)
- `Harbor.Storage.Memory` (ephemeral)
- `Harbor.Tui.Abstractions` (UiStore + UiReducer)
- `Microsoft.Maui.Controls` + `Microsoft.Maui.Controls.Xaml`
- `Microsoft.Maui.Essentials`
- `Microsoft.Maui.Core`
- `CommunityToolkit.Mvvm`
- `Markdig`

## Prerequisites

### Install MAUI workloads

```bash
# Windows
dotnet workload install maui-windows

# macOS (also installs maui-windows for cross-builds)
dotnet workload install maui-maccatalyst maui-windows
```

### Linux

MAUI does **not** support Linux. On Linux, exclude this project from solution builds:

```bash
dotnet build src/Harbor.Core/Harbor.Core.csproj
dotnet build apps/Harbor.App.Cli/Harbor.App.Cli.csproj
# Do NOT build Harbor.slnx on Linux — MAUI project will fail.
```

Or use the Avalonia app instead:

```bash
dotnet run --project apps/Harbor.App.Avalonia
```

## Supported platforms

| Platform        | TargetFramework         | Status          |
|-----------------|-------------------------|-----------------|
| Windows 10+     | `net10.0-windows`       | Skeleton builds |
| macOS (Catalyst)| `net10.0-maccatalyst`   | Skeleton builds |
| Android         | (not yet targeted)      | Not supported   |
| iOS             | (not yet targeted)      | Not supported   |
| Linux           | —                       | Not supported (use Avalonia) |

## Build

### Windows

```powershell
dotnet build apps\Harbor.App.Maui\Harbor.App.Maui.csproj -f net10.0-windows
dotnet run --project apps\Harbor.App.Maui\Harbor.App.Maui.csproj -f net10.0-windows
```

### macOS

```bash
dotnet build apps/Harbor.App.Maui/Harbor.App.Maui.csproj -f net10.0-maccatalyst
dotnet run --project apps/Harbor.App.Maui/Harbor.App.Maui.csproj -f net10.0-maccatalyst
```

## App identity

- `ApplicationTitle`: Harbor
- `ApplicationId`: `com.harbor.app`

## Usage

Set the model + provider via env vars before launching (same as CLI):

```bash
export HARBOR_PROVIDER=ollama
export HARBOR_MODEL=llama3.1:8b
dotnet run --project apps/Harbor.App.Maui -f net10.0-maccatalyst
```

## See also

- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md)
- [../../docs/DESKTOP_APP_PLAN.md](../../docs/DESKTOP_APP_PLAN.md)
- [../Harbor.App.Avalonia/README.md](../Harbor.App.Avalonia/README.md) — recommended cross-platform desktop GUI
