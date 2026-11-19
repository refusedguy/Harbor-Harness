# ADR-076: issue #2665 (permission modes и session-wide Allow all edits) без общего slice

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2665](https://github.com/refusedguy/Harbor-Harness/issues/2665) прочитан
> полностью, комментариев нет. Проверены `gh pr list --search 2665 --state all`
> (PR, реализующего issue, нет), `docs/adr/DECISIONS.md`, `docs/adr/`,
> `git log --all --grep=2665` и дерево `origin/dev` (`72308740`). Код, тесты,
> гейты, пороги, golden и AOT-профиль этим PR не меняются. Номер 076 выбран,
> потому что 073, 074 и 075 закрепили за собой открытые PR #3002, #3001 и #2999,
> пока этот PR был открыт; в `DECISIONS.md` и `docs/adr/` 076 свободен.

## Status

**Decided:** по #2665 не открывается общий slice. Четыре режима и in-memory
overlay, `WaitingApproval` в доменной модели, `DenyWithFeedback` и поведение
approval после рестарта имеют разные контракты и разные критерии приёмки.
Существующий аналог `PermissionRuleset` + persist-decisions не перевыводится.
Issue остаётся OPEN, комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2665 выведен из `product-feature-gaps.md` разделами A01-A04 и M17, а
также из Orca A01-A04 и T3code B1-B11. Он просит enum
`{ Supervised, AutoAcceptEdits, Auto, FullAccess }` и `SessionPermissionOverlay`
в памяти до конца сессии; approval как record с `SessionStatus.WaitingApproval`,
где текст ответа трактуется как `DenyWithFeedback`. Критерий приёмки:
auto-accept пропускает только write/edit и никогда `command`, а запрос после
рестарта завершается как `not_resumable`, а не висит. Ограничения issue: база
`origin/dev`, PR сразу `--base dev`, локально не запускать dotnet, тесты и гейты.

Инвентаризация `origin/dev` (`72308740`) показывает, что базовый механизм прав
уже влит, а четыре части из issue не имеют владельца в дереве.

| Часть #2665 | Что есть в дереве | Источник или разрыв |
|---|---|---|
| Правила и персистентные решения (Orca A04/A05) | `PermissionRuleset` и `PermissionAction` (`Allow`/`Ask`/`Deny`) в `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`; `PermissionService` с `_persisted` и `IPermissionConfigStore` в `src/Harbor.Permissions/`. | Аналог уже влит; ADR-062 прямо классифицирует A04 как готовый аналог, новый slice создал бы второй источник истины. |
| Четыре режима + `SessionPermissionOverlay` | `CommonConfig.PermissionMode` — строка `"default"` со значениями `default`/`permissive`/`strict` (`src/Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs`, `docs/CONFIGURATION.md`). | Это статическая конфигурация, не рантайм-overlay уровня сессии; enum из четырёх значений и самого типа `SessionPermissionOverlay` в `src/` и `apps/` нет. T3code B1 уже отнесён к item со своим владельцем в ADR-050. |
| `SessionStatus.WaitingApproval` и `DenyWithFeedback` | `SessionStatus` содержит `Idle`, `Working`, `Done`, `Error`, `Aborted` (`src/Harbor.Abstractions.Contracts/Models/Session.cs`). `StatusBarMode.AwaitingApproval` есть в `src/Harbor.Ui.Framework.Rendering/Widgets/StatusSegmentBar.cs`, но это режим отрисовки статус-бара, а не состояние сессии. `ask_user_question` (`AskUserQuestionTool`, `UserQuestionService`) задаёт структурированный вопрос, а не record approval с веткой feedback. | Доменного `WaitingApproval` и варианта `DenyWithFeedback` в `PermissionAction` нет. Требуется изменение публичного контракта и конвейера approval. |
| Approval после рестарта как `not_resumable` | `RunRecoveryPolicy.Evaluate` и `RecoveryDecision.IsResumable` в `src/Harbor.Abstractions.Contracts/Models/RunRecovery.cs` уже кодируют причины не-resumable для неподтверждённых границ tool call и model call. | Границы approval как таковой в политике нет; критерий #2665 требует отдельного значения причины и проверки, что запрос не остаётся в подвешенном состоянии. |

Смежные границы уже зафиксированы: ADR-062 развёл Orca A01-A03 (`ApprovalMode`
и cycle в hotkey) как item со своим владельцем, а ADR-050 отнёс t3code B1
(permission modes) к изменениям публичного контракта. Этот ADR не перевыводит
эти решения и не инвентаризует соседние issue.

## Decision

Код не писать. Конкретно:

- `PermissionRuleset` и persist-decisions не перевыводятся. Второй источник
  истины для правил создал бы рассогласование с уже влитой политикой.
- Четыре режима, overlay, `WaitingApproval`, `DenyWithFeedback` и restart-ветка
  не объединяются в один PR. У каждого свой публичный контракт и свой criterion
  приёмки; общий PR не дал бы единого способа доказать ни «auto-accept не
  пропускает `command`», ни «запрос не висит после рестарта».
- Режим status bar не выдаётся за доменное состояние сессии. Существующий
  `StatusBarMode.AwaitingApproval` остаётся представлением, а не новым
  `SessionStatus`.
- Новый guard не вводится. На дереве, где базовые правила влиты, а overlay и
  approval-record требуют владельца, проверка их наличия была бы вакуумной.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть, ослаблять гейты запрещено.
- Issue #2665 не закрывается. Это каталог разрыва, а не трекер одной задачи.
  Каждая недостающая часть открывается отдельным PR с владельцем и измеримой
  целью.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`, их прогоняет CI.
- Пересмотр возможен по новому факту: владелец называет конкретную часть,
  потребителя контракта (overlay, approval-record или restart-ветку) и
  измеримую цель. Тогда открывается отдельный PR, а не общий slice по #2665.

## References

- Issue [#2665](https://github.com/refusedguy/Harbor-Harness/issues/2665).
- [ADR-062](ADR-062-orca-features-part1-report-verdict.md): граница Orca
  A01-A04, A04 как готовый аналог.
- [ADR-050](ADR-050-t3code-features-part1-report-verdict.md): t3code B1 как
  изменение публичного контракта со своим владельцем.
- [ADR-072](ADR-072-file-rewind-preimage-design.md): предыдущий влитый номер.
- [DECISIONS.md](DECISIONS.md).
