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
or pulled into the CLI build: `Harbor.Hosting` compiles the alternative renderers in
only when built with `HarborWithSpectreTui=true` (NUKE/distro builds, or
`-p:HarborWithSpectreTui=true` on the command line; Sixel is not in that set and never
joins the CLI build) — plain `dotnet build` / `dotnet run` does NOT include them,
so `HARBOR_TUI=spectre*|terminal-gui|termina|razor` falls back to `plain` there. Pass `-p:HarborWithSpectreTui=false`
(or set `HARBOR_MINIMAL=true`) explicitly for a core-only binary:

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

### Renderer selection (build-time, slice A of #1144)

The runtime renderer stays `HARBOR_TUI` (untouched). The build-time selection
is `-p:HarborWithRenderer=all|cellforge|ansiplain`
(default `all`) or `./build.sh Publish --with-renderer <value>`:

```bash
# CLI without the optional renderer families
./build.sh Publish --with-renderer cellforge --dry-run
```

What each value excludes today:

| Value | Effect |
|---|---|
| `all` (default) | Every backend (status quo). |
| `cellforge`, `ansiplain` | The exclusion values of the #1144 slice. `HarborWithRenderer` gated exactly one optional family, the SharpConsoleUI backend, and that family is gone (ADR-019), so today all three values compose the same graph. |

CellForge and AnsiPlain stay in every CLI build on purpose: the agent-turn
driver (`apps/Harbor.App.Cli/Repl/ReplRunner.cs`, used by both `ask` and the
REPL) is CellForge-bound source, and the plain-pipe path plus the headless
pairing QR resolve through AnsiPlain (`CliInfrastructure.cs`). Decoupling them
into composable backends is the tracked remainder of #1144 gap (1) — the issue
stays open until then. Unknown values fail the build via the `Error` guards in
`src/Harbor.Hosting/Harbor.Hosting.csproj`; `Publish`/`Release`/`PublishIpc*`
targets forward the flag automatically.

**The `nickconsoleex` value is gone.** The SharpConsoleUI renderer left the
product and moved to `contrib/` (ADR-019), so there is no third-party submodule
to fetch and no vendored tree to compose. `HarborWithRenderer` accepts `all`,
`cellforge` and `ansiplain`; `nickconsoleex` is not one of them any more, and
passing it fails the build the same way any other unknown value does. A build
that wants that renderer has to point at the wrapper project in
`contrib/tui/Harbor.Tui.NickConsoleEx/` by hand.

### Daemon on/off (build-time, slice A of #1144)

How the pieces relate today (`build/` mentioned the daemon zero times):

- `harbor daemon start` spawns this same executable with `--headless`
  (`DaemonCommand.cs` → `HeadlessVerb.cs`), which requires an `IHarborServer`
  from the `Harbor.Ipc.Server` assembly and defaults `HARBOR_MODE=ipc-server`.
- `harbor status --all` and `harbor ide` are IPC *clients* (transports and
  `IdeSessionBridge` from the `Harbor.Ipc.Client` assembly).
- `PublishIpcServer`/`PublishIpcClient` only tag the binary with
  `HarborMode=ipc-server|ipc-client` (informational MSBuild property); the
  runtime mode switch is `HARBOR_MODE`, resolved by `HarborModeRegistry`.

The flag is `-p:HarborWithDaemon=false` or
`./build.sh Publish --with-daemon false` (default `true`; forced to `false`
by `HARBOR_MINIMAL=true` like the other granular flags):

```bash
# Single-process binary: no Server/Client assemblies, HARBOR_MODE=inprocess only
./build.sh Publish --with-daemon false --dry-run
```

Effect and degradation:

| Off (`false`) | Result |
|---|---|
| `Harbor.Ipc.Server` + `Harbor.Ipc.Client` refs | Excluded from CLI + Hosting. |
| Mode registry | `inprocess` strategy only (`HarborModeRegistry.cs`). |
| `status`, `ide` verbs | Excluded from the compile; print a rebuild hint. |
| `daemon start` / `--headless` | Still accepted; fail with "IPC server unavailable". |
| `daemon stop` / `daemon status` | Kept (pid-file ops need no IPC types). |
| `PublishIpcServer`/`PublishIpcClient` | Fail fast with the fix attached. |

## Run tests

```bash
# Run a specific test project — the known-good pattern (Release, no rebuild).
# Tests run as plain executables, one project at a time; this is what CI does.
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1

# Run a specific test class — TUnit uses --treenode-filter, not --filter,
# and it is forwarded to the test host after --
dotnet run --project tests/Harbor.Abstractions.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/IdentifiersTests/*"

# Run with detailed output
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --output Detailed
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) — fastest .NET test framework, source-generated.

> **Note:** `dotnet test` is not used in this repo and no CI job runs it, so its
> behaviour here is unverified. See
> [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test).

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

See [specs/08-native-aot.md](./specs/08-native-aot.md) for details.

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

## The CI build-output cache

The `build` job of `.github/workflows/ci.yml` restores `obj/` and `bin/` from
the GitHub Actions cache before it compiles anything (issue #1927).

MSBuild is already incremental: `CoreCompile` skips a project whose output is
newer than its inputs. CI was cold only because those two directories did not
exist, so every pull request recompiled the whole solution. The cache hands
the outputs back and lets MSBuild decide the fine-grained work — the build
itself (what is compiled, which flags, which configuration) does not change.

### The key

```
msbuild-v2-<os>-<arch>-sdk<sdk>-nuget<packages path>-<props hash>-<sha>
```

| field | what it pins | why it is in the key |
|---|---|---|
| `<os>-<arch>` | the runner family | the job is pinned to `ubuntu-24.04`; a runner that moves moves the compiler with it |
| `sdk<sdk>` | the RESOLVED SDK version (`dotnet --version`) | a different compiler writes different bytes into the same `obj/` |
| `nuget<packages path>` | the NuGet global-packages folder | the restored `obj/*.csproj.nuget.g.props` and `obj/project.assets.json` carry ABSOLUTE paths into it, so an entry is only valid for the folder it was built against |
| `<props hash>` | `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `global.json` | the package graph, the compiler flags and the SDK |
| `<sha>` | the commit the outputs were built from | rolls the entry forward, and it is also how the mtime step below learns which commit the restored outputs belong to |

The key is deliberately COARSE: it names the toolchain, not the source tree.
`restore-keys` holds the same prefix without the `<sha>`, so a run that finds
no entry for its own commit restores the most recent entry built from the
same toolchain — the base branch's cache.

The `v2` is the human version of the cache. It moves when the path list moves:
the path list is part of the cache's own version (a hash of its lines), so a
changed list already hides the entries written under the old one. Naming it in
the key makes the cold start readable — the lane says "no build-output cache
entry for this prefix — this lane compiles from scratch" — instead of a
restore that silently misses.

The NuGet folder is pinned to `/home/runner/.nuget/packages` in the job's
environment. That IS the runner default; it is written down explicitly so
that the key and the restore agree by construction, and a runner that moves
the folder gets a cold build instead of a wrong one.

The cached payload is `obj/` and `bin/` minus the runtime assets (the native
libraries of every RID) and the satellite assemblies: those are copies of what
the NuGet folder already holds, nothing in MSBuild's up-to-date check reads
them, and the build re-copies them. They were the bulk of the payload, and the
repository's cache budget is 10 GB. The debug symbols STAY: a missing `.pdb`
is a missing OUTPUT of `CoreCompile`, and MSBuild treats a missing output as
"must build" (issue #2117).

`tools/bin` does NOT go into the cache. It is the COMMITTED gate binary, not
a build output: the glob `**/bin` matched it, the restore unpacked the base
commit's copy of it over the one the checkout had just written, and the lane
then ran a gate that was not in the tree it was checking (pull request 2170,
run 39659933127 — "unknown gate: check-compiled-tree"). A cache of build
outputs must never be able to replace a committed artifact. The `build` job
also proves it on every lane: it restores `tools/bin` from the checkout and
checks it against `tools/bin/SHA256SUMS` before it runs a gate out of it.

### The mtime trap, and what the cache does about it

`actions/checkout` writes every source file with the CURRENT clock — git does
not store mtimes. A restored output is older than "now", so every project
looks stale and MSBuild recompiles the whole solution. The cache would buy
nothing.

`tools/bin/harbor-gates-linux-x64 normalize-mtimes` puts the mtimes back where
the truth is:

- the restored outputs (everything under `obj/` and `bin/`) get the current
  time, so they are newer than the other things MSBuild compares them
  against — the analyzer assemblies in the NuGet folder above all, which a
  restored NuGet cache extracts at the start of every run;
- a file whose content DIFFERS from the commit the cache was built from
  gets one second after that, so MSBuild rebuilds it, and through the
  project graph its dependents;
- a file whose content is IDENTICAL to it gets that commit's time, which is
  older than the restored outputs, so MSBuild skips it.

The first rule keeps the build honest: a changed file must be NEWER than the
output that was built from the old version of it, or the old output survives
and the build is silently stale. The second rule is what makes the cache pay
off.

The base commit is read out of the cache key that matched, so the tool keeps
no state of its own. If the base cannot be resolved (the branch that produced
the entry is gone and the commit cannot be fetched), the tool exits 3 and the
job DISCARDS the restored outputs and rebuilds from scratch: a cache whose
base is unknown cannot be told apart from a stale one, and a stale build is
worse than a slow one.

### The safety net: did the lane compile the tree it was given?

The normalisation above decides what MSBuild may skip, and a decision that is
wrong hides a COMPILE ERROR: a skipped project is never handed to the
compiler, so the error in it never reaches the build. That is issue #2100,
and the `build` job now checks the decision instead of trusting it.

`tools/bin/harbor-gates-linux-x64 check-compiled-tree` runs right after the
compile and asks, for every project whose SOURCES changed since the commit
the restored outputs were built from: was the project COMPILED in this run?

- the CHANGED set is read by CONTENT, and against the WORKING TREE — the
  tree the compiler read, not `HEAD`. A file the lane patched after the
  checkout is part of what was compiled, and the gate has to see it;
- the BUILD START is a marker file the workflow touches between the
  normalisation and the compile. Every output the compile produced is newer
  than it, and every output the compile skipped kept the one the cache
  carried, which is older. The marker is the one fact the normalisation
  cannot reach: it is written after it, and nothing it does can move it.

An output that is older than the build start, in a project whose sources
changed, is a project the compile skipped — and the gate turns that into a
RED lane. Without it, a tree that does not compile can ship behind a green
build.

The gate has a self-test (the planted compile error, with the outputs the
cache left behind) and it can be shown failing on the real lane: the `ci`
workflow takes a `plant_compile_error` input, and a dispatch with it plants
the shape of #2100 in the working tree — a source file of a project the cache
holds, carrying a compile error, identical in the cached base and in `HEAD`,
with the base commit's mtime. The compile then skips the project and stays
green, and the gate turns the lane red. It needs the cache, so the demo is a
pair of dispatches: the first one warms the cache, the second one plants the
error.

### Who writes, who only reads

| lane | restores | writes |
|---|---|---|
| pull request | yes | NEVER — one entry per pull request would fill the repository's 10 GB cache budget with pull-request entries and evict the ones the base branch needs |
| push to `dev` / `master` | yes | yes |
| manual `workflow_dispatch` | yes | yes — the deliberate way to exercise the cache end to end on a branch that is not `dev`/`master` |

The `aot-publish` job is deliberately OUT of the cache: it publishes
RID-specifically and self-contained (`-r linux-x64 --self-contained true`),
and its intermediate outputs live in `obj/Release/net10.0/linux-x64/`, which
is a different configuration from the one the cache holds.

### How to bust it

- **By itself.** The key moves when the SDK, `Directory.Packages.props`,
  `Directory.Build.props`, `Directory.Build.targets`, `global.json` or the
  NuGet folder moves — a package bump or an SDK bump is a cold build with no
  action needed.
- **On purpose.** Delete the entries (GitHub → the repository → Actions →
  Caches, or `gh cache delete --all`), or change the `v2` prefix in the key.
- **Locally.** `find . -type d \( -name obj -o -name bin \) -prune -exec rm -rf
  {} +` — the same thing the "Clean build artifacts" section above does.
- **If a restored cache is suspected of being wrong.** The mtime step throws
  away a cache whose base it cannot resolve. Anything else: delete the
  entries and re-run.

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
      # NOTE: per-project test invocations as plain executables — one process
      # per project, matching ci.yml. Use the NUKE build or loop over tests/*/.
      - run: for t in tests/Harbor.*.Tests; do dotnet run --project "$t" -c Release --no-build -- --minimum-expected-tests 1; done

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
│   ├── Harbor.Registries/            # Agent/Tool/Provider registries
│   ├── Harbor.Application/           # sessions, permissions, configuration
│   ├── Harbor.Hosting/               # DI modules wired by the CLI
│   ├── Harbor.Storage.{Jsonl,Memory,Sqlite}/ # session stores
│   ├── Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible,Shared}/ # LLM clients
│   ├── Harbor.Tools.Builtin/         # builtin tools under Tools/
│   ├── Harbor.Plugins.{Abstractions,Compilation,Instantiation,Registration,
│   │                    Hosting,Runtime,Storage}/ # Roslyn CS-source plugin system
│   ├── Harbor.Ipc.{Abstractions,InProcess,Client,Server}/ # daemon/remote IPC
│   ├── Harbor.Terminal.Abstractions/ + Harbor.Tui.{AnsiPlain,CellForge,Notifications}
│   └── ...                           # Ui.Framework*, Desktop.*, Telemetry.*, Logging, ...
├── apps/
│   ├── Harbor.App.Cli/               # CLI entry point (PackageId Harbor.App.Cli)
│   └── Harbor.App.Avalonia/          # cross-platform desktop GUI
├── contrib/                          # optional renderers/apps/scripting (Contrib.slnx),
│                                     # plus the unbuilt vendored ConsoleEx tree (ADR-019)
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
- **Microsoft.CodeAnalysis.BannedApiAnalyzers** — banned APIs (see `BannedSymbols.txt`)
- **AsyncFixer** — async best practices
- **ReflectionAnalyzers** — reflection correctness (binding flags, overloads, `nameof`); not a dynamic-code rule — see [`docs/ANALYZERS.md`](./ANALYZERS.md#reflection-rules-reflectionanalyzers-031)
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
