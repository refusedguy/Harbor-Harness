# ADR-027: Canvas Shape/Context из ratatui остаётся DEFERRED

> **Status (2026-11-18):** dated record, not normative. Решение зафиксировано по
> итогам отчёта в issue [#2689](https://github.com/refusedguy/Harbor-Harness/issues/2689)
> (part 1/1, `canvas-shape-decision.md`) на `origin/dev` = `91a66022`. Ссылки на
> код в отчёте сняты через `git show origin/dev:<path>`, а не с рабочей копии
> автора исследования. Документ не утверждает машино-проверяемых фактов о
> текущем дереве, поэтому здесь нет баннера `Status: normative`. Код, тесты,
> гейты и пороги этим PR не меняются.

- Дата: 2026-11-18
- Источник: issue [#2689](https://github.com/refusedguy/Harbor-Harness/issues/2689);
  исходное предложение: issue [#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168),
  триаж владельца 2026-10-16.
- Затрагивает: только этот ADR и индекс [DECISIONS.md](DECISIONS.md).

## §1. Вопрос

Нужно ли Harbor сейчас брать дизайн `Canvas`/`Shape`/`Context` из ratatui:
subcell-растеризацию (braille, quadrant, sextant, octant, half-block),
библиотеку векторных примитивов (line, rectangle, circle, map) и
world-координаты с независимым разрешением по осям?

Ответ: **нет**. Решение: оставить `#1168` в состоянии DEFERRED и записать
вердикт здесь, чтобы его не пересматривали заново без нового факта. Ни одного
продуктового файла этим решением не меняем.

## §2. Что установлено

### §2.1 Условие триггера не выполняется ни при одной формулировке

- `#1168` — OPEN, метки `area: tui`, `enhancement`, `p3`, `priority: low`.
  Тело: «Part of #1155… take as design reference only when epic #23 reaches
  charts/sparklines in TUI. Track only, do not implement. Volume L (5+d) when
  unblocked». Триаж владельца 2026-10-16: условие не снято, «только трекаем, не
  делаем».
- `#1155` прямо перечисляет в «Explicitly NOT taking»: «Canvas charts (defer)».
- `#23` не содержит slice про charts/sparklines в TUI (там word-diff, jump
  palette, setup guide, image preview, tabs, STT, screenshot markup). Строка
  «Token-usage sparkline in status bar» в `docs/ROADMAP.md` стоит в разделе
  Implemented, но это Avalonia-контрол, а не TUI Canvas.
- `#2382` (`CellChart`: столбцы и спарклайны с осями) CLOSED, коммит `071f64e9`.
  Это и есть TUI-чарт-примитив, появившийся после триажа `#1168`. Он явно
  исключает искомую 2D-геометрию: pie, radar, candlestick и sankey требуют
  2D-геометрии и L-сложности и оставлены вне объёма.

Буквально условие привязано к `#23`, где chart slice так и не появился; по
существу TUI-чарты появились через `#2382`. Ни та, ни другая формулировка не
делает `#1168` нужным.

### §2.2 Машинная часть дизайна в Harbor уже есть

| Что было предложено в ratatui | Где закрыто в Harbor |
|---|---|
| `Buffer` и точка `Painter::paint` | `ScreenBuffer` (`Fill`, `SetRune`, `SetText`, `SetStyleAt`), `Cell`, `CellStyle` |
| immediate-mode `Shape::draw` | `Scene`: display list с `Mark`/`Close`/`Record`/`Replay` — геометрию можно посчитать один раз и перепроиграть; в ratatui повторного drawing нет |
| texture atlas глифов | `GlyphCache` и `ScreenBuffer.GlyphCache` |
| инвалидация и diff всего `Buffer` | `DirtyRect`, `CellDamageContract`, `DiffEngine`, `FrameDiff` с порогом 0.25 |
| слои | `ZStackNode`, `OverlayLayer`, `PostFx` — на уровне узлов и ячеек, а не растровых точек |
| настоящая пиксельная графика | `Graphics`, `InlineImageEncoder`, `Osc1337Image` (kitty/Sixel/OSC 1337) — сильнее braille-аппроксимации |
| `sparkline.rs` | `SparklineBlock` — прямой порт: блоковые глифы, min/max-нормализация, санитизация NaN/Inf |
| `chart.rs` | `CellChart` (`#2382`): столбцы, спарклайн, оси значений и времени, zoom/pan, bucketing, golden-тесты |
| `gauge.rs` | `GaugeBar`, `CostBarsBlock` |
| композиция панелей | `ScreenBuilder` (`#1195`/`#1201`): листья и сплиты дают `LayoutTree`. Это композиция панелей, а не рисунок; Canvas в неё не встраивается |
| точечная half-block упаковка | `TerminalQrRenderer` — ad-hoc `█ ▀ ▄` для QR, без движения движка |

### §2.3 Непокрыт ровно один кластер, и у него нет потребителя

1. **Subcell-растеризация как примитив.** Ни один тип в `Rendering/` не принимает
   8 булевых точек и не собирает из них braille-глиф, quadrant/sextant/octant
   или пару цветов в `▀`/`▄`. `Cell` содержит ровно один `Rune` и одну пару
   `Fg`/`Bg`; `GlyphCache` ключуется только шириной 0/1/2. Чтобы повторить
   `PatternGrid<2,4>`, нужен новый слой-аккумулятор **до** `ScreenBuffer`, а не
   вызов существующего API.
2. **Векторные примитивы и world-координаты.** `Line`/`Rectangle`/`Circle`/`Map`
   и `Painter::get_point` не существуют. `CellChart` работает в целых ячейках:
   `Bucket(values, plotWidth)` и `BarRows(value, max, rows)`; его «масштаб» —
   ось значений, а не произвольный `f64` world-box с независимым x/y разрешением.
3. **Per-channel compositing внутри полотна.** `ZStackNode`/`OverlayLayer`
   компонуют узлы; у ratatui `LayerCell` компонует symbol/fg/bg трёх проходов
   в одну ячейку.

Почему это не триггерит реализацию:

- `git grep CellChart` по `src/` и `apps/` минус само определение возвращает
  пусто. Даже `#2382` пока примитив без подключения к панели. Поиск issue по
  `canvas OR braille OR raster OR half-block` даёт только `#1168` и `#1155`.
  Спроса на line/circle/map нет вообще.
- Владелец дважды прямо зафиксировал отказ: `#1155` — «Canvas charts (defer)»;
  `#2382` — 2D-геометрия и L-сложность оставлены вне объёма.
- Braille/quadrant нужны ради плотности точек, но Harbor ушёл в terminal
  graphics для настоящей растровой графики, а плотность временного ряда
  покрыта `SparklineBlock`.
- `PatternGrid` + `Painter` + четыре формы — новый L-подсистемный слой в
  BCL-only/AOT-движке с нулём вызовов. Это ровно то, что `#1168` сам описывает
  как «Track only, do not implement». Объём остаётся L (5+d), приоритет p3/low.

## §3. Решение

**Оставить DEFERRED.** `#1168` остаётся OPEN как track-only пункт; реализация
не начинается, пока не будут выполнены оба условия из §5. Этот ADR фиксирует
вердикт на бумаге и не добавляет работы. Он отвечает на вопрос «почему мы это не
сделали» при следующем чтении отчёта.

Ни один `.cs`-файл, тест, гейт или порог этим PR не трогается. Гейт поверх
подсистемы без потребителя был бы вакуумным, поэтому новый гейт не
предлагается; существующие пороги не ослабляются.

## §4. Условный срез на случай снятия DEFERRED (не рекомендация сейчас)

Если владелец снимет DEFERRED и появится реальный потребитель, минимальный
срез выглядит так. Он остаётся L и сегодня не нужен.

- Новый `SubcellGrid.cs` в `src/Harbor.Tui.CellForge.Engine/Rendering/`:
  `PatternGrid<W,H>` с `resolution()`, `paint(x,y,color)`, `save()`, `reset()`;
  braille-таблица — `U+2800 + bits`. Не трогать `Cell.cs`/`CellStyle.cs`:
  упаковка живёт до `ScreenBuffer`.
- `CanvasPainter.cs` рядом: world bounds `(f64,f64)`, `GetPoint(x,y)`,
  `Paint(x,y,color)`, `Scale`; вместо `Box<dyn Grid>` — интерфейс
  `ISubcellGrid` с одним вызовом.
- Две формы для старта, не пять: `Line` (отсечение Cohen–Sutherland +
  Bresenham) и `Points`; `Rectangle` — четыре вызова `Line`; circle и map
  отложить.
- Вывод через уже существующий `Scene.Record`/`Replay`, чтобы не дублировать
  replay.
- Тесты (TUnit, per-project executable): `SubcellGridTests` (разрешение 2×4,
  упаковка 8 точек в braille-глиф, reset/save), `CanvasPainterTests`
  (границы и выход за bounds, нефинитные координаты, вырожденные bounds),
  goldens по образцу `CellChart_Bars.golden.txt`. Non-vacuity: каждый тест
  обязан падать при удалении одного бита упаковки.

## §5. Последствия и условия пересмотра

- Продукт не меняется: риск регрессии нулевой, цена — только этот документ.
- Пересмотр начинается с **двух** условий вместе: владелец снимает DEFERRED в
  `#1168` и существует потребитель, который не описывается через
  `SparklineBlock`, `CellChart` и terminal graphics. Одно условие без другого
  недостаточно: снятие флага без потребителя снова даёт L-слой с нулём вызовов.
- Если срез всё же делается, границы из §4 обязательны: упаковка до
  `ScreenBuffer`, replay через `Scene`, сначала `Line` и `Points`.

## §6. Рассмотренные альтернативы

- **Реализовать сейчас** — отклонено: нет потребителя, объём L, владелец дважды
  прямо отказался.
- **Закрыть `#1168` как wont-do** — отклонено: владелец сказал «только трекаем»;
  пункт остаётся открытым и отслеживаемым.
- **Портировать весь Canvas API ratatui** (`Shape`-trait, closure `.paint`,
  слои) — отклонено: это выразительные детали Rust API, которые в C#/Harbor
  ложатся на уже существующие типы; ценность только в subcell-упаковке и
  алгоритмах растеризации.

## Источники

- `gh issue view 2689` — полный отчёт part 1/1, VERDICT и §4.
- [#1168](https://github.com/refusedguy/Harbor-Harness/issues/1168),
  [#1155](https://github.com/refusedguy/Harbor-Harness/issues/1155),
  [#23](https://github.com/refusedguy/Harbor-Harness/issues/23),
  [#2382](https://github.com/refusedguy/Harbor-Harness/issues/2382) и коммит
  `071f64e9`.
- `git show origin/dev:<path>` для файлов `Rendering/`, `Chat/Widgets/CellChart.cs`,
  `Ui.Framework.Rendering/Widgets/SparklineBlock.cs` и `TerminalQrRenderer.cs`;
  `git grep` по потребителям `CellChart`.
- Локальный shallow-клон `ratatui-widgets/src/canvas.rs` и
  `canvas/{line,rectangle,circle,map,points}.rs`; `websearch` не использовался.
