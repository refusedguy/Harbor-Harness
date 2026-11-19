# ADR-078: отчёт t3code-features part 1/2 (#2673) после вердикта — влит D1

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2673](https://github.com/refusedguy/Harbor-Harness/issues/2673) прочитан
> полностью: 90 concrete item A1-F11 в шести группах.
> [ADR-050](ADR-050-t3code-features-part1-report-verdict.md) зафиксировал
> вердикт на базе `82b915c2` и учёл три влитых item (A17, A3, A9); D1 он прямо
> отнёс к item, меняющим публичный контракт и требующим отдельного владельца.
> После вердикта в `dev` влит D1. Факты проверены через `gh pr view`,
> комментарии issue и `git merge-base --is-ancestor` на базе `origin/dev`
> (`8f38b89c`). Номер 078 выбран потому, что 074 влит в `dev` двумя файлами,
> 075 влит PR #2999, 076 занят открытым PR #3004, а 077 влит PR #3005. Код,
> тесты, гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикт ADR-050 сохраняется: одного общего slice по #2673 нет.
D1 не перевыводится. Issue остаётся OPEN как источник сравнения; комментарий
фиксирует только хеш коммита этого ADR.

## Context

ADR-050 на базе `82b915c2` нашёл три влитых item: A17 (PR
[#2780](https://github.com/refusedguy/Harbor-Harness/pull/2780), `822c2312`),
A3 (PR [#2863](https://github.com/refusedguy/Harbor-Harness/pull/2863),
`66046b72`) и A9 (PR
[#2871](https://github.com/refusedguy/Harbor-Harness/pull/2871), slice
`1006894c`, merge `9f9e5b02`). Он отнёс D1 к семи item с новым публичным
контрактом (A1, A2, B1, C6, D1, D3, F1), каждому из которых нужен свой
владелец. Комментарии issue фиксируют те же следы и не содержат нового
решения владельца.

Свежая проверка закрывает item, который ADR-050 не учитывал:

| Item | Что требовалось | След в дереве | Slice / follow-up | Merge commit | PR |
|---|---|---|---|---|---|
| D1, provider capabilities | immutable `ProviderCapabilities` value с `Intersect` как политикой деградации | `src/Harbor.Abstractions.Contracts/Models/ProviderCapabilities.cs`, `tests/Harbor.Abstractions.Tests/ProviderCapabilitiesTests.cs` | `87458fef`, test-fix `808d069b` | `72308740` | [#2994](https://github.com/refusedguy/Harbor-Harness/pull/2994) |

Slice и merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`. PR #2994 поставляет только сам value и
политику `Intersect`. Чтение capabilities из `providers/*.json` и использование
в `AgentLoop` остаются отдельными slice и по сей день не влиты; комментарий
issue называет их прямо.

Остальные 86 item отчёта не образуют одного владельца. ADR-050 развёл их по
категориям: item с прямым отказом в применимости, item «только сверить», item
из target design `[spec]` и item с собственным публичным контрактом. Нового
факта сверх слияния D1 нет: владелец не назвал потребителя и измеримую цель
ни для одной из оставшихся частей.

## Decision

Код не писать. Конкретно:

- D1 не перевыводится: у него влитый PR и файл в дереве, оба подтверждены как
  предки базы. Повторная правка `ProviderCapabilities` создала бы второй
  источник истины там, где отчёт требует одного владельца.
- Остальные item не объединяются в meta-PR. У каждого свой контракт и свой
  критерий приёмки; новый slice открывается отдельной issue и PR, как это уже
  сделано для A17, A3, A9 и D1.
- D1: чтение capabilities из `providers/*.json` и проводка в `AgentLoop` не
  открываются заодно с влитым value без владельца и потребителя.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; гейты ослаблять запрещено.
- Новый guard не вводится: на дереве, где часть item влита, а часть ждёт
  владельца, проверка «все item закрыты» была бы вакуумной.
- Issue #2673 не закрывается: это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR, без шаблонного
  текста о прогрессе.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), их прогоняет CI.
- Issue остаётся OPEN. Пересмотр возможен по новому факту: владелец называет
  конкретный item, потребителя и измеримую цель. Тогда открывается отдельный
  slice, а не переоткрытие всего отчёта.

## References

- Issue [#2673](https://github.com/refusedguy/Harbor-Harness/issues/2673) —
  полный текст отчёта `t3code-features` part 1/2.
- [ADR-050](ADR-050-t3code-features-part1-report-verdict.md) — исходный
  вердикт по этому отчёту.
- Merged PR [#2994](https://github.com/refusedguy/Harbor-Harness/pull/2994)
  (`72308740`, D1), slice `87458fef`, follow-up `808d069b`.
- [ADR-077](ADR-077-kilocode-part2-post-verdict.md) — та же форма post-verdict
  записи по влитому slice.
- [DECISIONS.md](DECISIONS.md).
