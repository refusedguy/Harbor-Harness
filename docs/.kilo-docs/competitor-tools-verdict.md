# Аудит tools конкурентов → вердикт для Harbor

> **СТАТУС (2026-08-27): частично внедрено.**
> - ✅ **SSRF-guard webfetch** — сделан: `WebFetchTool` блокирует loopback/link-local/RFC1918/IPv6-UL + metadata-endpoint;
> - ❌ todo/plan tool, question tool, MCP annotations, admission control субагентов — ещё НЕ реализованы
>   (toolset по-прежнему 14 builtin; актуальный план — [ROADMAP v0.5–v0.7](../docs/ROADMAP.md)).
>
> Ниже — point-in-time вердикт по клонам конкурентов от 25.08.2026; перед стартом нового спринта сверяйте пункты с ROADMAP.

> 4 параллельных субагента, 25.08.2026. Полные отчёты: audit-tools-{grok,codex,opencode,aider-goose}.md
> + codex-rs-tools-full.md (31КБ дип-дайв). Клоны: ~/Projects/Harbor-Harness-Analysis/repos/ + /tmp/audit-{aider,goose}, ~/opencode.

## Сводная таблица «чего нет у Harbor» (по всем конкурентам)

| Фича | Кто имеет | Вердикт | Куда в роадмап |
|---|---|---|---|
| **todo/plan tool** (checklist в контексте модели) | codex update_plan, grok todo_write, opencode todowrite — ВСЕ трое | **КРАСТЬ обязательно**. Три независимые реализации = консенсус индустрии. Семантика Claude Code: полная замена списка, ровно один in_progress. Codex хранит как pure event (no persistence), opencode — SQLite per-session | S1-смежный, дёшево: 1 tool + событие |
| **repo-map** (tree-sitter PageRank карта репо) | aider (живая проверка на себе: 867 строк) | **КРАСТЬ для read-контекста**. Конвейер: tags→граф→PageRank (веса: mentioned ×10, ref-in-chat ×50)→бинарный поиск под токен-бюджет. Для .NET — tree-sitter C# или ICSharpCode.Decompiler альтернативы | после ConsoleEx, отдельный спринт |
| **background shells + unified exec** | grok run_terminal_command(background), codex exec_command+write_stdin+wait, goose shell streaming | **КРАСТЬ**: долгие команды (тесты/билды) не блокируют цикл. Codex: session-per-name, write_stdin для интерактива, wait с таймаутом | S1-смежный (субагентам нужно то же ядро) |
| **apply_patch V4A freeform** | codex, opencode (только GPT), grok search_replace | ⚠️ НИШЕВОЕ: у нас edit exact-replace уже есть; V4A выгоден только для мультифайловых правок одним вызовом. Opencode честно гейтит по modelID — эвристика хрупкая | отложить; revisit при мультифайловых спринтах |
| **scheduler** (cron промптов) | grok scheduler_create/delete/list | **КРАСТЬ частично**: у юзера уже есть Hermes-cron снаружи; внутрь Harbor нужен минимум — durable wake-up между сессиями | после S1 |
| **monitor** (watch stdout → события) | grok monitor (token-bucket 10/2с, debounce 200мс, авто-kill) | **НИШЕВОЕ но вкусное** для S1: субагенты + long-running процессы требуют подписки на вывод | S1+ |
| **tool_search / deferred loading** (BM25 по MCP-схемам, лимит 8) | grok search_tool/use_tool, codex tool_search | **КРАСТЬ когда MCP-серверов >3**: сейчас Harbor грузит все схемы — на 50+ тулзах контекст сдувает | с ростом contrib-экосистемы |
| **subagent admission control** (лимит конкурентных, очередь, spawn/query/cancel) | grok 32 max + env override, goose async max 5 + TTL 600s | **КРАСТЬ в S1 напрямую**: лимит + очередь + load(taskId). Goose: воркеры НЕ получают delegate (запрет рекурсии фильтром типа сессии) | S1 |
| **orchestrator персистентных агентов** (list/view/start/send/interrupt) | goose orchestrator (717 строк) | **S2**: это следующий слой после S1 — долгоживущие агенты с рабочими директориями | S2 |
| **workflow движок** (Rhai, agent()/parallel(), бюджет 128) | grok workflow | ❌ МУСОР пока: преждевременно без S1/S2 базы | не брать |
| **goal tool с классификатором-ack** | grok update_goal | ❌ МУСОР: модель сама знает свой прогресс | не брать |
| **image_gen/edit/video** | grok imagine | ❌ вне канона кодинг-агента | не брать |
| **lsp tool** (diagnostics/references/definition) | grok lsp, opencode lsp (эксперименты у обоих!) | ⚠️ отложить: оба конкурента держат его в экспериментальном флаге — сырой. v0.7.0 роадмапа | v0.7 |
| **question/ask_user tool** | opencode question, codex request_user_input | **КРАСТЬ простой**: структурированный вопрос с опциями вместо свободного текста. Дёшево, UX сразу лучше | ConsoleEx-волна (ApprovalGate уже рядом) |
| **SSRF-защита web_fetch** (loopback/RFC1918/CGNAT/TEST-NET блок, TTL-кэш резолвов) | grok | **КРАСТЬ немедленно**: у Harbor web-fetch без этого — дыра. Публичный hostname → loopback должен оставаться заблокирован | ROP-хвост или отдельный фикс |
| **bash state persistence через capture/replay** (cwd/env/алиасы) | grok | ⚠️ НИШЕВОЕ: opencode вообще stateless и не страдает. Harbor bash уже с cwd-параметром | нет |
| **vendor-бандл бинарников** (ugrep/find самораспаковка) | grok | ❌ МУСОР для Harbor: AOT+BCL-only канон, внешние бинари ломают модель дистрибуции | не брать |
| **cursor rules on read** (.cursorrules инжект напоминаний) | grok | ⚠️ НИШЕВОЕ: у нас AGENTS.md читается при старте — эквивалент уже есть | нет |
| **in-process platform extensions** (developer suite БЕЗ внешнего MCP-процесса, реализует McpClientTrait) | goose | **ИНТЕРЕСНЫЙ ПАТТЕРН**: builtin-тулы как локальный «MCP-сервер» — единый протокол для builtin и внешних. Harbor сейчас два пути (ITool vs MCP) | обсудить при рефакторинге Tools |
| **MCP annotations** (readOnly/destructive/openWorld на каждом туле) | goose developer suite | **КРАСТЬ metadata**: дёшево, кормит permission-систему (C2 granular approvals уже есть — аннотации её усилят) | ROP-хвост |
| **tree-sitter AST пре-скан bash команд** (external_directory ask) | opencode | ⚠️ НИШЕВОЕ: пермишены Harbor уже гранулярные; tree-sitter-WASM тянет зависимость | нет |

## Топ-5 немедленных действий

1. **SSRF-guard в web-fetch** (grok-паттерн, ~100 строк) — дыра безопасности сегодня
2. **todo/plan tool** — консенсус троих, семантика описана, 1 день работы
3. **MCP annotations readOnly/destructive** → усилить существующие granular approvals
4. **question tool** — структурированные вопросы модели к юзеру
5. В **S1 (субагенты)** заложить сразу: admission limit + async + load(taskId) + запрет рекурсии (goose-паттерн)

## Анти-уроки (что НЕ копировать)

- Opencode: расхождение промпта и реализации («persistent shell» а его нет) — доки врут
- Codex: fail-open в sandbox-проверках путей (TODO в коде!) — security через платформенный sandbox нельзя оставлять единственным слоем
- Codex: string-replace сборка lark-грамматики — хрупкая генерация
- Grok: версионированные behavior-пресеты tools (legacy-0.4.10 рядом с current) — признание что ломают контракты; у нас AOT-freeze дисциплина строже
