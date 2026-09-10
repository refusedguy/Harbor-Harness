# Sprint: Renderer Unification

## Context
Выполняем после UI-V2. Цель: унифицировать рендереры, переименовать ConsoleEx, добавить Nickprotop/ConsoleEx как второй backend.

## Architecture
Shared business logic → multiple renderer backends. Каждый рендерер = отдельный проект.

```
Harbor.Ui.Framework.Rendering (shared)
    ↓
    ├── Harbor.Tui.CellForge       ← rename из ConsoleEx
    ├── Harbor.Tui.NickConsoleEx   ← wrapper над nickprotop/ConsoleEx
    ├── Harbor.Tui.AnsiPlain       ← merger Ansi+Plain
    ├── Harbor.Tui.Avalonia
    └── Harbor.Tui.Blazor
```

## Tasks

### Phase 1: Shared Business Logic
Вынести в `Harbor.Ui.Framework.Rendering`:
- ChatScreenLayout
- DiffBlock + UnifiedDiffParser
- ApprovalGateView
- StatusViewModel
- StreamingMarkdownRenderer

### Phase 2: CellForge Rename
- Переименовать `Harbor.Tui.ConsoleEx` → `Harbor.Tui.CellForge`
- Обновить namespace, csproj, все references
- Создать адаптер под ITuiRenderer/BaseTuiRenderer

### Phase 3: NickConsoleEx
- Добавить проект `Harbor.Tui.NickConsoleEx`
- Добавить зависимость на nickprotop/ConsoleEx
- Реализовать renderer через их abstractions
- Toggle в конфиг: `"ui.renderer": "cellforge|nickconsoleex|ansi|plain"`

### Phase 4: Unify Ansi/Plain
- Создать `Harbor.Tui.AnsiPlain` из Ansi + Plain
- Escape-code strategy в отдельный класс
- Убрать дублирование

### Phase 5: Visual Regression Tests
- Golden-frame tests для каждого рендерера
- `tests/Harbor.Tui.RendererTests/`

### Phase 6: Moat-Building Architecture (competitive differentiators)

Эти задачи выходят за рамки простой унификации. Они создают конкурентные рвы, которые команда $10M+ не сможет быстро скопировать — потому что требуют глубокой интеграции с существующими lock-free паттернами, cell-diff движком и UiStore CAS-циклами, которые уже отлажены в Harbor.

---

#### Task 6.1: Renderer Performance Contract & Automated CI Gate

**Objective.** Создать формальный performance contract (SLO) для каждого рендерера-бэкенда и автоматизировать enforcement через CI.

**Rationale.** Корректность-тесты — это table-stakes. Команды-конкуренты имеют unit-тесты для своих renderer-ов. Но ни у кого из открытых TUI-фреймворков (Orca, Kilo, Pi, OpenCode) нет формального контракта, который гарантирует **throughput, latency и memory ceiling** для каждого рендерера-бэкенда. Harbor делает это на уровне UiStore (lock-free CAS), но renderer layer — нет. Если мы зададим contract на уровне архитектуры, каждый новый бэкенд (Spectre, Terminal.Gui, RazorConsole, Sixel, WPF, Avalonia, Blazor, MAUI, Notifications) будет обязан его пройти — иначе его PR не смержится.

**Implementation sketch.**
1. Создать `Harbor.Ui.Framework.Rendering.PerformanceContracts` с тремя контрактами:
   - `ThroughputContract`: минимум N событий/сек на 1000-token stream.
   - `LatencyContract`: p99 времени рендера одного события < X мс.
   - `MemoryContract`: RSS ceiling после 10 минут steady-state streaming.
2. Создать `RendererBenchmarkSuite` в `tests/Harbor.Tui.PerfTests/` — запускает каждый бэкенд через идентичную синтетическую нагрузку (1000 delta/sec, 24×80 терминал).
3. Добавить GitHub Actions шаг `renderer-perf-gate`, который fails, если любой зарегистрированный бэкенд пробивает ceiling.
4. Опубликовать `BENCHMARKS_RENDERERS.md` с публичными числами (как `docs/BENCHMARKS.md`, но per-renderer).

**Files to create.**
- `src/Harbor.Ui.Framework.Rendering/PerformanceContracts/RendererPerformanceContract.cs`
- `src/Harbor.Ui.Framework.Rendering/PerformanceContracts/RendererBenchmarkSuite.cs`
- `tests/Harbor.Tui.PerfTests/RendererBenchmarkTests.cs`
- `.github/workflows/renderer-perf-gate.yml`
- `docs/BENCHMARKS_RENDERERS.md`

**Acceptance criteria.**
- Для каждого из 11+ renderer-бэкендов есть зафиксированные baseline-числа (throughput, p99 latency, RSS).
- CI pipeline fails, если PR вносит регрессию >5% по любому контракту.
- Добавление нового renderer-бэкенда невозможно без предварительного прохождения всех трех контрактов (enforced через `BenchmarkSuite` + PR template check).
- Бенчмарки reproducible локально через `dotnet run --project tests/Harbor.Tui.PerfTests -- --renderer all`.

---

#### Task 6.2: Cell-Diff Renderer Protocol — Portable Differential IR

**Objective.** Вынести cell-diff движок из `Harbor.Tui.ConsoleEx` (CellForge) в `Harbor.Ui.Framework.Rendering` как переносимый протокол `CellDiffMessage`, который все renderer-бэкенды могут emit и consume.

**Rationale.** Zero-alloc cell-diff — это уже конкурентный ров внутри CellForge (`DiffEngine.Flush` использует row-hash fast-path + FrameHint damage rects + fused compare-and-emit). Но сейчас этот ров заперт внутри одного бэкенда. SpectreTui, Terminal.Gui, RazorConsole и другие по-прежнему делают full-screen redraw. Если мы вынесем cell-diff как протокол, то:
- SpectreTui может consumer `CellDiffMessage[]` и применять изменения через `LiveUpdate()` вместо `Refresh()`.
- Blazor Server может применять diffs к DOM через SignalR с bandwidth reduction ~50× (по оценке audit §3.10).
- WPF/Avalonia получают incremental invalidation вместо full layout pass.
- Новый бэкенд получает differential rendering **из коробки** — это и есть competitive moat.

**Implementation sketch.**
1. Создать `Harbor.Ui.Framework.Rendering.Protocol` с типами:
   - `CellDiffMessage` (record: Position, OldCell, NewCell) — immutable, serializable.
   - `CellDiffBatch` — immutable array + `FrameHint` rects + sequence number.
   - `ICellDiffEncoder` / `ICellDiffDecoder` — интерфейсы для encode/decode.
2. Добавить в `ScreenBuffer` и `DiffEngine` статический метод `EncodeDiff(ScreenBuffer prev, ScreenBuffer next, List<Rect> hints)` который возвращает `CellDiffBatch`. Это preserves existing zero-alloc fast-path.
3. Создать `DifferentialRenderPipeline` — абстракция, которая принимает `UiState` и `CellDiffBatch` и применяет их к целевому output-у.
4. Добавить CellForge-адаптер: CellForge пишет в `CellDiffBatch`, а не напрямую в `AnsiWriter`. SpectreTui consume-ит batch.
5. Версионировать протокол: `CellDiffProtocolVersion` (v1 = baseline, v2 = FrameHint rects).

**Files to create.**
- `src/Harbor.Ui.Framework.Rendering/Protocol/CellDiffMessage.cs`
- `src/Harbor.Ui.Framework.Rendering/Protocol/CellDiffBatch.cs`
- `src/Harbor.Ui.Framework.Rendering/Protocol/ICellDiffEncoder.cs`
- `src/Harbor.Ui.Framework.Rendering/DifferentialRenderPipeline.cs`
- `src/Harbor.Tui.CellForge/Rendering/CellForgeDiffEncoder.cs`

**Acceptance criteria.**
- `DiffEngine.Flush` может эмитить `CellDiffBatch` вместо ANSI (opt-in через параметр).
- Минимум 2 renderer-бэкенда используют `DifferentialRenderPipeline` (CellForge + SpectreTui).
- Протокол versioned: backward-compatible decoder для v1 batch-ов.
- `BENCHMARKS_RENDERERS.md` содержит side-by-side сравнение: full-redraw vs cell-diff bandwidth для каждого бэкенда.

---

#### Task 6.3: Lock-Free Hot-Swappable Renderer Runtime

**Objective.** Построить runtime, который позволяет атомарно менять renderer-бэкенд **посередине сессии** без потери streaming-состояния, используя существующий lock-free CAS UiStore.

**Rationale.** UiStore уже использует `Interlocked.CompareExchange` для lock-free state swaps (§PERF-007 resolved). Ни один из конкурентов (Orca, Pi, OpenCode, Kilo) не предлагает hot-swap рендерера без reconnect. Это product-differentiator: пользователь может начать чат в lightweight `ansi` (пайп, CI), потом mid-conversation переключиться на `spectre-tui` для rich panels, и обратно — без потери ни одного токена. Реализация требует, чтобы:
- Новый renderer подхватил текущий `UiState` snapshot.
- Старый renderer корректно disposed без блокировки agent loop.
- Agent loop не знает о переключении — он продолжает publish события в тот же `UiStore`.

**Implementation sketch.**
1. Создать `RendererPipeline` (в `Harbor.Ui.Framework.Rendering`):
   - Держит `volatile ITuiRenderer _current` и `UiStore _store`.
   - `SwapRendererAsync(ITuiRenderer next)` — CAS-цикл на `_current`, аналогичный `UiStore.Dispatch`.
   - Во время swap: новый renderer инициализируется из `_store.State`; старый вызывает `Dispose`.
2. Добавить `IHarborTuiHost.SwapRenderer(string backendId)` — публичный API.
3. В `TuiModule` зарегистрировать `RendererPipeline` как singleton.
4. Добавить slash-команду `/renderer <backend>` для runtime swap.
5. Добавить config-ключ `ui.runtime_swappable: true` + env `HARBOR_TUI_RUNTIME_SWAP` со списком разрешённых бэкендов.
6. На уровне `DefaultAgent` добавить событие `RendererSwapping` — renderer-ы могут подписаться и подготовить свой state.

**Files to create.**
- `src/Harbor.Ui.Framework.Rendering/RendererPipeline.cs`
- `src/Harbor.Ui.Framework.Rendering/IRendererPipeline.cs`
- `src/Harbor.Hosting/RuntimeRendererSwapMiddleware.cs` (или integration в `TuiEffectHost`)
- `tests/Harbor.Tui.Tests/RendererPipelineTests.cs`

**Files to modify.**
- `src/Harbor.Hosting/Modules/TuiModule.cs`
- `src/Harbor.Application/Agents/TuiEffectHost.cs`

**Acceptance criteria.**
- E2E-тест: запустить streaming с `ansi`, mid-stream вызвать `SwapRendererAsync("spectre-tui")`, убедиться что:
  - Ни один токен не потерян (сравнить `UiState.Lines.Length` до и после).
  - Новый renderer корректно инициализирован из текущего `UiState`.
  - Старый renderer вызвал `Dispose` ровно один раз.
- `/renderer` slash-команда работает в interactive режиме.
- Swap атомарен с точки зрения `UiStore`: во время swap `Dispatch` не теряет событий (CAS retry проходит).

---

#### Task 6.4: Differential Markdown Streaming with Frozen-Tail Cache

**Objective.** Вынести `StreamingMarkdownRenderer` из CellForge в `Harbor.Ui.Framework.Rendering` и добавить `FrozenTailMarkdownCache` — кеш, который хранит завершённые markdown-блоки как immutable cell-snapshots и перерисовывает только незавершённый tail.

**Rationale.** Streaming markdown — самая затратная операция рендеринга. В CellForge `StreamingMarkdownRenderer` использует frozen-tail алгоритм (блоки парсятся один раз и кешируются), но эта оптификация заперта внутри одного бэкенда. Вынос в shared framework с formal performance contract создаёт architectural guarantee: **O(1) re-render cost для завершённых блоков, независимо от размера документа**. Конкуренты не могут это скопировать, потому что требует co-design markdown parser + cell-diff engine + immutable snapshot cache — трёх систем, которые в Harbor уже работают вместе, а в других проектах существуют раздельно.

**Implementation sketch.**
1. Создать `Harbor.Ui.Framework.Rendering.Markdown`:
   - `FrozenTailMarkdownCache` — thread-safe cache, где ключ = `MarkdownBlock.Id`, значение = `Cell[]` snapshot.
   - `DifferentialMarkdownPipeline` — принимает `UiState` + новые tokens, возвращает `CellDiffBatch` (только tail изменился).
2. Адаптировать `StreamingMarkdownRenderer` из CellForge: он теперь delegate-ит парсинг в `MarkdownBlockParser` (shared), а кеширует через `FrozenTailMarkdownCache`.
3. Добавить `MarkdownRenderPerformanceContract`:
   - Re-render завершённого блока: <1 ms (enforced через benchmark).
   - Memory ceiling: кеш держит не более 500 завершённых блоков (LRU eviction).
4. Сделать кеш observable: `FrozenTailMarkdownCache.BlockFrozen` event — другие renderer-ы могут подписаться и invalidate свои внутренние кеши.
5. Добавить perf-тест: 10 000-блоковый markdown документ, re-render после каждого токена — total time < 50 ms (зависит от количества незавершённых блоков).

**Files to create.**
- `src/Harbor.Ui.Framework.Rendering/Markdown/FrozenTailMarkdownCache.cs`
- `src/Harbor.Ui.Framework.Rendering/Markdown/DifferentialMarkdownPipeline.cs`
- `src/Harbor.Ui.Framework.Rendering/Markdown/MarkdownBlockParser.cs` (extracted from CellForge)
- `src/Harbor.Ui.Framework.Rendering/PerformanceContracts/MarkdownRenderPerformanceContract.cs`
- `tests/Harbor.Tui.PerfTests/DifferentialMarkdownBenchmark.cs`

**Files to modify.**
- `src/Harbor.Tui.CellForge/Widgets/Markdown/StreamingMarkdownRenderer.cs` (delegate to shared parser + cache)
- `tests/Harbor.Tui.Tests/StreamingMarkdownRendererTests.cs`

**Acceptance criteria.**
- `FrozenTailMarkdownCache` восстанавливает завершённые блоки за <1 ms каждый.
- Re-render 100-блокового документа (99 завершённых) занимает <2 ms.
- Memory ceiling: кеш не растёт бесконечно — LRU eviction работает при >500 блоков.
- SpectreTui и AnsiPlain используют `DifferentialMarkdownPipeline` (emit cell-diffs для markdown вместо full re-layout).

---

## Hard Rules
1. Один коммит = 1–2 файла
2. Nickprotop/ConsoleEx — это ДОБАВЛЕНИЕ, не замена CellForge
3. Мессенджеры НИКОГДА в ядре src/ — только contrib/plugins
4. НЕ трогай UiStore/reducers (кроме Task 6.3 где добавление RendererPipeline оправдано)
5. CellForge / ConsoleEx — оптимизации (AnsiWriter SGR automaton, cell-diff ScreenBuffer/DiffEngine) остаются нетронутыми. Работаем через адаптеры.
6. В Phase 6 все новые типы — immutable / record где возможно, lock-free где есть shared mutable state.

## Deliverables
- Harbor.Tui.CellForge
- Harbor.Ui.Framework.Rendering
- Harbor.Tui.NickConsoleEx
- Harbor.Tui.AnsiPlain
- Harbor.Ui.Framework.Rendering.PerformanceContracts
- Harbor.Ui.Framework.Rendering.Protocol (CellDiffMessage)
- Harbor.Ui.Framework.Rendering.RendererPipeline (hot-swap)
- Harbor.Ui.Framework.Rendering.Markdown (FrozenTailMarkdownCache)
- Visual regression tests
- Renderer performance CI gate
- HTML report в `.kilo-docs/sprints/renderer-unification/report.html`
- `docs/BENCHMARKS_RENDERERS.md`
