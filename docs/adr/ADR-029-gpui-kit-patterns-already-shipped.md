# ADR-029: gpui-kit: переносимые паттерны уже в Harbor, остальное неприменимо

> **Status (2026-11-18):** dated record, not normative. Отчёт issue [#2701](https://github.com/refusedguy/Harbor-Harness/issues/2701)
> (part 1/1) прочитан полностью, дубли проверены через `gh pr list --search`.
> Исходники gpui-kit читались из локального клона на коммите `249a963`
> (2026-11-17). Ссылки на Harbor сняты с `origin/dev` = `fc9aabde` через
> `git show`, а не с рабочего дерева. Код, тесты, гейты и пороги этим PR не
> меняются.

- Дата: 2026-11-18
- Источник: issue [#2701](https://github.com/refusedguy/Harbor-Harness/issues/2701).
- Затрагивает: только этот ADR и индекс [DECISIONS.md](DECISIONS.md).

## §1. Вопрос

Issue #2701 содержит полный отчёт competitor research по архитектуре gpui-kit.
В нём семь идей для CellForge, сравнение с текущим Harbor и список того, что
переносить нельзя из-за разницы между GPU и терминалом. Нужно выбрать item и
либо реализовать его отдельным PR, либо зафиксировать вердикт без кода.

## §2. Ответ

Код не пишем. Все семь переносимых идей уже влиты в `dev` отдельными PR, и
каждый PR ссылается на свой item из этого же отчёта. Восьмой идеи, которую можно
было бы реализовать без дублирования, в отчёте нет. Остаток отчёта описывает
GPU-механику, которой в терминале нет. Этот PR добавляет только вердикт, чтобы
семь уже закрытых идей не пересматривали заново как новую работу.

## §3. Сопоставление

| Идея отчёта | В gpui-kit | Уже в Harbor на `dev` | PR |
|---|---|---|---|
| Приоритет стилей | `resolve_style`, фиксированный порядок слоёв | `StylePriority.cs` явно ссылается на коммит gpui-kit `249a963` | [#2385](https://github.com/refusedguy/Harbor-Harness/pull/2385) |
| Типизированные part | `Checkbox / CheckboxIndicator` | `BorderPanel` и его части владеют своим состоянием | [#2393](https://github.com/refusedguy/Harbor-Harness/pull/2393) |
| Delegate для списков | `ListDelegate`, видимый диапазон | `VirtualListDelegate.cs` и `VirtualizedChatTimeline.Delegate.cs` | [#2387](https://github.com/refusedguy/Harbor-Harness/pull/2387) |
| Ключевая идентичность | `ElementId` хранит keyed state | `ScreenId`, `Panel.Id`, identity таймлайна | [#2392](https://github.com/refusedguy/Harbor-Harness/pull/2392) |
| Чистое дерево layout | `PaneTree`, `normalize`, reconciliation по `NodeId` | `LayoutTree` с normalize и сверкой панелей по id | [#2388](https://github.com/refusedguy/Harbor-Harness/pull/2388) |
| Один handle на viewport | инвариант 10 gpui-kit | смещение скролла в `UiState`, отдельный `StoreScroll` | [#2391](https://github.com/refusedguy/Harbor-Harness/pull/2391) |
| Время жизни подписок | подписка до `.detach()` или смерти владельца | `EventSubscription`, владелец в `ChatScreenBridge` | [#2390](https://github.com/refusedguy/Harbor-Harness/pull/2390) |

Семь строк таблицы дают полное покрытие списка из отчёта. Идеи 1, 2 и 3 занимают
первые три места в рейтинге отчёта, идеи 4, 6 и 7 отмечены как S, идея 5 как L.
Сложность больше не имеет значения: работа уже сделана и влита.

## §4. Почему без кода

`gh pr list --state all --search` нашёл по одному влитому PR на каждую идею.
Повторная реализация любой из них создала бы второй источник истины там, где
отчёт требует одного владельца: второй резолвер стилей, второй delegate, второй
ключ геометрии. Правила #555 здесь соблюдены. Все семь PR остались внутри
`src/Harbor.Tui.CellForge*`, `src/Harbor.DesignSystem` и
`src/Harbor.Ui.Framework*`. Никто не добавил маркер оси, дверь `Register*` или
запись в `PluginAxisRegistry`.

Оставшаяся часть отчёта не переносится по существу. У терминала нет
пикселей, DPI, GPU-кадра, SDF-глифов, alpha, blur и subpixel text. Нет
системного дерева доступности, IME, native menus и WebAssembly. Rope editing с
LSP и diagnostics принадлежит Harbor core, а UI только проецирует результат.
Три идеи отклонены отдельно, хотя технически их реализация возможна.
`actions!` и `cx.bind_keys` создали бы второй конвейер клавиш рядом с
`ChatKeyMap` и `LeaderKeyRouter`. `Entity<State>` создал бы второго писателя
`UiState` рядом с `UiStore`. Ещё один глобал темы конфликтовал бы с
`TerminalColorPalette.ThemeChanged`. Порядок наложения в gpui задаётся
вложенностью, у Harbor его задаёт `ZStackNode`. Общего `z_index` нет ни там,
ни здесь.

## §5. Последствия

Issue #2701 остаётся источником сравнения. Новый код, новые тесты и новые гейты
не появляются. Пересмотр возможен при новом факте: новая версия gpui-kit или
реальный потребитель паттерна, которого сейчас нет.

## §6. Альтернативы

Реализовать идею 1 или 3 заново. Отклонено: оба PR уже влиты, а дублирующий
резолвер или delegate создаст второго владельца там, где отчёт требует одного.

Добавить гейт, запрещающий gpui-паттерны в `src/`. Отклонено: на дереве нет ни
одного нарушения, которое такой гейт поймал бы. Гейт был бы вакуумным.
