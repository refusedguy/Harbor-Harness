# ADR-060: отчёт t3code-features part 2/2 (#2674) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2674](https://github.com/refusedguy/Harbor-Harness/issues/2674) прочитан
> полностью: concrete item F12–F14, G1–G14 и H1–H14 плюс таблица I1–I15
> «честно неприменимо»; весь отчёт насчитывает 121 item в обеих частях. Дубли
> проверены через `gh pr list --search 2674`, комментарии issue,
> `git log --all --grep=2674`, `git merge-base --is-ancestor` и
> `docs/adr/DECISIONS.md` на базе `origin/dev` (`5406294e`). Код, тесты, гейты,
> пороги, golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** по отчёту #2674 не открывается новый общий slice. Семь применимых
item уже влиты отдельными PR. Оставшиеся либо требуют собственного владельца и
контракта, либо прямо не применимы к терминальному .NET-агенту. Issue остаётся
OPEN как источник сравнения; комментарий фиксирует только хеш коммита этого ADR.

## Context

Issue #2674 — self-contained отчёт `t3code-features` part 2/2. Он сравнивает
Harbor с T3 Code по DX, диагностике, телеметрии, тестам и рабочим процессам с
GitHub. В этой части опубликованы item F12–F14, G1–G14, H1–H14 и таблица из 15
пунктов, прямо помеченных как неприменимые. Часть item отмечена как уже
имеющая аналог в Harbor, то есть отчёт описывает разрывы, а не пустые
подсистемы.

Инвентаризация `origin/dev` и истории PR показала семь влитых item. Все merge
commit подтверждены как предки `origin/dev` через
`git merge-base --is-ancestor`:

| Item | Что требовалось | PR (merge) |
|---|---|---|
| F13, импорт CLI-сессий | `harbor sessions import --from claude/codex` | [#2923](https://github.com/refusedguy/Harbor-Harness/pull/2923), `608327e0` |
| F14, prompt links | `harbor ask --prompt-link <url>` | [#2880](https://github.com/refusedguy/Harbor-Harness/pull/2880), `71893b36` |
| G2, resource gauge | сэмплинг RSS/CPU в канонический meter | [#2868](https://github.com/refusedguy/Harbor-Harness/pull/2868), `ac436e30` |
| G3, trace diagnostics | чтение медленных span из экспорта телеметрии | [#2793](https://github.com/refusedguy/Harbor-Harness/pull/2793), `e797a2b7` |
| G11, scheduled tasks | cron и HMAC-доказательство webhook | [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957), `0068e8e9` |
| H8, issues toolkit | `harbor issue list/read/comment` через gh | [#2913](https://github.com/refusedguy/Harbor-Harness/pull/2913), `6814c594` |
| H12, project add | клонирование с fallback SSH в HTTPS | [#2933](https://github.com/refusedguy/Harbor-Harness/pull/2933), `5dc29584` |

Комментарии в issue фиксируют те же slice-коммиты. Отдельные follow-up из G11
(HTTP-эндпоинт `harbor daemon hook` и fire-диспетчер) остаются внутри этого же
item и этим решением не перевыводятся.

Оставшиеся item не образуют одного владельца:

- Прямо не применимы к терминальному агенту или требуют облачной инфраструктуры:
  G8 (devcontainer), G12 (webhook relay), G14 (guest links), H6 (viewed files),
  H7 (PR commit links в diff pane), H9 (credit на merge), H10 (GitHub stacks),
  H13 (ghq-раскладка), H14 (remote write scope) и вся таблица I1–I15 — Electron,
  React, мобильные клиенты, T3 Connect relay, захват экрана, браузерный
  превью, диктовка, i18n, 30+ провайдеров, восемь forge-интеграций, биллинг.
- Имеют готовый аналог и требуют только сверки: G6 (свои гейты и seam graph
  вместо knip/check:changed), G7 (BENCHMARKS и frame budget CellForge), G9
  (`evals/` вместо scenarios), G10 (`Harbor.Benchmarks` вместо wire-bench),
  G5 в части log-ротации. Новый slice создал бы второй источник истины.
- Требуют собственного контракта и владельца: F12 (triage по логам и исходникам
  с подтверждением перед действиями), G1 (счётчики команд, эффектов, ран и git),
  G4 (сигнал по категории процесса), G5 в части TTL для effects/events/snapshots,
  G13 (idle reaper с durable thread ref), H1 (PR watch через gh), H2
  (auto-settle), H3 (связанные PR в сессии и обратный поиск), H4 (`IForgeClient`
  с адаптером GitHub), H5 (review guide по diff) и H11 (worktree handoff между
  сессиями с запретом кражи).

Part 1/2 того же отчёта (#2673) зафиксирован
[ADR-050](ADR-050-t3code-features-part1-report-verdict.md) и не перевыводится.

## Decision

Код не писать. Конкретно:

- Семь влитых item не перевыводятся. Повторная правка импорта сессий, prompt
  links, resource gauge, slow spans, scheduled tasks, issues toolkit и project
  add создала бы второй источник истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. Точечные порты открываются
  отдельными PR по конкретному item; не применимые не портируются; новые
  публичные контракты требуют владельца и измеримой цели.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть; документационные гейты `docs.yml` остаются на стороне
  CI.
- Issue #2674 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`), которые прогоняет CI.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельный PR, а не общий
  slice по #2674.

## References

- Issue [#2674](https://github.com/refusedguy/Harbor-Harness/issues/2674) —
  полный текст отчёта `t3code-features` part 2/2.
- Merged PR: [#2923](https://github.com/refusedguy/Harbor-Harness/pull/2923),
  [#2880](https://github.com/refusedguy/Harbor-Harness/pull/2880),
  [#2868](https://github.com/refusedguy/Harbor-Harness/pull/2868),
  [#2793](https://github.com/refusedguy/Harbor-Harness/pull/2793),
  [#2957](https://github.com/refusedguy/Harbor-Harness/pull/2957),
  [#2913](https://github.com/refusedguy/Harbor-Harness/pull/2913),
  [#2933](https://github.com/refusedguy/Harbor-Harness/pull/2933).
- Part 1/2 того же отчёта: issue
  [#2673](https://github.com/refusedguy/Harbor-Harness/issues/2673), вердикт
  [ADR-050](ADR-050-t3code-features-part1-report-verdict.md).
- [DECISIONS.md](DECISIONS.md).
