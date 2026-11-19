# ADR-077: отчёт kilocode-features part 2/2 (#2680) после вердикта — влиты F170 и F115

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) прочитан
> полностью (688 строк, 180 concrete item F001-F180). [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md)
> зафиксировал вердикт на базе `8db8393f` и учёл три влитых item. После него в
> `dev` влиты ещё два slice, которых тот ADR не знал. Факты проверены через
> `gh pr view`, комментарии issue и
> `git merge-base --is-ancestor` на базе `origin/dev` (`abc83115`). Номер 077
> выбран потому, что 074 закреплён за #2660 и #2665 в `dev`, 075 — за открытым
> PR #2999, а 076 — за открытым PR #3004. Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** вердикт ADR-049 сохраняется: одного общего slice по #2680 нет.
Два новых slice не перевыводятся. Issue остаётся OPEN как источник сравнения;
комментарий фиксирует только хеш коммита этого ADR.

## Context

ADR-049 на базе `8db8393f` нашёл три влитых item: F157 (PR #2866, `36443d63`),
F159 (PR #2792, `947412df`) и F179 (PR #2907, `7509231f`). Комментарии issue
фиксируют те же следы и не содержат нового решения владельца.

Свежая проверка закрывает два item, которые ADR-049 не учитывал:

| Item | Что требовалось | След в дереве | Merge commit | PR |
|---|---|---|---|---|
| F170, JetBrains plugin parity settings | `docs/CLIENT_PARITY.md` с таблицей настроек CLI/Avalonia | `docs/CLIENT_PARITY.md`, запись в `docs/toc.yml` | `6efb6df6` | [#2990](https://github.com/refusedguy/Harbor-Harness/pull/2990) |
| F115, автоматическая генерация имени git branch | `IBranchNameGenerator`, разбор имени в kebab-case slug | `src/Harbor.Tools.Builtin/Tools/Bash/BranchNameGenerator.cs`, `tests/Harbor.Tools.Builtin.Tests/BranchNameGeneratorTests.cs` | `a3881de7` | [#2995](https://github.com/refusedguy/Harbor-Harness/pull/2995) |

Оба merge commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`. F170 — docs-only slice, F115 — чистый
парсер без проводки в продукт: ни тот, ни другой не меняют публичный контракт
issue. F115 реализует только parse-половину; `harbor branch suggest` и вызов
из agent loop по-прежнему не влиты и владельца не имеют.

Остальные 175 item отчёта не образуют одного владельца. ADR-049 развёл их по
категориям: item с прямым отказом в применимости, item «только сверить» и item
с собственным публичным контрактом. Нового факта сверх слияния F170 и F115
нет: владелец не назвал потребителя и измеримую цель ни для одной из оставшихся
частей.

## Decision

Код не писать. Конкретно:

- F170 и F115 не перевыводятся: у каждого влитый PR и файл в дереве, оба
  подтверждены как предки базы. Повторная правка создала бы второй источник
  истины там, где отчёт требует одного владельца.
- Остальные item не объединяются в meta-PR. У каждого свой контракт и свой
  критерий приёмки; новый slice открывается отдельной issue и PR, как это уже
  сделано для F157, F159, F179, F170 и F115.
- F115: `IBranchNameGenerator` как публичный контракт и `harbor branch
  suggest` не открываются заодно с влитым парсером без владельца и потребителя.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; гейты ослаблять запрещено.
- Новый guard не вводится: на дереве, где часть item влита, а часть ждёт
  владельца, проверка «все item закрыты» была бы вакуумной.
- Issue #2680 не закрывается: это self-contained отчёт, а не трекер одной
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

- Issue [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) —
  полный текст отчёта `kilocode-features` part 2/2.
- [ADR-049](ADR-049-kilocode-features-part2-report-verdict.md) — исходный
  вердикт по этому отчёту.
- Merged PR [#2990](https://github.com/refusedguy/Harbor-Harness/pull/2990)
  (`6efb6df6`, F170) и
  [#2995](https://github.com/refusedguy/Harbor-Harness/pull/2995)
  (`a3881de7`, F115).
- [ADR-070](ADR-070-dx-docs-gaps-post-verdict.md) — та же форма post-verdict
  записи по влитым slice.
- [DECISIONS.md](DECISIONS.md).
