# Environment variables

Harbor reads its settings from `~/.harbor/config.json` and lets process
environment variables override selected fields. This page is the single
reference for those overrides and for the debug switches that are otherwise
scattered across the guides. The provider keys are also listed in the
[global AGENTS.md](../AGENTS.md#env-var-quick-reference), and the config keys
the variables override are in [Configuration, §6](./CONFIGURATION.md#6-environment-variable-overrides).

The canonical spelling of every `HARBOR_*` variable lives in
[HarborEnv.cs](../src/Harbor.Abstractions.Contracts/Env/HarborEnv.cs). Read sites
reference those constants, so a typo is a compile error rather than a silent
misconfiguration.

## How to read the tables

- **Default** is what the read site uses when the variable is unset. "config"
  means the value comes from `HarborConfig` first and then from a built-in
  default.
- **Debug-only** marks variables meant for diagnosis, benchmarks, demos or CI.
  They are off when unset and are not needed for normal use.
- A switch is enabled by setting it to `1` or `true` unless the Purpose column
  says otherwise.

## Provider credentials

Each provider declares its own `authEnvVar` in `providers/<id>.json`, so the
table in AGENTS.md is the index and the JSON files are the source of truth.

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `KILO_API_KEY` | Kilocode key; the free models need no card | unset | No |
| `ANTHROPIC_API_KEY` | Anthropic key | unset | No |
| `OPENAI_API_KEY` | OpenAI key | unset | No |
| `OPENROUTER_API_KEY` | OpenRouter key | unset | No |
| `DEEPSEEK_API_KEY` | DeepSeek key | unset | No |
| `GROQ_API_KEY` | Groq key | unset | No |
| `MISTRAL_API_KEY` | Mistral key | unset | No |
| `XAI_API_KEY` | xAI key | unset | No |
| `TOGETHER_API_KEY` | Together key | unset | No |
| `FIREWORKS_API_KEY` | Fireworks key | unset | No |
| `CEREBRAS_API_KEY` | Cerebras key | unset | No |
| `OLLAMA_HOST` | Base URL of a local Ollama server | `http://localhost:11434` | No |

> Kilocode's variable is `KILO_API_KEY`, not `KILOCODE_API_KEY`. The two local
> providers, Ollama and vLLM, need no key.

## Paths and home

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_HOME` | Root for config, sessions, spill and plugins; must be absolute | `~/.harbor` | No |
| `HARBOR_THEMES_DIR` | Directory the theme picker scans for extra `*.json` palettes | `~/.harbor/themes` | No |
| `HARBOR_THEME_FILE` | Theme JSON that takes precedence over the stored theme path | `~/.harbor/theme.json` when present | No |
| `HARBOR_PROJECT_PLUGINS` | Project directory for the out-of-process CS-plugin host | `plugins-cs` | No |
| `HARBOR_PLUGINS_DIR` | Plugin directory the CS loader reads and writes | `$HARBOR_HOME/plugins` | No |
| `HARBOR_MCP_CONFIG` | Explicit `mcp.json` path instead of the default lookup | unset | No |

## Core wiring

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_STORAGE` | Session store id: `jsonl`, `memory`, `sqlite` | config `storageBackend` | No |
| `HARBOR_SESSION_TIER` | Log tier: `local` or `keyless` (scrubs provider credentials) | `local` | No |
| `HARBOR_MODE` | Host composition: `inprocess`, `ipc-server`, `ipc-client` | `inprocess` | No |
| `HARBOR_MODEL` | Model id in `provider/model` form | config / fallback model | No |
| `HARBOR_TUI` | Startup renderer id: `plain`, `ansi`, `cellforge`; `consoleex` is a legacy alias | config `defaultTuiRenderer` | No |
| `HARBOR_LOGLEVEL` | Minimum console level: `Trace`, `Debug`, `Information`, `Warning`, `Error` | config `logLevel` | No |
| `HARBOR_SKIP_ONBOARDING` | Skip the first-run wizard when set to any non-empty value | unset | No |

## IPC and daemon

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_IPC_PIPE` | Named pipe / UDS socket name for the daemon connection | `harbor-ipc` | No |
| `HARBOR_LISTEN` | Optional TCP listener: `uds`, `loopback`, `tailscale0`, `all` | unset (local UDS only) | No |
| `HARBOR_PORT` | TCP port for the optional networked listener | `48710` | No |
| `HARBOR_MCP_OAUTH_TOKEN` | Bearer token for remote MCP servers; bypasses the OAuth cache | unset | No |

## Desktop and TUI

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_SHELL` | Avalonia shell id: `classic` or experimental `orca` | `classic` | No |
| `HARBOR_THEME` | Startup theme mode: `dark`, `light`, `system` | config | No |
| `HARBOR_LOCALE` | Locale override used by `UiStrings` | config | No |
| `HARBOR_TUI_RUNTIME_SWAP` | Renderer ids allowed in a mid-session swap | unset | No |
| `HARBOR_NO_APPROVER` | Suppress the interactive CellForge approval prompt | unset | No |
| `HARBOR_NO_AUTOTITLE` | Suppress automatic AI session titling in the REPL | unset | No |
| `HARBOR_TUI_NO_BUILTIN_PANELS` | Opt out of built-in panels in the contrib SpectreTUI renderer | unset | Yes |
| `HARBOR_CELLFORGE_WINDOWS` | Windows-only opt-out of CellForge during renderer selection | unset | Yes |
| `HARBOR_LEGACY_SETUP` | Send the first-run wizard back to the non-CellForge flow | unset | Yes |
| `HARBOR_INLINE_IMAGE` | Override inline-image detection: `off`, `osc1337`, `kitty` | terminal probe | No |
| `HARBOR_OSC777` | Force or suppress OSC 777 desktop notifications | terminal probe | No |
| `HARBOR_MASCOT` | Mascot rendering level in CellForge chat widgets | unset | No |
| `HARBOR_MASCOT_MODE` | Mascot expression mode (blinks, ambient state) | unset | No |
| `HARBOR_DIFFPLEX` | Enable the DiffPlex-based diff provider in the UI framework | unset | Yes |

## Telemetry

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_TELEMETRY_EXPORT` | Visible sink: `none`, `console`, `file` | `none` | No |
| `HARBOR_TELEMETRY_EXPORT_PATH` | File appended to by the `file` sink | unset | No |
| `HARBOR_TELEMETRY_MAX_RECORD_BYTES` | Cap on one exported record, in bytes | implementation default | Yes |
| `HARBOR_TELEMETRY_MAX_BUFFER_BYTES` | Cap on bytes buffered before dropping | implementation default | Yes |

## Demos, experiments and diagnostics

These are the switches the global AGENTS.md table does not cover. All are
off when unset.

| Variable | Purpose | Default | Debug-only |
| --- | --- | --- | --- |
| `HARBOR_DEMO` | Mark the process as a scripted `harbor demo` run | unset | Yes |
| `HARBOR_DEMO_TUI` | Renderer id the `harbor demo` command was asked to use | unset | Yes |
| `HARBOR_DEMO_STEP_MS` | Delay between scripted demo steps, in milliseconds | implementation default | Yes |
| `HARBOR_SKILL_FRESHNESS` | Opt in to the experimental Skill freshness panel | unset | Yes |
| `HARBOR_WITH_ALL_PROVIDERS` | Register every provider; unset registers only Ollama | unset | Yes |
| `HARBOR_E2E` | Run live E2E tests against a real provider | unset (tests skip) | Yes |
| `HARBOR_IPC_EVENTSTREAM` | Run the Linux named-pipe event-stream tests | unset (tests self-skip) | Yes |
| `HARBOR_UPDATE_GOLDENS` | Rewrite renderer goldens instead of comparing them | unset | Yes |
| `HARBOR_PERF_REPORT` | Write the renderer performance report to this path | unset | Yes |

## See also

- [Configuration](./CONFIGURATION.md) — config keys and precedence.
- [Getting started](./GETTING_STARTED.md) — first-run setup and provider keys.
- [Troubleshooting](./TROUBLESHOOTING.md) — symptom to action table.
- [E2E testing](./E2E_TESTING.md) — how the E2E variables are used in CI.
