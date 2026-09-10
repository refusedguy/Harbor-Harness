# 00 — Executive Overview

> Документ: обзорный. Отвечает на вопросы «зачем», «реально ли», «что в коробке». Детали — в последующих разделах.

## 1. Постановка проблемы

Пользователь эксплуатирует сразу несколько TypeScript-based AI coding CLI/TUI (kilo code, opencode, pi-agent). На старте каждый из них съедает порядка **1 ГБ RSS**. Это невыносимо для параллельной работы нескольких харнессов, для CI, для ноутбуков с ограниченной памятью, и для любого сценария «запустил-поработал-закрыл».

Параллельно существует класс Go-харнессов (crush от Charm), которые при сопоставимой функциональности укладываются в 50–150 МБ RSS и бинарник ~25 МБ. Это доказывает, что **проблема не в парадигме, а в стеке**.

Цель — построить .NET-харнесс с плагинной архитектурой, который:
- стартует за <30 мс;
- держит RSS <50 МБ в idle;
- компилируется в single-file NativeAOT-бинарник;
- остаётся **модульным** (провайдеры, тулзы, сессии — все подключаемые);
- кроссплатформенный (Linux/macOS/Windows);
- ре-имплементирует концепт «streaming → tool-calling → compaction», известный по pi/kilo/opencode/crush.

## 2. Короткий ответ: реально ли?

**Да, абсолютно.** Это не rocket-science. Пайплайн, описанный пользователем, действительно простой:

```
input → system prompt assembly → LLM stream → token-by-token render
              ↓                       ↓
        inject tools             tool-call detection
                                      ↓
                              tool execution (parallel/sequential)
                                      ↓
                              tool results → next turn
                                      ↓
                              (loop until stop_reason != "tool_use")
                                      ↓
                              context size check → compaction if needed
```

Все существующие харнессы (pi, kilocode, opencode, crush) реализуют именно этот цикл. Разница — в качестве изоляции, в UX, в экосистеме провайдеров, в footprint.

**Сложные места не там, где кажется**:

| Кажется сложным | На самом деле |
|---|---|
| LLM streaming | Тривиально — `HttpClient` + ручной SSE-парсер на 100 строк |
| Tool-calling loop | Цикл `while (true)` с проверкой stop_reason |
| System prompt assembly | Конкатенация строк с шаблонами |
| Sessions | JSONL или SQLite — оба варианта работают |
| **TUI под NativeAOT** | **Сложно** — Spectre.Console.Cli НЕ работает, нужен ConsoleAppFramework + custom ANSI |
| **Plugin isolation** | **Сложно** — `AssemblyLoadContext` collectible НЕ работает под AOT, нужны компромиссы |
| **Compaction quality** | **Сложно** — плохой summarization-промпт ломает long-running сессии |
| **Memory discipline** | **Сложно** — легко получить 200+ МБ просто на кэшах/контекстах/логах |

## 3. Почему не Node/TS, не Go, не Rust?

| Стек | Плюсы | Минусы для нашего кейса |
|---|---|---|
| **Node + TS** (kilo, opencode, pi) | Огромная экосистема, AI SDK, MCP SDK | V8 heap 50+ МБ baseline, dynamic import не спасает, native deps раздуты, 1 ГБ после тёплого старта |
| **Go** (crush) | Компилируемый, низкий RSS, bubbletea, cross-compile | Нет дженериков-_first-class, reflection ограничен, плагины только через `plugin` package (Linux-only, хрупкий) |
| **Rust** | Лучший footprint, memory safety | Steep learning curve, медленная компиляция, нет stable plugin story |
| **.NET 10 + NativeAOT** | Статическая типизация, source generators, NativeAOT, MS-стек, `Microsoft.Extensions.AI` | Меньше AI-экосистемы чем в TS, plugin isolation под AOT — компромиссный |

**Почему .NET для пользователя**: он сам пишет «я больше по дотнету». .NET 10 даёт:
- Статическую типизацию → меньше багов в plugin contracts.
- Source generators → compile-time schema generation, zero reflection.
- `Microsoft.Extensions.AI` → унифицированный `IChatClient` поверх OpenAI/Anthropic/Ollama.
- NativeAOT → 3–15 МБ бинарник, 15–25 МБ RSS.
- `Microsoft.Extensions.DependencyInjection` → привычный DI.
- `IAsyncEnumerable<T>` → нативный streaming primitive.
- `System.Threading.Channels` → producer/consumer без сторонних либ.

## 4. Архитектурный концепт в одном абзаце

Харнесс — это **headless ядро** (`Harbor.Core.dll`) + **CLI/TUI frontend** (`harbor` binary, NativeAOT) + **опциональные plugins** (NuGet-пакеты или compiled DLLs). Ядро содержит: `Agent` (stateful wrapper поверх event loop), `AgentLoop` (цикл streaming→tool→compaction), `SessionStore` (SQLite WAL или JSONL), `ToolRegistry` (builtin + plugin tools), `ProviderRegistry` (lazy-loaded LLM clients), `ExtensionHost` (грузит plugin DLLs через `AssemblyLoadContext`, в NativeAOT-mode — через pre-registered source-gen dispatch). TUI рендерит streaming-токены через `Console.Write` + ANSI escape codes (без Spectre.Console.Cli), slash-команды парсятся через `ConsoleAppFramework v5`.

## 5. Что НЕ делать (решения, принятые заранее)

1. **Не использовать Spectre.Console.Cli** — архитектурно несовместим с NativeAOT (TypeConverter quagmire, не починят до 1.0). См. `08-native-aot.md` §1.3.
2. **Не использовать EF Core** — не AOT-compatible. Используем Dapper.AOT или raw `Microsoft.Data.Sqlite`.
3. **Не использовать `AssemblyLoadContext` collectible** в NativeAOT-билдах — не работает. В JIT-билдах можно, в AOT — process isolation (spawn второго процесса для plugin-host).
4. **Не использовать Newtonsoft.Json** — reflection-heavy. Только `System.Text.Json` source-gen.
5. **Не тащить LanceDB / vector DB** в ядро — опциональный plugin, если вообще нужен. Memory — это фича, не базис.
6. **Не использовать Effect-TS analog** — обычный `async/await` + `IAsyncEnumerable<T>` + `Result<T, TError>` (через `OneOf` или record patterns). Effect-system даёт пользу в TS-мире из-за отсутствия нативной async-стек; в C# это overkill.
7. **Не делать SolidJS-in-terminal**. TUI — простой, на ANSI escape codes. UI логика — на `record State` + чистых функциях-редьюсерах (Elm-стиль, как у crush).
8. **Не включать MCP по умолчанию** — как плагин, по требованию. Pi сознательно не имеет MCP; crush имеет, но как опциональный transport. Кладём в `Harbor.Mcp` пакет, регистрируется через plugin contract.
9. **Не делать client-server по умолчанию**. Kilo/opencode пошли в HTTP+SSE server + thin clients — это даёт переиспользование сессии, но добавляет 30–60 МБ RAM и сложность. MVP — single-process. Server-mode — отдельная команда `harbor serve` если понадобится.
10. **Не делать LSP integration в ядре**. Plugin. У crush — 30+ builtin LSP-серверов, у kilo/opencode — то же. Это удобно, но это плагин, не ядро.

## 6. Что в коробке ( builtin )

| Слой | Builtin | Plugin |
|---|---|---|
| Providers | Anthropic, OpenAI, Google Gemini, Ollama (OpenAI-compatible) | Azure, Bedrock, Mistral, xAI, Groq, Cohere, OpenRouter, github-copilot, любые OpenAI-compat |
| Tools | `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls` | `webfetch`, `websearch`, `task` (subagent), `todo`, `diagnostics` (LSP), `skill`, MCP-tools |
| Sessions | SQLite WAL + JSONL (dual) | Git-based snapshots, vector memory |
| Modes/Agents | `code` (default), `plan` (read-only), `explore` (subagent, read-only fast) | `debug`, `architect`, `review`, любые кастомные |
| Compaction | Anchored summary (Markdown template, incremental) | Custom compaction strategies |
| Auth | API key from env/config | OAuth (Copilot, Anthropic Pro, Google) |
| CLI | `harbor run`, `harbor serve`, `harbor session`, `harbor plugin`, `harbor models` | TUI slash-commands |
| MCP | (plugin) | stdio/HTTP/SSE transports |
| LSP | (plugin) | 30+ language servers, auto-spawn |

## 7. Целевые метрики (KPI)

| Метрика | Текущие (kilo/opencode) | Цель (Harbor) | Как измерять |
|---|---|---|---|
| Cold start | ~500–1500 мс | **<50 мс** | `hyperfine 'harbor --version'` |
| RSS idle | ~500–1000 МБ | **<50 МБ** | `/usr/bin/time -v harbor --version` |
| RSS active session (1K messages) | ~700 МБ–1.5 ГБ | **<150 МБ** | `ps -o rss` во время работы |
| Binary size | N/A (Node bundled ~80 МБ) | **5–15 МБ** (NativeAOT, strip symbols) | `ls -lh harbor` |
| Streaming latency (token-to-screen) | ~30–80 мс | **<20 мс** | microbenchmark |
| Tool execution overhead (read) | ~50–100 мс | **<10 мс** | microbenchmark |
| Compaction (10K tokens) | ~5–15 s | **<3 s** | end-to-end test |

## 8. Что уже есть в экосистеме .NET (можно переиспользовать)

| Библиотека | Зачем | AOT? |
|---|---|---|
| `Microsoft.Extensions.AI` | Унифицированный `IChatClient`, `AIFunction`, `IFunctionInvocationService` | ✅ (через source-gen) |
| `Microsoft.Extensions.AI.OpenAI` | OpenAI provider | ✅ |
| `Microsoft.Extensions.DependencyInjection` | DI container | ✅ |
| `Microsoft.Extensions.Hosting` | `IHost`, `IHostedService`, lifecycle | ✅ |
| `Microsoft.Data.Sqlite` + `Dapper.AOT` | SQLite access, AOT-safe | ✅ |
| `ModelContextProtocol` (MS official) | MCP C# SDK | ✅ (с source-gen) |
| `System.Net.ServerSentEvents` (.NET 10) | SSE parsing (built-in!) | ✅ |
| `System.Threading.Channels` | Producer/consumer queues | ✅ (built-in) |
| `ConsoleAppFramework v5` | CLI parser, source-gen, zero reflection | ✅ |
| `Spectre.Console` ≥ 0.50 | ANSI rendering (panels, tables, markup) — **только рендеринг, не CLI** | ✅ |
| `Markdig` | Markdown → ANSI / HTML | ⚠️ (нужна конфигурация для AOT) |
| `DiffPlex` | Diff для edit-tool preview | ✅ |
| `SixLabors.ImageSharp` | Image processing (для read-image tool) | ✅ |
| `Serilog` | Structured logging | ✅ (с source-gen sink) |
| `OmniSharp.Extensions.LanguageServer.Client` | LSP client (для plugin) | ⚠️ (требует тестирования под AOT) |
| `OneOf` | Discriminated unions для C# | ✅ |

## 9. Структура спецификации

| Документ | Что внутри |
|---|---|
| `00-overview.md` | Этот файл — обзор, проблема, концепт, KPI |
| `01-architecture.md` | Слои, assembly layout, pipeline, DI, lifecycle, threading |
| `02-plugins.md` | Plugin contract, discovery, AssemblyLoadContext, source-gen dispatch под AOT |
| `03-providers.md` | LLM provider abstraction, SSE, streaming events, Anthropic/OpenAI/Google/Ollama |
| `04-tools.md` | Tool schema (JSON Schema из C# types), tool-calling loop, permission model, builtin tools |
| `05-sessions.md` | SQLite vs JSONL, schema, compaction strategy, branching, snapshot/revert |
| `06-mcp.md` | Model Context Protocol client, transports, tool aggregation |
| `07-tui.md` | Streaming render, slash-commands, split-panes, ANSI, ConsoleAppFramework integration |
| `08-native-aot.md` | AOT constraints, reflection, trimming, source generators, что НЕ работает |
| `09-benchmarks.md` | Memory analysis (kilo/opencode/pi/crush vs Harbor target), startup time, binary size |
| `10-repo-analysis.md` | Deep dive в 5 реп: что позаимствовать, что НЕ брать |
| `11-risks.md` | Risk register, что может пойти не так, mitigation |
| `12-roadmap.md` | MVP → v1 → v2, конкретные milestones, что в каком релизе |
| `13-questions-and-answers.md` | Ответы на исходные вопросы пользователя, что упущено |
| `README.md` | Навигация, TL;DR, ссылки |

## 10. Ключевые архитектурные решения (cheat sheet)

1. **Single-process MVP**, опциональный `harbor serve` для client/server.
2. **NativeAOT-first**, JIT-билд для dev (быстрая итерация).
3. **SQLite WAL** для sessions, **JSONL mirror** для human-readable export.
4. **Anchored-summary compaction** (как у kilo/opencode) с Markdown template.
5. **Plugin contract** — `IPlugin` interface + compiled DLLs, грузятся через `AssemblyLoadContext` (JIT) или pre-registered source-gen dispatch (AOT).
6. **LLM abstraction** — `IChatClient` из `Microsoft.Extensions.AI` + наш `LLMEvent` discriminated union поверх.
7. **Tool schema** — генерация JSON Schema из C# types через `NJsonSchema` или `System.Text.Json.Schema` (.NET 9+).
8. **Permission model** — `allow|ask|deny` per tool per glob (как у kilo).
9. **TUI** — custom ANSI wrapper (50 LOC) + `ConsoleAppFramework v5` для CLI/slash-commands, опционально `Spectre.Console` для rich widgets (panels/tables, не CLI).
10. **Modes = agents** — `code`, `plan`, `explore` builtin; custom через config.
11. **Streaming** — `IAsyncEnumerable<LLMEvent>` + `Channel<T>` для backpressure.
12. **JSON** — `System.Text.Json` source-gen (`JsonSerializerContext`), ноль reflection.

---

**Next**: `01-architecture.md` — детальная архитектура ядра.
