# Changelog

All notable changes to Harbor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — v0.2.0-alpha

#### Native LLM providers (3 new)
- `Harbor.Providers.Anthropic` — native Anthropic Messages API
  - `cache_control` (ephemeral, 1h TTL)
  - Extended thinking with `budget_tokens`
  - Fine-grained tool streaming beta
  - Interleaved thinking beta
  - `tool_result` as content block in user message
  - System prompt as separate field (not message)
- `Harbor.Providers.OpenAI` — native OpenAI provider
  - Chat Completions API for GPT-4o, GPT-4.1
  - Responses API for o1, o3, o4-mini, GPT-5+ (auto-detected)
  - `reasoning_effort` parameter
  - `max_completion_tokens` (vs legacy `max_tokens`)
  - `reasoning_content` streaming
- `Harbor.Providers.Ollama` — native Ollama provider
  - NDJSON (not SSE) parsing
  - `/api/chat` endpoint (not `/v1/chat/completions`)
  - `keep_alive` parameter for model persistence
  - No auth required (local)
  - Auto-detects models via `/api/tags`

#### Storage backends (2 new)
- `Harbor.Storage.Memory` — in-memory session storage (for tests, ephemeral)
- `Harbor.Storage.Sqlite` — SQLite-backed storage with indexed queries
  - WAL mode for concurrent reads
  - `PRAGMA cache_size = -8000` (8 MB, not 64 MB)
  - Foreign keys enabled
  - Schema: `sessions`, `messages` tables with indexes

#### TUI renderers (2 new)
- `Harbor.Tui.Plain` — plain text renderer (no ANSI, no colors)
  - For pipes, CI logs, accessibility, file output
  - Writes to any `TextWriter`
- `Harbor.Tui.Spectre` — Spectre.Console renderer
  - Rich panels, tables, markup
  - Better visual formatting
  - Uses Spectre.Console v0.50

#### Builtin tools (1 new)
- `TaskTool` — delegates work to sub-agents
  - Validates sub-agent exists and `IsSubAgent=true`
  - Implements Command pattern
  - Foundation for multi-agent orchestration

#### Sample plugins (4 new, separate projects)
- `Harbor.Plugin.WebSearch` — DuckDuckGo web search (no API key)
  - Demonstrates HTTP-based tool
  - HTML parsing with regex
- `Harbor.Plugin.TodoWrite` — per-session todo list
  - Demonstrates stateful tool (ConcurrentDictionary by SessionId)
  - Add/update/list/complete/clear operations
- `Harbor.Plugin.GitTools` — safe git wrapper
  - Demonstrates process-wrapping tool
  - Blocks dangerous commands (`push --force`, `reset --hard`)
  - Uses `ArgumentList` for safe quoting
- `Harbor.Plugin.FileTree` — directory tree visualization
  - Demonstrates read-only filesystem tool
  - Honors common ignore patterns (node_modules, bin, obj, .git)
  - Custom output formatting

#### Performance optimizations
- `FrozenDictionary<TKey, TValue>` in `ProviderRegistry` and `ToolRegistry`
  - O(1) lookups after `Freeze()` is called
  - Lock-free reads on frozen snapshot
- `IReadOnlyCollection<T>` / `IReadOnlyList<T>` in public APIs
- `ArrayPool<T>` extensions (`RentScoped`) for zero-alloc buffers
- `StringBuilderPool` for hot-path string building
- `Channel<T>` for backpressure-aware streaming in all LLM clients
- Lazy initialization for providers (only load when first used)
- Pre-allocated arrays in hot paths (no LINQ `ToList()` in loops)

#### Infrastructure
- `Directory.Build.targets` — test project detection, AOT defaults, embedded resources
- `BannedApi.txt` — banned APIs (Newtonsoft.Json, Thread.Sleep, etc.)
- Added analyzers:
  - `Microsoft.CodeAnalysis.NetAnalyzers` — performance, async
  - `Microsoft.CodeAnalysis.BannedApiAnalyzers` — banned API enforcement
  - `AsyncFixer` — async best practices
  - `ReflectionAnalyzers` — AOT-friendliness
  - `Meziantou.Analyzer` — comprehensive code quality (180+ rules)
- `MA0046` (unsafe code) set to **error** — project rule: no unsafe ever
- Comprehensive `.editorconfig` with 200+ analyzer severity configurations
- Embedded provider JSON configs (via `<EmbedProviders>true</EmbedProviders>`)
  - Builtin providers work after `dotnet publish` without external files

#### CLI improvements
- `HARBOR_TUI` env var — choose TUI renderer (ansi/plain/spectre)
- `HARBOR_STORAGE` env var — choose storage backend (jsonl/memory/sqlite)
- `harbor tui` command — show TUI options
- `harbor storage` command — show storage options
- `/tui` and `/storage` slash-commands in REPL
- Provider configs auto-discovered from 3 locations:
  1. Embedded resources (ship with binary)
  2. `~/.harbor/providers/` (user-global)
  3. `./providers/` (project-local, dev)

#### Documentation (3 new)
- `docs/GETTING_STARTED.md` — user-facing install/configure/run guide
- `docs/BUILD.md` — build, test, publish, distribute instructions
- `docs/PLUGIN_DEVELOPMENT.md` — write your own plugins (with patterns)

### Changed
- `ProviderRegistry` — now has `Freeze()` method for fast lookups
- `ToolRegistry` — now has `Freeze()` method for fast lookups
- All LLM clients — refactored to use `Channel<T>` pattern (no yield in try/catch)
- `ToolResult` — split into `ToolResult` (from tool) and `ToolResultEntry` (with call ID, in messages)
- `BashTool` — uses `ArgumentList.Add()` instead of `Arguments` string (safe quoting)
- Version bumped to 0.2.0-alpha

### Fixed
- `BashTool` quoting bug — `Arguments = "-c echo hello"` was parsed as 3 args
- `ProviderRegistry` tuple nullable mismatch
- Sonar analyzer `MA0046` — `KeyPressEventArgs` now inherits `EventArgs`

## [0.1.0-alpha] - 2026-07-16

Initial release with core agent loop, 13 JSON providers, 7 tools, JSONL storage, and 65 tests.

### Added
- `Harbor.Abstractions` — all interfaces and models
- `Harbor.Core` — EventBus, AgentLoop, registries, compaction, permissions
- `Harbor.Storage.Jsonl` — JSONL session store
- `Harbor.Providers.OpenAiCompatible` — generic OpenAI-compat client
- `Harbor.Tools.Builtin` — 7 builtin tools (read/write/edit/bash/glob/grep/ls)
- `Harbor.Tui.Abstractions` + `Harbor.Tui.Ansi` — ANSI streaming renderer
- `Harbor.Cli` — entry point
- 13 JSON provider configs
- 65 TUnit tests
- 16 design specification documents
