# ADR-049: отчёт kilocode-features part 2/2 (#2680) — одного общего slice нет

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2680](https://github.com/refusedguy/Harbor-Harness/issues/2680) прочитан
> полностью: 180 concrete item F001-F180 в тринадцати группах A-M. Дубли
> проверены через `gh pr list --search 2680` и `git log --all --grep=2680` на
> базе `origin/dev` (`8db8393f`). Код, тесты, гейты, пороги, golden и
> AOT-профиль этим PR не меняются.

## Status

**Decided:** одного slice по всему отчёту #2680 не открывается. Три item уже
влиты отдельными PR, остальные требуют собственного владельца и критерия
приёмки. Issue остаётся OPEN как источник сравнения; комментарий фиксирует
только хеш коммита этого ADR.

## Context

Issue #2680 — self-contained отчёт `kilocode-features` part 2/2. Он покрывает
hooks и плагины, task history и оркестрацию, TUI и терминал, разные tools,
config и навыки, sandbox и память, провайдеров и прочее. Каждый item несёт
исходный `file:line`, суть разрыва, аналог в Harbor и минимальный slice.
Итоговая сводка называет 180 concrete фич; часть прямо помечена как
неприменимая или частично применимая.

Проверка базы `8db8393f` и истории PR показывает три влитых item. Все три merge
commit подтверждены как предки `origin/dev` через `git merge-base --is-ancestor`:

| Item | Что требовалось | След | PR |
|---|---|---|---|
| F157, shell integration | генератор init-скрипта с `HARBOR_*` env | init script в tools | [#2866](https://github.com/refusedguy/Harbor-Harness/pull/2866), `36443d63` |
| F159, мутирующие git-команды | `GitCommandClassifier.IsMutating(args)` | классификатор в tools | [#2792](https://github.com/refusedguy/Harbor-Harness/pull/2792), `947412df` |
| F179, базовая ветка по умолчанию | `GitBaseDetector` для review | детектор в tools | [#2907](https://github.com/refusedguy/Harbor-Harness/pull/2907), `7509231f` |

Комментарии в issue фиксируют те же slice-хеши: `36443d63` для F157,
`7509231f` для F179 и merge `e482e532` для F159.

Оставшиеся 177 item не образуют одного владельца:

- Item с прямым отказом в применимости: F143 speech-to-text и F144 FIM
  автодополнение привязаны к редактору, F180 send-file — к мобильному клиенту,
  которого в Harbor нет. F133 вставка изображений из clipboard помечена как
  частично применимая.
- Item «только сверить»: F117 session fork, F129 daemon, F135 notebook,
  F147 question, F150 custom instructions уже имеют аналог в Harbor; новый
  slice без потребителя создал бы второй источник истины.
- Item, меняющие публичный контракт или выполняемую поверхность: F090
  `IToolSchemaDecorator`, F094 `IMessageHistoryTransform`, F100 упорядоченный
  hook runner, F102 `IWorktreeSessionManager`, F108/F109 wakeup и cron, F134
  semantic index, F153 marketplace, F158 sandbox, F169 ACP. У каждого свой
  контракт, свой риск для AOT и свой критерий приёмки. Их нельзя объединить в
  meta-PR и проверить одним прогоном.

Отчёт part 2/2 соседствует с part 1/2 (#2679), у которого тоже нет вердикта.
Этот ADR не инвентаризует #2679 и не покрывает item из соседнего issue.

## Decision

Код не писать. Конкретно:

- Три влитых item не перевыводятся. Повторная правка генератора init-скрипта,
  классификатора git-команд и детектора базовой ветки создала бы второй источник
  истины там, где отчёт требует одного владельца.
- Оставшиеся item не объединяются в meta-PR. У каждого свой публичный контракт
  и свой критерий приёмки. Новый slice открывается отдельной issue и PR, как это
  уже сделано для F157, F159 и F179.
- Гейты, пороги, golden-файлы, self-test и AOT-профиль не меняются. Docs-only PR
  не может их затронуть.
- Issue #2680 не закрывается. Это каталог разрывов, а не трекер одной задачи.

## Consequences

- Меняются только два markdown-файла: этот ADR и
  [DECISIONS.md](DECISIONS.md). Единственный риск — документационные гейты
  `docs.yml` (`check-md-links` и `md-lint`); оба прогоняются до PR.
- Новый guard не вводится. На дереве, где исполняемые item влиты или требуют
  владельца, он был бы вакуумным, а гейты ослаблять запрещено.
- Пересмотр возможен по новому факту: владелец называет конкретный item,
  потребителя и измеримую цель. Тогда открывается отдельная issue, а не meta-PR
  по #2680.
