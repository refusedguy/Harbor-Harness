# Configuration

Harbor's five apps (CLI, Avalonia, WPF, MAUI, Blazor) each ship their own
configuration record. They share a common base (`AppConfigBase`) and a common
persistence contract (`IAppConfigStore<T>`) so that the user's theme + last
provider follow them across GUIs, while per-app UX preferences (window size,
listen port, TUI renderer, etc.) stay non-overlapping.

This document is the single source of truth for:

- where config files live (paths, filenames, non-overlap rule)
- which fields are common vs app-specific
- how to read/write config from code
- how to migrate from the legacy `HarborConfig` (~/.harbor/config.json)
- example JSON files
- environment-variable overrides

---

## 1. File layout

All Harbor config files live under `~/.harbor/` (i.e. `%USERPROFILE%\.harbor`
on Windows, `$HOME/.harbor` on Linux/macOS):

```
~/.harbor/
├── cli.json          ← CliConfig       (Harbor.App.Cli)
├── avalonia.json     ← AvaloniaConfig  (Harbor.App.Avalonia)
├── wpf.json          ← WpfConfig       (Harbor.App.Wpf)
├── maui.json         ← MauiConfig      (Harbor.App.Maui)
├── blazor.json       ← BlazorConfig    (Harbor.App.Blazor)
├── config.json       ← legacy HarborConfig (CLI auth + provider presets)
├── theme.json        ← shared ThemeManager state (Harbor.Desktop.DesignSystem)
└── sessions/         ← shared session storage (JSONL)
    └── *.jsonl
```

### Non-overlap rule

Each app reads ONLY its own JSON file. The five per-app filenames
(`cli.json`, `avalonia.json`, `wpf.json`, `maui.json`, `blazor.json`) are
reserved — adding a sixth app MUST pick a new unique filename.

Shared cross-app state lives in dedicated files:
- `theme.json` — the persisted theme state for the desktop GUIs (managed by
  `Harbor.Desktop.DesignSystem.Themes.ThemeManager`).
- `sessions/*.jsonl` — session transcripts. Sessions are app-agnostic (the CLI,
  Avalonia, WPF, MAUI, Blazor all write here). The format is JSONL so any app
  can read another app's sessions for cross-app continuity.
- `config.json` — the LEGACY `HarborConfig` (auth + provider presets +
  compaction settings). Still used by the CLI's `AuthStore` and
  `OnboardingWizard`. New code SHOULD use per-app config; the legacy file is
  kept for backward compat. See §6 below.

---

## 2. Common fields (shared across every app)

All five per-app configs derive from `AppConfigBase`
(`Harbor.Desktop.Abstractions/Configuration/AppConfigBase.cs`) and inherit:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `AppId` | `string` (abstract) | — | Stable lowercase id: `"cli"`, `"avalonia"`, `"wpf"`, `"maui"`, `"blazor"`. |
| `ConfigFileName` | `string` (abstract) | — | Filename (no directory): `"cli.json"`, etc. |
| `ConfigDirectory` | `string` (virtual) | `~/.harbor` | Absolute directory path. Override only for tests. |
| `ConfigFilePath` | `string` (computed) | `~/.harbor/<ConfigFileName>` | Absolute path to this app's JSON file. |
| `Theme` | `string` | `"dark"` | `"dark"` \| `"light"` \| `"system"`. Apps without a theme subsystem (CLI) ignore this. |
| `LogLevel` | `string` | `"info"` | `"trace"` \| `"debug"` \| `"info"` \| `"warning"` \| `"error"`. Apps map to `Microsoft.Extensions.Logging.LogLevel` at startup. |
| `LastUsedProvider` | `string` | `""` | Provider ID last selected (e.g. `"anthropic"`). Empty until the user picks one. |
| `LastUsedModel` | `string` | `""` | Full model ID last selected (e.g. `"anthropic/claude-sonnet-4-20250514"`). |
| `LastUsedAgent` | `string` | `"code"` | `"code"` \| `"plan"` \| `"explore"`. |
| `RecentSessions` | `ImmutableList<string>` | `Empty` | Recent session IDs, most-recent-first. Apps cap at ~10 entries. |

---

## 3. Per-app fields

### CliConfig (`apps/Harbor.App.Cli/Configuration/CliConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `DefaultTuiRenderer` | `string` | `"auto"` | `"auto"` \| `"ansi"` \| `"plain"` \| `"spectre"` \| `"spectre-tui"` \| `"fullscreen"` \| `"terminal-gui"` \| `"termina"` \| `"razor"`. `"auto"` resolves to `"spectre-tui"` at startup. |
| `DefaultStorage` | `string` | `"jsonl"` | `"jsonl"` \| `"sqlite"` \| `"memory"`. |
| `EnableOnboardingWizard` | `bool` | `true` | Whether to run the first-run onboarding wizard on the next launch. |
| `EnableSlashCommands` | `bool` | `true` | Whether to register builtin slash commands (`/help`, `/clear`, `/agents`). |
| `DisabledTools` | `ImmutableList<string>` | `Empty` | Tool IDs NOT registered at startup (e.g. `["bash", "web-fetch"]`). |

### AvaloniaConfig (`apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `WindowWidth` | `int` | `1200` | Initial window width (DIPs). |
| `WindowHeight` | `int` | `800` | Initial window height (DIPs). |
| `WindowMaximized` | `bool` | `false` | Open maximized on next launch. |
| `FontFamily` | `string` | `"Inter"` | Sans-serif UI font. |
| `MonospaceFontFamily` | `string` | `"JetBrains Mono"` | Code-block + editor font. |
| `ShowStatusBar` | `bool` | `true` | Bottom status bar visible. |
| `ShowSessionSidebar` | `bool` | `true` | Left session-sidebar visible. |
| `OpenTabs` | `ImmutableList<string>` | `Empty` | Session IDs open in tabs (L-to-R). |

### WpfConfig (`apps/Harbor.App.Wpf/Configuration/WpfConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `WindowWidth` | `int` | `1200` | Initial window width (DIPs). |
| `WindowHeight` | `int` | `800` | Initial window height (DIPs). |
| `WindowMaximized` | `bool` | `false` | Open maximized on next launch. |
| `UseAvalonDock` | `bool` | `true` | Use AvalonDock (Dirkster.AvalonDock) as the panel system. `false` falls back to a simpler Grid layout. |

### MauiConfig (`apps/Harbor.App.Maui/Configuration/MauiConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `UseDarkMode` | `bool` | `true` | Force dark mode. `false` forces light mode. The shared `Theme` field stays `"system"` to delegate to the OS. |
| `LastPlatform` | `string` | `"windows"` | Last platform launched: `"windows"` \| `"maccatalyst"` \| `"ios"` \| `"android"`. |

### BlazorConfig (`apps/Harbor.App.Blazor/Configuration/BlazorConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `ListenPort` | `int` | `5000` | TCP port Kestrel listens on. `0` = OS-assigned ephemeral. |
| `AutoOpenBrowser` | `bool` | `true` | Auto-open host browser on startup. `--no-open-browser` CLI flag overrides at runtime. |
| `EnableHotReload` | `bool` | `true` | Whether Blazor hot-reload is enabled. |

---

## 4. Reading/writing config from code

### 4.1 Resolve from DI

Every app registers `IAppConfigStore<TAppConfig>` and `TAppConfig` itself as
singletons in its composition root. Inject either into your service:

```csharp
public sealed class MyService
{
    private readonly CliConfig _config;
    private readonly IAppConfigStore<CliConfig> _store;

    public MyService(CliConfig config, IAppConfigStore<CliConfig> store)
    {
        _config = config;
        _store = store;
    }

    public string Renderer => _config.DefaultTuiRenderer;
}
```

The `CliConfig` singleton is the snapshot loaded at startup. To pick up
changes made by another process, call `store.LoadAsync()` again. To persist
changes, use `SaveAsync` or `UpdateAsync`.

### 4.2 Read (load)

```csharp
Result<CliConfig> result = await store.LoadAsync(ct);
if (result.IsFailure)
{
    // File is missing OR corrupt — `result.Error` has the message.
    // The store already fell back to the default CliConfig in the
    // missing-file case, so this branch only fires for parse errors.
    logger.LogWarning("Failed to load CliConfig: {Error}", result.Error);
}
else
{
    CliConfig config = result.Value;
    // ...
}
```

`Result<T>` comes from `CSharpFunctionalExtensions` — same railway-oriented
pattern used throughout Harbor (see `docs/PATTERNS.md`).

### 4.3 Write (save)

```csharp
Result saveResult = await store.SaveAsync(config with { DefaultTuiRenderer = "spectre" }, ct);
if (saveResult.IsFailure)
{
    logger.LogError("Failed to save CliConfig: {Error}", saveResult.Error);
}
```

`SaveAsync` writes to `<file>.tmp` first, then `File.Move` atomically into
place. A crash mid-write leaves the previous file intact. Writes are
serialized through a `SemaphoreSlim` so concurrent callers don't truncate
each other's writes.

### 4.4 Read-modify-write (atomic)

```csharp
Result updateResult = await store.UpdateAsync(
    current => current with { LastUsedProvider = "anthropic" },
    ct);
```

`UpdateAsync` is a Load → mutate → Save pipeline wrapped in the store's
single lock, so the read-modify-write is atomic with respect to other
callers on the same `IAppConfigStore<T>` instance. It is NOT atomic across
processes — Harbor is a single-process-per-app system, so this is fine.

---

## 5. Environment-variable overrides

Some per-app fields are also overridable via env var at startup. The env var
always wins over the persisted JSON value (matches the legacy HarborConfig
behavior — env vars override persisted config).

### CLI

| Env var | Overrides | Notes |
| --- | --- | --- |
| `HARBOR_TUI` | `CliConfig.DefaultTuiRenderer` | Resolved after the literal `"auto"` is expanded. |
| `HARBOR_STORAGE` | `CliConfig.DefaultStorage` | |
| `HARBOR_MODEL` | (legacy HarborConfig.Model) | Format: `provider/model` (e.g. `anthropic/claude-sonnet-4-20250514`). |
| `HARBOR_LOGLEVEL` | (logging pipeline floor) | `Trace` \| `Debug` \| `Information` \| `Warning` \| `Error`. |

### Avalonia

| Env var | Overrides | Notes |
| --- | --- | --- |
| `HARBOR_STORAGE` | Session store backend | `"memory"` (default) \| `"jsonl"`. |
| `HARBOR_LOGLEVEL` | Logging pipeline floor | |
| `OLLAMA_HOST` | Ollama base URL | Defaults to `http://localhost:11434`. |

### WPF

| Env var | Overrides | Notes |
| --- | --- | --- |
| (none app-specific) | — | WPF reads config from `appsettings.json` next to the .exe + `~/.harbor/wpf.json`. |

### MAUI

| Env var | Overrides | Notes |
| --- | --- | --- |
| (none app-specific) | — | MAUI reads config from `~/.harbor/maui.json`. |

### Blazor

| Env var | Overrides | Notes |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `BlazorConfig.ListenPort` | Standard ASP.NET Core override; takes precedence. |
| `--no-open-browser` (CLI flag) | `BlazorConfig.AutoOpenBrowser` | Flag wins. |

---

## 6. Migrating from the legacy `HarborConfig`

The legacy `HarborConfig` (`src/Harbor.Application/Configuration/HarborConfig.cs`)
still exists at `~/.harbor/config.json`. It owns:

- API keys per provider (used by `AuthStore`)
- Custom provider configs (overrides the bundled JSON presets)
- Enabled plugins
- Disabled builtin tools
- Default max steps per agent run
- Cost limit per session
- Compaction settings

These stay in `config.json` because they are CLI-runtime concerns, not
per-app UX preferences. The new per-app config system does NOT replace
`HarborConfig` — it complements it:

| Concern | Lives in | File |
| --- | --- | --- |
| API keys, provider presets, compaction, cost limits | `HarborConfig` | `~/.harbor/config.json` |
| CLI UX (TUI renderer, storage, onboarding) | `CliConfig` | `~/.harbor/cli.json` |
| Avalonia UX (window, fonts, panels) | `AvaloniaConfig` | `~/.harbor/avalonia.json` |
| WPF UX (window, AvalonDock) | `WpfConfig` | `~/.harbor/wpf.json` |
| MAUI UX (dark mode, last platform) | `MauiConfig` | `~/.harbor/maui.json` |
| Blazor UX (listen port, browser-launch) | `BlazorConfig` | `~/.harbor/blazor.json` |
| Theme tokens (shared across desktop apps) | `ThemeManager` | `~/.harbor/theme.json` |
| Session transcripts (shared across all apps) | `JsonlSessionStore` | `~/.harbor/sessions/*.jsonl` |

### Migration path (if you want to consolidate)

If a future sprint wants to fold `HarborConfig` into `CliConfig`:

1. Add `ApiKeys`, `Providers`, `Compaction`, `MaxSteps`, `CostLimit` to
   `CliConfig` as new fields.
2. Update `AuthStore` to take `IAppConfigStore<CliConfig>` instead of
   `IConfigStore`.
3. Add a one-time migration: if `~/.harbor/cli.json` doesn't exist but
   `~/.harbor/config.json` does, copy the relevant fields over and rename
   `config.json` to `config.json.bak`.
4. Keep the legacy `JsonConfigStore` as a thin reader for the `.bak` file
   so users can roll back.

This is OUT OF SCOPE for the per-app config task (B2) — it's a follow-up.

---

## 7. Example JSON files

### `~/.harbor/cli.json`

```json
{
  "Theme": "dark",
  "LogLevel": "info",
  "LastUsedProvider": "anthropic",
  "LastUsedModel": "anthropic/claude-sonnet-4-20250514",
  "LastUsedAgent": "code",
  "RecentSessions": [
    "2026-07-18-abc123",
    "2026-07-17-def456"
  ],
  "DefaultTuiRenderer": "spectre-tui",
  "DefaultStorage": "jsonl",
  "EnableOnboardingWizard": false,
  "EnableSlashCommands": true,
  "DisabledTools": []
}
```

### `~/.harbor/avalonia.json`

```json
{
  "Theme": "system",
  "LogLevel": "warning",
  "LastUsedProvider": "openai",
  "LastUsedModel": "openai/gpt-4o",
  "LastUsedAgent": "plan",
  "RecentSessions": [],
  "WindowWidth": 1440,
  "WindowHeight": 900,
  "WindowMaximized": false,
  "FontFamily": "Inter",
  "MonospaceFontFamily": "JetBrains Mono",
  "ShowStatusBar": true,
  "ShowSessionSidebar": true,
  "OpenTabs": []
}
```

### `~/.harbor/wpf.json`

```json
{
  "Theme": "dark",
  "LogLevel": "info",
  "LastUsedProvider": "kilocode",
  "LastUsedModel": "tencent/hy3:free",
  "LastUsedAgent": "code",
  "RecentSessions": [],
  "WindowWidth": 1200,
  "WindowHeight": 800,
  "WindowMaximized": true,
  "UseAvalonDock": true
}
```

### `~/.harbor/maui.json`

```json
{
  "Theme": "system",
  "LogLevel": "info",
  "LastUsedProvider": "",
  "LastUsedModel": "",
  "LastUsedAgent": "code",
  "RecentSessions": [],
  "UseDarkMode": true,
  "LastPlatform": "windows"
}
```

### `~/.harbor/blazor.json`

```json
{
  "Theme": "dark",
  "LogLevel": "info",
  "LastUsedProvider": "ollama",
  "LastUsedModel": "ollama/qwen2.5-coder:7b",
  "LastUsedAgent": "code",
  "RecentSessions": [],
  "ListenPort": 5000,
  "AutoOpenBrowser": true,
  "EnableHotReload": true
}
```

---

## 8. Architecture

```
Harbor.Desktop.Abstractions/Configuration/
├── AppConfigBase.cs           ← abstract record (common fields + paths)
├── IAppConfigStore.cs         ← repository contract (Result<T> returns)
└── JsonAppConfigStore.cs      ← JSON-backed impl (atomic write + SemaphoreSlim)

apps/Harbor.App.Cli/Configuration/CliConfig.cs         ← sealed record : AppConfigBase
apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs
apps/Harbor.App.Wpf/Configuration/WpfConfig.cs
apps/Harbor.App.Maui/Configuration/MauiConfig.cs
apps/Harbor.App.Blazor/Configuration/BlazorConfig.cs
```

Each app's composition root wires:

```csharp
builder.Services.AddSingleton<IAppConfigStore<TAppConfig>>(sp =>
    new JsonAppConfigStore<TAppConfig>(
        new TAppConfig(),
        sp.GetRequiredService<ILogger<JsonAppConfigStore<TAppConfig>>>()));
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<IAppConfigStore<TAppConfig>>();
    var result = store.LoadAsync().GetAwaiter().GetResult();
    return result.IsSuccess ? result.Value : new TAppConfig();
});
```

The eager `LoadAsync` call at startup populates the `TAppConfig` singleton
that the rest of the composition root (RegisterTui, RegisterStorage,
ThemeService, MainViewModel) resolves synchronously.

### Thread safety

- `JsonAppConfigStore<T>` uses a single `SemaphoreSlim(1, 1)` to serialize
  Load/Save/Update. Concurrent callers block on the semaphore.
- The `TAppConfig` singleton registered in DI is a snapshot — it does NOT
  reflect writes from other processes. To pick up external writes, call
  `store.LoadAsync()` again.

### Atomicity

- `SaveAsync` writes to `<file>.tmp` first, then `File.Move` into place.
- On POSIX, `File.Move` is atomic when the destination doesn't exist;
  `SaveAsync` deletes the destination first if it exists.
- On Windows, `File.Move` with `File.Delete` first is the documented atomic-
  replace pattern (the .NET runtime does NOT expose `MoveFileEx` with
  `MOVEFILE_REPLACE_EXISTING` directly).
- A crash mid-write leaves the previous file intact (the temp file is
  orphaned but does NOT corrupt the destination).

---

## 9. DI tests

Each app's test project asserts that `IAppConfigStore<TAppConfig>` and
`TAppConfig` are registered:

| Test project | Test file | Assertions |
| --- | --- | --- |
| `Harbor.App.Cli.Tests` | `HostBuilderDiTests.cs` | `IAppConfigStore<CliConfig>`, `CliConfig` |
| `Harbor.App.Avalonia.Tests` | `AppHostDiTests.cs` | `IAppConfigStore<AvaloniaConfig>`, `AvaloniaConfig` |
| `Harbor.App.Wpf.Tests` | `AppDiTests.cs` | `IAppConfigStore<WpfConfig>`, `WpfConfig` |
| `Harbor.App.Maui.Tests` | `MauiProgramDiTests.cs` | `IAppConfigStore<MauiConfig>`, `MauiConfig` |
| `Harbor.App.Blazor.Tests` | `ProgramDiTests.cs` | `IAppConfigStore<BlazorConfig>`, `BlazorConfig` |

Each test class also has an aggregate `Build_AllDeclaredServices_Resolvable`
test that includes the new types in the required-services list — so the
`[Exposes(typeof(T))]` attributes on the composition roots stay in sync
with the actual registrations.

---

## 10. Future work

- **Migrate `HarborConfig` into `CliConfig`** — fold auth, provider presets,
  compaction, cost limits into `CliConfig` and deprecate `~/.harbor/config.json`.
  See §6 above.
- **Per-app config UI** — `SettingsView` in Avalonia/WPF/Blazor should
  surface the per-app fields as editable forms. Currently the Settings VM
  reads from the legacy `HarborConfig`; switch it to the app's per-app
  config record.
- **Cross-app sync** — when the user changes `LastUsedProvider` in one app,
  the other apps should pick it up on next launch. Currently each app reads
  its own config file only, so `LastUsedProvider` does NOT sync across apps.
  If cross-app sync is desired, add a `~/.harbor/shared.json` for the
  common fields and have each app's `IAppConfigStore<T>` merge it.
- **Schema validation** — currently `LoadAsync` falls back to defaults on
  JSON parse errors. Add a JSON-schema validator that surfaces specific
  field errors to the user (e.g. "Theme must be one of dark/light/system").
- **Encrypted secrets** — `LastUsedProvider` and `LastUsedModel` are not
  sensitive, but if we ever store API keys in per-app config, they should
  be encrypted via the OS keychain (DPAPI on Windows, Keychain on macOS,
  Secret Service on Linux). The legacy `HarborConfig.ApiKeys` currently
  stores them in plaintext — separate concern, tracked separately.
