# 12 — Roadmap

> Документ: план разработки. MVP → v1 → v2. Конкретные milestones, что в каком релизе, оценки времени, приоритеты.
>
> **CURRENT STATE (v0.4.0-alpha, R31):** MVP done, v0.2 done, v0.3 done (plugins + scripting), v0.4 in progress (UI decomposition + concurrent agents). See [docs/ROADMAP.md](../docs/ROADMAP.md) for the live status board with checkboxes; this spec is the original design intent. Major delta from original plan: v0.4 scope expanded to include cross-platform UI components (Avalonia/Blazor/WPF) + god-object decomposition work that wasn't originally planned.

## 1. Принципы

1. **MVP — минимальный**. Только ядро: agent loop + 4 tools + 2 провайдера + SQLite + compaction + TUI. Без MCP, без LSP, без plugins, без client-server.
2. **Каждый релиз — production-ready**. Если фича нестабильна — не входит в релиз.
3. **Memory discipline — first-class concern**. Каждый PR проверяется на RSS regression.
4. **Test coverage — non-negotiable**. Core logic — 80%+ coverage. Integration tests — на каждый LLM provider.
5. **Backward compatibility** — SemVer. Breaking changes только в major versions.

## 2. MVP (v0.1.0) — Core agent

**Цель**: работающий AI coding CLI с минимальным footprint, proof-of-concept для подхода.

**Время**: 4–6 недель одному разработчику.

### 2.1. Что входит

- **Ядро**:
  - `Agent`, `AgentLoop`, `AgentEvent` discriminated union.
  - `Session`, `UserMessage`, `AssistantMessage`, `ToolResultMessage`.
  - `ITool`, `ToolBase<TArgs>`, JSON Schema generation.
  - `ILlmClient`, `LLMEvent`, streaming через `IAsyncEnumerable<LLMEvent>`.
  - `SystemPromptBuilder` — basic template.
  - `CompactionService` — basic (one-shot summary, no incremental).
  - `PermissionService` — pattern matching, ask user.
  - `TokenEstimator` — `chars / 4` heuristic.

- **Storage**:
  - SQLite через `Microsoft.Data.Sqlite` + `Dapper.AOT`.
  - Schema: `sessions`, `messages`, `message_parts`, `file_snapshots`.
  - Migrations runner.

- **Providers** (2):
  - `AnthropicLlmClient` (Messages API, SSE, cache_control, extended thinking).
  - `OpenAILlmClient` (Chat Completions + Responses API for o1/o3).

- **Tools** (5):
  - `read`, `write`, `edit`, `bash`, `glob`.
  - (grep — v0.2, ls — v0.2)

- **Agents** (2):
  - `code` (default).
  - `plan` (read-only).

- **TUI**:
  - Custom ANSI wrapper.
  - Simple layout (status bar + chat + input).
  - Streaming text rendering.
  - Slash-commands: `/help`, `/clear`, `/model`, `/agent`, `/compact`, `/quit`.
  - Permission dialog.

- **CLI**:
  - `ConsoleAppFramework v5`.
  - Commands: `run` (interactive), `ask` (one-shot), `--version`.

- **Config**:
  - `~/.harbor/config.json`.
  - Env vars: `HARBOR_MODEL`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`.
  - `.harbor/config.json` (project-local).

- **NativeAOT**:
  - `PublishAot=true`, all optimizations.
  - Target binary: ~5–7 МБ.
  - Target RSS: <30 МБ idle.

### 2.2. Что НЕ входит в MVP

- MCP.
- LSP.
- Plugins (compile-time hardcoded list только).
- Client-server mode.
- Branching / forking sessions.
- Skills.
- OAuth flows.
- Effects-TS analog.
- Compaction v2 (pruning, incremental summary).
- Branch summaries.
- Snapshot/revert.
- Sub-agents (`task` tool).
- TodoWrite.
- WebSearch / WebFetch.
- Multi-platform binary packages.

### 2.3. MVP Milestones

| Week | Milestone | Deliverable |
|---|---|---|
| 1 | Spike | AOT binary работает с SQLite + HttpClient + System.Text.Json source-gen. `harbor --version` <30ms cold start, <30MB RSS. |
| 1 | Architecture | All interfaces in `Harbor.Abstractions`. DI container setup. |
| 2 | LLM streaming | Anthropic provider streaming. `harbor ask "hello"` выводит streamed text. |
| 2 | Storage | SQLite schema + migrations. Session append/get/list. |
| 3 | Tools | `read`, `write`, `edit`, `bash`, `glob` working end-to-end. |
| 3 | Agent loop | `AgentLoop` с tool execution. Permission dialog. |
| 4 | TUI | Interactive TUI with streaming rendering, slash-commands. |
| 4 | Compaction | Basic compaction on context overflow. |
| 5 | OpenAI | Second provider. Tests against both. |
| 5 | Tests | Unit + integration tests. Coverage >70% on core. |
| 6 | Polish | Documentation, examples, README. Release v0.1.0. |

### 2.4. MVP acceptance criteria

- [ ] `harbor run` запускает TUI за <50ms.
- [ ] `harbor ask "what is 2+2"` возвращает streamed response за <2s.
- [ ] RSS idle <30 МБ (measured via `/usr/bin/time -v`).
- [ ] RSS active (10K messages) <100 МБ.
- [ ] Cold start `harbor --version` <30ms.
- [ ] Binary size <10 МБ (AOT, stripped).
- [ ] Compaction triggered when context >80% of window.
- [ ] Permission ask для `bash` commands.
- [ ] `read`, `write`, `edit`, `bash`, `glob` работают end-to-end.
- [ ] Anthropic + OpenAI providers работают.
- [ ] Tests pass on Linux, macOS, Windows.
- [ ] 80%+ test coverage on `Harbor.Core`.

## 3. v0.2.0 — Tools expansion

**Цель**: добавить оставшиеся builtin tools и улучшить TUI.

**Время**: 2–3 недели.

### 3.1. Что входит

- **Tools**: `grep`, `ls`, `task` (subagent), `todo`, `question`.
- **Sub-agents**: `explore` (read-only fast).
- **TUI improvements**:
  - Streaming markdown rendering (basic, без prefix cache).
  - Diff rendering для `edit` tool.
  - Tool execution status indicators.
  - Better keyboard handling (Ctrl+W, Ctrl+U, history navigation).
- **Compaction v2**:
  - Background pruning после каждого turn.
  - Incremental summary update (previous summary передаётся).
  - Split turn detection.
- **Session operations**:
  - `harbor session list`.
  - `harbor session resume <id>`.
  - `harbor session export` (JSONL).
- **Slash-commands**: `/tools`, `/permissions`, `/cost`, `/tokens`.

## 4. v0.3.0 — Plugins (basic)

**Цель**: plugin system через compiled DLLs.

**Время**: 3–4 недели.

### 4.1. Что входит

- **Plugin contract**: `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ICommandPlugin`.
- **Plugin loader** (JIT mode): `AssemblyLoadContext` collectible.
- **Plugin loader** (AOT mode): out-of-process plugin-host.
- **Plugin discovery**: `~/.harbor/plugins/*.dll`, `.harbor/plugins/*.dll`, config paths.
- **Trust model**: trust prompt для project-local plugins.
- **Hot-reload** (JIT): FileSystemWatcher на plugins directory.
- **Plugin CLI**: `harbor plugin install <pkg>`, `harbor plugin list`, `harbor plugin uninstall`.
- **NuGet package distribution**: `harbor plugin install Harbor.Plugin.WebSearch`.
- **Sample plugins**: 
  - `Harbor.Plugin.WebSearch` (Exa/Brave API).
  - `Harbor.Plugin.TodoWrite` (extract todo tool from core).
  - `Harbor.Plugin.Skill` (load markdown skills).

## 5. v0.4.0 — MCP integration

**Цель**: MCP client через `ModelContextProtocol` NuGet.

**Время**: 2–3 недели.

### 5.1. Что входит

- **MCP client**: stdio, HTTP, SSE transports.
- **MCP tool adapter**: MCP tools → `ITool` interface.
- **MCP resources**: `list_mcp_resources`, `read_mcp_resource` tools.
- **MCP prompts**: slash-commands.
- **MCP instructions**: injected в system prompt.
- **OAuth flow** для MCP servers (github.com/mcp/, etc.).
- **Config**: `mcp` section в `config.json`.
- **Lifecycle**: lazy connect, reconnect on failure.
- **Hot-reload**: `FileSystemWatcher` на config.
- **CLI**: `harbor mcp list`, `harbor mcp connect`, `harbor mcp disconnect`.

## 6. v0.5.0 — Skills

**Цель**: Skills system (markdown files с frontmatter).

**Время**: 1–2 недели.

### 6.1. Что входит

- **Skill format**: `SKILL.md` с YAML frontmatter (`name`, `description`, `disable-model-invocation`).
- **Skill discovery**: `~/.harbor/skills/`, `.harbor/skills/`.
- **Skill tool**: `skill` tool загружает SKILL.md content.
- **System prompt injection**: `<available_skills>` XML block.
- **Skill marketplace** (future): `harbor skill install <name>`.
- **Sample skills**: 
  - `code-review` — strict code review workflow.
  - `commit` — generate conventional commit.
  - `pr-description` — generate PR description from commits.

## 7. v0.6.0 — LSP integration

**Цель**: LSP client для code intelligence.

**Время**: 3–4 недели.

### 7.1. Что входит

- **LSP client**: через `OmniSharp.Extensions.LanguageServer.Client`.
- **Builtin catalog**: 10+ language servers (TypeScript, Python, Go, Rust, C#, Java, etc.).
- **Auto-spawn**: при первом `read` файла с расширением, spawn соответствующего LSP server.
- **LSP tools**: `diagnostics`, `references`, `definition`, `hover`, `document_symbol`.
- **LSP-aware tools**: `read` injects current diagnostics, `edit` shows diagnostics after change.
- **Custom config**: `lsp` section в `config.json`.
- **CLI**: `harbor lsp list`, `harbor lsp status`.

## 8. v0.7.0 — Branching & sessions

**Цель**: session forking, branching, snapshot/revert.

**Время**: 2–3 недели.

### 8.1. Что входит

- **Session fork**: `harbor session fork <message-id>`.
- **Branch navigation**: `/tree` slash-command, navigate between branches.
- **Branch summaries**: LLM-generated summary при branch switch.
- **Snapshot/revert**: `harbor session revert <message-id>` — откатывает ФС + history.
- **Session search**: `harbor session search <query>` (по content messages).
- **Session import/export**: JSONL bi-directional.

## 9. v0.8.0 — OAuth flows

**Цель**: OAuth для Anthropic Pro/Max, OpenAI Codex, GitHub Copilot.

**Время**: 3–4 недели.

### 9.1. Что входит

- **OAuth framework**: PKCE flow, device flow, token storage.
- **Anthropic Pro/Max OAuth**: stealth mode (как у pi).
- **OpenAI Codex OAuth**: ChatGPT Plus/Pro.
- **GitHub Copilot OAuth**: device flow.
- **Token refresh**: automatic refresh при истечении.
- **Credential store**: OS keychain (macOS Keychain, Windows Credential Manager, Linux Secret Service).
- **CLI**: `harbor auth login <provider>`, `harbor auth status`, `harbor auth logout`.

## 10. v0.9.0 — Client-server mode

**Цель**: `harbor serve` для IDE integration.

**Время**: 3–4 недели.

### 10.1. Что входит

- **HTTP server**: ASP.NET Core minimal APIs.
- **SSE**: streaming events через `System.Net.ServerSentEvents`.
- **WebSocket**: для bidirectional communication.
- **OpenAPI**: auto-generated docs.
- **SDK**: TypeScript + C# client libraries (auto-generated from OpenAPI).
- **mDNS** (опционально): для local discovery.
- **CLI**: `harbor serve [--port=4096]`.
- **VS Code extension** (separate repo): thin client.
- **Modes**: `harbor run --mode=rpc` (headless JSON-over-stdio для editor integrations).

## 11. v1.0.0 — Production-ready

**Цель**: stable API, comprehensive features, ready for wide adoption.

**Время**: 2–3 месяца после v0.9.0 (stabilization period).

### 11.1. Что входит

- **API stability**: SemVer, deprecation policy.
- **Documentation**: complete, versioned, examples.
- **Performance**: meet all KPI targets (см. `00-overview.md` §7).
- **Test coverage**: 90%+ на core, 70%+ на providers/tools.
- **Benchmarks**: published, regression-tested в CI.
- **Multi-platform binaries**: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64, win-arm64.
- **NuGet distribution**: `dotnet tool install harbor`.
- **Migration guide**: from kilocode/opencode/pi/crush.
- **Plugin marketplace**: community-contributed plugins.

### 11.2. v1.0 acceptance criteria

- [ ] 1000+ GitHub stars (community validation).
- [ ] 10+ published plugins.
- [ ] Active contributors (>5 regular).
- [ ] No critical bugs open >30 days.
- [ ] Documentation complete.
- [ ] Benchmarks published и verified.

## 12. v1.x — Incremental improvements

После v1.0 — feature-driven releases, каждые 1–2 месяца:

- **v1.1**: More LLM providers (Google, Bedrock, Azure, Mistral, xAI, Groq, Together, DeepSeek, Cohere, OpenRouter, github-copilot).
- **v1.2**: LSP expansion (30+ languages, full feature parity с opencode).
- **v1.3**: Vector memory (optional, plugin — LanceDB или SQLite FTS5).
- **v1.4**: Custom agents (LLM-generated agent configs).
- **v1.5**: Codemode (execution sandbox, experimental).
- **v1.6**: ACP (Agent Client Protocol) для Zed integration.
- **v1.7**: System Context Algebra (diff-only system prompt updates).
- **v1.8**: Durable inbox pattern (enterprise-grade durability).
- **v1.9**: Streaming markdown prefix cache (perf).
- **v1.10**: Session sharing (publish to harbor.sh).
- **v1.11**: Multi-agent orchestration (experimental).

## 13. v2.0 — Major release

**Цель**: breaking changes cleanup, new architecture if needed.

**Possible changes**:
- New plugin ABI (if v1.x accumulated cruft).
- New session format (if SQLite schema needs major refactor).
- New LLM abstraction (if `Microsoft.Extensions.AI` limitations become blocking).
- New TUI framework (if custom ANSI не хватает — Terminal.Gui v2+ или новый фреймворк).

## 14. Out of scope (not planned)

- **Mobile apps**: harbor — desktop CLI/TUI tool, не mobile.
- **Web UI**: harbor — terminal, не browser. (Но IDE extensions через client-server — да.)
- **Cloud-hosted**: harbor — local-first. Cloud-hosted — отдельный продукт.
- **Real-time collaboration**: один пользователь в сессии. Multi-user — out of scope.
- **Visual programming**: harbor — text-based.
- **Voice input/output**: out of scope (use OS voice-to-text).
- **Image generation**: tools для image_gen (DALL-E, Stable Diffusion) — как plugin, не core.
- **Code execution sandbox**: codemode — experimental plugin, не core.
- **Vector DB**: опциональный plugin, не core dependency.

## 15. Development workflow

### 15.1. Git flow

- `main` — production-ready, всегда green.
- `develop` — integration branch.
- `feature/<name>` — feature branches.
- `fix/<issue>` — bugfix branches.
- Tags: `v0.1.0`, `v0.2.0`, etc.

### 15.2. Release process

1. Update `CHANGELOG.md`.
2. Bump version in `Directory.Build.props`.
3. Update `harbor --version` test.
4. Create release branch `release/v0.X.0`.
5. Run full CI on all platforms.
6. Build NativeAOT binaries for all RIDs.
7. Publish NuGet package.
8. Publish GitHub release with binaries.
9. Update docs.harbor.sh.
10. Tweet / post.

### 15.3. CI/CD pipeline

```yaml
# .github/workflows/ci.yml
name: CI
on: [push, pull_request]
jobs:
  build-test:
    strategy:
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet build -c Release --warnaserror
      - run: dotnet test -c Release --logger "console;verbosity=detailed"
  
  aot-build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - run: dotnet publish src/Harbor.Cli -c Release -r linux-x64
      - name: Check binary size
        run: |
          SIZE=$(stat -c%s bin/Release/net10.0/linux-x64/publish/harbor)
          echo "Binary size: $SIZE bytes"
          if [ $SIZE -gt 15728640 ]; then exit 1; fi  # 15 MB limit
  
  benchmarks:
    runs-on: ubuntu-latest
    if: github.event_name == 'pull_request'
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - run: dotnet publish src/Harbor.Cli -c Release -r linux-x64
      - name: Benchmark
        run: |
          hyperfine --warmup 3 --export-json bench.json \
            './bin/publish/harbor --version'
      - name: Check RSS
        run: |
          /usr/bin/time -v ./bin/publish/harbor --version 2>&1 | grep "Maximum resident" | awk '{print $6}'
```

### 15.4. Issue triage

- `bug` — confirmed bug.
- `feature` — feature request.
- `enhancement` — improvement to existing feature.
- `question` — usage question.
- `wontfix` — out of scope.
- `duplicate` — already reported.
- Priority: `P0` (critical), `P1` (high), `P2` (medium), `P3` (low).

### 15.5. Contribution guidelines

- All PRs must pass CI.
- New features require tests (80%+ coverage на changed code).
- New features require documentation.
- Breaking changes require `BREAKING CHANGE:` in commit message.
- Conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
- Code review by at least 1 maintainer.

## 16. Success metrics

| Metric | v0.1 (MVP) | v0.5 | v1.0 |
|---|---|---|---|
| GitHub stars | 50 | 200 | 1000 |
| Weekly downloads | 10 | 100 | 1000 |
| Plugins published | 0 | 3 | 10+ |
| Active contributors | 1 | 3 | 5+ |
| Issues closed | 5 | 30 | 100+ |
| Avg RSS idle | <30 MB | <30 MB | <25 MB |
| Cold start | <50 ms | <40 ms | <30 ms |

## 17. Risk-driven priorities

Из `11-risks.md` top-10:

| Risk | Mitigation milestone |
|---|---|
| Compaction quality | v0.1 (basic) → v0.2 (incremental) |
| TUI perf | v0.1 (basic) → v1.9 (prefix cache) |
| Scope creep | v0.1 strict MVP |
| Cost runaway | v0.1 doom loop detection |
| AOT incompatibility | v0.1 week 1 spike |
| Cross-platform | v0.1 CI on all OS |
| Concurrent sessions | v0.1 file lock |
| Single maintainer | v0.3 plugin architecture |
| Test coverage | v0.1 80%+ core |
| SQLite concurrency | v0.1 busy_timeout |

---

**Next**: `13-questions-and-answers.md` — ответы на исходные вопросы пользователя.
