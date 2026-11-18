# ADR-052: отчёт kilocode-features part 1/2 (#2679) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2679](https://github.com/refusedguy/Harbor-Harness/issues/2679) прочитан
> полностью: 89 concrete item F001-F089 в семи группах A-G. Дубли проверены
> через `gh pr list --search 2679`, комментарии issue и `git log --all
> --grep=2679` на базе `origin/dev` (`1992dc8f`). Код, тесты, гейты, пороги,
> golden и AOT-профиль этим PR не меняются.

## Status

**Decided:** одного slice по всему отчёту #2679 не открывается. Три item уже
влиты отдельными PR, остальные требуют собственного владельца и критерия
приёмки. Issue остаётся OPEN как источник сравнения; комментарий фиксирует
только хеш коммита этого ADR.

## Context

Issue #2679 — self-contained отчёт `kilocode-features` part 1/2. Он покрывает
агентов и режимы, разрешения, контекст и токены, чекпоинты и rewind, браузер и
изображения, MCP, хуки и плагины. Каждый item несёт исходный `file:line`, суть
разрыва, аналог в Harbor через `git grep` и минимальный slice. Всего 89 item
F001-F089 в семи группах A-G, часть прямо помечена как неприменимая или
частично применимая.

Проверка базы `1992dc8f` и истории PR показывает три влитых item. Все три merge
commit подтверждены как предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | След | PR |
|---|---|---|---|
| F019, sensitive `.env` | Встроенный guard с приоритетом над allow-правилами, исключение `.env.example` | path guard в `PermissionService` | [#2874](https://github.com/refusedguy/Harbor-Harness/pull/2874), `970d7f83` |
| F037, `/context` | Модель сама запрашивает токены, лимит и время | context section в `harbor_inspect` | [#2862](https://github.com/refusedguy/Harbor-Harness/pull/2862), `0eb8a39f` |
| F041, env overrides | `KILO_DISABLE_AUTOCOMPACT` / `KILO_DISABLE_PRUNE` без правки конфига | env-override в `CompactionPolicy` | [#2785](https://github.com/refusedguy/Harbor-Harness/pull/2785), `2c4964db` |

Комментарии в issue фиксируют те же хеши: `2c4964db` для F041, `0eb8a39f`
(slice `c480ba85`) для F037 и `970d7f83` (slice `c4d3cfaa`) для F019. Issue
закрывался после первого влитого slice и был переоткрыт владельцем: он остаётся
источником сравнения для остальных пунктов.

Оставшиеся 86 item не образуют одного владельца:

- Item с прямым отказом в применимости: F070 `browser_open`, F071
  `generate_image`, F072 image output modality, F073 generated files, F074
  drag-and-drop, F075 MCP browser и F076 VS Code webview требуют браузер,
  image-capable провайдер или desktop GUI. F066 share и F078 OAuth browser
  flow требуют сервер и callback-слушатель.
- Item «только сверить»: F001 дефолтные агенты, F006 orchestrator, F007 debug
  agent, F015 `Allow/Ask/Deny`, F036 managed tool output, F047 steering и F051
  continuation после компакции уже имеют аналог в Harbor. Новый slice без
  потребителя создал бы второй источник истины.
- Item, меняющие публичный контракт или выполняемую поверхность: F002
  `ImmutableDenySet`, F003 токенизация shell-аргументов, F005 `PlanFileStore`,
  F017 `ApproveAsync(once|always)`, F021 provenance правил, F025 `/yolo`, F033
  `ThresholdPercent`, F040 отдельная модель компакции, F043 `IContextSource`
  registry, F056 workspace snapshots, F062 retention policy, F065 session
  import, F077 MCP transports, F084 MCP auto rules, F086-F089 хуки с мутацией
  параметров генерации и аргументов инструмента. У каждого свой контракт, свой
  риск для AOT и свой критерий приёмки. Их нельзя объединить в meta-PR и
  проверить одним прогоном.

Отчёт part 1/2 соседствует с part 2/2 (#2680), покрытым
[ADR-049](ADR-049-kilocode-features-part2-report-verdict.md). Этот ADR не
инвентаризует #2680 и не покрывает item из соседнего issue.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка `.env`-guard, context
  section и env-override создала бы второй источник истины там, где отчёт
  требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный контракт
  и свой критерий приёмки. Новый slice открывается отдельной issue и PR, как это
  уже сделано для F019, F037 и F041.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2679 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2679.
