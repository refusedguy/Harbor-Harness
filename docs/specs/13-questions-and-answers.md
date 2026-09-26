# 13 — Вопросы и ответы

> Документ: ответы на исходные вопросы пользователя, что упущено, recap по всей спеке.

## 1. Исходные вопросы пользователя

### Q1: «Возможно ли накидать модульный с плагинами harness на .NET?»

**A**: Да, абсолютно. Это не rocket-science — пайплайн простой (streaming → tool-calling → compaction), и .NET 10 + NativeAOT даёт все нужные primitives:
- `IAsyncEnumerable<T>` для streaming.
- `System.Threading.Channels` для очередей.
- `System.Net.ServerSentEvents` (.NET 10 built-in) для SSE parsing.
- `Microsoft.Extensions.AI` для унифицированного `IChatClient`.
- `Microsoft.Extensions.DependencyInjection` для модульности.
- `System.Text.Json` source-gen для AOT-compatible JSON.
- `ConsoleAppFramework v5` для CLI (source-gen, zero reflection).

Плагины — единственное действительно сложное место из-за AOT-ограничений (`AssemblyLoadContext` collectible не работает под AOT). Решение: out-of-process plugin-host (как MCP server, через JSON-RPC over stdio). В JIT-билдах — collectible ALC с hot-reload.

См. `01-architecture.md` для детальной архитектуры, `02-plugins.md` для plugin system.

### Q2: «Меня заебало что мои CLI kilo code opencode итд жрут по 1гб на старте»

**A**: Понятно. Анализ показал, что 1 ГБ у kilocode/opencode складывается из:
- Bun runtime baseline: 50–80 МБ.
- SQLite page cache (`cache_size=-64000`): **64 МБ** ← самое простое исправление.
- tree-sitter WASM + grammars: 30–60 МБ.
- `photon-node` WASM (image processing): 20–40 МБ.
- `node-pty` + `bun-pty`: 10–20 МБ.
- `@parcel/watcher` (8 платформ): 10–20 МБ.
- `@aws-sdk/credential-providers`: 30–50 МБ.
- Vercel AI SDK + 20 providers: 100–200 МБ.
- Effect-TS + OpenTelemetry: 30–60 МБ.
- SolidJS TUI + shiki + marked: 50–100 МБ.
- LanceDB (vector DB): 50–150 МБ.
- Hono HTTP server + mDNS: 10–30 МБ.
- Session caching: 10–100 МБ.

**harbor решает это через**:
- NativeAOT — нет V8/JSC overhead, no JIT compiled code cache.
- `cache_size=-8000` (8 МБ вместо 64 МБ).
- No tree-sitter — regex-based shell parser.
- `SixLabors.ImageSharp` (нативный .NET) вместо WASM.
- `System.Diagnostics.Process` вместо `node-pty`.
- `System.IO.FileSystemWatcher` (built-in) вместо `@parcel/watcher`.
- 2-4 builtin провайдера + lazy-load остальных.
- `async/await` + DI вместо Effect-TS.
- Custom ANSI wrapper вместо SolidJS TUI.
- No LanceDB в core (опциональный plugin).
- No HTTP server по умолчанию (MVP — single-process).
- Paged session loading, не вся история в memory.

**Target**: ~30 МБ RSS idle, ~80 МБ active (10K messages). См. `09-benchmarks.md` для подробного сравнения.

### Q3: «Я сам концепт харнесс вполне понимаю лол... там простой пайплайн: при старте ты открываешь строку для ввода, вводишь - идет стриминг с апишки и ты в реальном времени выводишь, также должны быть тулзы, которые созданы по какой-то схеме и агент если отвечает схемой - вызывается эта тулза. нужно добавить сессии (sqlite - пойдет, но дохуя будто, можно что-то попроще), модульность для провайдеров и тулзов (основные должны быть read/write/bash условные, остальные допиливаются), которые подключены - автоматом улетают в системный промпт и инжектятся перед первым запросом в сессии. если лимит превышен - делаем компакцию. там нихуя сложного и тяжелого нет»

**A**: Полностью согласен. Пайплайн именно такой, как ты описал. Несколько уточнений:

- **Сессии**: SQLite — ОК, не "дохуя". С `cache_size=-8000` (8 МБ вместо 64 МБ у kilocode) — вообще незаметно. JSONL — альтернатива для embedded/portable сценариев. В harbor: SQLite primary + JSONL export для git/diff/archival. См. `05-sessions.md`.

- **Модульность**: `IPlugin` interface, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`. Plugin = DLL + `plugin.json` manifest. Грузятся из `~/.harbor/plugins/` (global) или `.harbor/plugins/` (project-local, с trust prompt). См. `02-plugins.md`.

- **Tools в system prompt**: да, `SystemPromptBuilder` собирает из зарегистрированных tools snippets для injection. Структура: base prompt (per-provider) + env + tools list + guidelines + skills + MCP instructions + context files. См. `04-tools.md` §6.

- **Compaction**: triggered when `estimatedTokens > contextWindow - reserveTokens`. Алгоритм: find cut point (walk backwards, accumulate tokens until `keepRecentTokens`), generate structured Markdown summary (Goal/Constraints/Progress/Key Decisions/Next Steps/Critical Context/Files), insert as `is_summary=true` message, mark head as compacted. См. `05-sessions.md` §4.

- **"Нихуя сложного нет"**: согласен на 80%. Сложные места:
  1. **Plugin isolation под AOT** — `AssemblyLoadContext` collectible не работает. Workaround: out-of-process plugin-host.
  2. **TUI под AOT** — Spectre.Console.Cli НЕ работает (TypeConverter quagmire). Custom ANSI + ConsoleAppFramework v5.
  3. **Compaction quality** — плохой summary ломает long-running сессии. Structured Markdown template + cumulative file tracking + tests.
  4. **Cross-platform nuances** — Windows console, ANSI escape, UTF-8, raw mode differences.
  5. **Memory discipline** — легко получить 200+ МБ просто на кэшах. Streaming everywhere, paged loading, no `List<T>` для hot paths.

### Q4: «Что я упустил? Что будет проблемного?»

**A**: Упущено несколько вещей:

1. **Doom loop detection** — LLM может зациклиться на одной tool call. Решение: 3 одинаковых calls → ask user.

2. **Permission system** — `bash` с `rm -rf /` нельзя вызывать без подтверждения. Pattern matching: `allow|ask|deny` per tool per glob.

3. **Truncated tool calls** — если LLM упёрся в output token limit, tool call args могут быть неполными. Solution: fail all tool calls с informative error, чтобы LLM переиздал.

4. **Steering & follow-up** — пользователь может прервать agent во время стриминга (Escape) или поставить в очередь на следующий turn. Channels для queue.

5. **Max steps per agent** — без лимита agent может работать вечно. `agent.MaxSteps = 50` по умолчанию.

6. **Cost tracking** — каждый `StepFinishEvent` содержит `Usage`, считаем cost. Limit в config: `maxCostPerSession`.

7. **Snapshot/revert** — `write`/`edit` делают snapshot оригинала в `~/.harbor/snapshots/`. `harbor session revert <msg-id>` откатывает ФС.

8. **JSON Schema generation** — для tool params. В .NET 9+ есть `JsonSerializerOptions.GetJsonSchemaAsNode()`. Source-gen для AOT.

9. **System prompt caching** (provider-specific) — Anthropic `cache_control: { type: "ephemeral" }` на system prompt + last 2 messages. Экономит tokens и cache hit rate.

10. **Per-provider system prompts** — Anthropic любит один формат, GPT другой, Gemini третий. 9 вариантов промптов.

11. **OAuth для subscription-based** (Anthropic Pro/Max, OpenAI Codex, GitHub Copilot) — нужен PKCE flow, token storage, auto-refresh.

12. **Reasoning models** (o1, o3, Claude w/ extended thinking) — `reasoning_effort` parameter, thinking content blocks в streaming.

13. **Branching sessions** — fork с любого message, навигация по дереву. JSONL tree через `parentId` pointers.

14. **LSP integration** — `diagnostics`, `references`, `definition`, `hover` для LLM. Опционально, как plugin.

15. **MCP** — Model Context Protocol для external tools (filesystem, github, postgres, etc.).

16. **Skills** — markdown files с frontmatter (`name`, `description`). LLM видит `<available_skills>` в system prompt, читает через `read` tool.

17. **Structured output** — `tool_choice: required` + `structured_output` tool для forced JSON output.

18. **Multi-line input** — Shift+Enter для newline в TUI input.

19. **Bracketed paste mode** — distinguish paste from typed input.

20. **Vim/emacs editor modes** — для power users.

См. `11-risks.md` для risk register, `12-roadmap.md` для prioritization.

### Q5: «Пока ничего не делай, просто детально подробно распиши»

**A**: Это и сделано — 14 документов с детальным разбором всех аспектов. См. `README.md` для навигации.

### Q6: «Я понимаю нюансы с написанием провайдеров и так далее, но эт уже просто невыносимо с таким потреблением ОЗУ»

**A**: Полностью согласен. 1 ГБ на старте — неприемлемо для CLI-тулы. harbor target: 30 МБ RSS idle, 80 МБ active. Это в 10–30 раз меньше, чем kilocode/opencode.

Провайдеры — самая "понятная" часть (HTTP + SSE parsing). Сложнее — plugin isolation под AOT, TUI rendering performance, compaction quality. См. `09-benchmarks.md` для memory analysis.

### Q7: «Я еще думал о https://github.com/nickna/SharpTS - он мб мог бы помочь что-то пиздить с kilocode/opencode/pi-agent»

**A**: SharpTS — НЕ подходит для этой задачи. Это **runtime TypeScript на .NET** (компилирует TS в IL, не в C#-исходники). Для конвертации TS-кода в поддерживаемый C# нужно использовать:
- **AI-ассистированную миграцию** (Claude Code / Cursor) файл-за-файлом, с ревью.
- **sigseg1v/typescript-to-csharp** для AST-верификации.
- **robin-han/typescript-converter** для DTO/интерфейсов.

Но для harbor — лучше **с нуля** в C#, копируя архитектурные паттерны (не код) из pi/kilo/opencode/crush. См. `10-repo-analysis.md` §1 для детального разбора SharpTS.

### Q8: «ты можешь в целом изучить их репы в теории мб... https://github.com/earendil-works/pi https://github.com/kilo-org/kilocode https://github.com/anomalyco/opencode»

**A**: Изучены все 4 репозитория (плюс crush и SharpTS). См. `10-repo-analysis.md` для полного разбора:
- **pi-agent**: JSONL sessions, extensions через jiti, нет MCP осознанно. Самый лёгкий из TS харнессов (~150-300 МБ).
- **kilocode**: форк opencode, Effect-TS, SolidJS TUI, LanceDB. Самый тяжёлый (~700-1100 МБ).
- **opencode**: client-server, Effect-TS, SolidJS TUI, 30+ LSP servers, ACP. Тяжёлый (~500-900 МБ).
- **crush**: Go + Bubbletea v2, single static binary ~25 МБ, idle ~50-150 МБ. Эталон по footprint.

Что позаимствовать — см. `10-repo-analysis.md` §6 (сводная таблица).

### Q9: «я еще нашел эту хуету: https://github.com/charmbracelet/crush»

**A**: crush — отличный референс. Go + Bubbletea v2, single binary ~25 МБ. Главная инновация — **streaming markdown prefix cache** (кэширует рендер stable-prefix'а, ре-рендерит только trailing partial). Это разница между 60 fps и 5 fps на streaming. **Copy verbatim** в harbor.

Также от crush: `Broker<T>` pattern для TUI events, two-broker permission service, `PrepareStep` hook в agent loop, Agent Skills standard (SKILL.md).

Что НЕ копировать: Go function iterators (нет C# эквивалента), `mvdan.cc/sh` embedded shell (слишком большой dep), 4579-line `ui.go` (split в partial classes).

См. `10-repo-analysis.md` §5.

### Q10: «составь просто огромную детальную спеку по всем фронтам раздельную по докам итд итп чтобы можно было скачать изучить. т.е. не просто одно полотно а реально детальный анализ по всем фронтам - реально это? как архитектурно, какие подводные итд итп»

**A**: Это и есть результат — 14 документов:

1. `00-overview.md` — executive summary, проблема, концепт, KPI.
2. `01-architecture.md` — слои, assembly layout, pipeline, DI, lifecycle, threading.
3. `02-plugins.md` — plugin contract, discovery, AssemblyLoadContext, AOT workarounds.
4. `03-providers.md` — LLM abstraction, SSE, Anthropic/OpenAI/Google/Ollama.
5. `04-tools.md` — tool schema, tool-calling loop, permission model, builtin tools.
6. `05-sessions.md` — SQLite vs JSONL, schema, compaction, branching, snapshot/revert.
7. `06-mcp.md` — MCP client, transports, tool aggregation, OAuth.
8. `07-tui.md` — streaming render, slash-commands, split-panes, ANSI, ConsoleAppFramework.
9. `08-native-aot.md` — AOT constraints, reflection, trimming, source generators.
10. `09-benchmarks.md` — memory analysis Node tools vs .NET target, конкретные цифры.
11. `10-repo-analysis.md` — deep dive в 5 реп: что позаимствовать, что НЕ брать.
12. `11-risks.md` — risk register, что может пойти не так.
13. `12-roadmap.md` — MVP → v1 → v2, конкретные milestones.
14. `13-questions-and-answers.md` — этот документ.

Плюс `README.md` с навигацией.

## 2. Что упущено (по результатам ревизии)

После написания всех 14 документов — переоценка того, что ещё можно было бы покрыть:

### 2.1. Telemetry и observability

В `01-architecture.md` §10 упомянуто, но без деталей. Должен быть отдельный раздел про:
- OpenTelemetry integration (traces, metrics, logs).
- `harbor telemetry` command — show current spans.
- Distributed tracing для multi-turn LLM calls.
- Cost dashboard.
- Error tracking (Sentry-like, опционально).

**В спеке v2**: добавить `14-telemetry.md`.

### 2.2. Security hardening

Только поверхностно. Нужны:
- Threat model (что атакующий может сделать).
- Plugin sandboxing (если делать — как).
- Secret scanning в tool outputs (не логировать API keys).
- Network policies (restrict outbound connections).
- Audit log (что пользователь делал, какие tools вызывал).

**В спеке v2**: `15-security.md`.

### 2.3. Internationalization

Не покрыто. Если harbor будет использоваться не только в English:
- System prompts на разных языках.
- Date/number formatting.
- TTY locale detection.

**Решение**: minimal MVP — English only. v1+ — i18n через `Microsoft.Extensions.Localization`.

### 2.4. Accessibility

TUI accessibility — сложная тема. Screen readers (VoiceOver, NVDA) не дружат с TUI.
- OSC 133 prompt markers (как у pi) — для shell integration.
- Plain text fallback mode (`harbor --plain`).
- Reduced motion (`harbor --no-spinner`).

**Решение**: MVP без accessibility. v1+ — добавить.

### 2.5. Performance profiling guide

Как profile harbor, если что-то медленное:
- `dotnet-counters` для live metrics.
- `dotnet-dump` для heap analysis.
- `perfview` (Windows).
- `valgrind massif` (Linux).
- Custom `harbor metrics` command.

**Решение**: добавить в `09-benchmarks.md` §11 (уже есть).

### 2.6. Migration guide

"Coming from kilocode/opencode/pi/crush? Here's what's different":
- Concept mapping table.
- Config translation.
- Session format conversion.
- Plugin equivalents.

**Решение**: добавить `16-migration-guide.md` в v1.

### 2.7. Examples и tutorials

Step-by-step гайды:
- "Build a custom tool plugin".
- "Add a new LLM provider".
- "Configure MCP server".
- "Set up OAuth for Anthropic Pro".

**Решение**: `samples/` директория в репо, со ссылками из README.

### 2.8. API reference

Полный reference всех публичных API:
- `IPlugin`, `ITool`, `ILlmClient`, `IToolRegistry`, etc.
- Каждый method, параметры, возвращаемое значение.
- Примеры использования.

**Решение**: DocFX-generated из XML doc comments. Автоматически.

### 2.9. FAQ

Частые вопросы:
- "How do I add my API key?" → `harbor auth set anthropic sk-...`
- "How do I change model?" → `/model claude-opus-4`
- "How do I see cost?" → `/cost`
- "How do I disable bash tool?" → `/tools disable bash`
- "How do I export session?" → `/session export`
- etc.

**Решение**: `docs/faq.md` в репо, обновлять по мере поступления issues.

### 2.10. Comparison table с конкурентами

Feature-by-feature comparison:
- harbor vs kilocode vs opencode vs pi vs crush vs claude-code vs codex.
- Что поддерживается, что нет.
- Memory footprint.
- Speed.

**Решение**: в README, обновлять каждый release.

## 3. Что бы я изменил, если начинал бы заново

После написания спеки — несколько мыслей о refinement:

### 3.1. JSONL вместо SQLite как primary

В спеке SQLite primary, JSONL mirror. Но JSONL — проще, нет schema migrations, append-only, git-friendly. Для MVP, возможно, лучше JSONL-only:
- Нет миграций.
- Нет SQLite native dependency.
- Single-file portable.
- Branching через parentId pointers.

Минус: slow queries для больших сессий.

**Решение**: оставить SQLite primary, но JSONL-only режим для portable/single-file distribution (v0.3).

### 3.2. Stream-based AgentMessage вместо immutable record

Текущий дизайн: `AgentMessage` immutable, partial updates создают новый instance. Это может быть тяжело для GC на длинных стримах.

Альтернатива: `AgentMessage` mutable, `Parts` — `List<ContentPart>`, append-only. Меньше allocations.

**Решение**: оставить immutable в MVP (чище код), оптимизировать в v0.2 если станет bottleneck.

### 3.3. Microsoft.Extensions.AI vs своя ILlmClient

В спеке: свой `ILlmClient` + adapter поверх `IChatClient`. Это даёт fine-grained control (cache_control для Anthropic, etc.), но diverges от .NET ecosystem.

Альтернатива: использовать `IChatClient` напрямую. Минус — теряем provider-specific features.

**Решение**: оставить свой `ILlmClient`. Anthropic — custom implementation (критичен для нас). OpenAI/Ollama — через `Microsoft.Extensions.AI` provider packages.

### 3.4. Spectre.Console vs custom ANSI

В спеке: custom ANSI в MVP, Spectre опционально в v1.

Если бы начинал заново: Spectre.Console (рендеринг, не CLI!) с самого начала. v0.50+ AOT-compatible, и rich widgets (tables, panels, progress bars) экономят много времени. Custom ANSI — для streaming render (где Spectre не помогает).

**Решение**: пересмотрел — Spectre.Console в MVP для static widgets (slash-command output, tables), custom ANSI для streaming. Обновить `07-tui.md` если решено.

### 3.5. Effect-system lite

В спеке: plain `async/await` + DI. Но для retry logic, circuit breakers, timeout — `Polly` v8.

Можно сделать свой mini-effect-system на records:
```csharp
public abstract record Effect<T>;
public sealed record Pure<T>(T Value) : Effect<T>;
public sealed record Retry<T>(Effect<T> Inner, int Count) : Effect<T>;
public sealed record Timeout<T>(Effect<T> Inner, TimeSpan Duration) : Effect<T>;
```

С interpreter'ом, который выполняет effect tree.

**Решение**: не делать — overkill. Polly v8 + `async/await` достаточно.

## 4. Quick recap по всей спеке

### 4.1. Что в MVP (v0.1.0)

✅ Core agent loop с streaming.
✅ 5 builtin tools (read, write, edit, bash, glob).
✅ 2 LLM providers (Anthropic, OpenAI).
✅ SQLite sessions с compaction.
✅ Custom ANSI TUI с slash-commands.
✅ ConsoleAppFramework v5 CLI.
✅ NativeAOT binary (~5–7 МБ).
✅ <30 МБ RSS idle.

### 4.2. Что в v1.0

✅ Plugin system (out-of-process для AOT).
✅ MCP integration.
✅ LSP integration (30+ languages).
✅ Skills.
✅ OAuth flows.
✅ Branching / snapshot / revert.
✅ Client-server mode.
✅ Multi-platform binaries.
✅ 10+ LLM providers.

### 4.3. Что в v2.0+

- Vector memory (опциональный plugin).
- System Context Algebra.
- Durable inbox pattern.
- ACP (Agent Client Protocol).
- Multi-agent orchestration.
- Streaming markdown prefix cache (perf).

### 4.4. Главные архитектурные решения

1. **Single-process MVP**, optional server mode в v1.
2. **NativeAOT-first**, JIT для dev.
3. **SQLite WAL** primary, JSONL export.
4. **Anchored-summary compaction** с Markdown template.
5. **Plugin contract** через `IPlugin` + compiled DLLs.
6. **LLM abstraction** — `ILlmClient` + `LLMEvent` discriminated union.
7. **Tool schema** — JSON Schema из C# types через source-gen.
8. **Permission model** — `allow|ask|deny` per tool per glob.
9. **TUI** — custom ANSI + ConsoleAppFramework v5, optional Spectre.Console.
10. **Modes = agents** — code, plan, explore.
11. **Streaming** — `IAsyncEnumerable<LLMEvent>` + `Channel<T>`.
12. **JSON** — `System.Text.Json` source-gen.

### 4.5. Главные риски (top-5)

1. **Compaction quality** — тестируем с разными LLM.
2. **TUI perf** — streaming markdown prefix cache.
3. **Scope creep** — strict MVP.
4. **AOT incompatibility** — spike в первой неделе.
5. **Single maintainer** — plugin architecture для community.

## 5. Следующие шаги для пользователя

Если хочешь начать реализацию:

1. **Прочитай всю спеку** (14 документов, ~3-4 часа).
2. **Spike project** (1 неделя): минимальный NativeAOT binary с SQLite + HttpClient + System.Text.Json source-gen + ConsoleAppFramework. Цель — `harbor --version` <30ms, <30MB RSS.
3. **Если spike успешен** — приступай к MVP по `12-roadmap.md` §2.3.
4. **Если spike провалился** — переоценка (что именно не работает: SQLite? JSON source-gen? AOT trim warnings?).
5. **Open-source с самого начала** — GitHub repo, MIT license, README с roadmap. Привлекай контрибьюторов.

Альтернативный путь: **использовать crush** (Go, уже работает, низкий footprint). Если Go не пугает — это fastest path к "легкому харнессу".

Если Go не вариант и хочется .NET — спека готова, можно начинать.

## 6. Финальные мысли

Концепт, который ты описал — действительно простой. Pipeline streaming → tool-calling → compaction реализован во всех существующих харнессах (pi, kilo, opencode, crush). Разница — в качестве engineering, не в concept.

**harbor's value proposition**:
- 10–30× меньше RAM, чем kilocode/opencode.
- 3–5× меньше RAM, чем crush.
- Single-file NativeAOT binary.
- .NET ecosystem (Microsoft.Extensions.AI, DI, Configuration).
- Plugin architecture для расширяемости.

**Главный вызов**: не написать код (это ~15-25K LOC для MVP), а соблюсти memory discipline на каждом уровне. Легко получить 200+ МБ просто на кэшах/контекстах/логах. Каждый PR нужно проверять на RSS regression.

Удачи с реализацией. Спека — отправная точка, не истина в последней инстанции. Адаптируй по мере находок.

---

**Spec complete.** Все 14 документов + README готовы для скачивания и изучения.
