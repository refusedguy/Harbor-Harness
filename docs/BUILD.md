# Build & Publish Guide

> How to build, test, publish, and distribute Harbor.

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

Alternative TUI renderers (Spectre, Spectre.Fullscreen, SpectreTui,
TerminalGui, Termina, RazorConsole, Sixel), desktop apps (Wpf / Maui /
Blazor) and the scripting stack (SharpTS/Jint) live under `contrib/` and
are **not** part of `Harbor.slnx`. Build them explicitly:

```bash
# All contrib projects that build on the current OS
dotnet build contrib/Contrib.slnx

# Contrib tests (TUnit)
dotnet run --project contrib/tests/Harbor.Tui.Contrib.Tests
dotnet run --project contrib/tests/Harbor.Scripting.Tests
```

Platform notes: `Harbor.App.Wpf` needs the Windows Desktop workload,
`Harbor.App.Maui` needs `maui-tizen`; both are commented out in
`contrib/Contrib.slnx` so the solution builds on Linux/macOS. The CLI still
compiles the alternative renderers in when `HarborWithSpectreTui=true`
(default) — pass `-p:HarborWithSpectreTui=false` (or `HARBOR_MINIMAL=true`)
for a core-only CLI binary.

## Run tests

```bash
# Run all tests
dotnet test

# Run a specific test project
dotnet test tests/Harbor.Core.Tests

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run a specific test
dotnet test tests/Harbor.Abstractions.Tests --filter "FullyQualifiedName~IdentifiersTests"
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) — fastest .NET test framework, source-generated.

Current test status: **65 passed, 1 skipped** across 4 test projects.

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

# Install globally
dotnet tool install --global --add-src ./apps/Harbor.App.Cli/nupkg Harbor.Cli

# Use
harbor
```

## Provider configs after publish

Provider JSON configs are **embedded as resources** in the binary (via `<EmbedProviders>true</EmbedProviders>` in `Harbor.Cli.csproj`). This means:

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
      - run: dotnet test --no-build

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
├── src/                          # 12 source projects
│   ├── Harbor.Abstractions/      # interfaces, models (zero deps)
│   ├── Harbor.Core/              # base implementations
│   ├── Harbor.Storage.Jsonl/     # JSONL session store
│   ├── Harbor.Storage.Memory/    # in-memory store
│   ├── Harbor.Storage.Sqlite/    # SQLite store
│   ├── Harbor.Providers.Anthropic/    # native Anthropic
│   ├── Harbor.Providers.OpenAI/       # native OpenAI
│   ├── Harbor.Providers.Ollama/       # native Ollama
│   ├── Harbor.Providers.OpenAiCompatible/ # generic OpenAI-compat
│   ├── Harbor.Tools.Builtin/     # 7 builtin tools + task tool
│   ├── Harbor.Tui.Abstractions/  # TUI interfaces
│   ├── Harbor.Tui.Ansi/          # ANSI streaming renderer
│   ├── Harbor.Tui.Plain/         # plain text renderer
│   ├── Harbor.Tui.Spectre/       # Spectre.Console renderer
│   └── Harbor.Cli/               # entry point
├── samples/plugins/              # 4 sample plugins
│   ├── Harbor.Plugin.WebSearch/
│   ├── Harbor.Plugin.TodoWrite/
│   ├── Harbor.Plugin.GitTools/
│   └── Harbor.Plugin.FileTree/
├── tests/                        # 4 test projects (65 tests)
├── providers/                    # 13 JSON provider configs
├── specs/                        # 16 design documents
└── docs/                         # user + dev guides
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
