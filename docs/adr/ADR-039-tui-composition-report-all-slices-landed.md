# ADR-039: отчёт TUI-композиции (#2688) — все конкретные пункты влиты, нового slice нет

> **Status (2026-11-19):** dated record, not normative. Product-код, тесты,
> golden-файлы, AOT-поведение, гейты и пороги этим PR не меняются. Факты
> подтверждены `git merge-base --is-ancestor` по `origin/dev` (`192ed9a0`) и
> `git log origin/dev --merges`: все семь идей отчёта имеют merged-след.
> Документ не несёт `Status: normative`, поэтому его отсылки к файлам не
> являются machine-checkable утверждениями.

## Status

**Decided:** code slice по #2688 не открывается. Issue — self-contained отчёт
`tui-composition-2026-11` с семью конкретными идеями, и все семь влиты в
`origin/dev` отдельными PR. Две из них (идеи 1 и 6, PR #2768 и #2877) отмечены
в самом issue как уже влитые на момент переоткрытия. Остальные пять влиты под
смежными номерами #2517-#2525. Issue остаётся OPEN как источник сравнения;
комментарий в нём фиксирует хеш коммита этого ADR.

## Context

Issue #2688 — self-contained отчёт от 2026-11-18, который сверялся с
`origin/dev` `eef054ff`. Проверка более свежего `origin/dev` (`192ed9a0`)
даёт такую картину по семи идеям из раздела 3 отчёта:

| # | Идея отчёта | Merged PR | След в дереве |
|---|---|---|---|
| 1 | `INode`/`NodeFrame` в production-хосте | #2877 (`f1dbd3a1`) | `PanelNodeAdapter` в `src/Harbor.Tui.CellForge/Generic/`, named `ElementId` из `PanelId`, адаптер подключён к `CellForgeTuiRenderer` |
| 2 | Ключевая идентичность для динамических детей DSL | #2722 (`669bde62`) | `FlexSplitNode.Add(child, key, track)`, `PrepaintForward.Named`, `SpecNode.Id` и разбор `.id` в генераторе |
| 3 | Layout-узлы в SpecKind (Row/Column/Overlay) | #2527 (`67710810`) | `SpecKind.Row/Column/Overlay`, `SpecLayoutEmitter`, метод `BuildLayout` в `Harbor.Tui.CellForge.CodeGen` |
| 4 | Замыкание `ReadEntities` в инвалидацию prepaint | #2523 (`41738b86`) | `EntityFingerprint` в `PrepaintContext` сравнивает версии прочитанных сущностей при `TryReuse` |
| 5 | Кэш layout по (Rect, track) | #2525 (`926eacc9`) | `ContainerNode` пропускает solver при неизменных Rect и fingerprint-ах треков, есть счётчики `Hits`/`Misses` |
| 6 | Декларативные биндинги в markup | #2768 (`37c6e2e1`) | `.bind` в `MarkupParser`, `KeyBindingsEmitter`, готовые `IReadOnlyList<Binding>` и `Register(BindingsMap)` |
| 7 | Headless Pilot для композиции без чата | #2524 (`b5c804d3`) | `CompositionPilot` в `tests/Harbor.Tui.CellForge.Tests/Pilot/`, работает с `INode` и `VirtualInputSource` |

Каждый пункт таблицы подтверждён не только сообщением коммита, но и фактом
`git merge-base --is-ancestor <commit> origin/dev`, то есть код реально
находится в базе, а не только в ветке. Идея 2 объединяет два коммита,
`2e908497` для runtime-ключа и `348e8f26` для `.id` в DSL, оба вошли в PR #2722.
Идея 4 влита коммитом `f995e0c0` через PR #2523, остальные идеи имеют по одному
feature-коммиту.

Отчёт evaluations ту же границу видит иначе: он написан до того, как пять
пунктов были влиты, и называет их «реально оставшимися». Это расхождение
даты снимка, а не пропущенная работа. Перевыводить любой из семи пунктов
означало бы создать второй источник истины: например, повторный
`PanelNodeAdapter` конфликтовал бы с уже подключённым адаптером в рендерере,
а повторный `SpecLayoutEmitter` изменил бы сгенерированный публичный API.

## Decision

Код не писать. Конкретно:

- Все семь идей не перевыводятся: каждая уже влита отдельным PR и является
  предком текущей базы.
- Отчёт не закрывается и не редактируется: он self-contained снимок, а не
  трекер одной задачи. Отдельно не открываются слайсы «на всякий случай».
- Golden-файлы, AOT-профили, гейты, пороги и self-test не меняются.
  Docs-only PR не может их затронуть.
- Новый guard не вводится: на дереве, где все исполняемые пункты влиты, он
  был бы красным или вакуумным, а гейты ослаблять запрещено.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Issue #2688 остаётся OPEN. Комментарий содержит только хеш коммита этого
  ADR, без шаблонного «прогресс» текста.
- Пересмотр возможен по новому факту: владелец называет конкретного
  потребителя и измеримую цель для контракта, которого действительно нет в
  дереве. Тогда открывается новый issue и новый ADR, а не переоткрытие
  этого отчёта.

## References

- Issue #2688 — полный текст отчёта `tui-composition-2026-11`.
- Merged PR, покрывающие все семь идей: #2523, #2524, #2525, #2527, #2722,
  #2768, #2877.
- [ADR-038](ADR-038-gpui-kit-components-no-new-slice.md) — та же форма
  вердикта по research-отчёту без нового slice.
- [ADR-031](ADR-031-steal-list-2026-11-no-new-slice.md) — прецедент для
  research-отчёта, у которого все применимые пункты уже в дереве.
- [DECISIONS.md](DECISIONS.md).
