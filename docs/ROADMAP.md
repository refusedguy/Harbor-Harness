# Roadmap

> Harbor development roadmap. Updated for v0.4.0-alpha.

## Current State: v0.4.0-alpha

### ✅ Completed

- **Architecture**: Abstractions-first, EventBus, AgentLoop, Plugin system
- **Providers**: 4 native (Anthropic, OpenAI, Ollama, OpenAiCompatible) + 13 JSON configs
- **Storage**: 3 backends (Jsonl, Memory, Sqlite)
- **TUI**: 8 renderers (`spectre-tui` default, `ansi` AOT fallback, `plain`, `spectre`, `fullscreen`, `terminal-gui`, `termina`, `razor`) with MVVM (CommunityToolkit.Mvvm)
- **Tools**: 8 builtin (read, write, edit, bash, glob, grep, ls, task)
- **Plugins**: 4 samples (WebSearch, TodoWrite, GitTools, FileTree)
- **Config**: JSON-based config with onboarding wizard, AuthStore with env var fallback
- **Performance**: ZLinq, NonBlocking dictionaries, FrozenDictionary, ArrayPool, StringBuilderPool, Channel<T>, ImmutableArray, StringPool, MemoryPack attributes
- **Patterns**: Strategy, Registry, Observer, Builder, Adapter, Command, Specification, ValueObject, Factory, Plugin, Repository, Chain of Responsibility
- **ROP**: Result<T> throughout Core for error handling
- **Tests**: 469 passed, 10 failed, 1 skipped (10 test projects + benchmarks) — suite currently RED
- **E2E verified**: Kilocode `tencent/hy3:free` works end-to-end
- **Analyzers**: 7 analyzer packages, 0% unsafe, 0 warnings in `src/` (106 test-project warnings exempt)
- **Libraries**: Spectre.Console 0.57.2, CommunityToolkit 8.4.0, ZLinq 1.5.6, MemoryPack 1.10.0

---

## v0.5.0 — Plugin System & Sub-agents

### Plugin loading from DLLs
- [ ] `IPluginHost` implementation with AssemblyLoadContext (JIT mode)
- [ ] Plugin discovery from `~/.harbor/plugins/*.dll`
- [ ] Plugin trust prompt for project-local plugins
- [ ] Hot-reload via FileSystemWatcher (JIT only)
- [ ] `harbor plugin install/list/uninstall` CLI commands

### Sub-agent execution
- [ ] Wire `TaskTool` to `IAgent` via `ToolContext.Services`
- [ ] Sub-agent runs in isolated context with own session
- [ ] Result returned to parent agent as tool output
- [ ] Support `explore`, `plan`, custom sub-agents

### TUI plugins
- [ ] `ITuiPlugin.RegisterTui(ViewRegistry, ViewModelRegistry)`
- [ ] Sample TUI plugin (e.g. token usage chart)
- [ ] Plugin views override builtin views

---

## v0.6.0 — MCP Integration

- [ ] MCP client via `ModelContextProtocol` NuGet
- [ ] Stdio, HTTP, SSE transports
- [ ] MCP tools → ITool adapter
- [ ] MCP resources as `read_mcp_resource` tools
- [ ] MCP prompts as slash-commands
- [ ] OAuth for MCP servers
- [ ] Lazy connect, reconnect on failure
- [ ] Config: `mcp` section in `config.json`

---

## v0.7.0 — Skills & LSP

### Skills
- [ ] `SKILL.md` format with YAML frontmatter
- [ ] Discovery from `~/.harbor/skills/` and `.harbor/skills/`
- [ ] `skill` tool to load skill content
- [ ] `<available_skills>` XML in system prompt
- [ ] `harbor skill install/list` CLI commands

### LSP Integration
- [ ] LSP client via `OmniSharp.Extensions.LanguageServer.Client`
- [ ] 10+ builtin language servers (TypeScript, Python, Go, Rust, C#)
- [ ] Auto-spawn on file open
- [ ] `diagnostics`, `references`, `definition` tools
- [ ] LSP-aware `read`/`edit` (inject diagnostics)

---

## v0.8.0 — Session Management

- [ ] Session branching (`harbor session fork <message-id>`)
- [ ] `/tree` slash-command for branch navigation
- [ ] Branch summaries (LLM-generated on branch switch)
- [ ] Snapshot/revert (`harbor session revert <message-id>`)
- [ ] Session search (`harbor session search <query>`)
- [ ] JSONL import/export

---

## v0.9.0 — Two-Process Architecture

- [ ] Core (NativeAOT) + TUI (JIT) in separate processes
- [ ] NDJSON over Unix Domain Socket wire protocol
- [ ] Late-attach with scrollback replay
- [ ] Multi-client support (terminal + IDE + web)
- [ ] `harbor serve` for headless mode
- [ ] `harbor tui` to attach to running core

---

## v1.0.0 — Production Release

- [ ] Multi-platform NativeAOT binaries (linux-x64/arm64, osx-arm64, win-x64)
- [ ] NuGet `dotnet tool install harbor`
- [ ] OAuth flows (Anthropic Pro/Max, OpenAI Codex, GitHub Copilot)
- [ ] OS keychain for API keys
- [ ] Full documentation (docs site)
- [ ] Plugin marketplace
- [ ] Migration guide from kilocode/opencode/pi/crush
- [ ] Benchmarks published and verified
- [ ] 90%+ test coverage on core

---

## Performance Goals

| Metric | Current | v1.0 Target |
|---|---|---|
| Cold start | ~2s (dotnet run) | <50ms (NativeAOT) |
| RSS idle | ~25MB | <30MB |
| RSS active (10K msg) | ~80MB | <100MB |
| Binary size | N/A (JIT) | ~5-7MB (AOT) |
| Token-to-screen | ~35ms | <20ms |
| Tests | 480 (469 pass / 10 fail) | 500+ |
| Build warnings | 0 | 0 |
| Unsafe code | 0% | 0% |

---

## Technology Stack (v0.4.0)

| Category | Package | Version |
|---|---|---|
| Runtime | .NET 10 RC2 | 10.0.100 |
| TUI framework | Spectre.Console | 0.57.2 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| High-perf | CommunityToolkit.HighPerformance | 8.4.0 |
| LINQ | ZLinq + DropInGenerator | 1.5.6 |
| Serialization | MemoryPack | 1.10.0 |
| Concurrent | NonBlocking | 2.1.2 |
| Functional | CSharpFunctionalExtensions | 3.7.0 |
| Testing | TUnit | 0.50.0 |
| Benchmarks | BenchmarkDotNet | 0.15.0 |
| SQLite | Microsoft.Data.Sqlite | 10.0.0 |
| Analyzers | Roslynator, Sonar, Meziantou, etc. | latest |

---

## Architecture Patterns Applied

| Pattern | Where | Count |
|---|---|---|
| **Strategy** | ILlmClient, ITool, ITuiRenderer, ISessionStore | 4+ interfaces |
| **Registry** | ProviderRegistry, ToolRegistry, AgentRegistry, ViewRegistry | 4 registries |
| **Observer** | IEventBus, InMemoryEventBus | 1 |
| **Builder** | ISystemPromptBuilder, IToolRegistryBuilder | 2+ |
| **Adapter** | MessageConverter, OpenAiCompatibleLlmClient | 2+ |
| **Command** | IAgent, DefaultAgent, TaskTool | 3 |
| **Specification** | PermissionRuleset | 1 |
| **Value Object** | SessionId, ProviderId, ToolName, etc. | 7 types |
| **Factory Method** | Session.Create, ToolResult.Success/Error | 5+ |
| **Plugin** | IPlugin, IToolPlugin, IProviderPlugin, ITuiPlugin | 4 contracts |
| **Repository** | ISessionStore | 1 |
| **Chain of Resp.** | AgentLoop (prompt → LLM → tool → compaction) | 1 |
| **MVVM** | ObservableObject, [ObservableProperty], [RelayCommand] | 28 usages |
| **ROP** | Result<T> throughout Core | 12+ usages |
| **Flyweight** | StringPool (tool name interning) | 1 |
| **Object Pool** | StringBuilderPool, ArrayPool | 18 usages |
