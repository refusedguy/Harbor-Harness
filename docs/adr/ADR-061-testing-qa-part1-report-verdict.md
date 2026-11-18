# ADR-061: отчёт testing-qa-gaps part 1/2 (#2704) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2704](https://github.com/refusedguy/Harbor-Harness/issues/2704) прочитан
> полностью: разделы 1–15 (T, S, H, E, F, P, M, C, B, V, I, D, R, Q, X), затем
> тело обрывается на заголовке X01. Дубли проверены через `gh pr list --search
> 2704`, комментарии issue, `git log --all --grep=2704`, `git merge-base
> --is-ancestor` и `docs/adr/DECISIONS.md` на базе `origin/dev` (`3f0ea1d0`).
> Номер 061 выбран, потому что 060 параллельно занят вердиктом по #2674. Код,
> тесты, гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2704 не открывается новый общий slice. Девять
применимых item уже влиты отдельными PR с явной меткой `#2704`. Ещё часть
помечена в самом отчёте как `ЕСТЬ` и запрещена к дублированию. Оставшиеся
требуют собственного владельца, CI-поверхности и измеримого критерия приёмки.
Issue остаётся OPEN как источник сравнения; комментарий фиксирует только хеш
коммита этого ADR.

## Context

Issue #2704 — self-contained отчёт `testing-qa-gaps` part 1/2 с идеями T01–T05,
S01–S04, H01–H05, E01–E04, F01–F06, P01–P04, M01–M02, C01–C03, B01–B04,
V01–V03, I01–I05, D01–D03, R01–R03 и Q01–Q04; раздел 15 оборван на X01.
Инвентаризация `origin/dev` (`5406294e`) и истории PR показывает девять влитых
item. Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| T04, автоперевод часов к таймеру | `ManualTimeProvider.AdvanceToNextTimer` | [#2767](https://github.com/refusedguy/Harbor-Harness/pull/2767), `d1f16eb7` | `efbc96ea` |
| E03, инварианты последовательности событий | `EventTranscriptAssert.AssertWellFormed` в TestKit.Support | [#2909](https://github.com/refusedguy/Harbor-Harness/pull/2909), `8561cd87` | `2e82699b` |
| E02, структурированный sink логов | `RecordingLoggerProvider` для retry-warning | [#2922](https://github.com/refusedguy/Harbor-Harness/pull/2922), `966c8609` | `31d7e79a` |
| P03, fuzz на все парсеры | seeded driver для JSONL line parser | [#2926](https://github.com/refusedguy/Harbor-Harness/pull/2926), `774b8132` | `9def6841` |
| D02, генератор случайных объектов | seeded agent-event transcript builder | [#2932](https://github.com/refusedguy/Harbor-Harness/pull/2932), `5a1ebb7a` | `052c0c67` |
| E04, диагностика неудачного ожидания | причина и список необработанных событий | [#2937](https://github.com/refusedguy/Harbor-Harness/pull/2937), `44098751` | `e2e74e57` |
| T05, изоляция глобальной культуры | fixtures с восстановлением `CurrentCulture` | [#2942](https://github.com/refusedguy/Harbor-Harness/pull/2942), `0a214da8` | `acad8b10` |
| H05, несколько контекстов с планировщиком | два `FakeEventBus` + один `ManualTimeProvider` | [#2947](https://github.com/refusedguy/Harbor-Harness/pull/2947), `55dff1e5` | `7adc89ee`, `6bf05488` |
| H03, исключения в headless harness | проброс ошибок compose/action/worker в тест | [#2953](https://github.com/refusedguy/Harbor-Harness/pull/2953), `94a7fb06` | `33a69940` |

Комментарии в issue фиксируют те же slice-коммиты. Часть формулировок
пересекается с part 2/2 (#2705), но там они отнесены к соседнему отчёту и
зафиксированы [ADR-056](ADR-056-testing-qa-part2-report-verdict.md); здесь они
не перевыводятся.

Оставшиеся идеи не образуют одного владельца:

- Уже в дереве по самой таблице отчёта: S04 (goldens перепроверяются в
  compare-режиме), E01 (`Pilot.Hook`), P02 (табличные `[Arguments]`), P04
  (determinism до shrink), M02 (self-test guards), C02 (coverage registry),
  B01 (`MockLlmServer`), D01/D03 (TestKit и fixtures), R01/R03 (shard plan и
  process boundary). Помечены `ЕСТЬ`, дублировать запрещено.
- Требуют CI-поверхности и решения владельца: quarantine lane с отдельным
  неблокирующим прогоном (F01–F04, Q04), учёт повторных попыток из TRX
  (Q01–Q03), differential coverage как report-only (C01, C03), выбор тестов по
  графу обратных зависимостей (R02). Каждый меняет workflow и пороги, а не
  только тесты.
- Требуют нового публичного контракта: `CellForgePilot.Resize` и named
  hit-testing (H01, H04), event-barrier со счётчиком message pumps (H02),
  виртуальный планировщик с seed (T02), связка `press script + golden` (S01),
  сравнение SVG как второй baseline (S02), injection fault на wire (B04).
  Каждый заводит отдельный slice с измеримой целью.
- Требуют владельца конкретных тестов: авто-pin `ChatPalette` с architecture
  guard (I01–I03), отказ от fallback на неверный MCP output (I04), изолированная
  копия исходников (I05), golden на примеры документации (S03), opt-in VLM
  проверка (V02). Ограничение глобального состояния process boundary уже
  описано в R03 и не заменяется новым mutex.
- X01 оборван на границе тела issue: источник и условие приёмки отсутствуют,
  поэтому правка по нему невозможна без нового факта от владельца.

## Decision

Код не писать. Конкретно:

- Девять влитых item не перевыводятся. Повторная правка fake time, event-transcript,
  logging sink, fuzz, генератора данных, диагностики ожиданий, культуры,
  планировщика и проброса исключений создала бы второй источник истины там, где
  отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Уже реализованные не дублируются;
  новые открываются отдельной issue и PR, как это уже сделано для девяти влитых
  item.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2704 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты, перенаправлены
  или требуют владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2704. Part 2/2 (#2705) уже зафиксирован ADR-056 и этим решением не
  перевыводится.

## References

- Issue [#2704](https://github.com/refusedguy/Harbor-Harness/issues/2704) —
  полный текст отчёта `testing-qa-gaps` part 1/2.
- Merged PR: [#2767](https://github.com/refusedguy/Harbor-Harness/pull/2767),
  [#2909](https://github.com/refusedguy/Harbor-Harness/pull/2909),
  [#2922](https://github.com/refusedguy/Harbor-Harness/pull/2922),
  [#2926](https://github.com/refusedguy/Harbor-Harness/pull/2926),
  [#2932](https://github.com/refusedguy/Harbor-Harness/pull/2932),
  [#2937](https://github.com/refusedguy/Harbor-Harness/pull/2937),
  [#2942](https://github.com/refusedguy/Harbor-Harness/pull/2942),
  [#2947](https://github.com/refusedguy/Harbor-Harness/pull/2947),
  [#2953](https://github.com/refusedguy/Harbor-Harness/pull/2953).
- Part 2/2 того же отчёта: issue
  [#2705](https://github.com/refusedguy/Harbor-Harness/issues/2705), вердикт
  [ADR-056](ADR-056-testing-qa-part2-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
