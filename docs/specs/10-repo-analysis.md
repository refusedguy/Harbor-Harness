# 10 — Анализ репозиториев

> Документ: глубокий разбор 5 референсных репозиториев (SharpTS, pi-agent, kilocode, opencode, crush). Для каждого — архитектура, что позаимствовать, что НЕ брать, конкретные паттерны.

## 1. SharpTS (nickna/SharpTS)

**URL**: https://github.com/nickna/SharpTS
**Stars**: 146 | **Лицензия**: MIT | **Язык**: C# 98.6%

### 1.1. Что это на самом деле

**SharpTS — НЕ конвертер TypeScript → C#**. Это **runtime + AOT-компилятор TypeScript на .NET**, генерирующий IL-инструкции (через `System.Reflection.Emit`), а не C#-исходники.

Это третья категория — closer к Node.js/Deno/Bun, только target — CLR/.NET, не V8.

### 1.2. Возможности

- Полная реализация TypeScript на C# (lexer → parser → type checker → IL compiler).
- 10,132 / 10,132 Test262 тестов проходят.
- 207 / 296 TypeScript conformance tests.
- Performance parity с Node.js на 5/7 workloads.
- Поддержка ESM, CommonJS, `import type`, `import.meta`, dynamic `import()`.
- Web API: `fetch`, `URL`, `TextEncoder`, `AbortController`, Web Streams, `Intl.*`.
- 20+ Node.js stdlib modules (`fs`, `path`, `os`, `process`, `crypto`, `http`, `net`, `tls`, `child_process`, `vm`, `worker_threads`, etc.).
- .NET interop: `import { X } from "dotnet:Ns.Type"`, `@DotNetType` decorator.
- NuGet packaging through `--pack`.
- LSP server (`sharpts-lsp`).
- VSCode extension.

### 1.3. Подходит ли для нашего кейса?

**НЕТ, если нужно портировать TS в C#-исходники** — SharpTS генерирует IL, не C#.

**ДА (с оговорками), если**:
- Хотим запустить существующий TS-харнесс на .NET.
- Готовы использовать reflection-based interop.
- Принимаем, что не все npm-пакеты заведутся (особенно с native bindings, WASM).

### 1.4. Альтернативы для TS→C# миграции

| Tool | Подход | Оценка |
|---|---|---|
| `robin-han/typescript-converter` | AST-based TS→C# через TS compiler | Старый, для DTO только |
| `Box-Of-Hats/ts-csharp` | interface/class → C# class (DTO) | Игрушка, заброшен |
| `sigseg1v/typescript-to-csharp` | Verification toolkit + Claude Code skill | Современный, AI-ассистированный |
| `codeconvert.ai` | Online AI converter | Сниппеты, не codebase |
| **Claude Code / Cursor + ручной port** | AI делает миграцию, человек ревьюит | **Самый практичный** |

### 1.5. Что взять из SharpTS для harbor

**Ничего напрямую**. SharpTS — отдельный проект, не библиотека для встраивания.

Но как **источник вдохновения**:
- Доказательство, что .NET 10 может быть runtime для динамических языков.
- LSP server на .NET — можно посмотреть паттерны.
- NativeAOT compatibility IL emit — SharpTS показывает, как делать это правильно (с `[DynamicallyAccessedMembers]`).

## 2. pi-agent (earendil-works/pi)

**URL**: https://github.com/earendil-works/pi
**Версия**: 0.80.7 | **Автор**: Mario Zechner | **Лицензия**: MIT

### 2.1. Архитектура

Monorepo из 5 пакетов:
- `pi-ai` — LLM abstraction, 36 провайдеров.
- `pi-agent-core` — agent runtime, harness.
- `pi-coding-agent` — CLI, TUI, tools, extensions.
- `pi-tui` — собственный TUI framework (differential rendering, CSI 2026 sync updates).
- `pi-orchestrator` — experimental.

### 2.2. Главные архитектурные решения

1. **JSONL tree-based sessions** — branching без копирования через `parentId` указатели.
2. **Skills (CLI tools + README) вместо MCP** — осознанное решение, обосновано в блогпосте автора.
3. **Extension system через `jiti`** — runtime TS loading, без компиляции. Virtual modules для Bun binary.
4. **4 run modes**: interactive (TUI), print (one-shot), json (event stream), rpc (headless JSON-over-stdio).
5. **AgentMessage vs LlmMessage** — разделение: AgentMessage = app-level (с BashExecution, Custom, BranchSummary, CompactionSummary), LlmMessage = LLM-format (только что отправляется провайдеру). `convertToLlm()` вызывается прямо перед LLM call.
6. **Streaming event protocol** — `AssistantMessageEvent` discriminated union с text_delta, thinking_delta, toolcall_*.
7. **Compaction с structured Markdown summary** — фиксированные секции (Goal/Constraints/Progress/Done/InProgress/Blocked/Key Decisions/Next Steps/Critical Context) + `<read-files>` / `<modified-files>` tracking.
8. **Split turn detection** — если один turn > keepRecentTokens, генерируются ДВЕ summary (history + turn prefix).
9. **Steering & follow-up queues** — interrupt agent while running tools, queue messages.
10. **`shouldStopAfterTurn` + `prepareNextTurn` hooks** — для mid-run compaction.

### 2.3. Что позаимствовать для harbor

**Must-have (прямые порты)**:

| Pi-концепция | C#/.NET эквивалент |
|---|---|
| `EventStream<T, R>` | `System.Threading.Channels.Channel<T>` + `IAsyncEnumerable<T>` |
| `AssistantMessageEvent` protocol | `record AssistantMessageEvent` discriminated union (через `[JsonPolymorphic]`) |
| `AgentTool<TParameters, TDetails>` | `abstract class AgentTool<TParams, TDetails>` + `JsonSerializerContext` |
| TypeBox schema | `System.Text.Json.Schema` (.NET 9+) или `NJsonSchema` |
| `validateToolArguments()` | `JsonSerializer.Deserialize(jsonString, schema)` + DataAnnotations |
| JSONL session tree | SQLite + JSONL mirror |
| Compaction с structured summary | Markdown template, cumulative file tracking |
| `estimateTokens(message)` | `message.Content.Length / 4` + image ~4800 chars |
| Steering/follow-up queues | `Channel<AgentMessage>` с `BoundedChannelOptions` |
| `beforeToolCall` / `afterToolCall` hooks | `Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>` |
| 4 run modes (interactive/print/json/rpc) | `IHostedService` implementations |
| Skills format (frontmatter + markdown) | `<available_skills>` XML в system prompt |
| System prompt structure | Available tools / Guidelines / Project context / Skills / CWD |
| Cut point rules (toolResult не может быть cut point) | То же правило |
| `terminate: true` hint | Для `notify_done`-style tools |
| Tool execution parallel с sequential override per-tool | То же |
| Extension event lifecycle | `project_trust → session_start → resources_discover → ...` |

**Should-have (адаптировать)**:

| Pi-концепция | C# адаптация |
|---|---|
| `jiti` runtime TS loading | `AssemblyLoadContext` (JIT) / out-of-process (AOT) |
| `typebox` schema | `System.Text.Json.Schema` + `[JsonDerivedType]` |
| Differential rendering TUI | Custom ANSI wrapper, может быть Spectre.Console |
| `undici` custom dispatcher | `HttpClientHandler` + `SocketsHttpHandler` |
| `cross-spawn` | `System.Diagnostics.Process` |
| `proper-lockfile` | `FileStream.Lock()` или `Mutex` |
| `diff` для edit tool preview | `DiffPlex` |
| `highlight.js` | `ColorCode.NET` или `Prism.NET` |
| `marked` markdown | `Markdig` |
| `@silvia-odwyer/photon-node` | `SixLabors.ImageSharp` |
| `@anthropic-ai/sdk` | Своя impl через `HttpClient` или `Anthropic.SDK` |
| `openai` SDK | `Microsoft.Extensions.AI.OpenAI` |

**Что НЕ переносить**:
- MCP клиент (pi сознательно его не имеет).
- Sub-agents (через `tmux`/extensions, не core).
- Permission popups (sandbox внешне).
- Plan mode / to-dos (extensions, не core).
- Biome (нет .NET эквивалента, `dotnet format` + `Roslynator`).

### 2.4. Ключевые файлы для reference

| Файл | Что |
|---|---|
| `packages/agent/src/agent-loop.ts` (793 LOC) | Ядро event loop |
| `packages/agent/src/types.ts` (431 LOC) | Все типы |
| `packages/ai/src/types.ts` (737 LOC) | LLM типы + AssistantMessageEvent |
| `packages/ai/src/utils/event-stream.ts` (89 LOC) | EventStream pattern |
| `packages/agent/src/harness/session/jsonl-storage.ts` (314 LOC) | Session storage |
| `packages/agent/src/harness/compaction/compaction.ts` (753 LOC) | Compaction |
| `packages/coding-agent/src/core/system-prompt.ts` (162 LOC) | System prompt |
| `packages/coding-agent/src/core/extensions/types.ts` (1675 LOC) | Extension API |

### 2.5. Оценка применимости

**Сложность порта**: средняя. ~15-25k LOC C# для MVP.

**Время на MVP** (4 tools + Anthropic + OpenAI + interactive + print + jsonl sessions + compaction): 2-3 месяца одному разработчику.

**Время на feature parity** (все провайдеры, extensions, RPC mode, branching, skills): 6-9 месяцев.

## 3. kilocode (kilo-org/kilocode)

**URL**: https://github.com/kilo-org/kilocode
**Лицензия**: MIT | **Язык**: TypeScript (Bun runtime)

### 3.1. Архитектура

Это **форк opencode** с Kilo-specific layers. `packages/opencode/` — форк upstream OpenCode, помечен `// kilocode_change` маркерами для upstream-merge.

**Client-server модель**:
- `kilo serve` — Bun-процесс, AI agent runtime, HTTP+SSE server.
- VS Code extension / JetBrains plugin — тонкие клиенты, общаются через `@kilocode/sdk`.

### 3.2. Стек

| Слой | Технология |
|---|---|
| Runtime | Bun 1.3.14 |
| LLM | Vercel AI SDK v6 + 20+ `@ai-sdk/*` |
| Effects | Effect-TS 4.0 beta |
| TUI | `@opentui/solid` + SolidJS |
| DB (sessions) | SQLite + Drizzle ORM (`bun:sqlite`) |
| DB (memory) | LanceDB (vector DB) |
| HTTP | Hono 4.12 |
| MCP | `@modelcontextprotocol/sdk` 1.29 |
| Code parsing | tree-sitter (WASM) |
| PTY | `@lydell/node-pty`, `bun-pty` |
| File watching | `@parcel/watcher` (8 платформ) |
| Auth | `@openauthjs/openauth`, `google-auth-library`, etc. |
| Image | `@silvia-odwyer/photon-node` (WASM) |

### 3.3. Почему ~1 ГБ RAM

| Источник | RSS |
|---|---|
| Bun runtime baseline | 50–80 МБ |
| SQLite cache (`cache_size=-64000`) | 64 МБ |
| tree-sitter WASM + grammars | 30–60 МБ |
| `@silvia-odwyer/photon-node` (WASM) | 20–40 МБ |
| `@lydell/node-pty` + `bun-pty` | 10–20 МБ |
| `@parcel/watcher` | 10–20 МБ |
| `@aws-sdk/credential-providers` | 30–50 МБ |
| AI SDK + 20 providers | 100–200 МБ |
| Effect-TS + OpenTelemetry | 30–60 МБ |
| SolidJS TUI + shiki + marked | 50–100 МБ |
| LanceDB | 50–150 МБ |
| Hono + mDNS | 10–30 МБ |
| Session caching | 10–100 МБ |
| **Итого** | **~450 МБ – 1.1 ГБ** |

### 3.4. Что позаимствовать для harbor

**✅ Стоит позаимствовать**:

1. **Client-server архитектура с HTTP+SSE** — для будущей IDE интеграции. MVP без неё, v1 — добавить.
2. **Tool-фреймворк на schema-first подходе** — `Tool.Def` interface с JSON Schema.
3. **Permission system с pattern-matching** — `allow|ask|deny` per tool per glob.
4. **Modes как agents с permission-ruleset'ами** — code/plan/general/explore, hidden utility-agents (compaction/title/summary).
5. **Anchored-summary compaction** — Markdown template, incremental update, `PRUNE_MINIMUM/PROTECT`, `DEFAULT_TAIL_TURNS=2`, `TOOL_OUTPUT_MAX_CHARS=2000`.
6. **SQLite normalized schema** (Session → Message → Part, + Todo, Project).
7. **System-prompt assembly как массив блоков** — `[env, mem, instructions, skills]` + provider-specific base-prompt + MAX_STEPS prefill.
8. **MCP support** через официальный C# SDK.
9. **Streaming adapter pattern** — перевод provider-events в унифицированный `LLMEvent` stream.
10. **Dynamic tool-description injection** — `task` tool получает список subagents, `skill` tool — список skills.
11. **InstanceState-style context-keyed caching** — пересоздание при смене config.
12. **`kilocode_change` marker-дисциплина** — если форкаете open-source.

**❌ НЕ стоит позаимствовать**:

1. **Effect-TS** — в C# есть `async/await` + `System.Threading.Channels` + `Microsoft.Extensions.AI`.
2. **SolidJS TUI в server-процессе** — TUI должен быть отдельным процессом.
3. **Загрузка всех 20+ `@ai-sdk/*` провайдеров** — lazy, но всё равно тяжело.
4. **tree-sitter WASM для парсинга shell** — regex-based достаточно для CLI.
5. **photon-node (WASM image processing)** — `SixLabors.ImageSharp` нативный.
6. **LanceDB как обязательный memory-backend** — опциональный plugin.
7. **`heap-snapshot-toolkit` на каждом запуске** — только в debug.
8. **mDNS advertising** — для локального CLI избыточно.
9. **`@parcel/watcher` с 8 платформ-вариантами** — `System.IO.FileSystemWatcher`.
10. **JSON file-storage (`storage/storage.ts`) рядом с SQLite** — выбрать одно.
11. **`marked-shiki` + `remend` + `dompurify`** для markdown-рендера в server.
12. **`xlsx`, `mammoth` (docx)** — вынести в опциональный plugin.
13. **`@agentclientprotocol/sdk` (ACP)** — если не нужен ACP-interop.

**⚠️ Позаимствовать с адаптацией**:

| Kilocode | .NET |
|---|---|
| Vercel AI SDK v6 | `Microsoft.Extensions.AI` + provider-packages |
| Drizzle ORM | EF Core (не AOT!) или Dapper.AOT |
| `yargs` CLI | `ConsoleAppFramework v5` |
| `@modelcontextprotocol/sdk` | NuGet `ModelContextProtocol` |
| Plugin-loading из `*.js`/`*.ts` | `AssemblyLoadContext` (JIT) / out-of-process (AOT) |
| Effect Schema → JSON Schema | `System.Text.Json.Schema` + `[JsonDerivedType]` |
| `KiloCli.bootstrap()/runner()/shutdown()` | `IHost` + `IHostedService` |

### 3.5. Ключевые файлы для reference

| Файл | Что |
|---|---|
| `packages/opencode/src/index.ts` | Entry point, CLI yargs setup |
| `packages/opencode/src/session/prompt.ts` | `runLoop` — главный цикл |
| `packages/opencode/src/session/llm.ts` | LLM streaming через AI SDK |
| `packages/opencode/src/session/llm/request.ts` | LLM request preparation |
| `packages/opencode/src/tool/tool.ts` | Tool definition interface |
| `packages/opencode/src/tool/registry.ts` | Tool registry |
| `packages/opencode/src/session/processor.ts` | Session processor |
| `packages/opencode/src/session/compaction.ts` | Compaction logic |
| `packages/opencode/src/session/system.ts` | System prompt assembly |
| `packages/opencode/src/provider/provider.ts` | Provider abstraction (2079 LOC!) |
| `packages/opencode/src/mcp/index.ts` | MCP integration |
| `packages/opencode/src/agent/agent.ts` | Agent definitions |

## 4. opencode (anomalyco/opencode, formerly sst/opencode)

**URL**: https://github.com/anomalyco/opencode
**Версия**: v1.18.2 | **Stars**: 186k | **Контрибьюторы**: 864+

### 4.1. Уточнение

В 2026 году SST (разработчик serverless-фреймворка) ребрендировалась в Anomaly. Репозиторий `sst/opencode` перенесён в `anomalyco/opencode`. Это **тот же самый opencode**, что изучался в контексте kilocode (kilocode — форк).

### 4.2. Что особенного (vs kilocode и pi)

1. **Client-server с HTTP+WebSocket API + OpenAPI** — единственный OSS AI coding CLI с такой архитектурой. mDNS для auto-discovery.
2. **Effect-TS как основа** — structured concurrency, error handling, retries, resource safety, telemetry.
3. **Две LLM-абстракции** — AI SDK (default) + собственная `@opencode-ai/llm` (native, opt-in). Готовятся отвязаться от Vercel.
4. **V2 Session Core с durable inbox pattern** — `session_input` таблица для durable prompt admission. `SessionRunCoordinator` коалесит wakeups.
5. **System Context Algebra** — каждый источник контекста моделируется как `Source<A>` с `baseline()`/`update()`/`removed()`. При изменении контекста между ходами отправляется **только diff**, не полный ре-промпт.
6. **LSP integration из коробки** — 30+ языков, auto-spawn, root detection. LLM может дёргать LSP через tool.
7. **ACP (Agent Client Protocol)** — Zed-протокол для editor↔agent.
8. **Per-provider system prompts** — 9 вариантов промптов.
9. **Plugin SDK с 19 хуками** — покрывает весь pipeline.
10. **Multi-agent из коробки** — build/plan/general/explore + internal compaction/title/summary.

### 4.3. Что позаимствовать

**Архитектурные паттерны (must-have для v1+)**:

1. **Client-server с HTTP+WebSocket API + OpenAPI** — для IDE integration в v1.
2. **LLMEvent unified stream** — `IAsyncEnumerable<LLMEvent>` поверх всех провайдеров.
3. **V2 durable inbox pattern** — для enterprise-durability.
4. **System Context Algebra** — diff-only обновления system prompt между ходами.
5. **LSP integration** — как plugin, но с builtin catalog на 30+ языков.
6. **ACP** — для Zed и других editor integrations.
7. **Per-provider system prompts** — 9 вариантов.
8. **Plugin SDK с 19 хуками** — покрывает весь pipeline.
9. **Snapshot + revert** — каждый LLM step делает FS snapshot.
10. **Share sessions** — `session.share()` публикует.

**Что НЕ переносить**:
- Effect-TS (использовать обычный `async/await` + DI).
- SolidJS+OpenTUI (использовать custom ANSI).
- LanceDB (опциональный plugin).
- mDNS по умолчанию (опционально для server mode).

### 4.4. System Context Algebra — деталь

Это **серьёзная оптимизация по токенам** (и cache hit rate). Реализация в C#:

```csharp
public interface ISystemContextSource<T>
{
    string Key { get; }
    Task<T> LoadAsync(CancellationToken ct);
    string RenderBaseline(T current);
    string RenderUpdate(T previous, T current);
    string? RenderRemoved(T previous);
}

public sealed class SystemContextBuilder
{
    private readonly List<(ISystemContextSource Source, object Current)> _sources = new();
    private readonly Dictionary<string, object> _previousValues = new();
    
    public async Task<string> BuildAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        
        foreach (var (source, _) in _sources.ToList())
        {
            var current = await source.LoadAsync(ct);
            _sources[_sources.FindIndex(s => s.Source == source)] = (source, current);
            
            if (_previousValues.TryGetValue(source.Key, out var prev))
            {
                // Diff
                var update = source.RenderUpdate(prev, current);
                if (!string.IsNullOrEmpty(update))
                    sb.AppendLine(update);
            }
            else
            {
                // Initial
                sb.AppendLine(source.RenderBaseline(current));
            }
            
            _previousValues[source.Key] = current;
        }
        
        return sb.ToString();
    }
}
```

Это даёт значительную экономию токенов при длинных сессиях — system prompt не переотправляется полностью каждый turn.

## 5. crush (charmbracelet/crush)

**URL**: https://github.com/charmbracelet/crush
**Стек**: Go 1.26 + Bubbletea v2 + Lipgloss v2 + Glamour v2

### 5.1. Архитектура

Entry: `main.go` → `cmd.Execute()` → `fang.Execute(rootCmd)` (Charm's cobra wrapper). `rootCmd.RunE` строит `tea.Program` с `ui.New(...)`, запускает `go ws.Subscribe(program)` — единственный мост из агента в TUI.

**Две топологии**:
- Локальная (in-process).
- Client/server (`CRUSH_CLIENT_SERVER=1`, HTTP/Unix-socket + SSE).

Центральный `pubsub.Broker[tea.Msg]` фан-инит события из 11 сервисов и форвардит их в `program.Send`.

### 5.2. TUI implementation

- Один `UI` model (`ui.go`, 4579 строк) с гигантским `switch msg := msg.(type)`.
- **Намеренный anti-pattern** относительно textbook Elm.
- Hybrid rendering: `uv.ScreenBuffer` (rectangles) + string-based для sub-components.
- Streaming rendering в **3 слоя**:
  - Debounced message writes.
  - Chat draw cache (memoized ANSI decode).
  - `streamingMarkdown` prefix-cache (главная инновация).

**Streaming markdown prefix cache**: кэширует рендер stable-prefix'а, ре-рендерит только trailing partial через `findSafeMarkdownBoundary`. Это разница между 60 fps и 5 fps на streaming.

### 5.3. Tool calling

- `fantasy.NewAgentTool(name, description, func(ctx, params T, call) (ToolResponse, error))`.
- **Schema из Go struct tags** (`json:"..." description="..."`).
- Description — `text/template` из embedded `.md.tpl`.
- ~26 builtin tools (bash, view, edit, multiedit, write, glob, grep, ls, fetch, web_fetch, web_search, download, sourcegraph, diagnostics, references, lsp_restart, todos, question, crush_info, crush_logs, job_output, job_kill, list_mcp_resources, read_mcp_resource, agent sub-agent, agentic_fetch).
- Tool-calling loop внутри `fantasy.Agent.Stream`: `PrepareStep` → stream → `OnToolCall`/`OnToolResult` → `OnStepFinish` → `StopWhen` check → loop or finish.

### 5.4. Permission model

- 2 брокера (request blocking + notification UI).
- `PermissionKey = (SessionID, ToolName, Action, Path)` для `GrantPersistent`.
- `autoApproveSessions` для non-interactive.
- Allowlist `allowed_tools`.
- Hook pre-approval via context.
- Bash: `safeCommands` read-only skip + `blockFuncs` (sudo, package managers) + auto-background after 60s.

### 5.5. Sessions

- **SQLite WAL, single connection** (`SetMaxOpenConns(1)` — против WAL desync).
- goose migrations embedded.
- Таблицы: `sessions` (с `summary_message_id`, `todos` JSON), `messages` (`parts` JSON array), `files` (versioned), `read_files`.
- Triggers для `updated_at`/`message_count`.

### 5.6. Compaction

- `StopWhen` условие (context window nearly full → `shouldSummarize=true`).
- `Summarize()` создаёт assistant message с `IsSummaryMessage=true`.
- Summary prompt (48 строк) — "brief a teammate taking over mid-task" структура (Current State / Files & Changes / Technical Context / Strategy / Exact Next Steps).
- Loop detection: `hasRepeatedToolCalls` sliding window.

### 5.7. Memory footprint

- Binary: Linux x86_64 ~24.7 MB compressed → ~75–90 MB uncompressed stripped.
- **Без CGO** (modernc SQLite pure Go) → fully static binary.
- SQLite cache_size = 8 MB.
- Pubsub broker per-subscriber cap 4096 events.
- Idle RSS ~50–150 МБ.

### 5.8. Что ОСОБЕННО стоит позаимствовать

| Crush паттерн | Что копировать |
|---|---|
| `Broker<T>` с двумя delivery semantics | `System.Threading.Channels` с `BoundedChannelOptions` |
| 11-subscriber fan-in в один `Channel<object>` в TUI main loop | То же |
| `Workspace` как transport-agnostic interface | Для будущего client/server mode |
| `App.Shutdown()` ordering | Cancel agents → flush debounced writes → parallel cleanup с timeout |
| `PrepareStep` hook в agent loop | Для cache_control, queue drain, hot-swap tools |
| **`streamingMarkdown` prefix-cache** | **Copy verbatim, разница между 60 fps и 5 fps** |
| Single-model-with-imperative-sub-components | Вместо nested Elm для масштаба |
| `fantasy.NewAgentTool` pattern | `Microsoft.Extensions.AI` `AIFunctionFactory.Create` (schema из method parameters) |
| Two-broker permission service | `PermissionKey` persistent grants + `WithHookApproval` context |
| SQLite WAL + `SetMaxOpenConns(1)` | + embedded migrations (→ `DbUp`/`Evolve`) |
| Agent Skills standard | SKILL.md + `ToPromptXML` формат |
| Auto-summarize | Dedicated `summary.md` system prompt + `IsSummaryMessage` flag |

### 5.9. Что НЕ копировать из crush

- **Go 1.26 function iterators** — нет C# эквивалента (используем `IAsyncEnumerable<T>`).
- **`mvdan.cc/sh` embedded shell** — слишком большой dep для harbor, сделаем ограниченный `${VAR}`/`$(cat)` expander.
- **2 SQLite драйвера** — возьмём один (`Microsoft.Data.Sqlite`).
- **4579-line `ui.go`** — split в partial classes в C#.
- **`fang` cobra wrapper** — используем `ConsoleAppFramework v5`.

## 6. Сводная таблица — что берем откуда

| Концепт | Источник | Приоритет |
|---|---|---|
| `EventStream<T, R>` → `Channel<T>` + `IAsyncEnumerable<T>` | pi | MVP |
| `AssistantMessageEvent` discriminated union | pi, opencode | MVP |
| JSONL session format (mirror) | pi | MVP |
| Compaction с structured Markdown summary | pi, kilocode | MVP |
| `estimateTokens` эвристика (chars/4) | pi, kilocode | MVP |
| Steering/follow-up queues | pi | MVP |
| `beforeToolCall` / `afterToolCall` hooks | pi | MVP |
| 4 run modes (interactive/print/json/rpc) | pi | MVP (rpc — v1) |
| Skills format (markdown + frontmatter) | pi, crush | MVP |
| System prompt structure | pi, kilocode | MVP |
| `terminate: true` hint | pi | v1 |
| Tool execution parallel+sequential override | pi | MVP |
| Permission pattern-matching | kilocode | MVP |
| Modes как agents | kilocode | MVP |
| Anchored-summary compaction params | kilocode | MVP |
| SQLite normalized schema | kilocode | MVP |
| MCP через official C# SDK | kilocode, crush | v1 |
| Per-provider system prompts | kilocode | v1 |
| Plugin SDK с hooks | opencode, pi | v1 |
| Client-server с HTTP+SSE | kilocode, opencode, crush | v1 |
| LSP integration (30+ languages) | opencode, crush | v1 (plugin) |
| System Context Algebra (diff-only) | opencode | v2 |
| Durable inbox pattern | opencode | v2 |
| ACP (Agent Client Protocol) | opencode | v2 |
| Streaming markdown prefix cache | crush | v1 (perf) |
| Auto-summarize w/ dedicated summary.md | crush | MVP |
| Snapshot/revert | opencode | v1 |
| Session forking with branch summaries | pi | v1 |
| `Broker<T>` for TUI events | crush | MVP |
| `PrepareStep` hook | crush | v1 |
| Two-broker permission service | crush | v1 |

## 7. Anti-patterns — чего избегать

| Anti-pattern | Где встречается | Почему плохо | Как в harbor |
|---|---|---|---|
| Effect-TS для всего | kilocode, opencode | Learning curve, no C# analog, overhead | `async/await` + DI |
| SolidJS в терминале | kilocode, opencode | Memory overhead, не для streaming | Custom ANSI |
| `cache_size=-64000` SQLite | kilocode | 64 МБ page cache | `-8000` (8 МБ) |
| Все провайдеры грузятся сразу | kilocode | 200+ МБ | Lazy-load per provider |
| heap-snapshot на каждом запуске | kilocode | Instrumentation overhead | Только `--profile` flag |
| mDNS по умолчанию | kilocode, opencode | Network discovery overhead | Опционально для server mode |
| `@parcel/watcher` с 8 платформ | kilocode | Native deps overhead | `System.IO.FileSystemWatcher` |
| JSON file-storage рядом с SQLite | kilocode | Dual storage, sync issues | Только SQLite + JSONL export |
| `marked-shiki` + `remend` + `dompurify` в server | kilocode | Rendering belongs to client | Server отдаёт raw markdown |
| `xlsx`, `mammoth` в core | kilocode | Bloat | Опциональный plugin |
| 4579-line UI файл | crush | Unmaintainable | Split в partial classes |
| Sub-agents in-process | kilocode | Memory overhead | Out-of-process (tmux pattern) |
| Permission popups in core | kilocode | UX complexity | Sandbox внешне (Docker) |
| Plan mode / to-dos in core | kilocode | Scope creep | Extensions/plugins |
| tree-sitter WASM для shell | kilocode | WASM overhead | Regex-based parser |

---

**Next**: `11-risks.md` — risk register, что может пойти не так.
