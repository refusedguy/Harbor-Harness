# ConsoleEx фаза 2.5 — builtin views: разбор лучших TUI + дизайн компонентов

> **СТАТУС (2026-08-27): реализовано (CE-2…CE-4).** Virtualized timeline со streaming-markdown (pacer-gated reveal),
> tool-call карты с unified-diff телами, status-segment bar + tick-spinner, multi-line composer — в
> `src/Harbor.Tui.ConsoleEx/Widgets/`. Сравнительный разбор grok/codex/lazygit/Spectre ниже оставлен как rationale
> (см. ADR-003 в DECISIONS.md).

> Спринт-7 волна аудитов Harbor. Компаньон к `consoleex-design.md` (фазы 0–2,
> ввод/конвейер) и `consoleex-celldiff.md` (ядро cell-diff). Эта волна — слой
> ВИДОВ поверх ядра: что показываем и как считаем. READ ONLY — код репо не
> менялся. Ограничения неизменны: .NET 10, **BCL-only**, AllowUnsafeBlocks=false,
> PublishAot=true, TreatWarningsAsErrors. Таргеты specs/07-tui.md: token-to-screen
> < 20 ms, память TUI < 20 MB, бюджет кадра < 16 ms (из celldiff-дока).
>
> Референсы изучены ПО ИСХОДНИКАМ, shallow clone от 25.08.2026:
>
> | Проект | Репо | Лицензия (проверена) | Что смотрели |
> |---|---|---|---|
> | grok build | xai-org/grok-build (SOURCE_REV `437c7c92…`, HEAD `c2ad97f8` 2026-08-24) | **Apache-2.0** ✅ | crates/codegen: `xai-grok-markdown*`, `xai-ratatui-textarea`, `xai-ratatui-inline`, `xai-grok-status-line`, `xai-grok-pager` (scrollback/input/app) |
> | codex-rs | openai/codex | **Apache-2.0** ✅ | `tui/src`: `history_cell/*`, `streaming/*`, `chatwidget/streaming.rs`, `bottom_pane/{textarea,chat_composer*,approval_overlay,footer}.rs`, `diff_render.rs`, `insert_history.rs`, `app/{resize_reflow,history_pagination}.rs` |
> | lazygit | jesseduffield/lazygit | **MIT** ✅ | `pkg/gui/layout.go`, `controllers/helpers/window_arrangement_helper.go`, vendor `lazycore/pkg/boxlayout`, `pkg/gui/context/{list_renderer,list_context_trait}.go`, `pkg/tasks/tasks.go` |
> | Spectre.Console | spectreconsole/spectre.console | **MIT** ✅ | `Rendering/{IRenderable,Segment,RenderPipeline,JustInTimeRenderable}.cs`, `Widgets/{Table/TableMeasurer,Tree,ProgressBar}.cs`, `Widgets/Layout/*`, `Live/LiveRenderable.cs` |
>
> Текущее состояние Harbor: 4 builtin view'а на 827 строках
> (`src/Harbor.Terminal.Abstractions/Views/*.cs` + `TuiViewModels.cs`) —
> стринговые префиксы `[user] `, потоковая конкатенация `StreamingText += delta`,
> статус-бар одной форматируемой строкой, диффы эвристикой `Contains("Wrote ")`.
> Всё это ниже перепахивается.

---

## 0. TL;DR

1. Все четыре проекта сошлись на одной архитектуре чата: **типизированные
   блоки-ячейки** (не строки!), у каждого блока measure(height,width) +
   render(buf, rect), а лента = виртуализированный список блоков с кэшем
   высот. Наши `ChatEntry(Role, Content, Timestamp)` — это 2010-е, меняем на
   `IChatBlock` (§3.1).
2. Никто не рендерит весь ответ заново на каждый токен. Четыре независимых
   решения одной задачи: frozen-tail с контрольными точками (grok),
   newline-gated коммиты + adaptive chunking с гистерезисом (codex),
   viewport-only рендер списка (lazygit), dirty-heights патч virtual_y за O(1)
   на стриминге (grok). Берём всё четыре — они про разные слои (§3.2–3.4).
3. Редактор ввода: **план → применение** с дискриминированным исходом
   (Unchanged/CursorOnly/TextOnly/TextAndCursor) даёт минимальный регион
   перерисовки и тестируемость без терминала (grok). Атомарные элементы
   (@файлы, /команды) двигаются как единое целое (codex). Наш InputView
   со строковыми срезами на каждое нажатие — на выброс (§3.5–3.6).
4. Статус-бар: типизированный контекст («значение, которое нельзя получить, —
   это None, а не ноль») + сегментная раскладка с усечением + машина режимов
   футера. Строковый Formatted с интерполяцией на каждый кадр — аллокационный
   мусор (§3.7–3.8).
5. Дифф: гуттер со знаками, подсветка ханка одним блоком (сохранение состояния
   парсера между строками), тема-aware фоны, сшивка перекрывающихся ханков.
   Эвристика Contains("Wrote ") заменяется типизированными событиями (§3.10).

Целевой состав: **13 компонентов** (§3), каждый — проникация (у кого украдено +
лицензия), API ≤ 15 строк, алгоритм рендера, бюджет аллокаций. Сводка — §4,
интеграция в дерево проектов — §5, тесты — §6, оценка — §7.

---

## 1. Что нашли в референсах

### 1.1. grok build — самый продвинутый стек из четырёх

xAI выложила полный Rust-стек своего CLI (полноэкранный TUI «pager», ~70
крейтов). Для нас важны пять штук.

**a) `xai-grok-markdown/src/streaming.rs` — инкрементальный рендер маркдауна
«frozen tail».** Вместо перерендера документа на каждый чанк:

```
push_and_render(chunk):
    source += normalizer.push(chunk)      // латекс-делимитеры, холдбек хвоста
    rerender_tail():
        truncate(output, frozen.lines_len)          // выкинуть протухший хвост
        tail = source[frozen.source_bytes..]        // нестабильный суффикс
        (tail_out, checkpoint, _) = render(tail)
        output.extend(tail_out)                     // + ребейз ссылок/код-блоков
        if checkpoint: frozen += cp                 // заморозить стабильный префикс
view() -> &MarkdownRenderOutput                     // дёшево: уже посчитано
```

Контрольная точка — граница блока после закрывающей пустой строки (после неё
маркдаун уже не изменится). Сложность падает с O(N²) до ~O(N) по документу;
после первых абзацев каждый токен рендерит только открытый блок. Детали,
которые обычно забывают и которые тут учтены: холдбек частичного делимитера на
границе чанка (`LatexDelimiterNormalizer`), резюмное состояние подсветки
открытого code-fence между вызовами (`OpenCodeHighlighter`), детект голых URL
только по свежему хвосту с идемпотентной дедупликацией, линки получают
стабильные ID только при заморозке. `set_max_table_width` /
`set_collapse_soft_breaks` сбрасывают frozen-state — изменение параметров
рендера инвалидирует кэш явно.

**b) `xai-ratatui-textarea/src/editor.rs` — редактор как чистая машина
«команда → план → применение».** EditCommand — плоский enum из 15 команд
(grapheme/word/line движения, удаления, вставка символа; стиль слова —
WordStyle::{Small, WhitespaceDelimited}). Ключевое — результат:

```rust
pub struct EditPlan { replaced_byte_range, replacement, removed_text,
                      cursor_byte, affinity, source_identity, generation }
pub enum EditOutcome { Unchanged, CursorOnly,
                       TextOnly(EditDelta), TextAndCursor(EditDelta) }
// apply(plan) -> Result<(), ApplyEditPlanError::StalePlan|InvalidRange|...>
```

План строится отдельно от применения, применение проверяет generation буфера
(stale-plan защита), исход говорит вьюхе: трогать только курсор или
перерисовывать текст, и какой диапазон байт сменился (EditDelta). Плюс
SingleLineViewport{visible_byte_range, cursor_display_column} — горизонтальный
скролл однострочного поля. Это ровно наш случай: 13 439 строк кода на редактор —
потому он и не течёт.

**c) `xai-grok-pager/src/scrollback/` — виртуализированная лента.**
ScrollbackEntry{id: EntryId(u64), block: RenderBlock, is_running,
is_pending_user_input, display_mode, finished_at, cached_*} — блоки 15 видов
(UserPrompt / AgentMessage / ToolCall(Execute|Read|Edit|…) / Thinking / System /
SessionEvent / BgTask / Subagent / Workflow / Btw / ContextInfo …). Кэши на
запись: готовый рендер с ключом (width, raw, theme, is_selected, cwd), отдельно
truncated-высота, отдельно cheap-оценка (content_width, lines) и — отдельная
драгоценность — cached_line_widths, переживающая resize: ширины исходных линий
не зависят от ширины окна, и именно их пересчёт делал resize O(всему разговору).

Сердце — `state::prepare_layout(width, height)` с тремя случаями:

- Case 1 — нет кэша или сменилась ширина: полный ребилд с ОЦЕНКАМИ высот
  (дешёвый estimate, не рендер), захват scroll-anchor (EntryId + строка), затем
  settle_visible_measurements — ТОЧНОЕ измерение только видимых (O(viewport),
  не O(history)); прогрев страниц выше низа отложен до конца кадра
  (профилировщики нашли это главной ценой resize).
- Case 2 — есть грязные высоты: патч virtual_y от первого грязного индекса;
  для стриминга (грязный всегда последний) — O(1); структурные изменения —
  ребилд cumulative-позиций; репин верха вьюпорта по якорю против прыжков.
- Case 3 — ничего не менялось: пересчитать total_height, follow-mode, settle
  видимых оценок при скролле вверх.

Поиск блока по Y — бинарный поиск по монотонному virtual_y (partition_point).

**d) `scrollback/sticky.rs` — липкие заголовки ходов как чистая математика.**
Промпты = секции; при прокрутке заголовок прилипает к верху, следующий его
выталкивает. Вся логика — 1D-координаты над {full_height, min_height}, без
единого обращения к рендеру ⇒ тестируется таблицей чисел.

**e) Прочее:** `xai-grok-status-line/context.rs` — типизированный payload
статус-строки с schema_version и политикой «нет данных ⇒ None, не ноль»;
`xai-ratatui-inline/src/scrollback.rs` — печать finalized-контента в скроллбек
с нуль-копийной нарезкой строк и переносом области вьюпорта;
`app/agent_view/key_owner.rs` — BlockingCard{Permission, CancelTurn, Question,
McpElicitation} решает, кто держит клавиатуру и что значит Esc; карточка «ждёт
юзера» рисует пульсирующую пулю вместо волны загрузки.

### 1.2. codex-rs — терминал как хранилище истории

OpenAI-шный подход принципиально другой: finalized-история пишется В скроллбек
терминала (`insert_history.rs`), alt-screen — только для оверлеев. Отсюда свои
жемчужины:

**a) HistoryCell — трейт с тремя представлениями.** Не один display_lines, а:
display_lines(width) для вьюпорта, raw_lines() для copy-friendly raw-режима,
transcript_lines(width) для оверлея Ctrl+T (ExecCell показывает больше, чем в
чате), + desired_height(width) через Paragraph::line_count (обязательно с
wrap'ом — логических строк мало, URL шире окна дают доп. строки), +
is_stream_continuation() (стрим-ячейка мержится с предыдущей), +
transcript_animation_tick() (анимированные ячейки сообщают тик, чтобы кэш
оверлея знал, что пора пересчитать). Реализаций ~20: Plain, PrefixedWrapped
(initial/subsequent префикс), CompositeHistoryCell, AgentMarkdownCell и др.

**b) Двухфазный стриминг.** Живой хвост = временная stream-cell, которая на
каждом коммите перерисовывается целиком; по newline-границам
(MarkdownStreamCollector: буфер исходника + committed_source_len, коммит только
завершённых строк) содержимое уходит в очередь готовых линий; финализация =
консолидация всех стрим-кусков в одну source-backed ячейку
(ConsolidateAgentMessage) — после этого resize может перерендерить ответ из
исходного маркдауна заново.

**c) Adaptive chunking с гистерезисом** (`streaming/chunking.rs`) — политика
слива очереди: Smooth = 1 линия/тик, CatchUp = слить всё. Пороги входа
(очередь ≥ 8 линий ИЛИ старейшей > 120 ms), выхода (≤ 2 линий И ≤ 40 ms),
удержания EXIT_HOLD=250 ms и REENTER_HOLD=250 ms против «дребезга передач» у
порога. Очередь хранит возраст каждой линии (enqueued_at) — давление считается
без заглядывания в текст. Решает ровно нашу проблему «токены по 5 мс → кадры
по 5 мс».

**d) MarkdownRenderCache** — single-entry кэш последней отрисовки source-backed
ячейки с ключом (width, syntax_theme_revision, terminal_fg/bg, color_level);
сменилась тема или палитра — ключ разъехался, перерендер.

**e) diff_render.rs** — гуттер: выровненные номера строк + знак +/−/space;
подсветка ханка ОДНИМ блоком (сохраняет состояние синтакс-парсера между строками
внутри ханка; межханковое сознательно не сохраняется); фоны диффа адаптируются к
светлости терминала (тёмные тинты / GitHub-pastel), фиксированные палитры на
truecolor/256/16, чтобы различимость не умерла при квантовании; перенос длинных
строк со сплитом спанов по границам символов без потери стиля.

**f) ApprovalOverlay** — очередь запросов подтверждения: current_request +
queue, dismiss_resolved_request retain-фильтром вычищает отменённые,
advance_queue тянет следующий; опции строятся из типа запроса (exec/patch/
permissions/elicitation) с хоткеями. Готовый скелет для Harbor-гейтов.

**g) footer.rs** — футер как машина режимов (ComposerEmpty/HasDraft/
HistorySearch/EscHint/ShortcutOverlay/QuitShortcutReminder) с явным
footer_height(props) ДО рендера и reset_mode_after_activity.

### 1.3. lazygit — layout и списки

**a) boxlayout.ArrangeWindows** (lazycore) — декларативное дерево боксов:
Direction ROW/COLUMN, статический Size XOR динамический Weight, Conditional*
колбэки; солвер резервирует статику, остаток делит по нормализованным весам с
распределением остатка. Возврат — плоская карта window→Dimensions. Весь лейаут
lazygit — одна чистая функция GetWindowDimensions(args), протестированная
таблицей кейсов (~900 строк тестов).

**b) Screen mode как веса.** HALF/FULL режимы — обнуление весов чужой стороны в
getMidSectionWeights; portrait-режим — смена направления сплитов по ширине окна.
Никакой императивщины «подвинь панель».

**c) Viewport-only рендер списков** (`list_context_trait.go`): при
renderOnlyVisibleLines рендерятся ТОЛЬКО строки видимого диапазона
(startIdx, length := ViewPortYBounds()), остальное — пустота; экономия памяти
клеток заявлена прямо в комментарии. Модель↔view индексы конвертируются учётом
non-model вставок (секции-заголовки).

**d) ViewBufferManager** (`pkg/tasks/tasks.go`) — асинхронная дозагрузка
контента по требованию (ReadLines(n) при росте панели на resize, ReadToEnd),
off-screen render + атомарный swap (beginRender/swapInRender): старый контент
висит на экране, пока новый не дочитался — никакого «полупустого» мигания.

### 1.4. Spectre.Console — контракты рендеринга (C#!)

Единственный референс на нашем языке — важен как образец API-стиля:

**a) IRenderable { Measurement Measure(RenderOptions, maxWidth);
IEnumerable<Segment> Render(RenderOptions, maxWidth); }** — measure/render
разделены; Measurement(min, max) даёт компоновщику свободу; результат рендера —
поток сегментов (Text, Style, Link?) + Segment.LineBreak, а не готовая сетка.
Композиторы (Panel, Padder, Columns) вызывают Measure детей и режут регион.

**b) Segment.Merge** — пост-обработка: соседние сегменты с одинаковым стилем и
линком склеиваются в один (меньше SGR-переключений downstream); контроль-коды
склеиваются отдельно. Прямо наш AnsiWriter-принцип, но на уровне сегментов.

**c) TableMeasurer.CalculateColumnWidths** — трёхступенчатое ужатие: max-ширины
колонок → схлопывать только wrappable → равномерно редьюсить все. Expand —
симметрично растить гибкие. Готовая формула для нашей таблицы tool-result'ов.

**d) Tree.Render** — обход стека с уровнями гайдов (`├─ └─ │`),
переиспользование prefix-уровней, детект циклов (CircularTreeException).
Для дерева задач/агентов Harbor.

**e) JustInTimeRenderable + RenderPipeline** — dirty-flag билд внутреннего
рендерабла и цепочка IRenderHook над всеми рендерами. Live/LiveRenderable —
позиционирование курсора `\r`+CursorUp и crop при overflow (разобрано в
celldiff-доке).

---

## 2. Диагноз: чего не хватает нашим 4 view'ам

| Симптом (факт из кода) | Последствие | Лечится в |
|---|---|---|
| `ChatEntry(Role, Content, Timestamp)` — плоская строка на всё | нет маркдауна, нет диффов, нет tool-деталей; `[tool]` и ответ модели выглядят одинаково | §3.1 |
| `StreamingText += td.Delta` + `RenderEntry` пишет `context.WriteLine(content)` | O(N²) аллокаций по токену, полный репейнт строки, нет wrap'а | §3.2 |
| Нет высот/виртуализации: `foreach (var entry in vm.Entries)` | длинная сессия = бесконечный скроллбек-рост, лаги растут линейно | §3.3 |
| `InputView.RenderTextWithCursor`: `text[..pos]`, `text[pos..]` — 3 подстроки на кадр | аллокации на каждое нажатие; нет multiline, word-move, истории, placeholder-логики нормальной | §3.5 |
| `StatusBarViewModel.Formatted` — интерполяция строки при каждом обращении | GC-давление на каждый кадр; нет сегментов/усечения/режимов | §3.7 |
| `DiffPreviewViewModel`: `Output.Contains("Wrote ")` — эвристика по тексту тулзы | диффы теряются/фантомные; нет подсветки, гуттера, навигации по ханкам | §3.10 |
| `InputViewModel.UpdateFromEventAsync` — пустышка, команды Submit/Cancel не связаны с клавиатурой | редактирование фактически мертво в TUI-режиме | §3.5–3.6 |

Отдельно: у нас НЕТ approval-gate view'а вообще (гейт существует в ядре
агента, но TUI его показывает строчкой), нет спиннера/thinking-анимации,
нет таблиц tool-result'ов. Это дыры, а не «плохо сделано».

---

## 3. Дизайн компонентов (13 штук)

Общие решения, единые для всех:
- **Модель ≠ вид.** VM остаётся ObservableObject (CommunityToolkit), но хранит
  типизированные блоки; view'ы становятся чистыми функциями
  `(vm, rect, ICellWriter) → void`. Рендер идёт прямо в ScreenBuffer ядра
  (celldiff-док §1) через `ICellWriter` — никаких `context.WriteLine`.
- **Ноль аллокаций в steady-state**: все буферы переиспользуются (ArrayPool +
  capacity-reuse как в celldiff §1.2); строки НЕ собираются никогда — только
  ReadOnlySpan<char> поверх существующих.
- Каждый компонент имеет `Measure(height|width)` отдельно от `Paint(...)`
  (Spectre-контракт) и dirty-флаг (JustInTimeRenderable).

### 3.1. ChatBlockModel + ChatBlockRegistry — типизированные блоки ленты

**Проникация:** grok `RenderBlock` enum (Apache-2.0) × codex `HistoryCell`
трейт (Apache-2.0). В C# — интерфейс вместо enum: расширяемо contrib-плагинами
(канон Harbor: мессенджеры/новые виды блоков — ТОЛЬКО contrib поверх абстракции).

```csharp
public interface IChatBlock
{
    string Kind { get; }                    // "user" | "assistant" | "tool-call" | ...
    bool IsStreamContinuation { get; }      // слить с предыдущей ячейкой (codex)
    BlockMeasure Measure(int width);        // MinLines/MaxLines + точная/оценка
    void Paint(BlockPaintContext ctx);      // rect + ICellWriter + Theme + tick
    IEnumerable<HyperlinkSpan> Hyperlinks(int width);   // OSC8 + клики мышью
    string RawText();                       // copy-friendly raw режим (codex raw_lines)
}
public sealed record UserBlock(string Text) : IChatBlock;
public sealed record AssistantMarkdownBlock(string Source, MarkdownRenderCache Cache) : IChatBlock;
public sealed record ToolCallBlock(ToolCallInfo Info, ToolResultBody? Body) : IChatBlock;
```

Алгоритм: лента = `List<IChatBlock>` в VM; registry сопоставляет Kind→дефолты
(сворачиваемость, лимит truncated-высоты). Paint каждого блока получает
клип-рект и рисует только видимое. Аллокации: нулевые в steady-state — блоки
иммутабельны, Measure кэшируется в самой записи обёртки (см. 3.3).

### 3.2. StreamingMarkdownView — frozen-tail рендер потока

**Проникация:** grok `StreamingMarkdownRenderer` (Apache-2.0), порт на C#.

```csharp
public sealed class StreamingMarkdownRenderer : IDisposable
{
    public void Push(ReadOnlySpan<char> chunk);       // холдбек частичного маркдауна
    public TailRenderResult RenderTail(int width);    // перерендер нестабильного хвоста
    public FrozenState Frozen { get; }                // lines, sourceBytes
    public IReadOnlyList<StyledLine> Lines { get; }   // готовый вывод
}
public readonly record struct Checkpoint(int OutputLines, int SourceBytes);
```

Алгоритм: как grok — контрольная точка на границе блока после пустой строки;
хвост (незакрытый fence/абзац) рендерится заново каждый токен; замороженный
префикс никогда не трогается. C#-специфика: `StringBuilder` вместо String,
`List<StyledLine>` с capacity-reuse, синтакс-подсветка кода без syntect —
минимальный токенизатор BCL-only или вынос в contrib (не блокирует MVP).
Аллокации: O(хвост) на токен, O(N) суммарно по документу. Интеграция с
cell-diff: RenderTail меняет только строки хвоста → FrameHint(dirtyRegion) —
ядро celldiff §2.2 само ограничит дифф.

### 3.3. VirtualizedChatTimeline — виртуальная лента с LayoutCache

**Проникация:** grok `prepare_layout` 3-case + `virtual_y` бинпоиск + lazygit
viewport-only (обе лицензии Apache-2.0/MIT).

```csharp
public sealed class TimelineLayoutCache
{
    public bool PrepareLayout(int width, int height);   // true = ребилд (Case1)
    public int TotalHeight { get; }
    public (int first, int last) VisibleRange(int scrollOffset, int viewportH);
    public int EntryAtY(long y);                        // бинпоиск virtual_y
}
// запись ленты:
internal struct TimelineSlot { IChatBlock Block; int ExactH; short EstH;
                              bool Measured; long VirtualY; EntryId Id; }
```

Алгоритм — прямая калька grok:
Case 1 (смена ширины/структура): оценки высот всех, settle ТОЧНОГО измерения
видимых, scroll-anchor (Id+row) против прыжков;
Case 2 (dirty heights): патч virtual_y от первого грязного индекса — стриминг
O(1); Case 3: пересчёт total_height. Follow-mode: если пользователь внизу —
прилипание к низу на каждом кадре; отлип при скролле вверх (codex/lazygit
поведение). Кэш ширин исходных линий переживает resize (grok cached_line_widths).
Аллокации: 0 в steady-state (массивы с capacity-reuse).

### 3.4. CommitTickPacer — адаптивный темп коммитов стрима

**Проникация:** codex `chunking.rs` (Apache-2.0). Решает «токены чаще кадров».

```csharp
public sealed class CommitTickPacer(TimeSpan baselineTick)
{
    public ChunkingMode Mode { get; }                 // Smooth | CatchUp
    public DrainPlan Decide(QueueSnapshot snap);      // depth, oldestAge, now
    public void OnDrained(int lines);
}
public enum DrainPlanKind { Single, BatchAll }
```

Алгоритм: тики приходят от конвейера кадров; Decide смотрит только на
глубину очереди и возраст старейшей линии.
Пороги codex: вход CatchUp — очередь ≥ 8 линий ИЛИ старейшая > 120 ms; выход —
≤ 2 линии И ≤ 40 ms; EXIT_HOLD=250 ms + REENTER_HOLD=250 ms против дребезга;
SEVERE > 300 ms — форс-слив. Очередь линий хранит `enqueued_at`. У нас тик =
кадр конвейера (celldiff §0): Smooth = 1 блок-строка за кадр (~60 Гц выглядит
как печать), CatchUp = всё накопленное за кадр. Аллокации: 0.

### 3.5. PromptBuffer — модель редактора ввода

**Проникация:** grok EditPlan/EditOutcome/EditBuffer (Apache-2.0) + codex
TextArea kill-ring/wrap-cache (Apache-2.0).

```csharp
public enum EditOutcomeKind { Unchanged, CursorOnly, TextOnly, TextAndCursor }
public readonly record struct EditDelta(int ReplacedStart, int ReplacedEnd,
                                        int InsertedStart, int InsertedEnd);
public sealed class PromptBuffer
{
    public ref struct Plan { ... }                    // range, replacement, cursorByte, affinity
    public EditOutcome Apply(EditCommand cmd);        // единственный вход клавиатуры
    public SingleLineViewport Viewport(int width);    // горизонтальный скролл
    public WrapCache Wrapping(int width);             // кэш переносов multiline
    public string KillRingYank();                     // Ctrl+K/Y (codex)
}
```

Алгоритм: HandleKey маппит KeyEvent → EditCommand; Apply строит Plan
(диапазон замены, замена, новый курсор), применяет и возвращает Outcome;
view по Outcome+EditDelta решает: ничего/курсор/минимальный регион текста.
EditCommand — enum: InsertChar, MoveGrapheme/Word/Line ± , DeleteBackward/
Forward, DeleteWord±, DeleteToLineStart/End, Kill/Yank. Grapheme-семантика:
StringInfo/TextElementEnumerator (BCL) вместо ручного UTF-8. Outcome говорит
view'у: Unchanged → ничего не рисуем; CursorOnly → перерисовать клетку курсора;
Text* → FrameHint на EditDelta-строки. Аллокации: 0 на движение курсора,
1 StringBuilder-операция на изменение текста (переиспользуемый буфер).
Stale-plan защита grok здесь не нужна (однопоточный UI-поток), но generation
оставляем — дёшево и страхует будущую многопоточность вставки пасты.

### 3.6. ComposerView — составитель с атомарными элементами

**Проникация:** codex ChatComposer (Apache-2.0): popup-роутинг, atomic elements,
история с Ctrl+R.

```csharp
public sealed class Composer
{
    // элементы (@файл, /команда, !bash) двигаются как единое целое
    public IReadOnlyList<TextElement> Elements { get; }
    public PopupState? ActivePopup { get; }           // slash/file-search/skill
    public ComposerKeyResult HandleKey(in KeyEvent key); // до PromptBuffer.Apply
    public HistorySearchSession? Search { get; }      // Ctrl+R: draft snapshot/restore
}
```

Алгоритм роутинга codex: если popup открыт — ключи идут в него (Tab/Enter завершают,
Esc записывает dismissal токена), иначе в PromptBuffer; после КАЖДОЙ клавиши
sync_popups по содержимому токена перед курсором. Enter = submit только когда
popup закрыт; Shift+Enter/Alt+Enter = newline (kitty-протокол из фазы 0 даёт
нам различение — вот зачем он был нужен). Placeholder рисуется dim'ом при
пустом буфере. Аллокации: popup-фильтрация — пул списков, 0 в steady-state.

### 3.7. StatusSegmentBar — статус-бар из сегментов

**Проникация:** grok StatusLineContext («нет данных ⇒ None») (Apache-2.0) ×
codex footer-режимы (Apache-2.0) × Spectre Segment.Merge (MIT).

```csharp
public readonly record struct StatusSeg(string Text, StatusAccent Accent, bool FixedPriority);
public sealed class StatusViewModel
{
    public bool TryGetContextTokens(out int v);       // None-семантика grok
    public Span<StatusSeg> BuildSegments(Span<StatusSeg> workspace); // без аллокаций
}
public static class StatusBarLayout
{
    public static int Fit(Span<StatusSeg> segs, int width); // усечение по приоритету
}
```

Алгоритм: BuildSegments пишет в переиспользуемый span; Fit считает ширины
(кэш ширин строк в VM — инвалидируется по изменению полей, CommunityToolkit
partial OnXChanged), режет с приоритетом: модель+статус всегда, cost/tokens
первыми под нож, ctx-бар последним (codex truncates-to-keep-mode-indicator
принцип). Контекст-заполненность — мини-бар `▰▰▱▱` 6 клеток, цвет по порогам
50/80%. Режимы футера codex: Idle / Running(spinner) / AwaitingApproval /
Compacting — смена режима = смена hint-сегмента справка. Аллокации: 0
(никакой интерполяции Formatted — смерть ей).

### 3.8. SpinnerStrip — анимация занятости

**Проникация:** codex ascii_animation/frames (Apache-2.0) + grok
is_pending_user_input пульсация (Apache-2.0).

```csharp
public sealed class SpinnerStrip(string[] framesAscii, string[] framesUnicode)
{
    public ReadOnlySpan<char> Frame(long monotonicTick); // tick/2 % len
}
// использование: Running → braille-точки; AwaitingUser → пульс-кольцо slower rate
```

Алгоритм: тик приходит из конвейера кадров (не свой таймер!). Два набора кадров:
ASCII-fallback и unicode. Различие «работает» vs «ждёт тебя» — разный кадр-набор
+ разный темп (grok-паттерн: волна vs пульсирующая пулю). Аллокации: 0.

### 3.9. ApprovalGateCard — карточка подтверждения

**Проникация:** codex ApprovalOverlay (очередь+опции) (Apache-2.0) + grok
BlockingCard/key_owner (Apache-2.0).

```csharp
public sealed class ApprovalQueue
{
    public void Enqueue(ApprovalRequest req);          // exec/patch/permission/elicitation
    public bool DismissResolved(RequestMatch m);       // retain-фильтр codex
    public ApprovalRequest? Current { get; }
    public KeyOwner Owner => Current is null ? KeyOwner.Prompt : KeyOwner.Card;
}
```

Алгоритм: карта рисуется в ленте (grok: карточка-блок с is_pending_user_input) и
перехватывает клавиатуру (key_owner): цифры 1..N = выбор, y/a/n — быстрые,
Esc = «вернуть промпту» (grok Tab-семантика). Пока карта висит — spinner
показывает пульс, не волну. Опции и хоткеи строятся из типа запроса (codex
build_options). Аллокации: enqueue — 1 запись; steady-state 0.

### 3.10. DiffBlock — дифф с гуттером и подсветкой

**Проникация:** codex diff_render.rs + grok pager-diff stitch (обе Apache-2.0).

```csharp
public sealed record DiffHunk(int OldStart, int NewStart, DiffLine[] Lines);
public static class HunkStitcher
{
    public static List<DiffHunk> Stitch(List<DiffHunk> hunks); // слить перекрывающиеся (grok)
}
public sealed class DiffBlock : IChatBlock
{
    public override BlockMeasure Measure(int w);       // строки с учётом wrap
    public override void Paint(BlockPaintContext c);   // gutter+sign+body, фон-тинты
}
```

Алгоритм codex: гуттер = правый-aligned номер + знак; тело ханка подсвечивается
ОДНИМ вызовом (состояние парсера между строками сохраняется; межханковое — нет);
фоны — фиксированные палитры на truecolor/256/16 (наш AnsiWriter уже умеет
downgrade из celldiff §3.2); wrap длинных строк со сплитом стилевых спанов.
Источник данных — ТИПИЗИРОВАННОЕ событие инструмента edit/write (path,
old/new или unified-diff строка), эвристику Contains("Wrote ") удаляем совсем.
Truncated-режим: первые N ханков + «… ещё K» (grok display_mode).
Аллокации: рендер — 0 (спаны поверх исходных строк), построение ханков —
O(diff) один раз при добавлении блока.

### 3.11. ToolResultTable — компактная таблица результатов

**Проникация:** Spectre TableMeasurer.CalculateColumnWidths (MIT) + lazygit
ListRenderer колонки (MIT).

```csharp
public sealed class ToolResultTable(IReadOnlyList<ToolColumn> columns)
{
    public int[] SolveWidths(int maxWidth);            // 3 ступени Spectre
    public void PaintRows(Range modelRange, BlockPaintContext ctx); // viewport-only lazygit
}
```

Алгоритм SolveWidths (Spectre): max-ширины → collapse wrappable → равномерный reduce;
lazygit: getDisplayStrings(start,end) только для видимых строк, выравнивание
колонок, non-model вставки («─── секция»). Применение: список файлов в read/
search результатах, env/tool-метаданные. Аллокации: solve — массив ширин из пула;
paint — 0.

### 3.12. TaskTreeBlock — дерево задач/агентов

**Проникация:** Spectre Tree (guide-уровни, MIT) × grok Subagent/BgTask блоки
(Apache-2.0).

```csharp
public sealed class TaskTreeBlock : IChatBlock
{
    public IReadOnlyList<TreeNode> Roots { get; }
    // guide-части: Continue/Fork/End/Space как статические строки темы
}
```

Алгоритм — обход Spectre: стек очередей, уровни префиксов `│ ├ └`, isLastChild,
переиспользование prefix, детект циклов (guard от зацикленных subagent'ов —
у нас реальная угроза). Сворачивание ветвей — display_mode per node (grok).
Аллокации: 0 в steady-state.

### 3.13. StickyTurnHeaders — липкие заголовки ходов (фаза 2+, опционально)

**Проникация:** grok sticky.rs (Apache-2.0).

```csharp
public static class StickyHeaderSolver
{
    public static IReadOnlyList<RenderedPrompt> Solve(
        IReadOnlyList<PromptDescriptor> prompts, long scrollOffset, int viewportH);
}
```

Алгоритм — чистая 1D математика над {entryIdx, yVirtual, fullHeight,
minHeight}: идём по промптам от текущего scrollOffset; последний промпт, чей
yVirtual ушёл за верх вьюпорта, получает render_height между min и full
(клэмп по месту под ним); следующий при подходе сверху ВЫТЕСНяет прилипшего.
Рендерер получает height-budget и сам решает, что резать (grok-разделение:
математика ничего не знает про внутренности блока). Полностью unit-тестируемо
числами. Включается профилем «полный» (celldiff §5 layout уже даёт зоны).
Аллокации: выходной список из пула, ≤ видимых секций за кадр → 0 steady.


## 4. Сводка: 13 компонентов

| # | Компонент | Украдено у (лицензия) | Аллокации/кадр (steady) | Сложность |
|---|---|---|---|---|
| 3.1 | IChatBlock + registry | grok RenderBlock, codex HistoryCell (Apache-2.0) | 0 | M |
| 3.2 | StreamingMarkdownView | grok streaming.rs (Apache-2.0) | O(хвост) на токен | L |
| 3.3 | VirtualizedChatTimeline | grok prepare_layout ×3-case + lazygit viewport-only | 0 | L |
| 3.4 | CommitTickPacer | codex chunking.rs (Apache-2.0) | 0 | S |
| 3.5 | PromptBuffer | grok EditPlan/Outcome (Apache-2.0) | 0 движение / 1 правка | L |
| 3.6 | ComposerView | codex ChatComposer (Apache-2.0) | 0 steady | M |
| 3.7 | StatusSegmentBar | grok StatusLineContext + codex footer + Spectre Merge | 0 | S |
| 3.8 | SpinnerStrip | codex frames + grok пульс (Apache-2.0) | 0 | XS |
| 3.9 | ApprovalGateCard | codex ApprovalOverlay + grok key_owner (Apache-2.0) | 1/enqueue | M |
| 3.10 | DiffBlock | codex diff_render + grok stitch (Apache-2.0) | 0 рендер / O(diff) постр. | L |
| 3.11 | ToolResultTable | Spectre TableMeasurer (MIT) + lazygit ListRenderer (MIT) | 0 paint | S |
| 3.12 | TaskTreeBlock | Spectre Tree (MIT) + grok BgTask/Subagent (Apache-2.0) | 0 steady | S |
| 3.13 | StickyTurnHeaders | grok sticky.rs (Apache-2.0) | 0 | S |

S ≤ 1 дн., M ≈ 2 дн., L ≈ 3–5 дн. (см. §7).

Что сознательно НЕ берём:
- Терминал-как-хранилище codex (insert_history в скроллбек): красиво, но у нас
  уже выбран alt-screen путь фазы 2 (celldiff), а двойной режим — удвоение
  поверхности тестов. Из codex берём ячейки/пейсера/дифф, не транспорт.
- gocui-наследие lazygit — проект сам ушёл на tcell; layout-функцию берём, GUI нет.
- Полный syntect-порт: подсветка кода — contrib-плагин с минимальным ядром;
  MVP живёт без неё (моноширинный блок + рамка).
- Латекс/mermaid grok — вне канона «легко/понятно куда» для v1.

---

## 5. Интеграция в дерево проектов

Куда ложится (celldiff §5 layout-зоны):

```
Harbor.Terminal.Abstractions
├── Views/            → переписываются поверх новых моделей (тонкие)
├── ViewModels/       → TuiViewModels.cs: ChatEntry → IChatBlock-лента
└── Blocks/           ← НОВОЕ: IChatBlock + builtin-блоки (§3.1–3.13)
Harbor.Tui.ConsoleEx  ← рендер-конвейер celldiff: FrameHint от EditOutcome/
                         RenderTail/Timeline dirty — точечные апдейты §2.2
```

ПОПРАВКА (после ревизии Avalonia-наследия): НЕ плодить второй стейт-слой.
Канонический источник правды — Harbor.Ui.Framework (его уже юзают десктопы):
`ChatViewReducer.Reduce(AgentEvent → ChatViewState)` — чистый фолд;
`StreamingSync.ShouldFlush` + `ChunkedBuffer` — политика флеша стриминга
(мини-аналог codex chunking, пороги 256/2048 уже подобраны);
`ToolCallViewModel` (Id, Status, Duration, IsExpanded, DiffFull, StatusPill) —
готовая модель tool-card; панельные контракты (ITuiPanelPlugin).
TUI-виджеты = ПРОЕКЦИЯ ChatViewState в клетки, а не свой VM-стек.
Умирают: BaseTuiRenderer-строки + Terminal.Abstractions/Views/* +
TuiViewModels.cs (дублируют ChatViewReducer хуже — string-concat вместо
ChunkedBuffer). Пишется новое только то, чего в Ui.Framework нет: высоты/
wrap-кэши, cell-paint, композер, фокус-роутинг, сегменты статус-бара.
Расширения самого Ui.Framework (маленькие): таймлайн-порядок
(сейчас Lines и ToolCalls — раздельные массивы, ленте нужна интерливинг-
последовательность), типизированный diff в ToolExecutionEndEvent.
Поток данных: AgentEvent → ChatViewReducer → ChatViewState →
(десктоп-биндинги | TUI block-projection → клетки). Клавиатура: KeyOwner-машина (§3.9) решает адресата —
ApprovalQueue > Composer.Search > Composer.Popup > PromptBuffer > TimelineScroll.

Событийные хвосты, которые надо ДОБАВИТЬ в ядро событий Harbor (сейчас их нет):
типизированный ToolCallCompleted(Path?, OldText?, NewText? | UnifiedDiff) вместо
парсинга текста вывода — это изменение в Harbor.Core, не в TUI (одна строка
в эмиттере инструментов edit/write). Без него DiffBlock кормить нечем.

---

## 6. Тесты (по канону: тестируемость = свойство)

1. PromptBuffer: таблица команд×исходов без терминала (grok-паттерн editor_tests:
   planning/keys/editing/viewport отдельными файлами). Golden: outcome-тип +
   delta-диапазон + cursor. ~40 кейсов.
2. CommitTickPacer: детерминированная фейковая очередь — burst-профили
   (равномерный, пачками, лавина) → ожидаемые переключения Smooth/CatchUp с
   гистерезисом. ~15 кейсов.
3. TimelineLayoutCache: append-only стрим = O(1)-патчи; resize ширины = полный
   ребилд с сохранением scroll-anchor; бинпоиск EntryAtY против линейного
   эталона на fuzz-лентах. Property: virtual_y монотонен всегда.
4. StreamingMarkdown: golden-кейсы «токен за токеном == цельный документ»
   (посимвольная эквивалентность frozen-tail vs полный рендер) — главный
   инвариант grok'овского дизайна, переносим как есть. Fuzz: случайные разрезы
   чанков.
5. StatusSegmentBar Fit: приоритет усечения при ширинах 20/40/80/120.
6. DiffBlock: unified-diff фикс → гуттер/знаки/фон-спаны golden-byte;
   сшивка перекрывающихся ханков (grok pager-diff тесты).
7. StickyHeaderSolver: числовая таблица прокруток (grok-подход).
8. Composer popup-роутинг: токенизация перед курсором, dismissal-память Esc.

Всё — чистые классы без I/O; golden-byte ANSI только там, где проверяем
закодированный вывод (AnsiWriter уже покрыт в celldiff-волне).

---

## 7. Оценка и порядок внедрения

Волна 1 (скелет, ~5 дн.): 3.1 IChatBlock+лента-VM, 3.5 PromptBuffer,
3.7 StatusSegmentBar, 3.8 SpinnerStrip, 3.4 CommitTickPacer.
Закрывает: аллокационные грехи текущих view'ов, мёртвый ввод, статус-мусор.

Волна 2 (стриминг и память, ~7 дн.): 3.2 StreamingMarkdownView,
3.3 VirtualizedChatTimeline, 3.10 DiffBlock (+ типизированное событие в Core).
Закрывает: O(N²) стрима, бесконечную ленту, эвристику диффов.

Волна 3 (UX-слой, ~5 дн.): 3.6 ComposerView (popup/история),
3.9 ApprovalGateCard, 3.11 ToolResultTable, 3.12 TaskTreeBlock.

Опционально (~1.5 дн.): 3.13 StickyTurnHeaders.

Итого ~16–18 дн. тремя волнами, каждая волна самостоятельна (пуш+PR, все
тесты зелёные — DoD). Риски: маркдаун-рендер C# без готовой либы — самый
толстый кусок волны 2 (fallback: plain-text wrap + code-fence рамки);
графемы через StringInfo медленнее UTF-8-индексов grok — приемлемо, замерим.

---

## 8. Источники (проверено 25.08.2026)

- xai-org/grok-build — Apache-2.0; SOURCE_REV `437c7c92…`; HEAD `c2ad97f8` 2026-08-24.
  crates/codegen/xai-grok-markdown/src/streaming.rs; xai-ratatui-textarea/src/editor.rs;
  xai-grok-pager/src/scrollback/{entry,block,state/mod,state/layout,sticky}.rs;
  xai-ratatui-inline/src/scrollback.rs; xai-grok-status-line/src/context.rs;
  app/agent_view/{key_owner,panes}.rs.
- openai/codex — Apache-2.0. codex-rs/tui/src/history_cell/{mod,base}.rs;
  streaming/{mod,chunking,commit_tick,controller}.rs; markdown_stream.rs;
  bottom_pane/{textarea,chat_composer,approval_overlay,footer,status_line_style}.rs;
  diff_render.rs; insert_history.rs; app/{resize_reflow,history_pagination}.rs.
- jesseduffield/lazygit — MIT. pkg/gui/layout.go;
  pkg/gui/controllers/helpers/window_arrangement_helper.go;
  vendor/github.com/jesseduffield/lazycore/pkg/boxlayout/boxlayout.go;
  pkg/gui/context/{list_renderer,list_context_trait}.go; pkg/tasks/tasks.go.
- spectreconsole/spectre.console — MIT. src/Spectre.Console/Rendering/
  {IRenderable,Measurement,Segment,RenderPipeline,JustInTimeRenderable}.cs;
  Widgets/Table/TableMeasurer.cs; Widgets/{Tree,ProgressBar}.cs;
  Widgets/Layout/{Layout,LayoutSplitter}.cs; Live/LiveRenderable.cs.
- Текущий код Harbor: src/Harbor.Terminal.Abstractions/Views/*.cs (104+82+91+85+64),
  ViewModels/TuiViewModels.cs (345) — прочитаны целиком.

