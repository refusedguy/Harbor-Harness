# Harbor — Спецификация .NET AI Coding Harness

> Подробная техническая спецификация для модульного .NET-харнесса с плагинами, NativeAOT, минимальным потреблением ОЗУ. Альтернатива kilocode/opencode/pi-agent (TS, ~1ГБ RAM) и crush (Go, ~100МБ RAM).

## ⚠ Версия спецификации

**v2 (актуальная)** — после feedback loop с пользователем. Главные изменения:
- **Two-process architecture**: Core (NativeAOT) + TUI (JIT) общаются через Unix domain sockets по NDJSON event protocol.
- **TUI = Terminal.Gui v2** (markdown, inline mode) — без AOT-ограничений, в отдельном JIT-процессе.
- **SharpTS как plugin runtime** — TS-плагины компилируются в IL, грузятся в TUI процессе.
- **JSONL-first storage** — вместо SQLite. SQLite опционально через `ISessionStore`.
- **Dynamic providers** — `modelsUrl` + generic `openai-compatible` adapter. Zero-code provider addition.
- **TUI plugins через event bus** — расширение UI как в pi-core.

Документы 14-15 — это **revised архитектура и dynamic providers**. Документы 01-07 — исходная v1, сохранены для reference; где они расходятся с v2 — приоритет у 14-15.

## TL;DR

**Цель**: построить .NET 10 + NativeAOT AI coding CLI/TUI харнесс с:
- **<30 МБ RSS idle** для core (TUI ~80 MB в отдельном процессе).
- **<50 ms cold start** для core.
- **~5–7 МБ core binary** (NativeAOT) + ~80 MB TUI binary (self-contained JIT).
- **Plugin architecture** для tools/providers/agents/UI — SharpTS (TS), C#, out-of-process.
- **Streaming-first** — `IAsyncEnumerable<LLMEvent>` + `Channel<T>` + NDJSON over UDS.
- **Cross-platform** — Linux, macOS, Windows.

**Реалистично?** Да. Two-process architecture снимает главное ограничение (AOT-compatibility TUI). Core под NativeAOT остаётся lean, TUI под JIT берёт Terminal.Gui v2 с rich features.

**Время на MVP**: 4–6 недель одному разработчику.

---

## Документы

### Revised v2 (приоритет)

| # | Документ | Что внутри |
|---|---|---|
| **14** | [**Revised Architecture**](./14-architecture-revised.md) | **Event bus + process split. Core (AOT) + TUI (JIT). Wire protocol. Late-attach. SharpTS plugins.** |
| **15** | [**Dynamic Providers**](./15-providers-dynamic.md) | **`modelsUrl` + generic OpenAI-compat adapter. Zero-code provider addition. Marketplace future.** |

### Базовые (v1, частично overridden)

| # | Документ | Что внутри |
|---|---|---|
| 00 | [Executive Overview](./00-overview.md) | Проблема 1ГБ RAM, концепт harbor, KPI, что в коробке, стек |
| 01 | [Архитектура ядра](./01-architecture.md) | Слои, assembly layout, pipeline, DI, lifecycle, threading model *(v1, см. 14 для revised)* |
| 02 | [Plugin система](./02-plugins.md) | Контракт плагина, discovery, AssemblyLoadContext *(v1, SharpTS добавлен в 14)* |
| 03 | [LLM Провайдеры](./03-providers.md) | `ILlmClient`, SSE streaming, Anthropic/OpenAI/Google/Ollama *(v1, dynamic providers в 15)* |
| 04 | [Tools и tool-calling](./04-tools.md) | Tool schema, JSON Schema gen, tool-calling loop, permission model, builtin tools |
| 05 | [Сессии и компакция](./05-sessions.md) | SQLite vs JSONL, schema, compaction strategy, branching, snapshot/revert *(v1, JSONL-first в 14)* |
| 06 | [MCP](./06-mcp.md) | Model Context Protocol client, transports, OAuth (опциональный plugin) |
| 07 | [TUI](./07-tui.md) | Streaming render, slash-commands, ANSI *(v1, Terminal.Gui v2 + event bus в 14)* |

### Глубокий анализ

| # | Документ | Что внутри |
|---|---|---|
| 08 | [NativeAOT](./08-native-aot.md) | AOT constraints, reflection, trimming, source generators, что НЕ работает |
| 09 | [Бенчмарки](./09-benchmarks.md) | Memory analysis Node tools vs .NET target, конкретные цифры |
| 10 | [Анализ репозиториев](./10-repo-analysis.md) | Deep dive в SharpTS, pi-agent, kilocode, opencode, crush |
| 11 | [Risk Register](./11-risks.md) | Что может пойти не так, mitigation |

### Planning

| # | Документ | Что внутри |
|---|---|---|
| 12 | [Roadmap](./12-roadmap.md) | MVP → v1 → v2, конкретные milestones |
| 13 | [Q&A](./13-questions-and-answers.md) | Ответы на исходные вопросы, что упущено |

---

## Ключевые архитектурные решения

1. **Single-process MVP**, опциональный `harbor serve` в v1.
2. **NativeAOT-first**, JIT-билд для dev (быстрая итерация).
3. **SQLite WAL** для sessions, JSONL mirror для export.
4. **Anchored-summary compaction** с Markdown template (как у kilocode/opencode).
5. **Plugin contract** через `IPlugin` interface + compiled DLLs.
6. **`ILlmClient`** abstraction + `LLMEvent` discriminated union.
7. **Tool schema** — JSON Schema из C# types через source-gen.
8. **Permission model** — `allow|ask|deny` per tool per glob.
9. **TUI** — custom ANSI wrapper + `ConsoleAppFramework v5`.
10. **Modes = agents** — `code`, `plan`, `explore`.
11. **Streaming** — `IAsyncEnumerable<LLMEvent>` + `Channel<T>`.
12. **JSON** — `System.Text.Json` source-gen, ноль reflection.

---

## Целевые метрики (KPI)

| Метрика | kilocode/opencode | crush | **harbor (target)** |
|---|---|---|---|
| Cold start | ~500–1500 ms | ~30–80 ms | **<50 ms** |
| RSS idle | ~700 МБ | ~100 МБ | **<30 МБ** |
| RSS active (10K msg) | ~1 ГБ | ~200 МБ | **<80 МБ** |
| Binary size | ~80 МБ | ~25 МБ | **~5–7 МБ** |
| Streaming latency | ~30 ms | ~15 ms | **<20 ms** |

**Улучшение vs kilocode**: 10–30× по памяти, 20–30× по startup.

---

## Стек

| Слой | Технология | Почему |
|---|---|---|
| Runtime | .NET 10 + NativeAOT | LTS, NativeAOT mature |
| CLI parsing | `ConsoleAppFramework v5` | Source-gen, zero reflection, AOT-safe |
| JSON | `System.Text.Json` source-gen | Built-in, AOT-compatible |
| DI | `Microsoft.Extensions.DependencyInjection` | Standard, AOT-compatible |
| Hosting | `Microsoft.Extensions.Hosting` | Standard lifecycle |
| LLM abstraction | `Microsoft.Extensions.AI` + custom `ILlmClient` | Unified, but fine-grained control for Anthropic |
| Storage | `Microsoft.Data.Sqlite` + `Dapper.AOT` | AOT-compatible, no EF Core |
| Streaming | `IAsyncEnumerable<T>` + `System.Threading.Channels` | Built-in primitives |
| SSE parsing | `System.Net.ServerSentEvents` (.NET 10) | Built-in! |
| MCP | `ModelContextProtocol` NuGet (Microsoft official) | v1 |
| TUI | Custom ANSI + опционально `Spectre.Console` ≥ 0.50 | AOT-compatible rendering |
| Markdown | `Markdig` (с явно указанными extensions) | AOT-friendly config |
| Image | `SixLabors.ImageSharp` | Native .NET, no WASM |
| Diff | `DiffPlex` | AOT-compatible |
| Logging | `Serilog` (file + console) | AOT-compatible |
| Telemetry | `System.Diagnostics.ActivitySource` (OpenTelemetry) | Built-in |

**НЕ используем**:
- ❌ `Spectre.Console.Cli` — TypeConverter quagmire, не AOT-compatible.
- ❌ `EF Core` — не AOT-compatible (используем Dapper.AOT).
- ❌ `Newtonsoft.Json` — reflection-heavy.
- ❌ `AutoMapper` / `MediatR` — reflection-based (используем Mapperly / Mediator если нужно).
- ❌ `AssemblyLoadContext` collectible в AOT-mode (только JIT).
- ❌ Effect-TS analog — обычный `async/await` + DI.
- ❌ SolidJS TUI — custom ANSI вместо.

---

## Что в MVP (v0.1.0)

### Входит

- **Ядро**: Agent, AgentLoop, AgentEvent discriminated union, Session, Message, Part.
- **5 builtin tools**: `read`, `write`, `edit`, `bash`, `glob`.
- **2 LLM providers**: Anthropic (Messages API, cache_control, extended thinking), OpenAI (Chat Completions + Responses API).
- **2 agents**: `code` (default), `plan` (read-only).
- **SQLite sessions** с WAL mode, compaction.
- **Custom ANSI TUI** с slash-commands (`/help`, `/clear`, `/model`, `/agent`, `/compact`, `/quit`).
- **Permission system** с pattern matching.
- **ConsoleAppFramework v5** CLI (`run`, `ask`, `--version`).
- **NativeAOT binary** ~5–7 МБ.

### НЕ входит (отложено в v1+)

- MCP integration.
- LSP integration.
- Plugins (только compile-time hardcoded).
- Client-server mode.
- Branching / forking sessions.
- Skills.
- OAuth flows.
- Sub-agents (`task` tool).

См. `12-roadmap.md` для полного плана.

---

## Что позаимствовано из референсных репозиториев

| Концепт | Источник | Документ |
|---|---|---|
| `EventStream<T,R>` → `Channel<T>` + `IAsyncEnumerable<T>` | pi-agent | `01-architecture.md`, `03-providers.md` |
| `AssistantMessageEvent` discriminated union | pi, opencode | `03-providers.md` |
| JSONL session format (mirror) | pi | `05-sessions.md` |
| Compaction с structured Markdown summary | pi, kilocode | `05-sessions.md` |
| `estimateTokens` эвристика (chars/4) | pi, kilocode | `05-sessions.md` |
| Steering/follow-up queues | pi | `01-architecture.md` |
| Permission pattern-matching (`allow\|ask\|deny` per glob) | kilocode | `04-tools.md` |
| Modes как agents с permission rulesets | kilocode | `01-architecture.md` |
| Anchored-summary compaction params | kilocode | `05-sessions.md` |
| SQLite normalized schema | kilocode | `05-sessions.md` |
| MCP через official C# SDK | kilocode, crush | `06-mcp.md` |
| Per-provider system prompts | kilocode | `04-tools.md` |
| Plugin SDK с hooks | opencode, pi | `02-plugins.md` |
| Client-server с HTTP+SSE | kilocode, opencode, crush | `12-roadmap.md` (v1) |
| LSP integration (30+ languages) | opencode, crush | `12-roadmap.md` (v1) |
| **Streaming markdown prefix cache** | **crush** | `07-tui.md` (критично для perf) |
| Auto-summarize с dedicated summary.md | crush | `05-sessions.md` |
| `Broker<T>` для TUI events | crush | `07-tui.md` |
| Two-broker permission service | crush | `04-tools.md` |
| Agent Skills standard (SKILL.md) | pi, crush | `12-roadmap.md` (v0.5) |
| Snapshot/revert | opencode | `12-roadmap.md` (v0.7) |

См. `10-repo-analysis.md` для полного разбора каждого репо.

---

## Главные риски (top-5)

1. **Compaction quality** — плохой summary ломает long-running сессии. Structured Markdown template + cumulative file tracking + golden tests.
2. **TUI perf** — streaming markdown rendering может быть медленным. Streaming markdown prefix cache (как у crush), throttling 30fps.
3. **Scope creep** — попытка реализовать все фичи в MVP. Strict MVP scope, deferred features в roadmap.
4. **AOT incompatibility** — какая-то критическая либа может не работать под AOT. Spike project в первой неделе.
5. **Single maintainer** — невозможно поддерживать 36 провайдеров + 30 LSP servers. Plugin architecture для community contributions.

См. `11-risks.md` для полного risk register.

---

## Следующие шаги

### Если хочешь начать реализацию

1. **Прочитай всю спеку** (14 документов, ~3-4 часа).
2. **Spike project** (1 неделя):
   - Минимальный NativeAOT binary.
   - SQLite + HttpClient + System.Text.Json source-gen + ConsoleAppFramework.
   - Цель: `harbor --version` <30ms, <30MB RSS.
3. **Если spike успешен** → MVP по `12-roadmap.md` §2.3.
4. **Если spike провалился** → переоценка (что именно не работает).

### Альтернативный путь

**Использовать crush** (Go, уже работает, ~25 МБ binary, ~100 МБ RSS). Если Go не пугает — это fastest path к "легкому харнессу".

Если Go не вариант и хочется .NET — спека готова, можно начинать.

### Open-source стратегия

- GitHub repo с самого начала.
- MIT license.
- README с roadmap и benchmarks.
- Привлекай контрибьюторов через plugin architecture.
- Публикация как `dotnet tool install harbor`.

---

## Структура файлов

```
specs/
├── README.md (этот файл)
├── 00-overview.md
├── 01-architecture.md
├── 02-plugins.md
├── 03-providers.md
├── 04-tools.md
├── 05-sessions.md
├── 06-mcp.md
├── 07-tui.md
├── 08-native-aot.md
├── 09-benchmarks.md
├── 10-repo-analysis.md
├── 11-risks.md
├── 12-roadmap.md
└── 13-questions-and-answers.md
```

Суммарно ~5000+ строк детальной технической спецификации.

---

## Лицензия спецификации

Документация — MIT. Используй, модифицируй, распространяй freely.

Если реализуешь harbor по этой спеке — дай знать, интересно послеживать.

---

**Spec version**: 1.0
**Last updated**: 2026-07-16
**Total documents**: 14 (включая README)
**Estimated reading time**: 3-4 часа
