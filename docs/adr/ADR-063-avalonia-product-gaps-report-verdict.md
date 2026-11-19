# ADR-063: отчёт avalonia-product-gaps (#2681) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2681](https://github.com/refusedguy/Harbor-Harness/issues/2681) прочитан
> полностью: разделы 0–20, 100 Concrete item G01–G100 плюс таблица «какие
> контролы Avalonia нельзя взять как есть». Дубли проверены через `gh pr list
> --search 2681`, комментарии issue, `git log --all --grep=2681`, `git
> merge-base --is-ancestor` и `docs/adr/DECISIONS.md` на базе `origin/dev`
> (`204f4f44`). Номер 063 выбран как следующий свободный после ADR-062, который
> занят вердиктом по #2671. Код, тесты, гейты, пороги, golden и AOT-профиль этим
> PR не меняются.

## Status

**Decided:** по отчёту #2681 не открывается новый общий slice. Одиннадцать
применимых item уже влиты отдельными PR с явной меткой `#2681`, ещё один item
находится в открытом PR. Оставшиеся требуют собственного владельца,
CI/golden-поверхности и измеримого критерия приёмки. Issue остаётся OPEN как
источник сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2681 — self-contained аудит продуктовых пробелов `Harbor.App.Avalonia`.
Отчёт сопоставляет заявленный ORCA-уровень из `docs/DESKTOP_APP_PLAN.md` с
фактическим состоянием 46 AXAML-вью и перечисляет 100 конкретных разрывов.
Инвентаризация `origin/dev` (`204f4f44`) и истории PR показывает одиннадцать
влитых item. Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| G08, Stop во время стриминга | `IsVisible` на `IsAgentRunning` вместо `IsThinking` | [#2777](https://github.com/refusedguy/Harbor-Harness/pull/2777), `6625ef47` | `4fce521c` |
| G65, auto-follow системной темы | подписка на `ActualThemeVariantChanged` | [#2867](https://github.com/refusedguy/Harbor-Harness/pull/2867), `f6c0e7dd` | `aa94aa30` |
| G25, fuzzy поиск сессий | переиспользование scoring из palette | [#2899](https://github.com/refusedguy/Harbor-Harness/pull/2899), `0cf659b9` | `54a34573` |
| G48, dismiss кнопка на тосте | `Command` на `ToastService.Dismiss(id)` | [#2940](https://github.com/refusedguy/Harbor-Harness/pull/2940), `150c6e23` | `3163dfbd` |
| G77, контраст всех HDS-палитр | параметризованный тест на 6 палитр | [#2945](https://github.com/refusedguy/Harbor-Harness/pull/2945), `4324ac04` | `286a24d6` |
| G02, история промпта | Alt+Up/Down в `ChatViewModel` | [#2955](https://github.com/refusedguy/Harbor-Harness/pull/2955), `d0324c6c` | `5125da50` |
| G85, weighted fuzzy palette | веса по полям строки | [#2950](https://github.com/refusedguy/Harbor-Harness/pull/2950), `452cccb9` | `3d6d39a7` |
| G27, виртуализация списка сессий | `VirtualizingStackPanel` в `SessionsFlyoutView` | [#2963](https://github.com/refusedguy/Harbor-Harness/pull/2963), `46878c71` | `1e02befc` |
| G18, убрать недостижимый sqlite | удаление backend'а из списка | [#2967](https://github.com/refusedguy/Harbor-Harness/pull/2967), `b461f1f5` | `e87e93df`, `820b1cec` |
| G74, LiveSetting на тостах | `AutomationProperties.LiveSetting` | [#2973](https://github.com/refusedguy/Harbor-Harness/pull/2973), `bd5c6e42` | `ea8acf10` |
| G81, виртуализация token chart | горизонтальный `VirtualizingStackPanel` + pin-тест | [#2974](https://github.com/refusedguy/Harbor-Harness/pull/2974), `2f4606f5` | `b21f2f78`, `39abe7be` |

Комментарии в issue фиксируют те же slice-коммиты. Item G24 (переиспользование
`SessionRow` в `SessionsFlyoutView`) находится в открытом PR
[#2980](https://github.com/refusedguy/Harbor-Harness/pull/2980); этим решением
он не перевыводится.

Оставшиеся идеи не образуют одного владельца:

- Требуют публичного контракта human-in-the-loop: G05/G06 и G38–G44. Включение
  `ask_user_question` в desktop preset, `UserQuestionPresenter`,
  `IPermissionAsker` и 4-аргументный `DecideApproval` меняют composition root и
  approval-сеам; это отдельные slice с измеримой целью, а не правка одной вью.
- Требуют CI/golden-поверхности: G71–G73 (a11y smoke и AutomationPeer E2E),
  G79 (perf budget), G84/G88/G98–G100 (golden на palette, token chart, composer,
  settings, diff, editor, session list и расширение `ViewInflationTests` до 46
  вью), G97 (gate congruency между doc и props). Каждый добавляет тест или гейт,
  а не только код.
- Требуют владельца платформенной поверхности: G45/G46 (OS-уведомления и tray),
  G50 (background runs), G30 (detachable panel), G70 (conditional chrome),
  G93/G94 (auto-update и подпись). Часть из них во внешнем packaged-пайплайне и
  не закрывается правкой Avalonia-проекта.
- Требуют владельца конкретной вью: G01/G03/G04, G09–G14, G15–G23, G26/G28/G29,
  G32–G37, G51–G57, G58–G64, G66–G69, G75/G76, G82/G83/G86/G87, G89–G92,
  G95/G96. Детализация есть в issue, но без потребителя и измеримой цели эти
  пункты остаются каталогом.
- Выведены из scope самим отчётом: G07 (Vim mode), G31/G56 (mobile/touch), G61
  (multi-cursor в AvaloniaEdit не поддерживается), G80 (сначала измерять, потом
  оптимизировать). По ним нет slice до нового факта от владельца.

## Decision

Код не писать. Конкретно:

- Одиннадцать влитых item и G24 в открытом PR не перевыводятся. Повторная
  правка тостов, тем, fuzzy, virtualized списков, истории промпта и контраста
  создала бы второй источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Уже реализованные не дублируются;
  новые открываются отдельной issue и PR, как это уже сделано для одиннадцати
  влитых item.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2681 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты, отправлены к
  владельцу или выведены из scope, он был бы вакуумным, а гейты ослаблять
  запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2681.

## References

- Issue [#2681](https://github.com/refusedguy/Harbor-Harness/issues/2681) —
  полный текст аудита `avalonia-product-gaps`.
- Merged PR: [#2777](https://github.com/refusedguy/Harbor-Harness/pull/2777),
  [#2867](https://github.com/refusedguy/Harbor-Harness/pull/2867),
  [#2899](https://github.com/refusedguy/Harbor-Harness/pull/2899),
  [#2940](https://github.com/refusedguy/Harbor-Harness/pull/2940),
  [#2945](https://github.com/refusedguy/Harbor-Harness/pull/2945),
  [#2955](https://github.com/refusedguy/Harbor-Harness/pull/2955),
  [#2950](https://github.com/refusedguy/Harbor-Harness/pull/2950),
  [#2963](https://github.com/refusedguy/Harbor-Harness/pull/2963),
  [#2967](https://github.com/refusedguy/Harbor-Harness/pull/2967),
  [#2973](https://github.com/refusedguy/Harbor-Harness/pull/2973),
  [#2974](https://github.com/refusedguy/Harbor-Harness/pull/2974).
- Открытый PR по G24:
  [#2980](https://github.com/refusedguy/Harbor-Harness/pull/2980).
- [DECISIONS.md](DECISIONS.md).
