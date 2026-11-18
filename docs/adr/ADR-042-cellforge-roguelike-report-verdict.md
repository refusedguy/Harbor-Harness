# ADR-042: отчёт CellForge как_load-test для игр (#2697). Вердикт по 15 примитивам

> **Status (2026-11-19):** dated record, not normative. Product-код, тесты,
> golden-файлы, AOT-профиль, гейты и пороги этим PR не меняются. Факты
> подтверждены `git merge-base --is-ancestor` по `origin/dev` (`3cefd7cd`):
> каждыйfeature-коммит таблицы ниже является предком базы. Документ не несёт
> `Status: normative`, поэтому его отсылки к файлам не являются
> machine-checkable утверждениями.

## Status

**Decided:** новый product-срез по #2697 этим PR не открывается. Issue —
self-contained отчёт «Терминальные игры как нагрузочный тест CellForge» с 15
примитивами (4 «ЕСТЬ», 9 «ЧАСТИЧНО», 2 «НЕТ») и топ-10 по формуле
польза/стоимость. Проверка текущей базы показывает, что все исполняемые
пункты, имеющие потребителя в продукте, уже влиты отдельными PR. Оставшиеся
пункты либо игроспецифичны, либо требуют названного потребителя и решения
владельца. Issue остаётся OPEN как источник сравнения; комментарий в нём
фиксирует только хеш коммита этого ADR.

## Context

Issue #2697 — отчёт от 2026-11-17, сверявшийся с `origin/dev` `7ab88c84`.
Комментарии владельца фиксируют три влитых slice самого отчёта: #2779
(`a5cf953e`, `VisibilityMap`), #2885 (`eb3797d3`, numpad в `KeyCode` и
декодерах) и #2910 (`4a451864`, `TileCamera`). Четвёртый примитив,
`TileAtlas` вместе с `TileMapNode`, влит под смежным #2696 (`a436fa9a`, merge
`5dbc28f1`). Все четыре коммита проверены как предки `origin/dev`.

Картина по 15 пунктам отчёта на `origin/dev` `3cefd7cd`:

| # | Примитив отчёта | Состояние в базе | След |
|---|---|---|---|
| 1 | Tile/sprite atlas (было «НЕТ») | влит | `TileAtlas` + `TileMapNode` в `Harbor.Tui.CellForge.Engine/Rendering/`, PR #2883 (`a436fa9a`) |
| 2 | Half-block / Braille | частично, без изменений | Braille-фреймы в `SpinnerStrip`/`RetryCountdown`, блоковые символы в `GenericProgress`; half-block с разным fg/bg по-прежнему требует выхода за пределы 16-байтовой `Cell` |
| 3 | Viewport + camera + scrolling 2D | влит | `TileCamera` в `Harbor.Tui.CellForge.Engine/Rendering/TileCamera.cs`, PR #2910 (`a1b2e954`); одномерный `ScrollableViewport` остался |
| 4 | FOV / lighting overlay | частично влит | `VisibilityMap` в `Harbor.Tui.CellForge.Engine/Rendering/VisibilityMap.cs`, PR #2779 (`8c9600e5`); `LightingFx` над `PostFxPipeline` отсутствует |
| 5 | Послойная композиция terrain/item/creature/FX | частично, без изменений | `ZStackNode` и `OverlayLayer` есть; `TileLayerStack` как матрица тайлов отсутствует |
| 6 | Статичный фон + dirty regions | частично влит | `DirtyRect` и `ViewDamageTracker` в `Harbor.Tui.CellForge.Engine/Rendering/` (`b1ce35a1`, #2354); трекер не подключён к продуктовому кадровому циклу, `DamageTiles` отсутствует |
| 7 | Анимация 30–60 fps | частично, без изменений | `FrameTicker` с пределом 60 fps и монотонным временем есть; continuous mode в REPL runner отсутствует |
| 8 | Детерминированный тайминг кадра | ЕСТЬ | `FrameTicker`, `CommitTickPacer`, `SpringFx`, `StreamBlock`, `AmbientMascot` |
| 9 | Клавиатура: chords / numpad / vi-keys | влит | `KeyCode.Keypad0..9` и decode в `KittyKeyDecoder`/`EscapeSequenceParser`, PR #2885 (`eb3797d3`); `KeyModifiers` и `LeaderKeyRouter` уже были |
| 10 | Mouse tile picking | ЕСТЬ | `MouseEvent`, `MouseRouter`, `SelectionEngine` с координатами в ячейках |
| 11 | Minimap (было «НЕТ») | absent | `MinimapView` в `src/` не найден |
| 12 | Текстовый лог со scrollback | ЕСТЬ | `VirtualizedChatTimeline`, `TimelineRing`, `ScrollableViewport` |
| 13 | Модальные оверлеи | ЕСТЬ | `OverlayStack`, `DialogOverlay`, `HelpPanel`, `CommandPaletteView` |
| 14 | Сохранение состояния экрана | частично | `ScreenSession.OfferSwap`/`AdoptPendingSwap` для двух кадров в памяти; сериализуемого снимка сетки, палитры и камеры в продукте нет (`ScreenSnapshot` в `tests/Harbor.E2E.Framework/TuiDriver.cs` — локальный helper) |
| 15 | Palette swapping | частично | `ChatPalette` с `PinFrame`/`UnpinFrame` и live reload темы есть; индирекции `PaletteRef` → `PaletteTable` вне `Cell` нет |

Каждый feature-коммит таблицы проверен через `git merge-base --is-ancestor
<commit> origin/dev`, то есть код реально в базе. Три пункта помечены «ЕСТЬ»
в самом отчёте (8, 10, 12, 13 — четыре), остальные не требовали работы на
момент снимка или закрыты влитыми slice.

Расхождение с формулировками отчёта объясняется датой снимка: #2697 писался
по `7ab88c84`, а `TileAtlas` и `TileCamera` влиты после этой даты. Повторная реализация
любого из четырёх влитых пунктов создала бы второй источник истины в
`Harbor.Tui.CellForge.Engine`.

## Decision

Код не писать. Конкретно:

- Четыре влитых пункта не перевыводятся: `VisibilityMap`, `TileCamera`,
  numpad и `TileAtlas`/`TileMapNode` уже являются предками базы.
- Игроспецифичные пункты (`MinimapView`, `LightingFx`, `TileLayerStack`,
  `PaletteTable`, сериализуемый `ScreenSnapshot`) не открываются «на всякий
  случай». Harbor — агентный харнесс, а не игровой движок; отчёт сам
  использует игры как нагрузочный тест, а не как продукт.
- Общие пункты, которые остаются (`DamageTiles` и подключение
  `ViewDamageTracker` к кадровому циклу, continuous mode в REPL runner),
  остаются кандидатами на отдельные issue. Этот ADR их не закрывает и не
  резервирует: владелец по-прежнему решает, какой item превращается в slice.
- Golden-файлы, AOT-профиль, гейты, пороги и self-test не меняются.
  Docs-only PR не может их затронуть.
- Новый guard не вводится: на дереве, где часть пунктов влита, а часть
  осознанно не требуется, проверка отсутствия типов была бы вакуумной.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются CI.
- Issue #2697 остаётся OPEN. Комментарий содержит только хеш коммита этого
  ADR, без шаблонного текста о прогрессе.
- Пересмотр возможен по новому факту: владелец называет потребителя и
  измеримую цель для действительно отсутствующего контракта. Тогда
  открывается новый issue и новый slice, а не переоткрытие этого отчёта.

## References

- Issue #2697 — полный текст отчёта `cellforge-roguelike-demands`.
- Merged PR, покрывающие исполняемые пункты: #2354, #2779, #2883, #2885,
  #2910.
- [ADR-040](ADR-040-cellforge-engine-gaps-report-verdict.md) и
  [ADR-041](ADR-041-cellforge-tui-primitives-report-all-slices-landed.md) —
  та же форма вердикта по смежным CellForge-отчётам.
- [DECISIONS.md](DECISIONS.md).
