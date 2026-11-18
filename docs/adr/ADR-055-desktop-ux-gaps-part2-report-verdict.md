# ADR-055: отчёт desktop-ux-gaps part 2/2 (#2683) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2683](https://github.com/refusedguy/Harbor-Harness/issues/2683) прочитан
> полностью: concrete item K13–K41 и десять formulation в разделе «Итог».
> Дубли проверены через `gh pr list --search 2683`, комментарии issue,
> `git log --all --grep=2683` и `docs/adr/DECISIONS.md` на базе
> `origin/dev` (`e5656566`). Код, тесты, гейты, пороги, golden и AOT-профиль
> этим PR не меняются.

## Status

**Decided:** по отчёту #2683 не открывается новый общий slice. Пять
применимых item уже влиты отдельными PR. Оставшиеся требуют собственного
владельца, поверхности и критерия приёмки либо прямо отнесены отчётом к
другой части. Issue остаётся OPEN как источник сравнения; комментарий
фиксирует только хеш коммита этого ADR.

## Context

Issue #2683 — self-contained отчёт `desktop-ux-gaps` part 2/2 с 41 пунктом
gpui-kit компонентов (здесь K13–K41). Инвентаризация `origin/dev` (`e5656566`)
и истории PR показывает пять влитых item. Все merge commit подтверждены как
предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| K29, searchable settings | поиск по настройкам и no-results состояние | [#2788](https://github.com/refusedguy/Harbor-Harness/pull/2788), `5c09f4fb` |
| K13, word-level inline diff | подсветка изменённых слов в `HdsDiffCompact` | [#2891](https://github.com/refusedguy/Harbor-Harness/pull/2891), `b08e1228` |
| K24, breadcrumbs | кликабельный путь в редакторе кода | [#2905](https://github.com/refusedguy/Harbor-Harness/pull/2905), `e109d344` |
| K25, Accordion | режимы `Single`/`Multiple`, bordered/disabled | [#2917](https://github.com/refusedguy/Harbor-Harness/pull/2917), `5a17b417` |
| K17, resizable drawer | тянущаяся правая панель с snap-to-close | [#2929](https://github.com/refusedguy/Harbor-Harness/pull/2929), `6c055f1c` |

Комментарии в issue фиксируют те же slice-коммиты: `5c09f4fb` (K29),
`b08e1228` (K13), `2d3edc66` и `e109d344` (K24), `908ba906` и `5a17b417`
(K25), `fade21a5` и `6c055f1c` (K17). Issue закрывался после первого влитого
slice (#2788) и был переоткрыт владельцем: он остаётся каталогом разрывов, а
не трекером одной задачи.

Оставшиеся 24 item не образуют одного владельца:

- Есть частичный аналог в Harbor, нужен точечный порт, а не общий slice:
  K14 (`CodeSyntaxTokenizer` в `Harbor.Tui.CellForge.Syntax` — подсветка
  второй стороны hunk), K16 (`SelectionEngine.cs` для cell-буфера — hit
  testing в `HdsDiffCompact`), K26
  (`VirtualizedChatTimeline.StoreScroll.cs` — follow tail и splice в Avalonia
  binder), K38 (`GenericSelectionList.cs` и `SessionListViewModel` — delegate
  для списков), K23 (context menu consuming команды Z11–Z24 из part 1/2) и
  K15 (gutter decorations — основа review comments по Z46).
- Требуют отдельного владельца и измеримой цели: K18 (drag вкладок), K19
  (анимация ширины sidebar), K20 (секции header/footer/menu), K21 (inspector
  стилей), K27 (grouping сообщений), K28 (`NotificationId` и `Delivery`),
  K30 (accessibility label), K31 (skeleton/shimmer), K32 (color picker),
  K33 (bar chart tooltip), K34 (input popovers) и K40 (frontmatter). У каждого
  свой публичный контракт и своя поверхность в Avalonia или TUI.
- Низкая применимость или прямое условие: K22 (задержки hover), K35, K36 и
  K37 (группы статусбара, токены titlebar, toolbar groups) — косметика и
  токены, не новые control; K39 отчёт помечает как «только если появится
  voice input»; K41 — правило состояния для всех A-слайсов, а не отдельный
  runtime-контракт.
- Десять formulation в «Итоге» смешивают K-пункты с Z/G-пунктами part 1/2
  (#2682). Пункты 1–4, 6 и 8–10 целиком относятся к #2682; пункт 5 закрыт в
  части K13, но unified/split из Z48 остаётся за part 1/2; пункт 7 требует
  и Z46, и K15. Этот ADR не перевыводит #2682.

## Decision

Код не писать. Конкретно:

- Пять влитых item не перевыводятся. Повторная правка поиска по настройкам,
  word diff, breadcrumbs, Accordion и resizable drawer создала бы второй
  источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Точечные порты открываются
  отдельными PR по конкретному item, частично неприменимые не портируются,
  новые контракты требуют владельца и критерия приёмки.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть; документационные гейты `docs.yml` остаются на
  стороне CI.
- Issue #2683 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2683.

## References

- Issue [#2683](https://github.com/refusedguy/Harbor-Harness/issues/2683) —
  полный текст отчёта `desktop-ux-gaps` part 2/2.
- Merged PR: [#2788](https://github.com/refusedguy/Harbor-Harness/pull/2788),
  [#2891](https://github.com/refusedguy/Harbor-Harness/pull/2891),
  [#2905](https://github.com/refusedguy/Harbor-Harness/pull/2905),
  [#2917](https://github.com/refusedguy/Harbor-Harness/pull/2917),
  [#2929](https://github.com/refusedguy/Harbor-Harness/pull/2929).
- Part 1/2 того же отчёта: issue
  [#2682](https://github.com/refusedguy/Harbor-Harness/issues/2682) — этим
  решением не перевыводится.
- [DECISIONS.md](DECISIONS.md).
