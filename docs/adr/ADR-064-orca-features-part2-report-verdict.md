# ADR-064: отчёт orca-features part 2/2 (#2672) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2672](https://github.com/refusedguy/Harbor-Harness/issues/2672) прочитан
> полностью: concrete item F03–F13 и G01–G13, раздел 8 «честно не применимо».
> Дубли проверены через `gh pr list --search 2672`, комментарии issue,
> `git log --all --grep=2672` и `docs/adr/DECISIONS.md` на базе `origin/dev`
> (`50011c23`). Номер 064 — следующий свободный после ADR-063, который PR
> #2983 закрепил за отчётом #2681. Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2672 не открывается новый общий slice. Три item уже
влиты отдельными PR. Остальные либо имеют готовый аналог в Harbor, либо
требуют собственного владельца и публичного контракта, либо не применимы к
терминальному агенту. Issue остаётся OPEN как источник сравнения; комментарий
фиксирует только хеш коммита этого ADR.

## Context

Issue #2672 — self-contained отчёт `orca-features` part 2/2. Он продолжает
part 1/2 (#2671, ADR-062) и объединяет те же два проекта: `echoVic/orca-agent`
(Rust, префикс `[A]`) и `stablyai/orca` (TypeScript/Electron, префикс `[D]`).
Инвентаризация `origin/dev` (`50011c23`) и истории PR показала три влитых
item. Все merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) | Slice |
|---|---|---|---|
| F06, retained output | `RetainedOutput` с лимитом 1 MiB и chunk-чтением 8 KiB | [#2781](https://github.com/refusedguy/Harbor-Harness/pull/2781), `d193931e` | `3d7cce04` |
| F13, onboarding disclosure | `FirstRunState` отдельно от trust в `Harbor.Onboarding` | [#2865](https://github.com/refusedguy/Harbor-Harness/pull/2865), `a4220347` | `3dcea21e` |
| G03, diff review state | `DiffReviewState` в `Harbor.ChangeTracking` с защитой от stale review | [#2898](https://github.com/refusedguy/Harbor-Harness/pull/2898), `ed9b457a` | `54d94678` |

Комментарии в issue фиксируют slice-коммиты `54d94678` (G03) и merge-коммиты
`d193931e` (F06) и `a4220347` (F13); slice `3d7cce04` и `3dcea21e`
восстановлены из истории PR.

Оставшиеся item не образуют одного владельца:

- Готовый аналог в Harbor, новый slice не нужен: F04 (`IToolRegistry` в
  `Harbor.Abstractions/Tools` уже знает схему и execution profile до вызова),
  F10 (`Harbor.Plugins.*` и CS-source plugins уже дают discovery, validation
  и capability kinds). Второй источник истины здесь создал бы рассогласование.
- Требуют своего владельца и измеримой цели. Каждый назван отчётом как
  минимальный slice, но у каждого свой публичный контракт и критерий приёмки:
  F03 (`IExtensionData` в `Harbor.Abstractions`), F05 (`IConfigurationResolver`
  с пятью слоями приоритета), F07 (`ISandboxProbe` с флагами `Available`,
  `Enforced` и `Evidence`), F08 (`INetworkPolicy` в `Harbor.Providers.Shared`), F09
  (`McpStartupReport` в event bus), F11 (docs matrix и probe-gated parser),
  F12 (`ComputeDigest()` в `AgentEventEnvelope`), G01 (новые block types в
  `ChatViewProjector`), G02 (`IConversationSource` с priority), G04
  (`IAutomationStore` и CLI `automation`), G05 (`IAutomationTrigger`),
  G07 (dedupe key по `(sessionId, toolCallId)` для уведомлений), G08
  (`RateLimitWindow` в provider model), G09 (tool `work_item start`), G10
  (`IExecutionHost` с local и ssh backend). Часть из них уже имеет
  предпосылки в дереве: `PermissionRuleset` для F07, OSC 7501 emitter для
  F11, `SchemaVersion` на `AgentEvent` (E27, part 1/2) для F12,
  `INotificationProcessRunner` для G07 и `session_steer` для G02/G09.
- Не применимы к терминальному агенту: G06 (dictation зависит от микрофона и
  desktop UI; опция через stdin — не ядро), G11 (мобильный bridge; протокол
  steer уже есть как `session_steer`), G12 (design mode требует GUI-браузера;
  терминальный аналог — MCP tool), G13 (мобильный эмулятор; только внешний
  MCP tool). Раздел 8 отчёта отдельно называет непереносимыми GUI Orca,
  CoreML, desktop-панели, мобильное приложение, computer use, полный inline
  image без fallback, OCR через тяжёлые зависимости и peak/off-peak
  тарификацию.

Отчёт part 2/2 соседствует с part 1/2 (#2671, ADR-062). Этот ADR не
перевыводит item из соседнего issue; если там появится новый факт, он
рассматривается отдельным решением.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка `RetainedOutput`,
  `FirstRunState` и `DiffReviewState` создала бы второй источник истины там,
  где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный
  контракт и критерий приёмки; часть имеет готовый аналог, часть не
  применима к терминальному агенту. Новый slice открывается отдельным PR,
  как это уже сделано для F06, F13 и G03.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть; документационные гейты `docs.yml` остаются на
  стороне CI.
- Issue #2672 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2672.

## References

- Issue [#2672](https://github.com/refusedguy/Harbor-Harness/issues/2672) —
  полный текст отчёта `orca-features` part 2/2.
- Merged PR: [#2781](https://github.com/refusedguy/Harbor-Harness/pull/2781),
  [#2865](https://github.com/refusedguy/Harbor-Harness/pull/2865),
  [#2898](https://github.com/refusedguy/Harbor-Harness/pull/2898).
- Part 1/2 того же отчёта: issue
  [#2671](https://github.com/refusedguy/Harbor-Harness/issues/2671) и
  [ADR-062](ADR-062-orca-features-part1-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
