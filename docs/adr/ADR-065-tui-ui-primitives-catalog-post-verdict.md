# ADR-065: отчёт каталога UI-примитивов (#2706), P05 влит

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2706](https://github.com/refusedguy/Harbor-Harness/issues/2706) прочитан
> полностью. Факты проверены через `gh pr view`, `git log` и дерево
> `origin/dev` (`74060665`). Код, тесты, гейты, пороги, golden и AOT-профиль
> этим PR не меняются.

## Status

**Decided:** [ADR-044](ADR-044-tui-ui-primitives-catalog-verdict.md) оставил
исход P05 открытым, не предрешая результат PR #2906. PR влит 2026-11-18
коммитом `650f4b38`. Все три примитива каталога, которые требовали тонкого
контракта, теперь в дереве. Нового общего slice по #2706 не открывается. Issue
остаётся OPEN как источник сравнения.

## Context

Отчёт #2706 сводит 22 терминальных приложения к 15 примитивам P01..P15 и
выделяет три, для которых нужен отдельный контракт: P02 two-pane explorer,
P13 faceted record и P05 `ChangeBlock`. Остальные помечены как shipped.

ADR-044 на базе `f700d122` зафиксировал P02 и P13 влитыми, а P05 как открытый
PR. Проверка на текущей базе `74060665` закрывает последний пункт:

| Пункт | Контракт | След в дереве | Merge commit |
|---|---|---|---|
| P02 | `TwoPaneState` | `TwoPaneExplorer.cs` | #2884, `bf6d55ff` |
| P13 | `FacetedHistoryState` | `FacetedHistory.cs` | #2903, `f185d0aa` |
| P05 | `NavigableChangeBlocks` | `NavigableChangeBlocks.cs`, тесты в `NavigableChangeBlocksTests.cs` | #2906, `650f4b38` |

P04 остаётся единственным расхождением между отчётом и каталогом: отчёт пишет,
что временные ряды в ячейках не закрыты, а каталог помечает примитив shipped.
Этот ADR расхождение не разрешает заново. Доменный chart-примитив
[CellChart](https://github.com/refusedguy/Harbor-Harness/issues/2382) уже
закрыт, а новый общий контракт numeric series без владельца и потребителя с
измеримой целью создал бы второй источник истины.

## Decision

Код не писать. Конкретно:

- P02, P13 и P05 не перевыводятся. Каждый имеет влитый PR и файл в
  `src/Harbor.Tui.CellForge.Engine/Rendering/`.
- P04 и двенадцать shipped-примитивов не открываются повторно. В отчёте нет
  владельца и измеримой цели для нового контракта поверх существующего
  виджета.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть.
- Новый guard не вводится: на дереве, где все три тонких контракта влиты,
  проверка «примитив есть в реестре» была бы вакуумной. Гейты не ослабляются.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`: `check-md-links` и `md-lint`. Их прогоняет CI.
- Issue #2706 не закрывается. Комментарий содержит только хеш коммита этого
  ADR, без шаблонного текста о прогрессе.
- Пересмотр возможен по новому факту: владелец называет потребителя и
  измеримую цель для примитива, который остаётся не закрытым. Тогда открывается
  отдельный issue и отдельный slice, а не новый вердикт по всему каталогу.

## References

- Issue [#2706](https://github.com/refusedguy/Harbor-Harness/issues/2706),
  полный текст отчёта `tui-ui-primitives-catalog`.
- Merged PR: [#2884](https://github.com/refusedguy/Harbor-Harness/pull/2884),
  [#2903](https://github.com/refusedguy/Harbor-Harness/pull/2903),
  [#2906](https://github.com/refusedguy/Harbor-Harness/pull/2906).
- [ADR-044](ADR-044-tui-ui-primitives-catalog-verdict.md), предыдущий вердикт
  по этому отчёту.
- [DECISIONS.md](DECISIONS.md).
