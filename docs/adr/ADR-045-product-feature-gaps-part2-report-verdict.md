# ADR-045: отчёт product-feature-gaps part 2/3 (#2685) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2685](https://github.com/refusedguy/Harbor-Harness/issues/2685) прочитан
> полностью (937 строк, разделы 9-20: L01-L08, W01-W10, D01-D12, G01-G06,
> I01-I10, E01-E04, N01-N07, T01-T10, R01-R14, B01-B09, P01-P12, O01-O10,
> Q01-Q08, J01-J08, K01-K06). Дубли проверены через `gh pr list --search 2685`
> и инвентаризацию `origin/dev` (`bbd559c2`). Код, тесты, гейты, пороги, golden
> и AOT-профиль этим PR не меняются.

## Status

**Decided:** одного slice по всему отчёту #2685 не открывается. Отчёт содержит
более ста независимых concrete item в пятнадцати разделах; часть уже влита
отдельными PR, остальные требуют собственного владельца и критерия приёмки.
Issue остаётся OPEN как источник сравнения; комментарий фиксирует только хеш
коммита этого ADR.

## Context

Issue #2685 — self-contained отчёт `product-feature-gaps` part 2/3. Он покрывает
LSP и диагностики в агенте, Git worktree flow, diff review, 3-way merge,
изображения и вложения, речь, уведомления, телеметрию и приватность,
remote/daemon UX, IDE bridge, расширяемость и runtime/provider/forge/DX. Каждый
item несёт исходный `file:line`, суть разрыва, состояние в Harbor и минимальный
slice с приоритетом.

Проверка базы `bbd559c2` и истории PR показывает, что три item из отчёта уже
имеют влитые следы:

| Item | Что требовалось | След | PR |
|---|---|---|---|
| D09, scroll restore при перерисовке diff | хранить позицию прокрутки на файл в `DiffViewerOverlay` | per-file scroll в diff viewer восстановлен | [#2784](https://github.com/refusedguy/Harbor-Harness/pull/2784), `68a22232` |
| N07, OSC 777 как уведомление | `INotificationBackend` с OSC 777 для headless/SSH | terminal backend для уведомлений в headless/SSH | [#2876](https://github.com/refusedguy/Harbor-Harness/pull/2876), `e96c2af2` |
| T03, redaction проверяется тестами | property-test на попадание секретов в экспорт | тест, что сгенерированные секреты не попадают в telemetry exports | [#2875](https://github.com/refusedguy/Harbor-Harness/pull/2875), `2fdc608d` |

Остальные разделы не имеют одного владельца. У L01-L08, W01-W10, G01-G06,
I01-I10, R01-R14, B01-B09, P01-P12 и O/Q/J/K каждый item меняет отдельный
интерфейс: LSP-инструмент, worktree verb, состояние diff, notification backend,
IDE bridge, plugin axis или provider capability. Объединить их в один PR
значит собрать слабо связанные изменения без единого способа проверить
результат. Часть item в отчёте прямо помечена как «не переносить» (E01-E04,
I01-I10 для web-виджетов); они остаются owners decision владельца продукта.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся: повторная правка per-file scroll в diff,
  OSC 777 и telemetry redaction создала бы второй источник истины там, где
  отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный контракт
  и свой критерий приёмки; они открываются отдельными issue, как это уже
  сделано для D09, N07 и T03.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2685 не закрывается: это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Новый guard не вводится: на дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный issue, а не
  meta-PR по #2685.
