# ADR-041: отчёт.cellforge-примитивов (#2698) — все конкретные пункты влиты, нового slice нет

> **Status (2026-11-19):** dated record, not normative. Product-код, тесты,
> golden-файлы, AOT-поведение, гейты и пороги этим PR не меняются. Факты
> подтверждены `git merge-base --is-ancestor` по `origin/dev` (`bc310e34`):
> каждый исполняемый пункт отчёта имеет merged-след в базе. Документ не несёт
> `Status: normative`, поэтому его отсылки к файлам не являются
> machine-checkable утверждениями.

## Status

**Decided:** code slice по #2698 не открывается. Issue — self-contained отчёт
`cellforge-tui-primitive-gaps` с 15 примитивами (4 «ЕСТЬ», 10 «ЧАСТИЧНО», 1 «НЕТ»)
и топ-8 по формуле польза/стоимость. Проверка `origin/dev` показывает, что все
исполняемые пункты уже влиты отдельными PR, большая часть — ещё до снимка
отчёта. Два slice самого #2698 уже в базе: `LogStreamController` (#2769) и
`TwoPaneExplorer` (#2884). Issue остаётся OPEN как источник сравнения;
комментарий в нём фиксирует только хеш коммита этого ADR.

## Context

Issue #2698 — self-contained отчёт от 2026-11-18, сверявшийся с `origin/dev`
`77a0382`. Проверка более свежей базы (`bc310e34`) даёт такую картину по 15
пунктам раздела «Сопоставление по примитивам»:

| # | Примитив отчёта | Merged в dev | След в дереве |
|---|---|---|---|
| 1 | Fuzzy filter list + pluggable preview | #2337 (`736c8d4b`, feat `e681c301`); кэш #2641 (`b1484135`) | `FilterList<T>` в `Harbor.Tui.CellForge.Engine/Widgets/FilterList.cs`, позиции совпадений `FuzzyMatcher.MatchIndices` |
| 2 | Двухпанельный файловый менеджер (было «НЕТ») | #2884 (`bf6d55ff`, feat `084c6b51`, test `8e4fd60c`); docs #2706 | `TwoPaneExplorer` в `Harbor.Tui.CellForge.Engine/Rendering/TwoPaneExplorer.cs` |
| 3 | Сортируемая таблица со sticky header | #2343 (`05b99c37`, feat `7b29d445`) | `SortableTable` в `Harbor.Tui.CellForge/Generic/Widgets/SortableTable.cs` |
| 4 | Time-series sparkline / граф | уже в дереве на момент отчёта; #2382 (`071f64e9`) | `SparklineBlock` в `Harbor.Ui.Framework.Rendering/Widgets/`, `CellChart` с осями |
| 5 | Сайд-бай-сайд дифф с навигацией | уже в дереве на момент отчёта | `DiffViewerOverlay` (split/unified, hunk-навигация), `WordDiff` / `LineDiff` |
| 6 | Легенда клавиш из регистрации | #2336 (`17c3a838`, feat `3aa7467a`, fix `e57e1339`) | `KeyLegend` в `Harbor.Tui.CellForge.Engine/Input/KeyLegend.cs`, строки строятся из `BindingsMap.ShownKeys()` |
| 7 | Command palette с историей и алиасами | #2329 (`d567fb61`, feat `817679a4`); #2682 Z01 `88c586e1`, Z02 `660ddb07`, Z03 `577df0dc` | `CommandPaletteView`, `PromptHistory`, `BindingsMap`; в Avalonia — query history, usage ranking и resolve алиасов |
| 8 | Видимое выделение и copy mode | #2332 (`ef10435c`) | `CopyMode` в `Harbor.Tui.CellForge/Generic/Widgets/CopyMode.cs` (v/V, h/j/k/l, OSC52) поверх `SelectionEngine` |
| 9 | Изменяемые по размеру split panes | #2328 (feat `b33b5355`); #2744 (`f36b19b9`) | `SplitPane` в `Harbor.Tui.CellForge.Engine/Rendering/SplitPane.cs`, фокус/fullscreen |
| 10 | Log stream с follow и фильтром | #2334 (`f322f86d`, feat `b1825616`); slice #2698 #2769 (`4686598f`, feat `c0cc09d7`) | `LogStreamController` в `Harbor.Ui.Framework.Abstractions/Diagnostics/`, `LogRows`, `ScrollableViewport` |
| 11 | Breadcrumbs + назад/вперёд | #2339 (`30ab7e95`, feat `54c41ca4`) | `BreadcrumbTrail` в `Harbor.Tui.CellForge.Engine/Widgets/BreadcrumbTrail.cs` |
| 12 | Дерево с expand/collapse и preview | #2335 (`d58d4430`, feat `63a9ae0e`); #1487 (`7f3ff6da`) | `TreeView` / `TreeNode` с preview, in-place раскрытие через `PanelExpandedDirs`, `FilePickerView.PreviewProvider` |
| 13 | Progress с ETA | #2870 (`2392420b`, feat `fcd289d1`, «#2690 idea 6») | `ProgressEta` в `Harbor.Tui.CellForge/Generic/Widgets/ProgressEta.cs`, EWMA-скорость и форматирование ETA |
| 14 | Inline syntax-highlighted code view | уже в дереве на момент отчёта | `CodeSyntaxTokenizer` в `Harbor.Tui.CellForge.Syntax/Rendering/` |
| 15 | Toast / notification | #2412 (`c891a1fc`, feat `43949bf0`) | `ToastOverlay`, `IToastService`, общая таблица `ToastPresentation` / `Alert.Presentation` |

Каждый feature-коммит таблицы проверен через `git merge-base --is-ancestor
<commit> origin/dev`, то есть код реально в базе, а не только в истории ветки.
Пункты 4, 5 и 14 отмечены в самом отчёте как «ЕСТЬ» — они не требовали работы
на момент снимка. Пункты 1, 3, 6–9, 11, 12 и 15 влиты под смежными номерами
PR #2320–#2412 ещё до даты снимка; расхождение с формулировками отчёта («Частично»)
— в дате снимка, а не в пропущенной работе.

Отчёт отдельно называет «самым полезным из НЕТ, но самым дорогим» двухпанельный
менеджер (п.2): он влит #2884 как domain-free `TwoPaneExplorer` с активной
панелью и синхронным preview. Оговорка #2769 (`LogStreamController`) и #2884 —
это два slice самого #2698; оба подтверждены как предки `origin/dev`.

## Decision

Код не писать. Конкретно:

- Все 15 пунктов не перевыводятся: исполняемые уже влиты отдельными PR и
  являются предками текущей базы, остальные помечены «ЕСТЬ» в самом отчёте.
- Отчёт не закрывается и не редактируется: он self-contained снимок, а не
  трекер одной задачи. Отдельно не открываются слайсы «на всякий случай».
- Golden-файлы, AOT-профили, гейты, пороги и self-test не меняются.
  Docs-only PR не может их затронуть.
- Новый guard не вводится: на дереве, где все исполняемые пункты влиты, он
  был бы вакуумным, а гейты ослаблять запрещено.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Issue #2698 остаётся OPEN. Комментарий содержит только хеш коммита этого
  ADR, без шаблонного «прогресс» текста.
- Пересмотр возможен по новому факту: владелец называет конкретного
  потребителя и измеримую цель для контракта, которого действительно нет в
  дереве. Тогда открывается новый issue и новый ADR, а не переоткрытие
  этого отчёта.

## References

- Issue #2698 — полный текст отчёта `cellforge-tui-primitive-gaps`.
- Merged PR, покрывающие исполняемые пункты: #2332, #2334, #2335, #2336,
  #2337, #2339, #2343, #2412, #2682, #2769, #2870, #2884.
- [ADR-039](ADR-039-tui-composition-report-all-slices-landed.md) — та же форма
  вердикта по research-отчёту с полностью влитыми пунктами.
- [ADR-038](ADR-038-gpui-kit-components-no-new-slice.md) — прецедент для
  research-отчёта, где proposed-слайс уже в дереве.
- [DECISIONS.md](DECISIONS.md).
