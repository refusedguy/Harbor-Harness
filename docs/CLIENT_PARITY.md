# CLI / Avalonia settings parity

Harbor ships two clients: the terminal CLI and the Avalonia desktop app. Both
read the same shared settings from `~/.harbor/config.json`, then add their own
UX settings in `~/.harbor/cli.json` and `~/.harbor/avalonia.json`. This page is
the parity table asked for by item F170 of the `kilocode-features` report
(issue #2680). It describes the tree as it stands; the config records and
[Configuration](./CONFIGURATION.md) stay the source of truth.

The two clients do not expose the same controls. Shared settings are the ones
a user expects to carry over when switching from the terminal to the desktop
app. App-specific settings describe how a client draws or drives itself and
are deliberately not copied between clients.

## Shared settings

All rows live in `config.json` as fields of `CommonConfig` in
`src/Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs`.

| Setting | JSON key | Default | CLI | Avalonia |
| --- | --- | --- | --- | --- |
| API keys per provider | `apiKeys` | empty map | Yes | Yes |
| Default provider | `defaultProvider` | `anthropic` | Yes | Yes |
| Default model | `defaultModel` | `claude-sonnet-4` | Yes | Yes |
| Default agent | `defaultAgent` | `AgentName.Fallback` | Yes | Yes |
| Theme | `theme` | `system` | Yes | Yes, with an app override |
| Storage backend | `storageBackend` | empty, resolved by host | Yes | Yes |
| Log level | `logLevel` | `info` | Yes | Yes |
| File logging | `enableFileLogging` | `true` | Yes | Yes |
| Permission mode | `permissionMode` | `default` | Yes | Yes |
| Tools always allowed or denied | `alwaysAllowTools`, `alwaysDenyTools` | empty lists | Yes | Yes |
| Plugins enabled | `enablePlugins` | `true` | Yes | Yes |
| Plugin directories | `pluginDirectories` | empty | Yes | Yes |
| HTTP proxy and timeout | `httpProxy`, `httpTimeoutSeconds` | empty, `30` | Yes | Yes |
| Compaction budget | `compactionReserveTokens`, `compactionKeepRecentTokens`, `compactionTailTurns` | `16384`, `20000`, `2` | Yes | Yes |
| Onboarding completed | `onboardingCompleted` | `false` | Yes | Yes |

A shared field has one writer path. Both clients go through
`ICommonConfigStore.UpdateAsync`, which performs an atomic read-modify-write
on `config.json`. The CLI also exposes `harbor config` for these fields;
Avalonia writes them from the Settings view
(`apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs`).

## App-specific settings

These fields are not shared. Each client owns its own file and its own
defaults.

| Client | Setting | JSON key | Default |
| --- | --- | --- | --- |
| CLI | Default TUI renderer | `defaultTuiRenderer` | `auto` |
| CLI | Run onboarding wizard | `enableOnboardingWizard` | `true` |
| CLI | Allow renderer swap mid-session | `runtimeSwappable` | `true` |
| CLI | Register slash commands | `enableSlashCommands` | `true` |
| CLI | Disabled builtin tools | `disabledTools` | empty list |
| CLI | Input history file | `historyFile` | empty, means `~/.harbor/history` |
| CLI | History entry cap | `maxHistoryEntries` | `1000` |
| Avalonia | Theme override | `theme` | `system` |
| Avalonia | Window width and height | `windowWidth`, `windowHeight` | `1200`, `800` |
| Avalonia | Open maximized | `windowMaximized` | `false` |
| Avalonia | UI font | `fontFamily` | `Inter` |
| Avalonia | Monospace font | `monospaceFontFamily` | `JetBrains Mono` |
| Avalonia | Show status bar | `showStatusBar` | `true` |
| Avalonia | Show session sidebar | `showSessionSidebar` | `true` |
| Avalonia | Open session tabs | `openTabs` | empty list |

The CLI record is `CliConfig` in
`apps/Harbor.App.Cli/Configuration/CliConfig.cs`. The Avalonia record is
`AvaloniaConfig` in `apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs`.
The Avalonia `theme` field is an override, not a copy of the shared `theme`:
`system` defers to `CommonConfig`, while a literal theme or palette name
forces the desktop app only.

## Environment overrides

An environment variable wins over the persisted value. The full list is in
[Environment variables](./ENV_VARS.md); the ones that matter for parity are
these.

| Variable | Overrides | Clients |
| --- | --- | --- |
| `HARBOR_TUI` | `CliConfig.DefaultTuiRenderer` | CLI only |
| `HARBOR_STORAGE` | `CommonConfig.StorageBackend` | Both |
| `HARBOR_MODEL` | Legacy model setting | CLI only |
| `HARBOR_LOGLEVEL` | `CommonConfig.LogLevel` | Both |
| `OLLAMA_HOST` | Ollama base URL | Both |

## What parity does not mean

The table is a map of where a setting lives, not a promise that both clients
render every shared field in a Settings screen. A new shared field needs a
writer in each client that lets the user change it; a client that has no
control for it still honours the persisted value at startup. Adding another
app also needs a new per-app filename, because the five existing names are
reserved.

There is no gate that compares these tables to the config records. A change
to `CommonConfig`, `CliConfig`, or `AvaloniaConfig` should update this page in
the same commit.

## References

- [Configuration](./CONFIGURATION.md) for file layout, migration, and code
  examples.
- [Environment variables](./ENV_VARS.md) for the complete override list.
- `src/Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs`,
  `apps/Harbor.App.Cli/Configuration/CliConfig.cs`, and
  `apps/Harbor.App.Avalonia/Configuration/AvaloniaConfig.cs` for the fields
  and defaults above.
