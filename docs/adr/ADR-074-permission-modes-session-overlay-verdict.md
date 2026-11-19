# ADR-074: issue #2665 (permission modes и session-wide Allow all edits) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2665](https://github.com/refusedguy/Harbor-Harness/issues/2665) прочитан
> полностью: gap, три пункта «что сделать» и acceptance. Дубли проверены через
> `gh issue view 2665 --comments` (0 комментариев), `gh pr list --search 2665`,
> `git log --all --grep=2665` и `docs/adr/DECISIONS.md` на базе `origin/dev`
> (`72308740`). Номер 074 выбран после проверки открытых PR: 073 уже занят
> PR #3002 (issue #2661), 075 занят PR #2999 (issue #2664). Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по issue #2665 не открывается один общий slice. Ни один из четырёх
пунктов не влит, но у каждого свой публичный контракт и свой владелец. Issue
остаётся OPEN как источник сравнения; комментарий фиксирует только хеш коммита
этого ADR.

## Context

Issue #2665 — product-gap из трёх отчётов (product-feature-gaps A01-A04/M17,
Orca A01-A04, T3code B1-B11). Он просит четыре вещи:

1. `enum PermissionMode { Supervised, AutoAcceptEdits, Auto, FullAccess }`.
2. `SessionPermissionOverlay` in-memory до конца сессии.
3. Approval как record с `SessionStatus.WaitingApproval` и текстовый ответ как
   `DenyWithFeedback`.
4. Acceptance: auto-accept пропускает только write/edit, никогда command;
   запрос переживает рестарт как `not_resumable`, а не висит.

Проверка базы `72308740` показывает, что ни один пункт не влит:

| Пункт | Что есть в дереве | Разрыв |
|---|---|---|
| Режимы | `CommonConfig.cs:249` хранит `PermissionMode` как строку `"default"` с тремя описанными значениями `default/permissive/strict`; в оценку разрешений строка не попадает | Нет enum из четырёх режимов и нет `Evaluate(tool, args, mode)` |
| Session overlay | `PermissionRuleset` в `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs` — immutable набор правил `Ask/Allow/Deny` | Нет per-session overlay с TTL до конца сессии |
| Approval record | `ApprovalCoordinator` в `src/Harbor.Permissions/ApprovalCoordinator.cs:21` держит in-memory gates; `ApprovalResolution(bool Approved, bool PersistDecision)` в `IApprovalCoordinator.cs:30` | Нет durable record, `WaitingApproval` и `DenyWithFeedback` |
| Restart | `SessionStatus` в `src/Harbor.Abstractions.Contracts/Models/Session.cs:10` содержит `Idle/Working/Done/Error/Aborted`; `not_resumable` в `src/` и `apps/` не встречается | Нет события о недоступном после рестарта запросе |

`gh pr list --search 2665` не находит ни одного PR с этим номером, история
коммитов по `2665` тоже пуста. Смежные ADR не снимают этот gap: ADR-046 и
ADR-047 фиксируют вердикты по другим отчётам, ADR-071 касается execution graph
и command receipts, а ADR-072 и влитые PR #2994/#2998/#3000 описывают другие
контракты.

## Decision

Код не писать. Конкретно:

- Четыре пункта не объединяются в meta-PR. Enum и `Evaluate` меняют контракт
  оценки разрешений и его AOT-профиль, overlay меняет время жизни сервиса,
  approval record меняет модель сессии и форму записи в JSONL, а `not_resumable`
  требует журнала gates и recovery-сервиса. У каждого пункта свой критерий
  приёмки и свой ревьюер по безопасности.
- Acceptance про auto-accept и `DenyWithFeedback` меняет как минимум
  `PlainPermissionAsker`, `CellForgePermissionAsker` и форму `ToolResult`. Это
  отдельный slice с consumer в TUI, а не часть вердикта.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2665 не закрывается: это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются CI.
- Новый guard не вводится: на дереве, где ни один из четырёх пунктов не влит,
  он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный пункт,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не общий
  slice по #2665.
