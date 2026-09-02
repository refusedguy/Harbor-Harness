# Build & Publish Guide

> How to build, test, publish, and distribute Harbor.

## Rebuild-set (спринт 5v2, Ф1 — замер после развязки Abstractions)

Методика: полный билд до установившегося состояния → правка-проба (строка
комментария в файле целевого слоя) → `dotnet build Harbor.slnx --no-restore -v:d`
→ три счётчика: **артефакт-строки** (`X -> path`, проекты с запущенным
Build-пайплайном), **реальных Csc-компиляций**, **физических копий файлов**
(`Copying file from`). Замеры 2026-08-24, Debug, одна машина; шум ±3 копии.

| Сценарий | артефакт-строки | Csc-компиляций | скопировано файлов |
|---|---|---|---|
| ДО (e6c9128): правка `Harbor.Domain` (модели) | 78 | 1 | 228 |
| ДО (e6c9128): правка `Harbor.Extensions` | 78 | 1 | 222 |
| ПОСЛЕ: правка `Harbor.Abstractions.Contracts` (модели) | 78 | 1 | 225 |
| ПОСЛЕ: правка `Harbor.Extensions` | 78 | 1 | **81** |

Выводы:

1. **Extensions: 222 → 81 физических копий (−63%)** — хелперы больше не
   ре-экспортируются фасадом, их получают только 3 прямых потребителя.
2. Правка доменных моделей каскад не сокращает (78/78, ~225 копий): модели
   потребляют все 78 проектов решения — связь существенная, а не случайная
   (см. ADR-007: PermissionRuleset в подписях агентов, Pricing внутри ModelInfo).
3. «95 проектов» из ранних оценок соответствуют старому составу solution;
   текущий граф даёт 78 артефакт-строк на любую правку нижних слоёв — это
   копировальный каскад MSBuild, а не перекомпиляция: детерминированные сборки
   держат число реальных Csc равным 1 во всех сценариях.

## Prerequisites

- .NET 10 SDK (https://dot.net)
- Git
- Optional: Docker (for cross-platform builds)

## Build from source

```bash
git clone https://github.com/refusedguy/Harbor-Harness
cd Harbor-Harness

# Restore + build the main solution (production core)
dotnet build Harbor.slnx

# Build Release configuration
dotnet build -c Release
```

### contrib/ — optional extensions (separate build)

Alternative TUI renderers (`contrib/tui/`: Spectre, Spectre.Fullscreen,
SpectreTui, TerminalGui, Termina, RazorConsole, Sixel), desktop apps
(Wpf / Maui / Blazor) and the scripting stack (SharpTS/Jint) live under `contrib/`
and are **not** listed in `Harbor.slnx`. They are built by `contrib/Contrib.slnx`
or pulled into the CLI build: the CLI compiles the alternative renderers in when
`HarborWithSpectreTui=true` (the default) — pass
`-p:HarborWithSpectreTui=false` (or set `HARBOR_MINIMAL=true`) for a core-only binary:

```bash
# All contrib projects that build on the current OS
dotnet build contrib/Contrib.slnx

# Contrib tests (TUnit)
dotnet run --project contrib/tests/Harbor.Tui.Contrib.Tests
dotnet run --project contrib/tests/Harbor.Scripting.Tests
```

Platform notes: `Harbor.App.Wpf` needs the Windows Desktop workload,
`Harbor.App.Maui` needs `maui-tizen`; both are commented out in
`contrib/Contrib.slnx` so the solution builds on Linux/macOS.

Other CLI feature flags exclude categories of project references:
`-p:HarborWithPlugins=false` (removes the Roslyn plugin loader),
`-p:HarborWithAllProviders=false` (keeps only Ollama); `HARBOR_MINIMAL=true`
is the legacy shorthand forcing all of them off. The NUKE build
(`build/_build/`) exposes these as command-line flags.

## Run tests

```bash
# Run a specific test project — the known-good pattern (Release, no rebuild)
dotnet test tests/Harbor.Core.Tests -c Release --no-build

# Run a specific test class — TUnit uses --treenode-filter, not --filter
dotnet test tests/Harbor.Abstractions.Tests -c Release --no-build \
  --treenode-filter "/*/*/IdentifiersTests/*"

# Run with detailed output
dotnet test tests/Harbor.Core.Tests -c Release --no-build --logger "console;verbosity=detailed"
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) — fastest .NET test framework, source-generated.

> **Known limitation:** running the whole `Harbor.slnx` suite with a single
> `dotnet test` breaks under the MTP host. Always target one test-project
> directory at a time (`tests/<Project>`); there are 28 of them.

## Run the CLI

```bash
# Interactive REPL
dotnet run --project apps/Harbor.App.Cli

# One-shot prompt
dotnet run --project apps/Harbor.App.Cli -- ask "Hello"

# Show help
dotnet run --project apps/Harbor.App.Cli -- help
```

## Publish as a single binary

### Framework-dependent (small, requires .NET 10 runtime)

```bash
dotnet publish apps/Harbor.App.Cli -c Release -o ./publish

# Run
./publish/harbor
```

Size: ~5 MB.

### Self-contained (no .NET runtime needed)

```bash
dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 --self-contained -o ./publish-linux

# Cross-platform variants:
dotnet publish apps/Harbor.App.Cli -c Release -r osx-arm64 --self-contained -o ./publish-osx
dotnet publish apps/Harbor.App.Cli -c Release -r win-x64 --self-contained -o ./publish-win
```

Size: ~80 MB (includes .NET runtime).

### Single-file self-contained

```bash
dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

Size: ~80 MB, but a single executable.

### NativeAOT (smallest, fastest startup — experimental)

```bash
dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 -p:PublishAot=true -o ./publish-aot
```

Size: ~5-10 MB. Startup: <50ms. RSS: <30MB.

**Note:** NativeAOT has limitations:
- No `AssemblyLoadContext` collectible (plugin hot-reload)
- No reflection emit
- Source-gen JSON required
- Some libraries may not be AOT-compatible

See [specs/08-native-aot.md](../specs/08-native-aot.md) for details.

## Publish as `dotnet tool`

```bash
# Pack
dotnet pack apps/Harbor.App.Cli -c Release

# Install globally (package id is Harbor.App.Cli; binary is `harbor` via ToolCommandName)
dotnet tool install --global --add-src ./apps/Harbor.App.Cli/nupkg Harbor.App.Cli

# Use
harbor
```

## Provider configs after publish

Provider JSON configs are **embedded as resources** in the binary (via `<EmbedProviders>true</EmbedProviders>` in `Harbor.App.Cli.csproj`). This means:

- Builtin providers (anthropic, openai, openrouter, kilocode, deepseek, groq, etc.) work out of the box.
- User overrides: place JSON in `~/.harbor/providers/<name>.json` — these take precedence.
- Project-local: place JSON in `./providers/<name>.json` — these also work.

To verify after publish:
```bash
./publish/harbor providers
# Should list all builtin providers
```

## Multi-platform CI (GitHub Actions)

```yaml
# .github/workflows/build.yml
name: Build
on: [push, pull_request]
jobs:
  build:
    strategy:
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet build -c Release --warnaserror
      # NOTE: per-project test invocations — whole-solution dotnet test breaks
      # under the MTP host. Use the NUKE build or loop over tests/*/.
      - run: for t in tests/Harbor.*.Tests; do dotnet test "$t" -c Release --no-build; done

  publish:
    needs: build
    if: startsWith(github.ref, 'refs/tags/v')
    strategy:
      matrix:
        include:
          - os: ubuntu-latest
            rid: linux-x64
          - os: ubuntu-latest
            rid: linux-arm64
          - os: macos-latest
            rid: osx-arm64
          - os: windows-latest
            rid: win-x64
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet publish apps/Harbor.App.Cli -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o ./publish
      - uses: actions/upload-artifact@v4
        with:
          name: harbor-${{ matrix.rid }}
          path: ./publish/
```

## Project structure

```
harbor/
├── src/                              # 50 source projects (Clean/Hexagonal layering)
│   ├── Harbor.Abstractions/          # base contracts (zero deps)
│   ├── Harbor.Abstractions.Contracts/ # models, events, ValueObjects, PermissionRuleset
│   ├── Harbor.Core/                  # EventBus, AgentLoop, config, compaction
│   ├── Harbor.Registries/            # Agent/Tool/Provider registries
│   ├── Harbor.Application/           # sessions, permissions, configuration
│   ├── Harbor.Hosting/               # DI modules wired by the CLI
│   ├── Harbor.Storage.{Jsonl,Memory,Sqlite}/ # session stores
│   ├── Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible,Shared}/ # LLM clients
│   ├── Harbor.Tools.Builtin/         # builtin tools under Tools/
│   ├── Harbor.Plugins.{Abstractions,Compilation,Instantiation,Registration,
│   │                    Hosting,Runtime,Storage}/ # Roslyn CS-source plugin system
│   ├── Harbor.Ipc.{Abstractions,InProcess,Client,Server}/ # daemon/remote IPC
│   ├── Harbor.Terminal.Abstractions/ + Harbor.Tui.{Ansi,Plain,ConsoleEx,Notifications}
│   └── ...                           # Ui.Framework*, Desktop.*, Telemetry.*, Logging, ...
├── apps/
│   ├── Harbor.App.Cli/               # CLI entry point (PackageId Harbor.App.Cli)
│   └── Harbor.App.Avalonia/          # cross-platform desktop GUI
├── contrib/                          # optional renderers/apps/scripting (Contrib.slnx)
├── samples/plugins/                  # 4 legacy DLL-based sample plugins
├── samples/plugins-cs/               # CS-source sample plugin(s)
├── samples/mcp/                      # sample MCP servers
├── providers/                        # JSON LLM provider configs (embedded via EmbedProviders)
├── specs/                            # design specification documents
├── tests/                            # 27 test-project directories (TUnit)
└── docs/                             # user + dev guides
```

## Code analyzers

The project uses these analyzers (configured in `Directory.Build.props` and `.editorconfig`):

- **Roslynator.Analyzers** — code style, refactoring suggestions
- **SonarAnalyzer.CSharp** — code quality, security
- **Microsoft.CodeAnalysis.NetAnalyzers** — performance, async
- **Microsoft.CodeAnalysis.BannedApiAnalyzers** — banned APIs (see `BannedApi.txt`)
- **AsyncFixer** — async best practices
- **ReflectionAnalyzers** — AOT-friendliness
- **Meziantou.Analyzer** — comprehensive code quality

All warnings are treated as errors.

## Clean build artifacts

```bash
# Remove bin/obj from all projects
find . -type d -name "bin" -exec rm -rf {} + 2>/dev/null
find . -type d -name "obj" -exec rm -rf {} + 2>/dev/null

# Or use dotnet clean
dotnet clean
```

## Troubleshooting build

### NU1603: Package version not found

Update to latest version in `Directory.Build.props` or specific `.csproj`. Run `dotnet restore --force`.

### NU1903: Known vulnerability in package

Update the package to a non-vulnerable version. Check the advisory link in the error.

### CS1591: Missing XML doc comment

Add `/// <summary>` to public members, or set `<GenerateDocumentationFile>false</GenerateDocumentationFile>` for the project.

### MA0046: EventArgs required

Use `EventHandler<TEventArgs>` where `TEventArgs : EventArgs`. Create a wrapper class if needed.

### Test discovery fails

Make sure test project has `<OutputType>Exe</OutputType>` (set automatically by `Directory.Build.targets`).
