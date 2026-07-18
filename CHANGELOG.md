# Changelog

All notable changes to Harbor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed — Code Principles Audit: Sprint 1 + Sprint 2 fixes

Resolved 10 of the 11 critical/high findings from `docs/CODE_PRINCIPLES_AUDIT.md`
(one — §PERF-005 — partial with documented decision). Each fixed finding now carries
a `✅ RESOLVED` block in the audit doc; acknowledged-but-deferred findings carry
`⚠️ ACKNOWLEDGED` or `⚠️ PARTIAL`.

**Critical (Sprint 1):**

- **§ROP-002** — `PermissionService.CheckAsync`/`GetRuleset` now pattern-match
  `Result<AgentName>` instead of calling `.Value` (which threw on invalid input).
  Invalid agent names return `Result.Failure<PermissionResponse>` /
  `PermissionRuleset.Empty` instead of crashing the call stack.
- **§OOP-001** — `OpenAiCompatibleLlmClient._toolCallIndexToId` field removed; the
  tool-call index→id map is now a local `Dictionary<int, string>` inside
  `StreamAsync`, threaded into `MapChunk`/`MapChunkFromDocument` via parameter.
  Concurrent `StreamAsync` calls on the same singleton client no longer race.
- **§FP-003** — `AgentLoop.ToolContext.ReportProgress` is now `async`/`await` with
  a try/catch that logs failures at Warning level, instead of
  `_ = _eventBus.PublishAsync(...)` fire-and-forget.
- **§FP-006** — `TuiEffectHost.Run` attaches `.ContinueWith(OnlyOnFaulted |
  RunSynchronously)` to each `PromptAsync`/`RunSlashAsync`/`AbortAsync` call so
  unobserved-task exceptions are logged via the newly-injected
  `ILogger<TuiEffectHost>`. The `Run` contract stays synchronous.
- **§ROP-001** — `JsonlSessionStore.DeserializeMessage` now returns
  `Result<AgentMessage>` (was `AgentMessage?`). `GetMessagesAsync` aggregates
  per-line parse errors into a `List<string>` and logs them at Warning level,
  while still returning the successfully-deserialized messages (a single corrupt
  line no longer truncates the whole transcript).
- **§OOP-003** — `DeserializeMessage` takes `string sessionId` as a parameter
  (was a `""` placeholder), so the reconstructed `AgentMessage` is always in a
  valid state.

**High-perf (Sprint 2):**

- **§PERF-007** — `UiStore.Dispatch` is now lock-free: `lock(_gate)` replaced
  with a CAS loop on `volatile UiState _state`. `Dispatch(AgentEvent)`,
  `Dispatch(UiMsg)`, and `Transition` all use the same pattern with a no-op
  short-circuit.
- **§PERF-009** — `ChatMarkdown.Cache` is now a `ConcurrentDictionary`, removing
  the per-render `lock(Cache)`. The `Cache.Count > 2048 → Clear()` thundering-herd
  eviction is removed (the cache is already bounded upstream by
  `ChatTranscriptCache._rows`).
- **§PERF-006** — `BashTool` now rents `stdout`/`stderr` from `StringBuilderPool`
  (capacities 4096/1024). Each is capped at `MaxOutputChars = 100_000`; dropped
  bytes are counted and logged at Warning level. `Append('\n')` replaces
  `AppendLine()` for platform-independent separators.
- **§ROP-004** — `OpenAiCompatibleLlmClient.MapChunk` now returns
  `new[] { new ErrorEvent($"Parse failed: {ex.Message}") }` on parse failure
  (was `Enumerable.Empty<LlmEvent>()`), so the agent loop aborts the turn loudly
  instead of stalling on a silently-dropped chunk.
- **§OOP-002** — `ApplyCompatFlags` extracted to Strategy pattern:
  `IProviderCompatFlag` in `Harbor.Providers.OpenAiCompatible/Compat/`.
  `ProviderConfig.Quirks` carries the per-provider list (populated by
  `ProviderCompatFlags.For(providerId)` in registration code);
  `ApplyCompatFlags` simply iterates the list. New providers with quirks no
  longer require editing the client. Built-in implementations:
  `DeepSeekReasonerCompatFlag`, `GroqMaxTokensCompatFlag`.

**Acknowledged-but-deferred:**

- **§PERF-005** (partial) — The full `Utf8JsonReader` rewrite for
  `JsonlSessionStore.GetMessagesAsync` was judged too risky without AOT testing.
  `JsonDocument.Parse` is kept, but the ROP path is fixed (see §ROP-001).
- **§FP-007** (acknowledged) — `UiStore.Transition` left as `internal` escape
  hatch. `TuiEffectHost` legitimately needs to fold follow-up state after
  running an effect; removing it requires restructuring `TuiEffectHost` to emit
  `UiMsg` values instead of mutating state directly.
- **§FP-004** (partial) — `ChatMarkdown.Cache` is now a `ConcurrentDictionary`,
  but `Enabled` is left as a process-wide static toggle (markdown is on/off for
  the whole UI, not per-renderer). Making it per-renderer would require
  threading a flag through every call site.

**Sprint 3+ remaining:** §SOLID-001 (`AgentLoop` SRP), §SOLID-002 (`ChatScreen`
god-class), §OOP-004 (visitor for `LlmEvent`), §OOP-005 (Composite registry),
§FP-001/§FP-002/§FP-005 (mutation escapes), §ROP-003 (silent upsert),
§PERF-001/§PERF-002/§PERF-003/§PERF-004/§PERF-008 (perf), §AOT-001/§AOT-002
(JsonSerializerContext source-gen), plus all GoF / Low-level / Concurrency
duplicates.

### Added — Harbor.Scripting (in-process JavaScript / TypeScript plugins)

New assembly `Harbor.Scripting` adds an in-process scripting layer so plugins
can be authored in JavaScript (or TypeScript, pre-compiled via `tsc`). The
long-term direction is to swap the engine to [SharpTS](https://github.com/nickna/SharpTS)
for native TypeScript execution — the `IScriptEngine` abstraction is shaped so
that swap requires no call-site changes.

- **New project:** `src/Harbor.Scripting/`
  - `IScriptEngine` — abstraction over a JS/TS engine
  - `JintScriptEngine` — Jint-based impl (pure .NET, AOT-friendly, sandboxed)
  - `ScriptContext` — what's exposed to scripts: `IToolRegistry`,
    `IProviderRegistry`, `IAgentRegistry`, `ILogger`, `CancellationToken`,
    plus resource limits (timeout, memory, statements, recursion depth)
  - `ScriptLoader` — discovers `.js` / `.ts` files in `~/.harbor/scripts/`
  - `ScriptTool` — `ITool` adapter so script-registered tools become invocable
    by agents
  - `TypeScriptTranspiler` — shells out to `tsc` if on PATH; cached
- **New tests:** `tests/Harbor.Scripting.Tests/` — 10 tests covering
  expression evaluation, global `Harbor` object, timeout enforcement, denied
  built-ins (`process`/`require`/`print`), and tool registration via script
- **New sample:** `samples/scripts/hello.ts` (and `.js` twin) — a 10-line
  TypeScript plugin that registers a `hello` tool
- **New doc:** [docs/SCRIPTING.md](./docs/SCRIPTING.md) — full comparison of
  CS (Roslyn) / JS (Jint) / TS (SharpTS / tsc+Jint) / MCP, with performance
  table, security model, recommendation matrix, and migration paths
- **CLI:** `harbor --script <path>` runs a script file at startup, after
  plugins are loaded. Script-registered tools become invocable by agents in
  the same session

#### Security model

- `AllowClr=false`, `AllowOperatorOverloading=false` — no CLR access from scripts
- `require` / `process` / `print` not registered (undefined)
- Default timeout 5 s (configurable via `ScriptContext.Timeout`)
- Default memory cap 10 MB (`ScriptContext.MemoryLimitBytes`)
- Default statement budget 1,000,000 (`ScriptContext.MaxStatements`)
- Default recursion depth 1,000 (`ScriptContext.MaxRecursionDepth`)
- All expected failures (syntax error, runtime exception, timeout, denied
  built-in, conversion error) return `Result.Failure` — no exceptions leak
- Each `Evaluate` call uses its own `Engine` instance (Jint engines are not
  thread-safe)

#### PoC limitations (documented in SCRIPTING.md §9)

1. `execute` functions must be synchronous — Promise draining is on the roadmap
2. `Harbor.registerProvider` is a no-op in scripts (providers need `ILlmClient`)
3. TypeScript requires `tsc` on PATH (SharpTS will remove this)
4. Per-call engine cost ~1-2 ms (SharpTS expected to enable engine reuse)
5. No source-map support (TS error traces show JS line numbers)

### Code Principles Audit — 2026-07-17

Проведён детальный аудит кодовой базы по принципам OOP/SOLID/GoF/FP/ROP/perf/low-level.

- **New doc:** [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) — 41 finding (11 critical), примеры кода, рекомендации, приоритизированный план рефакторинга на 4 спринта.
- **New doc:** [docs/SPECTRE_TUI_DEEP_DIVE.md](./docs/SPECTRE_TUI_DEEP_DIVE.md) — детальный разбор SpectreTUI-рендерера: архитектура, layout tree, scroll conventions, flow данных, грабли, и рецепты для переноса фич из opencode/kilocode/pi-agent (diff-view, slash-completion, file-tree, LSP-diagnostics, token-breakdown).
- **TODO-комментарии** расставлены в коде: `// TODO(principles)[CATEGORY]: ...` с обратной ссылкой на раздел аудита. Поиск: `grep -rn "TODO(principles)" src/`.
- **Updated:** [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) — добавлена секция "Code principles" с краткой сводкой и эталонными реализациями.
- **Updated:** [docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md) — добавлен "Principles checklist" для PR (OOP/SOLID/GoF/FP/ROP/perf/low-level/concurrency/AOT).
- **Updated:** [CLAUDE.md](./CLAUDE.md) ↔ [AGENTS.md](./AGENTS.md) — двусторонние ссылки, синхронизированы разделы "principles".
- **Updated:** [README.md](./README.md) — добавлены ссылки на новые документы в секции "For developers".

#### Топ-3 критических нарушения

1. **§ROP-002** — `PermissionService.CheckAsync` бросает исключение на expected failure (invalid agent name) — краш под нагрузкой.
2. **§OOP-001** — `OpenAiCompatibleLlmClient._toolCallIndexToId` — instance-level mutable state, гонка при параллельных сессиях на одном singleton-клиенте.
3. **§PERF-005** — `JsonlSessionStore.GetMessagesAsync` — `JsonDocument.Parse` на каждой строке, ~10k аллокаций на длинную сессию.

Полный план рефакторинга — [docs/CODE_PRINCIPLES_AUDIT.md §Prioritized plan](./docs/CODE_PRINCIPLES_AUDIT.md#10-приоритизированный-план-рефакторинга).



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
