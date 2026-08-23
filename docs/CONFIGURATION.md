# Configuration

Harbor's five apps (CLI, Avalonia, WPF, MAUI, Blazor) split configuration into
**two layers**:

1. **Common** (`~/.harbor/config.json`) — settings shared across every app:
   API keys, default provider/model/agent, storage backend, logging,
   permissions, plugins, network, compaction. If the user sets an API key in
   the CLI, the desktop apps see it on next launch.
2. **App-specific** (`~/.harbor/<app>.json`) — UX preferences unique to each
   app: window size, fonts, TUI renderer, listen port, onboarding flag. Each
   app's JSON file contains ONLY app-specific fields — no duplication.

This split (task C1) replaces the previous layout (task B2) where every app's
`AppConfigBase` re-declared the same common fields (Theme, LogLevel,
LastUsed*, RecentSessions), so changing one app's preference didn't propagate
to the others.

This document is the single source of truth for:

- where config files live (paths, filenames, non-overlap rule)
- which fields are common vs app-specific
- how to read/write config from code
- how to migrate from the legacy `HarborConfig` / B2 layout
- example JSON files
- environment-variable overrides

---

## 1. File layout

All Harbor config files live under `~/.harbor/` (i.e. `%USERPROFILE%\.harbor`
on Windows, `$HOME/.harbor` on Linux/macOS):

```
~/.harbor/
├── config.json          ← CommonConfig          (SHARED: API keys, default provider/model,
│                                                  storage, logging, permissions, plugins,
│                                                  network, compaction)
├── cli.json             ← CliConfig             (CLI-specific: TUI renderer, onboarding, history)
├── avalonia.json        ← AvaloniaConfig        (window size, fonts, theme override, open tabs)
├── wpf.json             ← WpfConfig             (window size, AvalonDock)
├── maui.json            ← MauiConfig            (dark mode, last platform)
├── blazor.json          ← BlazorConfig          (listen port, auto-open browser, hot reload)
├── theme.json           ← shared ThemeManager state (Harbor.Desktop.DesignSystem)
├── sessions/            ← shared session storage (JSONL)
│   └── *.jsonl
└── logs/                ← rotating log files (when CommonConfig.EnableFileLogging=true)
```

### Non-overlap rule

Each app reads ONLY its own `<app>.json` file. The five per-app filenames
(`cli.json`, `avalonia.json`, `wpf.json`, `maui.json`, `blazor.json`) are
reserved — adding a sixth app MUST pick a new unique filename.

The single `config.json` file is read by EVERY app — it is the shared layer
(`CommonConfig`). Apps never write to it directly; they go through
`ICommonConfigStore.UpdateAsync` to perform an atomic read-modify-write.

Shared cross-app state lives in dedicated files:
- `config.json` — `CommonConfig` (the shared layer).
- `theme.json` — the persisted theme state for the desktop GUIs (managed by
  `Harbor.Desktop.DesignSystem.Themes.ThemeManager`).
- `sessions/*.jsonl` — session transcripts. Sessions are app-agnostic (the CLI,
  Avalonia, WPF, MAUI, Blazor all write here). The format is JSONL so any app
  can read another app's sessions for cross-app continuity.

### Legacy file note

Before task C1, `~/.harbor/config.json` was owned by the legacy
`HarborConfig` (CLI auth + provider presets + compaction settings). Task C1
**re-purposes** this file path for `CommonConfig`. The legacy `HarborConfig`
is still loaded by the CLI's `AuthStore` / `OnboardingWizard` (via the
`IConfigStore` / `JsonConfigStore` types in `Harbor.Core.Configuration`) —
both layers coexist during the migration window. See §6 below.

---

## 2. Common fields (shared across every app)

All five per-app configs AND the shared `CommonConfig` are `sealed record`
types in `Harbor.Desktop.Abstractions/Configuration/`. The common fields
live on `CommonConfig` itself (`CommonConfig.cs`):

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `ConfigVersion` | `string` | `"1"` | Schema version. Bumped on backward-incompatible JSON shape changes. |
| `ApiKeys` | `ImmutableDictionary<string,string>` | `Empty` | API keys per provider (`anthropic`, `openai`, `openrouter`, `deepseek`, `groq`, `mistral`, `xai`, `together`, `fireworks`, `cerebras`, `kilocode`). UI redacts values. |
| `DefaultProvider` | `string` | `"anthropic"` | Default provider ID used on first launch. |
| `DefaultModel` | `string` | `"claude-sonnet-4"` | Default full model ID. |
| `DefaultAgent` | `string` | `"code"` | Default agent mode: `code` \| `plan` \| `explore`. |
| `StorageBackend` | `string` | `"jsonl"` | `jsonl` \| `sqlite` \| `memory`. Env `HARBOR_STORAGE` overrides. |
| `StoragePath` | `string` | `""` | Optional override for session dir. Empty = `~/.harbor/sessions/`. |
| `LogLevel` | `string` | `"info"` | `trace` \| `debug` \| `info` \| `warning` \| `error`. |
| `EnableFileLogging` | `bool` | `true` | Write rotating logs to `~/.harbor/logs/`. |
| `MaxLogFiles` | `int` | `50` | Max rotating log files to retain. |
| `PermissionMode` | `string` | `"default"` | `default` \| `permissive` \| `strict`. |
| `AlwaysAllowTools` | `ImmutableList<string>` | `Empty` | Tool IDs to skip permission prompt for. |
| `AlwaysDenyTools` | `ImmutableList<string>` | `Empty` | Tool IDs to never run. |
| `EnablePlugins` | `bool` | `true` | Whether the CS plugin runtime is enabled. |
| `PluginDirectories` | `string` | `""` | Comma-separated extra plugin dirs. |
| `EnableScripting` | `bool` | `true` | Whether Jint/SharpTS in-process script tool is enabled. |
| `HttpProxy` | `string` | `""` | HTTP proxy URL. Empty = direct. |
| `HttpTimeoutSeconds` | `int` | `30` | Per-request HTTP timeout. |
| `UserAgent` | `string` | `"Harbor/0.7"` | Outbound `User-Agent` header. |
| `CompactionReserveTokens` | `int` | `16384` | Tokens to reserve below context window before compacting. |
| `CompactionKeepRecentTokens` | `int` | `20000` | Target token count for kept tail. |
| `CompactionTailTurns` | `int` | `2` | Minimum recent turns to keep verbatim. |
| `ConfigDirectory` | `string` (init) | `~/.harbor` | Absolute dir. Init-only so tests can override. |
| `ConfigFileName` | `string` | `"config.json"` | Filename (no dir). |
| `ConfigFilePath` | `string` (computed) | `~/.harbor/config.json` | Absolute path. |

Persistence: `ICommonConfigStore` / `JsonCommonConfigStore` — same
atomic-write + thread-safe pattern as `IAppConfigStore<T>`.

---

## 3. Per-app fields (app-specific ONLY)

Each per-app config derives from `AppConfigBase` (which now contains ONLY path
plumbing — no common fields). The derived records add their own app-specific
fields.

### CliConfig (`apps/Harbor.App.Cli/Configuration/CliConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `DefaultTuiRenderer` | `string` | `"auto"` | `auto` \| `ansi` \| `plain` \| `spectre` \| `spectre-tui` \| `fullscreen` \| `terminal-gui` \| `termina` \| `razor`. `auto` resolves to `spectre-tui`. Env `HARBOR_TUI` overrides. |
| `EnableOnboardingWizard` | `bool` | `true` | Whether to run first-run onboarding on next launch. |
| `EnableSlashCommands` | `bool` | `true` | Whether to register builtin slash commands (`/help`, `/clear`, `/agents`). |
| `DisabledTools` | `ImmutableList<string>` | `Empty` | Tool IDs NOT registered at startup. |
| `HistoryFile` | `string` | `""` | Path to readline-style history file. Empty = `~/.harbor/history`. |
| `MaxHistoryEntries` | `int` | `1000` | Max input-history entries to retain. |

### AvaloniaConfig (`apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `Theme` | `string` | `"system"` | App-specific theme override: `system` (use shared) \| `dark` \| `light`. |
| `WindowWidth` | `int` | `1200` | Initial window width (DIPs). |
| `WindowHeight` | `int` | `800` | Initial window height (DIPs). |
| `WindowMaximized` | `bool` | `false` | Open maximized on next launch. |
| `FontFamily` | `string` | `"Inter"` | Sans-serif UI font. |
| `MonospaceFontFamily` | `string` | `"JetBrains Mono"` | Code-block + editor font. |
| `ShowStatusBar` | `bool` | `true` | Bottom status bar visible. |
| `ShowSessionSidebar` | `bool` | `true` | Left session-sidebar visible. |
| `OpenTabs` | `ImmutableList<string>` | `Empty` | Session IDs open in tabs (L-to-R). |

### WpfConfig (`contrib/apps/Harbor.App.Wpf/Configuration/WpfConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `Theme` | `string` | `"system"` | App-specific theme override. |
| `WindowWidth` | `int` | `1200` | Initial window width (DIPs). |
| `WindowHeight` | `int` | `800` | Initial window height (DIPs). |
| `WindowMaximized` | `bool` | `false` | Open maximized on next launch. |
| `UseAvalonDock` | `bool` | `true` | Use AvalonDock (Dirkster.AvalonDock) as panel system. |

### MauiConfig (`contrib/apps/Harbor.App.Maui/Configuration/MauiConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `UseDarkMode` | `bool` | `true` | Force dark mode. `false` forces light. |
| `LastPlatform` | `string` | `"windows"` | `windows` \| `maccatalyst` \| `ios` \| `android`. |

### BlazorConfig (`contrib/apps/Harbor.App.Blazor/Configuration/BlazorConfig.cs`)

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `ListenPort` | `int` | `5000` | TCP port Kestrel listens on. `0` = OS-assigned ephemeral. |
| `AutoOpenBrowser` | `bool` | `true` | Auto-open host browser on startup. `--no-open-browser` CLI flag overrides. |
| `EnableHotReload` | `bool` | `true` | Whether Blazor hot-reload is enabled. |

---

## 4. Common vs app-specific — quick reference

| Concern | Lives in | File |
| --- | --- | --- |
| API keys | `CommonConfig.ApiKeys` | `~/.harbor/config.json` |
| Default provider / model / agent | `CommonConfig.Default*` | `~/.harbor/config.json` |
| Storage backend (jsonl / sqlite / memory) | `CommonConfig.StorageBackend` | `~/.harbor/config.json` |
| Logging verbosity + file logging | `CommonConfig.LogLevel` + `EnableFileLogging` + `MaxLogFiles` | `~/.harbor/config.json` |
| Permission mode + allow/deny tool lists | `CommonConfig.PermissionMode` + `AlwaysAllowTools` + `AlwaysDenyTools` | `~/.harbor/config.json` |
| Plugin / scripting enablement | `CommonConfig.EnablePlugins` + `EnableScripting` | `~/.harbor/config.json` |
| HTTP proxy / timeout / user-agent | `CommonConfig.Http*` + `UserAgent` | `~/.harbor/config.json` |
| Compaction tuning | `CommonConfig.Compaction*` | `~/.harbor/config.json` |
| CLI TUI renderer | `CliConfig.DefaultTuiRenderer` | `~/.harbor/cli.json` |
| CLI onboarding / slash commands / history | `CliConfig.*` | `~/.harbor/cli.json` |
| Avalonia window size / fonts / panels | `AvaloniaConfig.*` | `~/.harbor/avalonia.json` |
| WPF window size / AvalonDock | `WpfConfig.*` | `~/.harbor/wpf.json` |
| MAUI dark mode / last platform | `MauiConfig.*` | `~/.harbor/maui.json` |
| Blazor listen port / browser-launch / hot-reload | `BlazorConfig.*` | `~/.harbor/blazor.json` |
| Theme tokens (shared across desktop apps) | `ThemeManager` | `~/.harbor/theme.json` |
| Session transcripts (shared across all apps) | `JsonlSessionStore` | `~/.harbor/sessions/*.jsonl` |

---

## 5. Reading/writing config from code

### 5.1 Resolve from DI

Every app registers BOTH `ICommonConfigStore` + `CommonConfig` (shared) AND
`IAppConfigStore<TAppConfig>` + `TAppConfig` (app-specific) as singletons in
its composition root. Inject either (or both) into your service:

```csharp
public sealed class MyService
{
    private readonly CommonConfig _common;
    private readonly ICommonConfigStore _commonStore;
    private readonly CliConfig _app;
    private readonly IAppConfigStore<CliConfig> _appStore;

    public MyService(
        CommonConfig common,
        ICommonConfigStore commonStore,
        CliConfig app,
        IAppConfigStore<CliConfig> appStore)
    {
        _common = common;
        _commonStore = commonStore;
        _app = app;
        _appStore = appStore;
    }

    public string ApiKey => _common.ApiKeys.GetValueOrDefault("anthropic", "");
    public string Renderer => _app.DefaultTuiRenderer;
}
```

If a service needs fields from BOTH layers, take `CompositeConfig<TAppConfig>`
instead — it pairs the two snapshots into a single dependency:

```csharp
public sealed class CompactionService
{
    public CompactionService(CompositeConfig<CliConfig> config)
    {
        _reserveTokens = config.Common.CompactionReserveTokens;
        _tui = config.App.DefaultTuiRenderer;
    }
}
```

The `CommonConfig` / `CliConfig` singletons are snapshots loaded at startup.
To pick up changes made by another process, call `store.LoadAsync()` again.
To persist changes, use `SaveAsync` or `UpdateAsync`.

### 5.2 Read (load)

```csharp
Result<CommonConfig> result = await commonStore.LoadAsync(ct);
if (result.IsFailure)
{
    // File is missing OR corrupt — `result.Error` has the message.
    // The store already fell back to the default CommonConfig in the
    // missing-file case, so this branch only fires for parse errors.
    logger.LogWarning("Failed to load CommonConfig: {Error}", result.Error);
}
else
{
    CommonConfig config = result.Value;
    // ...
}
```

`Result<T>` comes from `CSharpFunctionalExtensions` — same railway-oriented
pattern used throughout Harbor (see `docs/PATTERNS.md`).

### 5.3 Write (save)

```csharp
Result saveResult = await commonStore.SaveAsync(
    commonConfig with { DefaultProvider = "openai" },
    ct);
if (saveResult.IsFailure)
{
    logger.LogError("Failed to save CommonConfig: {Error}", saveResult.Error);
}
```

`SaveAsync` writes to `<file>.tmp` first, then `File.Move` atomically into
place. A crash mid-write leaves the previous file intact. Writes are
serialized through a `SemaphoreSlim` so concurrent callers don't truncate
each other's writes.

### 5.4 Read-modify-write (atomic)

```csharp
Result updateResult = await commonStore.UpdateAsync(
    current => current with
    {
        ApiKeys = current.ApiKeys.SetItem("anthropic", newKey)
    },
    ct);
```

`UpdateAsync` is a Load → mutate → Save pipeline wrapped in the store's
single lock, so the read-modify-write is atomic with respect to other
callers on the same `ICommonConfigStore` instance. It is NOT atomic across
processes — Harbor is a single-process-per-app system, so this is fine.

---

## 6. Environment-variable overrides

Some fields are also overridable via env var at startup. The env var always
wins over the persisted JSON value.

| Env var | Overrides | Notes |
| --- | --- | --- |
| `HARBOR_TUI` | `CliConfig.DefaultTuiRenderer` | Resolved after the literal `"auto"` is expanded. CLI only. |
| `HARBOR_STORAGE` | `CommonConfig.StorageBackend` | Every app honours this. |
| `HARBOR_MODEL` | (legacy `HarborConfig.Model`) | Format: `provider/model` (e.g. `anthropic/claude-sonnet-4-20250514`). CLI only. |
| `HARBOR_LOGLEVEL` | `CommonConfig.LogLevel` | `Trace` \| `Debug` \| `Information` \| `Warning` \| `Error`. |
| `OLLAMA_HOST` | Ollama base URL | Defaults to `http://localhost:11434`. Avalonia + CLI. |
| `ASPNETCORE_URLS` | `BlazorConfig.ListenPort` | Standard ASP.NET Core override; takes precedence. Blazor only. |
| `--no-open-browser` (CLI flag) | `BlazorConfig.AutoOpenBrowser` | Flag wins. Blazor only. |

---

## 7. Migrating from B2 / legacy `HarborConfig`

### 7.1 B2 → C1 (per-app common fields → CommonConfig)

Before task C1, `AppConfigBase` declared the common fields directly:

```csharp
// B2 layout — DEPRECATED
public abstract record AppConfigBase {
    public string Theme { get; init; } = "dark";
    public string LogLevel { get; init; } = "info";
    public string LastUsedProvider { get; init; } = "";
    public string LastUsedModel { get; init; } = "";
    public string LastUsedAgent { get; init; } = "code";
    public ImmutableList<string> RecentSessions { get; init; } = ...;
}
```

Each per-app JSON file duplicated these fields, so changing one app's
`LogLevel` didn't affect the others. Task C1 moved them all to
`CommonConfig` (~/.harbor/config.json) so they're shared.

**Migration path (manual, not auto-applied):**

1. The user launches any app under task C1 for the first time.
2. `JsonCommonConfigStore.LoadAsync()` finds `~/.harbor/config.json` either
   missing (first-ever run) or containing the legacy `HarborConfig` JSON
   (provider/model/apiKeys/...).
3. The legacy `HarborConfig` and the new `CommonConfig` JSON shapes overlap
   on the `apiKeys` / `LogLevel` / `Storage` / `Compaction*` fields but use
   different casing / property names. `JsonCommonConfigStore` does NOT
   auto-migrate — it loads whatever fields it can map and uses defaults for
   the rest.
4. The user can run `harbor config migrate` (planned, not yet implemented) to
   copy the orphaned common fields from each `<app>.json` into
   `config.json` and zero them out in the per-app files. Auto-migration is
   intentionally NOT done at startup — it's risky and reversible only by
   hand.

**What to do today:** if you have a `~/.harbor/cli.json` from B2 with
`LogLevel` / `Theme` / `LastUsedProvider` / `LastUsedModel` / `LastUsedAgent`
/ `RecentSessions` fields, those fields are now silently ignored by the CLI
(JsonAppConfigStore<CliConfig> no longer has those properties to
deserialize into). The CLI falls back to CommonConfig defaults. To preserve
the user's intent, manually copy those values to `~/.harbor/config.json`:

```bash
# Example: copy LogLevel from cli.json to config.json
jq -s '.[0].LogLevel = .[1].LogLevel | .[0]' config.json cli.json > config.json.new
mv config.json.new config.json
```

### 7.2 Legacy `HarborConfig` (still in use)

The legacy `HarborConfig` (`src/Harbor.Application/Configuration/HarborConfig.cs`)
still exists at `~/.harbor/config.json`. It owns:

- API keys per provider (used by `AuthStore`)
- Custom provider configs (overrides the bundled JSON presets)
- Enabled plugins
- Disabled builtin tools
- Default max steps per agent run
- Cost limit per session
- Compaction settings

**Important:** task C1 RE-PURPOSED the `~/.harbor/config.json` path for
`CommonConfig`. Both `HarborConfig` and `CommonConfig` are now reading/writing
the SAME file. The two types overlap on the `apiKeys` / `LogLevel` /
`Storage` / `Compaction*` fields (System.Text.Json is case-insensitive in
web-default mode, so `apiKeys` and `ApiKeys` deserialize to the same
property). Fields unique to one type are ignored by the other's loader.

| Concern | Lives in (during migration) | File |
| --- | --- | --- |
| API keys, provider presets, compaction, cost limits | `HarborConfig` (legacy `IConfigStore`) | `~/.harbor/config.json` |
| Default provider / model / agent (SHARED) | `CommonConfig` (new) | `~/.harbor/config.json` |
| Storage backend (SHARED) | `CommonConfig` (new) | `~/.harbor/config.json` |
| Logging (SHARED) | `CommonConfig` (new) | `~/.harbor/config.json` |
| CLI UX (TUI renderer, onboarding, history) | `CliConfig` | `~/.harbor/cli.json` |
| Avalonia UX (window, fonts, panels) | `AvaloniaConfig` | `~/.harbor/avalonia.json` |
| WPF UX (window, AvalonDock) | `WpfConfig` | `~/.harbor/wpf.json` |
| MAUI UX (dark mode, last platform) | `MauiConfig` | `~/.harbor/maui.json` |
| Blazor UX (listen port, browser-launch) | `BlazorConfig` | `~/.harbor/blazor.json` |
| Theme tokens (shared across desktop apps) | `ThemeManager` | `~/.harbor/theme.json` |
| Session transcripts (shared across all apps) | `JsonlSessionStore` | `~/.harbor/sessions/*.jsonl` |

### 7.3 Future migration (consolidate)

If a future sprint wants to fold `HarborConfig` entirely into `CommonConfig`:

1. Move the remaining legacy fields (`Provider`/`Model`/`Agent`/`Onboarded`/
   `Tui`/`Storage`/`MaxSteps`/`CostLimit`/`EnabledPlugins`/`DisabledTools`/
   `Providers`) into `CommonConfig`.
2. Update `AuthStore` + `OnboardingWizard` + `ConfigCommand` to take
   `ICommonConfigStore` instead of `IConfigStore`.
3. Provide a `harbor config migrate` CLI command that copies orphaned fields
   from per-app JSONs into `config.json` and writes a `.bak` of the original.
4. Delete `HarborConfig` + `JsonConfigStore` + `IConfigStore` once no
   production code references them.

This is OUT OF SCOPE for task C1 — it's a follow-up.

---

## 8. Example JSON files

### `~/.harbor/config.json` (CommonConfig — SHARED)

```json
{
  "configVersion": "1",
  "apiKeys": {
    "anthropic": "sk-ant-xxx",
    "openai": "sk-proj-yyy"
  },
  "defaultProvider": "anthropic",
  "defaultModel": "claude-sonnet-4",
  "defaultAgent": "code",
  "storageBackend": "jsonl",
  "storagePath": "",
  "logLevel": "info",
  "enableFileLogging": true,
  "maxLogFiles": 50,
  "permissionMode": "default",
  "alwaysAllowTools": [],
  "alwaysDenyTools": [],
  "enablePlugins": true,
  "pluginDirectories": "",
  "enableScripting": true,
  "httpProxy": "",
  "httpTimeoutSeconds": 30,
  "userAgent": "Harbor/0.7",
  "compactionReserveTokens": 16384,
  "compactionKeepRecentTokens": 20000,
  "compactionTailTurns": 2
}
```

### `~/.harbor/cli.json` (CliConfig — CLI-specific ONLY)

```json
{
  "defaultTuiRenderer": "spectre-tui",
  "enableOnboardingWizard": false,
  "enableSlashCommands": true,
  "disabledTools": [],
  "historyFile": "",
  "maxHistoryEntries": 1000
}
```

### `~/.harbor/avalonia.json` (AvaloniaConfig — Avalonia-specific ONLY)

```json
{
  "theme": "system",
  "windowWidth": 1440,
  "windowHeight": 900,
  "windowMaximized": false,
  "fontFamily": "Inter",
  "monospaceFontFamily": "JetBrains Mono",
  "showStatusBar": true,
  "showSessionSidebar": true,
  "openTabs": []
}
```

### `~/.harbor/wpf.json` (WpfConfig — WPF-specific ONLY)

```json
{
  "theme": "system",
  "windowWidth": 1200,
  "windowHeight": 800,
  "windowMaximized": true,
  "useAvalonDock": true
}
```

### `~/.harbor/maui.json` (MauiConfig — MAUI-specific ONLY)

```json
{
  "useDarkMode": true,
  "lastPlatform": "windows"
}
```

### `~/.harbor/blazor.json` (BlazorConfig — Blazor-specific ONLY)

```json
{
  "listenPort": 5000,
  "autoOpenBrowser": true,
  "enableHotReload": true
}
```

---

## 9. Architecture

```
Harbor.Desktop.Abstractions/Configuration/
├── AppConfigBase.cs           ← abstract record (path plumbing ONLY — no common fields)
├── IAppConfigStore.cs         ← per-app repository contract (Result<T> returns)
├── JsonAppConfigStore.cs      ← per-app JSON impl (atomic write + SemaphoreSlim)
├── CommonConfig.cs            ← NEW (C1): sealed record, shared across every app
├── ICommonConfigStore.cs      ← NEW (C1): shared repository contract
├── JsonCommonConfigStore.cs   ← NEW (C1): JSON impl for CommonConfig
└── CompositeConfig.cs         ← NEW (C1): pairs CommonConfig + TAppConfig for DI

apps/Harbor.App.Cli/Configuration/CliConfig.cs         ← sealed record : AppConfigBase
apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs
contrib/apps/Harbor.App.Wpf/Configuration/WpfConfig.cs
contrib/apps/Harbor.App.Maui/Configuration/MauiConfig.cs
contrib/apps/Harbor.App.Blazor/Configuration/BlazorConfig.cs
```

Each app's composition root wires BOTH layers:

```csharp
// Shared layer (~/.harbor/config.json)
builder.Services.AddSingleton<ICommonConfigStore>(sp =>
    new JsonCommonConfigStore(
        new CommonConfig(),
        sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<ICommonConfigStore>();
    var result = store.LoadAsync().GetAwaiter().GetResult();
    return result.IsSuccess ? result.Value : new CommonConfig();
});

// App-specific layer (~/.harbor/<app>.json)
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

// Convenience pair for services that need fields from BOTH layers
builder.Services.AddSingleton<CompositeConfig<TAppConfig>>(sp =>
    new CompositeConfig<TAppConfig>(
        sp.GetRequiredService<CommonConfig>(),
        sp.GetRequiredService<TAppConfig>()));
```

The eager `LoadAsync` calls at startup populate the `CommonConfig` +
`TAppConfig` singletons that the rest of the composition root resolves
synchronously.

### Thread safety

- `JsonCommonConfigStore` uses a single `SemaphoreSlim(1, 1)` to serialize
  Load/Save/Update. Concurrent callers block on the semaphore.
- `JsonAppConfigStore<T>` uses the same pattern.
- The `CommonConfig` / `TAppConfig` singletons registered in DI are snapshots —
  they do NOT reflect writes from other processes. To pick up external writes,
  call `store.LoadAsync()` again.

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

## 10. DI tests

Each app's test project asserts that BOTH `ICommonConfigStore` + `CommonConfig`
(shared) AND `IAppConfigStore<TAppConfig>` + `TAppConfig` (app-specific) AND
`CompositeConfig<TAppConfig>` (composite) are registered
(Wpf/Maui/Blazor test projects live in `contrib/tests/` since sprint-2):

| Test project | Test file | Assertions |
| --- | --- | --- |
| `Harbor.App.Cli.Tests` | `HostBuilderDiTests.cs` | `ICommonConfigStore`, `CommonConfig`, `CompositeConfig<CliConfig>`, `IAppConfigStore<CliConfig>`, `CliConfig` |
| `Harbor.App.Avalonia.Tests` | `AppHostDiTests.cs` | `ICommonConfigStore`, `CommonConfig`, `CompositeConfig<AvaloniaConfig>`, `IAppConfigStore<AvaloniaConfig>`, `AvaloniaConfig` |
| `Harbor.App.Wpf.Tests` | `AppDiTests.cs` | `ICommonConfigStore`, `CommonConfig`, `CompositeConfig<WpfConfig>`, `IAppConfigStore<WpfConfig>`, `WpfConfig` |
| `Harbor.App.Maui.Tests` | `MauiProgramDiTests.cs` | `ICommonConfigStore`, `CommonConfig`, `CompositeConfig<MauiConfig>`, `IAppConfigStore<MauiConfig>`, `MauiConfig` |
| `Harbor.App.Blazor.Tests` | `ProgramDiTests.cs` | `ICommonConfigStore`, `CommonConfig`, `CompositeConfig<BlazorConfig>`, `IAppConfigStore<BlazorConfig>`, `BlazorConfig` |

Each test class also has an aggregate `Build_AllDeclaredServices_Resolvable`
test that includes the new types in the required-services list — so the
`[Exposes(typeof(T))]` attributes on the composition roots stay in sync
with the actual registrations.

---

## 11. Future work

- **`harbor config migrate` CLI command** — copy orphaned common fields from
  each `<app>.json` into `config.json` and zero them out in the per-app
  files. Auto-migration at startup is intentionally NOT done (risky); the
  command makes it a one-step opt-in.
- **Fold `HarborConfig` into `CommonConfig`** — move the remaining legacy
  fields (`Provider`/`Model`/`Agent`/`Onboarded`/`Tui`/`Storage`/`MaxSteps`/
  `CostLimit`/`EnabledPlugins`/`DisabledTools`/`Providers`) into
  `CommonConfig` and update `AuthStore` + `OnboardingWizard` +
  `ConfigCommand` to take `ICommonConfigStore`. See §7.3 above.
- **Per-app config UI** — `SettingsView` in Avalonia/WPF/Blazor should
  surface the per-app fields as editable forms. Currently the Settings VM
  reads from the legacy `HarborConfig`; switch it to the app's per-app
  config record + the shared `CommonConfig`.
- **Schema validation** — currently `LoadAsync` falls back to defaults on
  JSON parse errors. Add a JSON-schema validator that surfaces specific
  field errors to the user (e.g. "StorageBackend must be one of
  jsonl/sqlite/memory").
- **Encrypted secrets** — `CommonConfig.ApiKeys` currently stores API keys
  in plaintext. They should be encrypted via the OS keychain (DPAPI on
  Windows, Keychain on macOS, Secret Service on Linux). Separate concern,
  tracked separately.
- **Cross-app realtime sync** — when the user changes `DefaultProvider` in
  one app, the other apps should pick it up on next launch. Currently each
  app reads `~/.harbor/config.json` once at startup; if realtime sync is
  desired, add a `FileSystemWatcher` to `JsonCommonConfigStore` that
  reloads the singleton on external writes.
