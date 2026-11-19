# ADR-074: Thread lineage + merge-back из форка дельтой (#2660) — вердикт

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2660](https://github.com/refusedguy/Harbor-Harness/issues/2660) прочитан
> полностью. Проверка `origin/dev` (`72308740`) и `gh pr list --search 2660`
> показала: PR по issue нет, ADR-вердикта по #2660 нет. Первая версия PR
> использовала номер 071; пока PR был открыт, 071 и 072 закрепили за другими
> issue, а 073 занял открытый PR. Поэтому этот ADR перенумерован в 074 и его
> запись в DECISIONS.md вставлена после ADR-070, а не в конец файла. Код, тесты,
> гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** код не писать. Линия форка уже влита и повторно не выводится.
Merge-back дельтой остаётся отдельным slice с владельцем. Issue остаётся OPEN
как источник сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2660 (gap `product-feature-gaps` S01/S03/G04) просит две вещи:
`ThreadRelation { kind, sourceSessionId, sourcePoint, targetSessionId }` и
`session_merge_back(sourceSessionId, fromMessageId)` с `--strategy 3way` и базой
на точке форка. Acceptance: результат форка вливается в изменившуюся
родительскую сессию без полного transcript, плюс тест на конфликтующие правки.

На базе `72308740` первая половина уже в дереве:

| Требование | Где влито |
|---|---|
| lineage fork → child | `Session.ParentSessionId` в `src/Harbor.Abstractions.Contracts/Models/Session.cs:54`, вид выводится через `SessionKind` |
| ветвление с префиксом до границы включительно | `SessionForkService` в `src/Harbor.SessionContext/Sessions/SessionForkService.cs`, slice `ac33aec5` |
| CLI-поверхность | `harbor sessions fork <id> <message-id>`, slice `a025edbf` |

[ADR-062](ADR-062-orca-features-part1-report-verdict.md) уже зафиксировал
`SessionForkService` как готовый аналог (B03), для которого новый slice не нужен.

Вторая половина отсутствует: `ThreadRelation`, `session_merge_back`, стратегия
`3way` и тест на конфликт в дереве есть. Каталог
`src/Harbor.Tools.Builtin/Tools/Session/` содержит только `SessionReadTool`,
`SessionSteerTool`, `DeferredSessionStore` и `SessionSupervision`.

## Decision

Код не писать. Конкретно:

- Линия форка не перевыводится: `ParentSessionId`, `SessionForkService` и CLI
  уже влиты и подтверждены как часть базы.
- Merge-back не открывается заодно. Ему нужны четыре решения, которых в issue
  нет: (1) где хранится `ThreadRelation` — новая строка в store или
  переиспользование `ParentSessionId`; (2) правило идентичности сообщений при
  слиянии — fork копирует префикс с исходными id, перештампованными на child
  session id, поэтому «база на точке форка» должна быть определена; (3)
  представление конфликта и форма результата `3way`; (4) поверхность
  инструмента `session_merge_back` плюс запись в `PermissionRuleset.Default`.
  Один тест на конфликт не закрывает эти контракты.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются: docs-only PR
  не может их затронуть.
- Новый guard не вводится: на дереве, где форк есть, а merge-back нет, проверка
  «вся линия закрыта» была бы вакуумной.

## Consequences

- Меняются два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`).
- Issue #2660 остаётся OPEN. Комментарий содержит только хеш коммита этого ADR.
- Пересмотр по новому факту: владелец называет контракт хранения
  `ThreadRelation`, правило идентичности сообщений между fork и merge, форму
  результата конфликта `3way` и потребителя инструмента. Тогда открывается
  отдельный slice, а не переоткрытие всей issue.

## References

- Issue [#2660](https://github.com/refusedguy/Harbor-Harness/issues/2660) —
  полный текст.
- [ADR-062](ADR-062-orca-features-part1-report-verdict.md) —
  `SessionForkService` как готовый аналог (B03).
- `src/Harbor.SessionContext/Sessions/SessionForkService.cs` — влитая линия
  форка.
- `src/Harbor.Abstractions.Contracts/Models/Session.cs` — `ParentSessionId`.
- [DECISIONS.md](DECISIONS.md).
