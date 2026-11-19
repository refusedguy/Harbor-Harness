# ADR-069: отчёт gpui declarative devtools (#2700), последний in-flight slice влит

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2700](https://github.com/refusedguy/Harbor-Harness/issues/2700) прочитан
> полностью, включая предыдущий вердикт. Факты проверены через `gh pr view`
> и `git merge-base --is-ancestor` на базе `origin/dev` (`56608667`). Код,
> тесты, гейты, пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** [ADR-034](ADR-034-gpui-declarative-devtools-no-new-slice.md)
оставил PR #2765 единственным открытым slice отчёта и не предрешал его исход.
PR влит 2026-11-18 коммитом `192ed9a0` в `dev`. Нового slice по #2700 не
открывается, issue остаётся OPEN как источник сравнения.

## Context

ADR-034 на базе `09a673bc` развёл восемь исполнимых идей отчёта по трём
категориям: семь влиты или зафиксированы owner-decision, а идея 5
(dev reload pipeline из `watch.rs`) имела один in-flight slice. В таблице и в
разделе References прямо значилось, что PR #2765 OPEN и что другой агент
владеет контрактом debounce, burst coalescing и atomic publication; этот ADR
его не дублировал.

Свежая проверка закрывает последний пункт:

| Идея | Что требовалось | След в дереве | Merge commit |
|---|---|---|---|
| 5 | debounce, burst coalescing, atomic publication watcher'а | `DslDevFileWatcher.cs` и тесты в дереве | #2765, `192ed9a0` |

`git merge-base --is-ancestor 192ed9a0 origin/dev` подтверждает, что коммит
реально в базе. Остальные строки таблицы ADR-034 не изменились. Влитые PR
2442, 2489, 2528, 2531, 2535, 2538, 2727, 2761 и 2789 уже в `dev`, а
sentinel slot discovery остаётся owner-decision, поскольку в скомпилированном
C# DSL нет runtime-шаблона, который можно было бы выполнить с sentinel-аргументами.

Нового факта сверх слияния #2765 нет: владелец не назвал потребителя и
измеримую цель для runtime-интерпретатора или второго dev reload контракта.

## Decision

Код не писать. Конкретно:

- Идею 5 и остальные семь идей не перевыводятся: каждая уже имеет влитый PR
  и файл в дереве, а #2765 закрывает последний открытый slice.
- Sentinel slot discovery не открывается заново без owner-decision, отменяющей
  обоснование из ADR-034.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only
  PR не может их затронуть.
- Новый guard не вводится: на дереве, где все исполнимые идеи влиты или
  owner-decided, проверка «все slice на месте» была бы вакуумной. Гейты
  ослаблять запрещено.
- Issue #2700 не закрывается: это self-contained отчёт, а не трекер одной
  задачи. Комментарий содержит только хеш коммита этого ADR.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск это документационные гейты
  `docs.yml`: `check-md-links` и `md-lint`. Их прогоняет CI.
- Пересмотр возможен по новому факту: владелец представляет compiled-template
  дизайн, которому нужен runtime slot discovery, или новую devtool-идею с
  конкретным терминальным аналогом. Тогда открывается новый issue и новый
  ADR, а не переоткрытие #2700.

## References

- Issue [#2700](https://github.com/refusedguy/Harbor-Harness/issues/2700),
  полный текст отчёта по devtools для декларативного UI.
- Merged PR [#2765](https://github.com/refusedguy/Harbor-Harness/pull/2765),
  merge commit `192ed9a055bf4dd09b3533d445ba9ffe85b03564`.
- [ADR-034](ADR-034-gpui-declarative-devtools-no-new-slice.md), предыдущий
  вердикт по этому отчёту.
- [ADR-065](ADR-065-tui-ui-primitives-catalog-post-verdict.md), та же форма
  post-verdict по влитому in-flight slice.
- [DECISIONS.md](DECISIONS.md).
